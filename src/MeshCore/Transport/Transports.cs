using System.IO.Ports;
using System.Net.Sockets;
using System.Threading.Channels;

namespace MeshCore;

/// <summary>A byte-frame link to a companion radio (USB serial, TCP/WiFi, Bluetooth LE, or a mock).</summary>
public interface IMeshTransport : IAsyncDisposable
{
    /// <summary>Human-readable description, e.g. "COM5" or "192.168.1.20:5000".</summary>
    string Description { get; }
    bool IsConnected { get; }
    bool SupportsPipelinedReads { get; }

    /// <summary>Frames received from the radio. A fresh reader is created by every successful <see cref="ConnectAsync"/>;
    /// it completes when the link drops.</summary>
    ChannelReader<byte[]> Received { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task SendAsync(byte[] payload, CancellationToken ct = default);
}

public class TransportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Framing used by the USB-serial and TCP companion firmware:
/// app→radio: '&lt;' + u16 LE length + payload; radio→app: '&gt;' + u16 LE length + payload.</summary>
public static class FrameCodec
{
    public const byte OutboundDelimiter = 0x3C; // '<'
    public const byte InboundDelimiter = 0x3E;  // '>'
    public const int HeaderSize = 3;

    public static byte[] Encode(byte[] payload, byte delimiter = OutboundDelimiter)
    {
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = delimiter;
        frame[1] = (byte)(payload.Length & 0xFF);
        frame[2] = (byte)((payload.Length >> 8) & 0xFF);
        Array.Copy(payload, 0, frame, HeaderSize, payload.Length);
        return frame;
    }
}

/// <summary>Stateful decoder that buffers partial frames.</summary>
public sealed class FrameDecoder(byte delimiter = FrameCodec.InboundDelimiter, int maxFrame = 4096)
{
    private readonly List<byte> _buffer = new();

    public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data.ToArray());
        var frames = new List<byte[]>();
        while (true)
        {
            var start = _buffer.IndexOf(delimiter);
            if (start < 0) { _buffer.Clear(); break; }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < FrameCodec.HeaderSize) break;
            var len = _buffer[1] | (_buffer[2] << 8);
            if (len > maxFrame) { _buffer.RemoveAt(0); continue; } // resync on garbage
            if (_buffer.Count < FrameCodec.HeaderSize + len) break;
            frames.Add(_buffer.GetRange(FrameCodec.HeaderSize, len).ToArray());
            _buffer.RemoveRange(0, FrameCodec.HeaderSize + len);
        }
        return frames;
    }

    public void Reset() => _buffer.Clear();
}

/// <summary>Base class for stream transports (serial, TCP) that use <see cref="FrameCodec"/>.</summary>
public abstract class StreamTransportBase : IMeshTransport
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    protected Stream? Stream;

    public abstract string Description { get; }
    public bool IsConnected { get; private set; }
    public virtual bool SupportsPipelinedReads => true;
    public ChannelReader<byte[]> Received => _channel.Reader;

    /// <summary>Raised once when the link drops unexpectedly (reader thread, or the writer when a write finds the link
    /// dead). The received-frames reader is completed first, so the session's receive loop ends.</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>When the last unexpected connection loss was observed (Swift passes this time to its disconnection handler).</summary>
    public DateTimeOffset? DisconnectedAt { get; private set; }

    protected abstract Task<Stream> OpenAsync(CancellationToken ct);
    protected abstract void Close();

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;
        _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        Stream = await OpenAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _lossReported, 0);
        IsConnected = true;
        _readCts = new CancellationTokenSource();
        var channel = _channel;
        var token = _readCts.Token;
        _readTask = Task.Run(() => ReadLoop(channel, token));
    }

    private async Task ReadLoop(Channel<byte[]> channel, CancellationToken ct)
    {
        var decoder = new FrameDecoder();
        var buffer = new byte[4096];
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = Stream;
                if (stream is null) break;
                var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) break;
                foreach (var frame in decoder.Decode(buffer.AsSpan(0, n)))
                    channel.Writer.TryWrite(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            if (!ct.IsCancellationRequested) HandleConnectionLost(channel, error);
            else
            {
                IsConnected = false;
                channel.Writer.TryComplete();
            }
        }
    }

    private int _lossReported;

    /// <summary>One teardown and one notification per connection, whether the reader or a failed write sees the loss
    /// first (port of the Swift WiFi transport's <c>handleReceiveTermination</c>).</summary>
    private void HandleConnectionLost(Channel<byte[]> channel, Exception? error)
    {
        var wasConnected = IsConnected;
        IsConnected = false;
        channel.Writer.TryComplete();
        if (!wasConnected || Interlocked.Exchange(ref _lossReported, 1) != 0) return;
        DisconnectedAt = DateTimeOffset.UtcNow;
        Disconnected?.Invoke(error);
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        _readCts?.Cancel();
        try { Close(); } catch { /* ignore */ }
        Stream = null;
        _channel.Writer.TryComplete();
        if (_readTask is not null)
        {
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
        }
        _readTask = null;
    }

    public async Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        var stream = Stream;
        if (stream is null || !IsConnected) throw new TransportException("Not connected");
        var frame = FrameCodec.Encode(payload);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransportException("Write timed out");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // The link is dead: finish the received stream now so the session sees the loss even if the read is stuck.
            // Report first (while IsConnected is still true), then stop the reader.
            HandleConnectionLost(_channel, ex);
            _readCts?.Cancel();
            try { Close(); } catch { /* ignore */ }
            throw new TransportException("Write failed: " + ex.Message, ex);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Companion radio over WiFi (TCP, default port 5000).</summary>
public sealed class TcpTransport(string host, int port) : StreamTransportBase
{
    /// <summary>Companion WiFi TCP port (firmware <c>TCP_PORT</c> default; the radio CLI cannot read it).</summary>
    public const int DefaultPort = 5000;

    private TcpClient? _client;
    public string Host { get; } = host;
    public int Port { get; } = port;
    public override string Description => $"{Host}:{Port}";

    protected override async Task<Stream> OpenAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TransportException($"Timed out connecting to {Description}");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new TransportException($"Could not connect to {Description}: {ex.Message}", ex);
        }
        _client = client;
        return client.GetStream();
    }

    protected override void Close()
    {
        _client?.Close();
        _client = null;
    }
}

/// <summary>Companion radio over USB serial (115200 baud, 8N1).</summary>
public sealed class SerialTransport(string portName, int baudRate = 115200) : StreamTransportBase
{
    private SerialPort? _port;
    public string PortName { get; } = portName;
    public override string Description => PortName;

    public static string[] AvailablePorts()
    {
        try { return SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p).ToArray(); }
        catch { return []; }
    }

    protected override Task<Stream> OpenAsync(CancellationToken ct)
    {
        var port = new SerialPort(PortName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
        };
        try { port.Open(); }
        catch (Exception ex)
        {
            port.Dispose();
            throw new TransportException($"Could not open {PortName}: {ex.Message}", ex);
        }
        port.DiscardInBuffer();
        _port = port;
        return Task.FromResult(port.BaseStream);
    }

    protected override void Close()
    {
        try { _port?.Close(); } catch { /* ignore */ }
        _port?.Dispose();
        _port = null;
    }
}

/// <summary>In-memory transport for tests: frames written by the app are handed to <see cref="OnSend"/>,
/// and the test can inject radio frames with <see cref="Inject"/>.</summary>
public sealed class MockTransport : IMeshTransport
{
    private Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
    public List<byte[]> Sent { get; } = new();
    public Func<byte[], IEnumerable<byte[]>>? OnSend { get; set; }
    public string Description => "mock";
    public bool IsConnected { get; private set; }
    public bool SupportsPipelinedReads { get; set; } = true;
    public ChannelReader<byte[]> Received => _channel.Reader;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _channel = Channel.CreateUnbounded<byte[]>();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        _channel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        if (!IsConnected) throw new TransportException("Not connected");
        lock (Sent) Sent.Add(payload);
        if (OnSend is not null)
            foreach (var reply in OnSend(payload)) _channel.Writer.TryWrite(reply);
        TaskCompletionSource? held = null;
        lock (_holdLock)
        {
            if (_parkNextSend)
            {
                _parkNextSend = false;
                held = _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        if (held is not null) await held.Task.ConfigureAwait(false);
    }

    private readonly object _holdLock = new();
    private bool _parkNextSend, _failHeldSend;
    private TaskCompletionSource? _held;

    /// <summary>Parks the next send (after it is recorded in <see cref="Sent"/>) until <see cref="ReleaseHeldSend"/>, so a
    /// test can inject a reply or a loss before the write returns.</summary>
    public void HoldNextSend() { lock (_holdLock) _parkNextSend = true; }

    /// <summary>The parked send throws <see cref="TransportException"/> when released.</summary>
    public void FailHeldSend() { lock (_holdLock) _failHeldSend = true; }

    /// <summary>True while a send is parked by <see cref="HoldNextSend"/>.</summary>
    public bool SendIsWaiting { get { lock (_holdLock) return _held is not null; } }

    /// <summary>Resumes the parked send (a no-op when nothing is parked).</summary>
    public void ReleaseHeldSend()
    {
        TaskCompletionSource? held;
        bool fail;
        lock (_holdLock)
        {
            held = _held;
            _held = null;
            _parkNextSend = false;
            fail = _failHeldSend;
            _failHeldSend = false;
        }
        if (fail) held?.TrySetException(new TransportException("simulated send failure"));
        else held?.TrySetResult();
    }

    public void Inject(byte[] frame) => _channel.Writer.TryWrite(frame);
    public void SimulateDrop() { IsConnected = false; _channel.Writer.TryComplete(); }
    public ValueTask DisposeAsync() => new(DisconnectAsync());
}

using System.Diagnostics;

namespace MeshCore;

/// <summary>
/// Owns a transport, parses incoming frames, dispatches events and provides typed request/response commands.
/// Port of the Swift <c>MeshCoreSession</c> actor.
/// </summary>
public sealed partial class MeshCoreSession : IAsyncDisposable
{
    private readonly object _stateLock = new();
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private volatile bool _isRunning;
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    // auto message fetch
    private EventSubscription? _autoFetchSub;
    private Task? _autoFetchTask;
    private CancellationTokenSource? _autoFetchCts;
    private Task<MessageResult>? _inFlightGetMessage;
    private readonly object _getMessageLock = new();

    public MeshCoreSession(IMeshTransport transport, SessionConfiguration? configuration = null)
    {
        Transport = transport;
        Configuration = configuration ?? SessionConfiguration.Default;
    }

    public IMeshTransport Transport { get; }
    public SessionConfiguration Configuration { get; }
    public EventDispatcher Dispatcher { get; } = new();
    public ContactManager Contacts { get; } = new();
    public SelfInfo? SelfInfo { get; private set; }
    public DateTimeOffset? DeviceTime { get; private set; }

    /// <summary>The radio's capabilities from the last device-info reply (<see cref="QueryDeviceAsync"/>), if any.
    /// Firmware-gated commands check this; when it is unknown they are sent and the radio decides.</summary>
    public DeviceCapabilities? DeviceCapabilities { get; private set; }

    public bool IsRunning => _isRunning;

    /// <summary>Orders every request/response exchange on the wire (one in flight at a time).</summary>
    internal RequestResponseSerializer RequestSerializer { get; } = new();

    /// <summary>Orders configuration read-modify-write workflows (other params) through their cache refresh.
    /// Separate from <see cref="RequestSerializer"/>, which is not reentrant.</summary>
    internal RequestResponseSerializer ConfigSerializer { get; } = new();
    public ConnectionState ConnectionState { get { lock (_stateLock) return _connectionState; } }

    /// <summary>Raised (on a background thread) whenever the session connection state changes.</summary>
    public event Action<ConnectionState>? ConnectionStateChanged;

    /// <summary>Raised for every frame sent or received (for diagnostics / debug log).</summary>
    public event Action<bool, byte[]>? FrameTraced;

    private void UpdateState(ConnectionState s)
    {
        lock (_stateLock) _connectionState = s;
        ConnectionStateChanged?.Invoke(s);
    }

    // MARK: Lifecycle

    public async Task StartAsync(int? reconnectingAttempt = null, CancellationToken ct = default)
    {
        if (_isRunning) return;
        UpdateState(reconnectingAttempt is { } a ? new ConnectionState(ConnectionStateKind.Reconnecting, Math.Max(1, a)) : ConnectionState.Connecting);
        try
        {
            await Transport.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UpdateState(new ConnectionState(ConnectionStateKind.Failed, Error: ex.Message));
            throw;
        }
        _isRunning = true;
        UpdateState(ConnectionState.Connected);
        _receiveCts = new CancellationTokenSource();
        var reader = Transport.Received;
        var token = _receiveCts.Token;
        _receiveTask = Task.Run(() => ReceiveLoop(reader, token));
        try
        {
            SelfInfo = await SendAppStartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _isRunning = false;
            _receiveCts.Cancel();
            await Transport.DisconnectAsync().ConfigureAwait(false);
            UpdateState(new ConnectionState(ConnectionStateKind.Failed, Error: ex.Message));
            throw;
        }
    }

    public async Task StopAsync(bool disconnectTransport = true)
    {
        _isRunning = false;
        StopAutoMessageFetching();
        _receiveCts?.Cancel();
        Dispatcher.FinishAll();
        if (disconnectTransport) await Transport.DisconnectAsync().ConfigureAwait(false);
        UpdateState(ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ReceiveLoop(System.Threading.Channels.ChannelReader<byte[]> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
                HandleReceived(frame);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Debug.WriteLine($"Receive loop error: {ex}");
        }
        if (!_isRunning || ct.IsCancellationRequested) return;
        _isRunning = false;
        StopAutoMessageFetching();
        Dispatcher.Dispatch(new MeshEvent.ConnectionStateChanged(ConnectionState.Disconnected));
        UpdateState(ConnectionState.Disconnected);
    }

    /// <summary>Parses a raw frame and dispatches it (also used by tests).</summary>
    public void HandleReceived(byte[] data)
    {
        if (data.Length == 0) return;
        FrameTraced?.Invoke(false, RedactForTrace(data, ResponseCode.CliReply));
        var e = PacketParser.Parse(data);
        if (e is MeshEvent.StatusResponseEvent sr && sr.Response.Layout == StatusLayout.Repeater
            && Contacts.GetByKeyPrefix(sr.Response.PublicKeyPrefix) is { Type: ContactType.Room })
        {
            e = Parsers.ParseStatusResponse(data.From(1), StatusLayout.RoomServer);
        }
        Contacts.TrackChanges(e);
        switch (e)
        {
            case MeshEvent.CurrentTime t: DeviceTime = t.Time; break;
            case MeshEvent.SelfInfoEvent s: SelfInfo = s.Info; break;
            case MeshEvent.DeviceInfo d: DeviceCapabilities = d.Info; break;
        }
        Dispatcher.Dispatch(e);
    }

    /// <summary>Radio CLI frames can carry a WiFi password or a BLE PIN, so the trace shows only their opcode.</summary>
    private static byte[] RedactForTrace(byte[] frame, ResponseCode? cliReply = null) =>
        frame.Length > 1 && (cliReply is { } r ? frame[0] == (byte)r : frame[0] == (byte)CommandCode.RunCliCommand)
            ? [frame[0]]
            : frame;

    /// <summary>Throws <see cref="MeshCoreErrorKind.Unsupported"/> when the radio's firmware is known to be older than
    /// <paramref name="minimum"/>. Unknown capabilities let the command through (the radio answers "unsupported").</summary>
    private void RequireFirmware(byte minimum, string feature)
    {
        if (DeviceCapabilities is { } caps && caps.FirmwareVersion < minimum)
            throw MeshCoreException.Unsupported(minimum, feature);
    }

    /// <summary>A stopped session reports cancellation, not a radio timeout or a lost link (Swift CancellationError).</summary>
    private Exception StoppedOr(Exception e) => _isRunning ? e : SessionStopped();

    private static OperationCanceledException SessionStopped() => new("The radio session was stopped.");

    // MARK: Subscriptions

    public EventSubscription Subscribe(Func<MeshEvent, bool>? filter = null) => Dispatcher.Subscribe(filter);

    /// <summary>Waits for the next event matching <paramref name="predicate"/>; returns null on timeout.</summary>
    public async Task<MeshEvent?> WaitForEventAsync(Func<MeshEvent, bool> predicate, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var sub = Dispatcher.Subscribe(predicate);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? Configuration.DefaultTimeout);
        try
        {
            if (await sub.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false) && sub.Reader.TryRead(out var e)) return e;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return null;
    }

    // MARK: Request / response core

    internal enum Disposition { Ignore, Success, Failure }

    internal readonly record struct Match<T>(Disposition Kind, T? Value = default, Exception? Error = null)
    {
        public static Match<T> Ok(T value) => new(Disposition.Success, value);
        public static Match<T> Fail(Exception e) => new(Disposition.Failure, default, e);
        public static readonly Match<T> Skip = new(Disposition.Ignore);
    }

    /// <summary>
    /// Writes one frame. A stopped session never writes (the transport may survive for the next session): it throws
    /// <see cref="OperationCanceledException"/>. A disconnected transport throws <see cref="MeshCoreErrorKind.NotConnected"/>.
    /// </summary>
    public async Task SendRawAsync(byte[] data, CancellationToken ct = default)
    {
        if (!Transport.IsConnected) throw MeshCoreException.NotConnected();
        if (!_isRunning) throw SessionStopped();
        FrameTraced?.Invoke(true, RedactForTrace(data));
        try
        {
            await Transport.SendAsync(data, ct).ConfigureAwait(false);
        }
        catch (TransportException ex)
        {
            throw new MeshCoreException(MeshCoreErrorKind.ConnectionLost, ex.Message, inner: ex);
        }
    }

    internal Task<T> WithSerialization<T>(Func<Task<T>> operation, CancellationToken ct) =>
        RequestSerializer.WithSerializationAsync(operation, ct);

    internal Task<T> SendAndMatchAsync<T>(byte[] data, Func<MeshEvent, Match<T>> matcher, TimeSpan? timeout = null, CancellationToken ct = default) =>
        WithSerialization(() => SendAndMatchHoldingSerialization(data, matcher, timeout, ct), ct);

    /// <summary>
    /// One send-and-wait exchange; the caller holds <see cref="RequestSerializer"/> for the whole exchange.
    /// <paramref name="beforeSend"/> runs just before the write (it may throw to prevent it). With
    /// <paramref name="drainOnCancel"/> the wait ignores <paramref name="ct"/> once the frame is written, so a cancelled
    /// caller still consumes its own reply and doesn't leave an orphan for the next command.
    /// </summary>
    internal async Task<T> SendAndMatchHoldingSerialization<T>(byte[] data, Func<MeshEvent, Match<T>> matcher, TimeSpan? timeout,
        CancellationToken ct, Action? beforeSend = null, bool drainOnCancel = false)
    {
        using var sub = Dispatcher.Subscribe();
        beforeSend?.Invoke();
        await SendRawAsync(data, ct).ConfigureAwait(false);
        using var cts = drainOnCancel ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? Configuration.DefaultTimeout);
        try
        {
            await foreach (var e in sub.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                if (e is MeshEvent.ConnectionStateChanged { State.Kind: ConnectionStateKind.Disconnected })
                    throw new MeshCoreException(MeshCoreErrorKind.ConnectionLost);
                var m = matcher(e);
                if (m.Kind == Disposition.Success) return m.Value!;
                if (m.Kind == Disposition.Failure) throw m.Error!;
            }
        }
        catch (OperationCanceledException) when (drainOnCancel || !ct.IsCancellationRequested)
        {
            throw StoppedOr(MeshCoreException.Timeout());
        }
        throw StoppedOr(new MeshCoreException(MeshCoreErrorKind.ConnectionLost));
    }

    /// <summary>Matcher for a plain OK / firmware error reply.</summary>
    internal static Match<bool> OkOrDeviceError(MeshEvent e) => e switch
    {
        MeshEvent.Ok { Value: null } => Match<bool>.Ok(true),
        MeshEvent.Error err => Match<bool>.Fail(MeshCoreException.Device(err.Code)),
        _ => Match<bool>.Skip,
    };

    internal Task<T> SendAndWaitAsync<T>(byte[] data, Func<MeshEvent, T?> pick, TimeSpan? timeout = null, CancellationToken ct = default) where T : class =>
        SendAndMatchAsync(data, e => pick(e) is { } v ? Match<T>.Ok(v) : Match<T>.Skip, timeout, ct);

    internal Task<T> SendAndWaitWithErrorAsync<T>(byte[] data, Func<MeshEvent, T?> pick, TimeSpan? timeout = null, CancellationToken ct = default) where T : class =>
        SendAndMatchAsync(data, e =>
        {
            if (e is MeshEvent.Error err) return Match<T>.Fail(MeshCoreException.Device(err.Code));
            return pick(e) is { } v ? Match<T>.Ok(v) : Match<T>.Skip;
        }, timeout, ct);

    internal Task SendSimpleCommandAsync(byte[] data, CancellationToken ct = default) =>
        SendAndMatchAsync<object>(data, e => e switch
        {
            MeshEvent.Ok { Value: null } => Match<object>.Ok(true),
            MeshEvent.Error err => Match<object>.Fail(MeshCoreException.Device(err.Code)),
            _ => Match<object>.Skip,
        }, null, ct);

    private static void RequireFullKey(byte[] publicKey, string operation)
    {
        if (publicKey.Length != PacketBuilder.PublicKeySize)
            throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, $"Full 32-byte public key required for {operation}");
    }

    // MARK: Auto message fetching

    public void StartAutoMessageFetching()
    {
        if (_autoFetchSub is not null) return;
        _autoFetchCts = new CancellationTokenSource();
        var sub = Dispatcher.Subscribe(e => e is MeshEvent.MessagesWaiting);
        _autoFetchSub = sub;
        var token = _autoFetchCts.Token;
        _autoFetchTask = Task.Run(async () =>
        {
            try
            {
                while (await sub.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    // Coalesce every pending "messages waiting" signal into one drain pass.
                    while (sub.Reader.TryRead(out _)) { }
                    await DrainMessagesAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Kicks an immediate drain of the radio's message queue (used after reconnects).</summary>
    public void RequestMessageDrain() => Dispatcher.Dispatch(new MeshEvent.MessagesWaiting());

    private async Task DrainMessagesAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var r = await GetMessageAsync(ct: token).ConfigureAwait(false);
                if (r is MessageResult.NoMoreMessages) break;
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Debug.WriteLine($"Auto message fetch error: {ex.Message}"); }
    }

    public void StopAutoMessageFetching()
    {
        _autoFetchCts?.Cancel();
        _autoFetchSub?.Dispose();
        _autoFetchSub = null;
        _autoFetchCts = null;
    }

    public bool IsAutoFetching => _autoFetchSub is not null;
}

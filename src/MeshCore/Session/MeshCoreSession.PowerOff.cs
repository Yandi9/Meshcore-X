namespace MeshCore;

/// <summary>How a power-off exchange ended (port of Swift <c>PowerOffExchangeResult</c>). Only
/// <see cref="Silence"/> means the radio powered off.</summary>
public enum PowerOffOutcome
{
    /// <summary>Not sent: cancelled before the frame was handed to the transport.</summary>
    NotSubmittedCancelled,
    /// <summary>Not sent: the session is not running.</summary>
    NotSubmittedNotRunning,
    /// <summary>Not sent: the transport is not connected.</summary>
    NotSubmittedNotConnected,
    /// <summary>The board answered with a CLI reply (possibly empty) and stayed on.</summary>
    StillOn,
    /// <summary>The radio rejected the command with a firmware error (e.g. 1 = unsupported).</summary>
    Rejected,
    /// <summary>The write failed and nothing was buffered.</summary>
    SendFailed,
    /// <summary>No reply within the reply window after the frame was written (or the link dropped right after it):
    /// the radio powered off.</summary>
    Silence,
    /// <summary>The session was stopped while waiting; the outcome is unknown.</summary>
    Stopped,
}

/// <param name="Outcome">How the exchange ended.</param>
/// <param name="Reply">For <see cref="PowerOffOutcome.StillOn"/>: the CLI reply body ("" when empty).</param>
/// <param name="ErrorCode">For <see cref="PowerOffOutcome.Rejected"/>: the firmware error code.</param>
public sealed record PowerOffExchangeResult(PowerOffOutcome Outcome, string? Reply = null, byte? ErrorCode = null)
{
    public bool WasSubmitted => Outcome is not (PowerOffOutcome.NotSubmittedCancelled or PowerOffOutcome.NotSubmittedNotRunning
        or PowerOffOutcome.NotSubmittedNotConnected);

    public bool PoweredOff => Outcome == PowerOffOutcome.Silence;
}

/// <summary>Lets cancellation win only before the power-off frame is handed to the transport (port of Swift
/// <c>PowerOffSubmissionGate</c>). Thread-safe.</summary>
public sealed class PowerOffSubmissionGate
{
    private readonly object _lock = new();
    private bool _cancelled, _submitted;
    private DateTimeOffset? _submissionTime;

    /// <summary>Raised (on the session's thread) once submission wins. Marshal to the UI thread before touching UI.</summary>
    public event Action? Submitted;

    /// <summary>True when this call is the one that prevents the frame from being sent; false when it was already sent.</summary>
    public bool CancelIfUnsubmitted()
    {
        lock (_lock)
        {
            if (_submitted) return false;
            _cancelled = true;
            return true;
        }
    }

    /// <summary>False when cancellation already won. Records the submission time on success.</summary>
    public bool BeginSubmission()
    {
        lock (_lock)
        {
            if (_cancelled || _submitted) return false;
            _submitted = true;
            _submissionTime = DateTimeOffset.UtcNow;
        }
        Submitted?.Invoke();
        return true;
    }

    public bool DidSubmit { get { lock (_lock) return _submitted; } }
    public DateTimeOffset? SubmissionTime { get { lock (_lock) return _submissionTime; } }
}

public sealed partial class MeshCoreSession
{
    /// <summary>
    /// Asks the paired radio to cut its power (<c>poweroff</c> over <c>CMD_RUN_CLI_COMMAND</c>, firmware v14+).
    /// Returns true when the frame was written and no reply arrived within <see cref="SessionConfiguration.PowerOffReplyWindow"/>
    /// (the radio powered off); false when the board answered and stayed on.
    /// </summary>
    /// <exception cref="MeshCoreException"><see cref="MeshCoreErrorKind.Unsupported"/> (radio known older than v14);
    /// <see cref="MeshCoreErrorKind.DeviceError"/> (firmware rejected it); <see cref="MeshCoreErrorKind.NotConnected"/>
    /// (not connected or the write failed).</exception>
    /// <exception cref="OperationCanceledException">Cancelled before sending, or the session stopped.</exception>
    public async Task<bool> PowerOffAsync(CancellationToken ct = default)
    {
        RequireFirmware(DeviceCapabilities.RadioCliFirmwareVersion, "Power off");
        var r = await ExchangePowerOffAsync(null, ct).ConfigureAwait(false);
        return r.Outcome switch
        {
            PowerOffOutcome.Silence => true,
            PowerOffOutcome.StillOn => false,
            PowerOffOutcome.Rejected => throw MeshCoreException.Device(r.ErrorCode),
            PowerOffOutcome.SendFailed or PowerOffOutcome.NotSubmittedNotConnected => throw MeshCoreException.NotConnected(),
            _ => throw SessionStopped(),
        };
    }

    /// <summary>
    /// Sends <c>poweroff</c> and reports in detail what happened (never throws). A CLI reply or firmware error that was
    /// already buffered wins over a later write failure or a stopped session. Cancelling <paramref name="ct"/> (or
    /// <see cref="PowerOffSubmissionGate.CancelIfUnsubmitted"/>) only prevents the send; once submitted the exchange runs
    /// to its end.
    /// </summary>
    public async Task<PowerOffExchangeResult> ExchangePowerOffAsync(PowerOffSubmissionGate? gate = null, CancellationToken ct = default)
    {
        gate ??= new PowerOffSubmissionGate();
        using var reg = ct.Register(() => gate.CancelIfUnsubmitted());
        try
        {
            await RequestSerializer.AcquireAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PowerOffExchangeResult(PowerOffOutcome.NotSubmittedCancelled);
        }
        try
        {
            return await PerformPowerOffExchangeAsync(gate).ConfigureAwait(false);
        }
        finally { RequestSerializer.Release(); }
    }

    private async Task<PowerOffExchangeResult> PerformPowerOffExchangeAsync(PowerOffSubmissionGate gate)
    {
        if (!_isRunning) return new PowerOffExchangeResult(PowerOffOutcome.NotSubmittedNotRunning);
        if (!Transport.IsConnected) return new PowerOffExchangeResult(PowerOffOutcome.NotSubmittedNotConnected);

        using var sub = Dispatcher.Subscribe(e => e is MeshEvent.CliReply or MeshEvent.Error
            or MeshEvent.ConnectionStateChanged { State.Kind: ConnectionStateKind.Disconnected });
        if (!_isRunning) return new PowerOffExchangeResult(PowerOffOutcome.NotSubmittedNotRunning);
        if (!gate.BeginSubmission()) return new PowerOffExchangeResult(PowerOffOutcome.NotSubmittedCancelled);

        var frame = PacketBuilder.RunCliCommand(PacketBuilder.PowerOffCommand);
        var sendFailed = false;
        try
        {
            FrameTraced?.Invoke(true, RedactForTrace(frame));
            await Transport.SendAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { sendFailed = true; }

        using var window = new CancellationTokenSource(sendFailed ? Configuration.PowerOffBufferedReplyGrace : Configuration.PowerOffReplyWindow);
        try
        {
            while (await sub.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
            {
                while (sub.Reader.TryRead(out var e))
                {
                    switch (e)
                    {
                        case MeshEvent.CliReply r: return new PowerOffExchangeResult(PowerOffOutcome.StillOn, r.Text);
                        case MeshEvent.Error err: return new PowerOffExchangeResult(PowerOffOutcome.Rejected, ErrorCode: err.Code ?? 0);
                        case MeshEvent.ConnectionStateChanged when !sendFailed:
                            // The link dropped right after a written poweroff: the board went down.
                            return new PowerOffExchangeResult(PowerOffOutcome.Silence);
                    }
                }
            }
            // The subscription was finished: the session stopped before anything arrived.
            return new PowerOffExchangeResult(PowerOffOutcome.Stopped);
        }
        catch (OperationCanceledException) { }
        return new PowerOffExchangeResult(sendFailed ? PowerOffOutcome.SendFailed : PowerOffOutcome.Silence);
    }
}

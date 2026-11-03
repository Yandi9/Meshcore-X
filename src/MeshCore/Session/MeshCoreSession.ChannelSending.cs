namespace MeshCore;

public sealed partial class MeshCoreSession
{
    /// <summary>
    /// Applies a channel's flood scope and sends the channel message while holding one request/response slot, so no other
    /// command (another channel's send, a scope change from the UI, a retry) can change the scope between the two
    /// exchanges. Port of Swift <c>sendChannelMessage(channel:text:timestamp:floodScope:)</c>.
    /// <list type="bullet">
    /// <item>A scope rejection throws <see cref="MeshCoreErrorKind.DeviceError"/> and the message is not sent.</item>
    /// <item>Cancelling while queued writes nothing. Cancelling after the scope frame was written resumes the caller at
    /// once with <see cref="OperationCanceledException"/>, but the outstanding ACK is still drained under the held slot
    /// (so the next command can't consume it) and the message frame is not written.</item>
    /// </list>
    /// <see cref="ResolvedFloodScope.Unscoped"/> needs firmware v12+.
    /// </summary>
    public async Task SendChannelMessageAsync(byte channel, string text, DateTimeOffset timestamp, ResolvedFloodScope floodScope, CancellationToken ct = default)
    {
        if (floodScope is ResolvedFloodScope.Unscoped) RequireFirmware(12, "Unscoped flood sends");
        var commands = new[] { floodScope.ToCommand(), PacketBuilder.SendChannelMessage(channel, text, timestamp) };
        await WithDrainingSerialization(async () =>
        {
            foreach (var command in commands)
                await SendAndMatchHoldingSerialization(command, OkOrDeviceError, null, ct,
                    beforeSend: ct.ThrowIfCancellationRequested, drainOnCancel: true).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> under <see cref="RequestSerializer"/>, detached from the caller's cancellation
    /// (Swift serializer semantics): a caller cancelled while queued releases the slot without writing; a caller cancelled
    /// mid-exchange is resumed immediately while the operation keeps the slot until its own exchange resolves.
    /// </summary>
    private async Task<T> WithDrainingSerialization<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        await RequestSerializer.AcquireAsync(ct).ConfigureAwait(false);
        Task<T> work;
        try
        {
            ct.ThrowIfCancellationRequested();
            work = Task.Run(operation, CancellationToken.None);
        }
        catch
        {
            RequestSerializer.Release();
            throw;
        }
        _ = work.ContinueWith(t => { _ = t.Exception; RequestSerializer.Release(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!ct.CanBeCanceled) return await work.ConfigureAwait(false);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(() => cancelled.TrySetResult()).ConfigureAwait(false))
        {
            if (await Task.WhenAny(work, cancelled.Task).ConfigureAwait(false) != work)
                throw new OperationCanceledException(ct);
        }
        return await work.ConfigureAwait(false);
    }
}

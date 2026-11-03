namespace MeshCore;

/// <summary>
/// A non-reentrant async lock that admits one request/response exchange (or workflow) at a time, in arrival order.
/// Port of the Swift <c>RequestResponseSerializer</c>; public so app services can order their own read-modify-write
/// workflows the same way. <see cref="WaiterCount"/> lets tests wait until a command is queued.
/// </summary>
public sealed class RequestResponseSerializer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _waiters;

    /// <summary>Callers currently waiting to acquire (not counting the holder).</summary>
    public int WaiterCount => Volatile.Read(ref _waiters);

    /// <summary>True while an exchange holds the serializer.</summary>
    public bool IsHeld => _gate.CurrentCount == 0;

    public async Task AcquireAsync(CancellationToken ct = default)
    {
        if (_gate.Wait(0)) return;
        Interlocked.Increment(ref _waiters);
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _waiters); }
    }

    public void Release() => _gate.Release();

    public async Task<T> WithSerializationAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try { return await operation().ConfigureAwait(false); }
        finally { Release(); }
    }

    public async Task WithSerializationAsync(Func<Task> operation, CancellationToken ct = default)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); }
        finally { Release(); }
    }
}

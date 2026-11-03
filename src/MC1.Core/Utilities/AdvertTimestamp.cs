namespace MC1.Core.Utilities;

/// <summary>
/// Whether an advert's timestamp (set by the sending node's clock, epoch seconds) can be trusted
/// (port of the iPhone app's <c>AdvertTimestamp</c>).
/// </summary>
public static class AdvertTimestamp
{
    /// <summary>2020-01-01 00:00 UTC. Older stamps come from nodes whose clock was never set.</summary>
    public const long MinimumTimestamp = 1_577_836_800;

    /// <summary>How far in the future a stamp may be (small clock drift).</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static bool IsPlausible(long timestamp, DateTimeOffset reference) =>
        timestamp >= MinimumTimestamp && timestamp <= reference.ToUnixTimeSeconds() + (long)FutureTolerance.TotalSeconds;
}

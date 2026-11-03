using MC1.Core.Models;

namespace MC1.Core.Services;

/// <summary>
/// Screens a stored status capture as a whole before it is charted (ported from the iPhone's RadioHistoryFilter): one
/// impossible signal value (a garbled reply) drops every value of that capture from the charts, so its counters
/// can't leave spikes either. The snapshots themselves stay in the database (and in exports).
/// </summary>
public static class RadioHistoryFilter
{
    /// <summary>The SNR range the radio can report (a signed quarter-dB byte).</summary>
    public const double MinSnr = -32.0, MaxSnr = 31.75;

    /// <summary>The RSSI / noise floor range the radio can report (dBm).</summary>
    public const int MinSignal = -256, MaxSignal = 98;

    /// <summary>True when every signal value present in the capture is possible (missing values are fine).</summary>
    public static bool Includes(NodeSnapshotRecord snapshot)
    {
        if (snapshot.LastSnr is { } snr && !(snr >= MinSnr && snr <= MaxSnr)) return false;
        if (snapshot.LastRssi is { } rssi && rssi is < MinSignal or > MaxSignal) return false;
        if (snapshot.NoiseFloor is { } noise && noise is < MinSignal or > MaxSignal) return false;
        return true;
    }

    /// <summary>The captures that may be charted, in order.</summary>
    public static IReadOnlyList<NodeSnapshotRecord> Chartable(IEnumerable<NodeSnapshotRecord> snapshots) => snapshots.Where(Includes).ToList();

    /// <summary>Whether any chartable capture has radio data (battery or signal values).</summary>
    public static bool HasRadioData(IEnumerable<NodeSnapshotRecord> snapshots) =>
        snapshots.Any(s => Includes(s) && (s.BatteryMv is not null || s.LastSnr is not null || s.LastRssi is not null || s.NoiseFloor is not null
            || s.PacketsSent is not null || s.PacketsReceived is not null));
}

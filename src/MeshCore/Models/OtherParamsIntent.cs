namespace MeshCore;

/// <summary>
/// Fields a caller means to change in the radio's "other params" record (port of the Swift <c>OtherParamsIntent</c>).
/// Null leaves a field untouched. The intent is merged into the current record inside the session's configuration
/// serializer (<see cref="MeshCoreSession.ApplyOtherParamsAsync"/>), so concurrent writers never revert each other.
/// An empty intent, or one that changes nothing, sends no command.
/// </summary>
public sealed record OtherParamsIntent
{
    /// <summary>Telemetry modes use the low two bits; higher bits are not a mode.</summary>
    public const byte TelemetryModeMask = 0b11;

    public bool? ManualAddContacts { get; init; }
    public byte? TelemetryModeBase { get; init; }
    public byte? TelemetryModeLocation { get; init; }
    public byte? TelemetryModeEnvironment { get; init; }
    public byte? AdvertisementLocationPolicy { get; init; }
    public byte? MultiAcks { get; init; }

    public bool IsEmpty =>
        ManualAddContacts is null && TelemetryModeBase is null && TelemetryModeLocation is null
        && TelemetryModeEnvironment is null && AdvertisementLocationPolicy is null && MultiAcks is null;

    /// <summary>The telemetry bits the wire keeps. Null fields stay null.</summary>
    public OtherParamsIntent Normalized() => this with
    {
        TelemetryModeBase = TelemetryModeBase is { } b ? (byte)(b & TelemetryModeMask) : null,
        TelemetryModeLocation = TelemetryModeLocation is { } l ? (byte)(l & TelemetryModeMask) : null,
        TelemetryModeEnvironment = TelemetryModeEnvironment is { } e ? (byte)(e & TelemetryModeMask) : null,
    };

    /// <summary>Returns a copy of <paramref name="config"/> with the supplied fields applied (telemetry masked; the
    /// advertisement policy byte is stored as given). <paramref name="config"/> itself is not modified.</summary>
    public OtherParamsConfig AppliedTo(OtherParamsConfig config)
    {
        var i = Normalized();
        var updated = config with { };
        if (i.ManualAddContacts is { } m) updated.ManualAddContacts = m;
        if (i.TelemetryModeBase is { } tb) updated.TelemetryModeBase = tb;
        if (i.TelemetryModeLocation is { } tl) updated.TelemetryModeLocation = tl;
        if (i.TelemetryModeEnvironment is { } te) updated.TelemetryModeEnvironment = te;
        if (i.AdvertisementLocationPolicy is { } a) updated.AdvertisementLocationPolicy = a;
        if (i.MultiAcks is { } ma) updated.MultiAcks = ma;
        return updated;
    }
}

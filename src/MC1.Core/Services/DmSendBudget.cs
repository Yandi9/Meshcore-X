namespace MC1.Core.Services;

/// <summary>
/// How often a direct message (or room post) is sent before it fails: attempts on the contact's known path, then — when
/// "Fall back to flood" is on — the path is cleared once and the flood attempts follow (port of Swift <c>DMSendBudget</c>,
/// MeshCore One 1.7). A contact with no known path is sent by flood throughout.
/// </summary>
public readonly record struct DmSendBudget
{
    public const int DefaultDirectAttempts = 4;
    public const int DefaultFloodAttempts = 4;
    public const int MaxDirectAttempts = 4;
    public const int MaxFloodAttempts = 4;

    /// <summary>Choices for "Attempts on path".</summary>
    public static IReadOnlyList<int> DirectChoices { get; } = [1, 2, 3, 4];

    /// <summary>Choices for "Flood attempts" (zero is stored by switching "Fall back to flood" off).</summary>
    public static IReadOnlyList<int> FloodChoices { get; } = [1, 2, 3, 4];

    public static DmSendBudget Default { get; } = new(DefaultDirectAttempts, DefaultFloodAttempts);

    public int DirectAttempts { get; }
    public int FloodAttempts { get; }
    public int TotalAttempts => DirectAttempts + FloodAttempts;
    public bool FallsBackToFlood => FloodAttempts > 0;

    public DmSendBudget(int directAttempts, int floodAttempts)
    {
        DirectAttempts = ClampDirect(directAttempts);
        FloodAttempts = ClampFlood(floodAttempts);
    }

    public static DmSendBudget From(AppSettings s) => new(s.DmDirectAttempts, s.DmFloodAttempts);

    public static int ClampDirect(int value) => Math.Clamp(value, 1, MaxDirectAttempts);
    public static int ClampFlood(int value) => Math.Clamp(value, 0, MaxFloodAttempts);

    /// <summary>Off stores zero; on restores the default only when the stored count is zero.</summary>
    public static int FloodAttemptsFor(bool fallbackOn, int stored) => !fallbackOn ? 0 : stored > 0 ? ClampFlood(stored) : DefaultFloodAttempts;

    /// <summary>The value the "Flood attempts" menu shows (1–4).</summary>
    public static int MenuFloodAttempts(int stored) => stored is >= 1 and <= MaxFloodAttempts ? stored : DefaultFloodAttempts;

    /// <summary>Whether attempt <paramref name="attempt"/> (0-based) is the one before which the path is reset to flood.</summary>
    public bool ResetsPathBefore(int attempt) => FloodAttempts > 0 && attempt == DirectAttempts;
}

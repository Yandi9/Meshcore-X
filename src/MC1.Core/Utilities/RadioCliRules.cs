using MeshCore;

namespace MC1.Core.Utilities;

/// <summary>
/// Decides whether a line typed for the local radio goes to the radio itself (firmware 1.18 radio CLI, CMD 66) instead
/// of the app's own commands. Port of Swift <c>RadioCLIPassthrough</c>: the same key sets and the same app-mapped
/// verbs, plus <c>stats</c>, which only the Windows app maps.
/// </summary>
public static class RadioCliPassthrough
{
    /// <summary>Keys the radio CLI reads and writes.</summary>
    public static readonly IReadOnlySet<string> GetSetKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "af", "dutycycle", "int.thresh", "cad", "radio.rxgain", "radio.rxps", "rxdelay",
        "agc.reset.interval", "txdelay", "direct.txdelay",
        RadioCliKey.TimezoneOffset, RadioCliKey.WifiSsid, RadioCliKey.WifiPassword, RadioCliKey.WifiEnabled,
        "radio.fem.rxgain", "radio.fem.txgain", "fan",
    };

    /// <summary>Keys the radio CLI only reads.</summary>
    public static readonly IReadOnlySet<string> GetOnlyKeys = new HashSet<string>(StringComparer.Ordinal) { RadioCliKey.WifiStatus, RadioCliKey.WifiIp };

    /// <summary>Keys the radio CLI only writes.</summary>
    public static readonly IReadOnlySet<string> SetOnlyKeys = new HashSet<string>(StringComparer.Ordinal) { "pin", RadioCliKey.WifiClear, "fan.lo", "fan.hi" };

    /// <summary>First words the app handles itself. Power-off verbs stay app-mapped because the radio powers off without
    /// a CLI reply.</summary>
    private static readonly HashSet<string> MappedFirstWords = new(StringComparer.Ordinal)
    {
        "clock", "ver", "board", "advert", "advert.zerohop", "floodadv", "reboot", "poweroff", "shutdown",
        "get", "set", "help", "clear", "session", "login", "logout", "nodes", "channels",
        // Windows only: the local statistics summary.
        "stats",
    };

    /// <summary>True when <paramref name="line"/> should be sent to the local radio's CLI (firmware v14+).</summary>
    public static bool ShouldSend(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToLowerInvariant()).ToArray();
        if (parts.Length == 0) return false;
        var first = parts[0];
        if (first == "get")
        {
            if (parts.Length < 2) return false;
            var key = parts[1];
            if (key.StartsWith('_')) return false;
            return GetSetKeys.Contains(key) || GetOnlyKeys.Contains(key);
        }
        if (first == "set")
        {
            if (parts.Length < 2) return false;
            var key = parts[1];
            if (key.StartsWith('_')) return false;
            return GetSetKeys.Contains(key) || SetOnlyKeys.Contains(key);
        }
        if (MappedFirstWords.Contains(first)) return false;
        return true;
    }
}

/// <summary>A command that gets no reply from the node it is sent to (port of Swift <c>RemoteCLINoReplyCommand</c>).</summary>
public enum RemoteCliNoReplyKind { Reboot, PowerOff }

public static class RemoteCliNoReply
{
    /// <summary>
    /// A repeater or room command that gets no reply: matched on the lowercased, trimmed verb, or that verb plus a space
    /// (<c>poweroffx</c> does not match).
    /// </summary>
    public static RemoteCliNoReplyKind? ForNode(string command)
    {
        var normalized = command.ToLowerInvariant().Trim();
        if (Matches(normalized, "reboot") || Matches(normalized, "clkreboot")) return RemoteCliNoReplyKind.Reboot;
        if (Matches(normalized, "poweroff") || Matches(normalized, "shutdown")) return RemoteCliNoReplyKind.PowerOff;
        return null;
    }

    /// <summary>A contact's radio matches the whole command, including case.</summary>
    public static RemoteCliNoReplyKind? ForContactRadio(string command) => command switch
    {
        "reboot" => RemoteCliNoReplyKind.Reboot,
        "poweroff" or "shutdown" => RemoteCliNoReplyKind.PowerOff,
        _ => null,
    };

    private static bool Matches(string normalized, string verb) => normalized == verb || normalized.StartsWith(verb + " ", StringComparison.Ordinal);
}

/// <summary>Completion lines for radio CLI commands (port of the radio parts of Swift <c>CLICompletionEngine</c>).</summary>
public static class RadioCliCompletion
{
    /// <summary>Verbs a chat contact's radio accepts over remote CLI.</summary>
    public static readonly IReadOnlyList<string> ContactRadioVerbs = ["get", "set", "reboot", "poweroff", "shutdown", "board", "ver"];

    /// <summary>Keys that are opcode-mapped on the paired radio and CLI on a remote radio (<c>freq</c> is get-only).</summary>
    private static readonly string[] RemoteRadioKeys = ["name", "radio", "tx", "path.hash.mode", "multi.acks"];

    /// <summary>
    /// A completed firmware command must match exactly, so a separator is added only while it needs another argument:
    /// <c>get</c> and <c>set</c> need a key, and <c>set &lt;key&gt;</c> needs a value (except <c>set wifi.clear</c>).
    /// </summary>
    public static bool NeedsArgument(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        return words[0].ToLowerInvariant() switch
        {
            "get" => words.Length == 1,
            "set" => words.Length == 1 || (words.Length == 2 && words[1] != RadioCliKey.WifiClear),
            _ => false,
        };
    }

    /// <summary><paramref name="line"/> with a trailing space when it still needs an argument.</summary>
    public static string Completed(string line) => NeedsArgument(line) ? line + " " : line;

    /// <summary>Lines added to the local radio's completions on firmware 1.18 (radio CLI keys and power off).</summary>
    public static IReadOnlyList<string> LocalRadioLines()
    {
        var lines = new List<string>();
        lines.AddRange(GetSetSorted(RadioCliPassthrough.GetSetKeys.Concat(RadioCliPassthrough.GetOnlyKeys)).Select(k => "get " + k));
        lines.AddRange(GetSetSorted(RadioCliPassthrough.GetSetKeys.Concat(RadioCliPassthrough.SetOnlyKeys)).Select(k => Completed("set " + k)));
        lines.Add("poweroff");
        lines.Add("shutdown");
        return lines;
    }

    /// <summary>Completions for a chat contact's radio: its verbs and the radio CLI keys.</summary>
    public static IReadOnlyList<string> ContactRadioLines()
    {
        var lines = new List<string> { "help", "clear" };
        lines.AddRange(ContactRadioVerbs.Where(v => v is not ("get" or "set")));
        var getKeys = RadioCliPassthrough.GetSetKeys.Concat(RemoteRadioKeys).Concat(RadioCliPassthrough.GetOnlyKeys).Append("freq");
        var setKeys = RadioCliPassthrough.GetSetKeys.Concat(RemoteRadioKeys).Concat(RadioCliPassthrough.SetOnlyKeys);
        lines.AddRange(GetSetSorted(getKeys).Select(k => "get " + k));
        lines.AddRange(GetSetSorted(setKeys).Select(k => Completed("set " + k)));
        return lines;
    }

    private static IEnumerable<string> GetSetSorted(IEnumerable<string> keys) => keys.Distinct().OrderBy(k => k, StringComparer.Ordinal);
}

/// <summary>Firmware feature checks that use the firmware code first and the version text as a fallback.</summary>
public static class RadioFeatures
{
    private const int AutoAddConfigMinimumFirmware = 9;
    private const int AutoAddMaxHopsMinimumFirmware = 10;

    /// <summary>
    /// Auto-add filters beyond the manual-add switch (port of Swift <c>supportsAutoAddConfig</c>). Code 9+ guarantees
    /// them; code 8 covers 1.10 through 1.12, so a 1.12 version string still counts. Version text such as
    /// "PowerSaving18" has no major.minor pair and relies on the code.
    /// </summary>
    public static bool SupportsAutoAddConfig(DeviceCapabilities? caps) =>
        caps is not null && (caps.FirmwareVersion >= AutoAddConfigMinimumFirmware || IsAtLeast(caps.Version, 1, 12));

    /// <summary>Auto-add max hops: code 10 or version 1.14.</summary>
    public static bool SupportsAutoAddMaxHops(DeviceCapabilities? caps) =>
        caps is not null && (caps.FirmwareVersion >= AutoAddMaxHopsMinimumFirmware || IsAtLeast(caps.Version, 1, 14));

    /// <summary>True when the first "major.minor" in <paramref name="version"/> is at least the given one.</summary>
    public static bool IsAtLeast(string? version, int major, int minor)
    {
        if (string.IsNullOrEmpty(version)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(version, @"(\d+)\.(\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var ma) || !int.TryParse(m.Groups[2].Value, out var mi)) return false;
        return ma > major || (ma == major && mi >= minor);
    }
}

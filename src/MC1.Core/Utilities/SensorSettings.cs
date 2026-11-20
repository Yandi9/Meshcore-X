using System.Text;

namespace MC1.Core.Utilities;

/// <summary>A setting a sensor's firmware lists (<c>sensor list</c>); key and value are kept verbatim.</summary>
public sealed record SensorSetting(string Key, string Value)
{
    /// <summary>Room for the CLI correlation prefix ("XX|") within a 160-byte message.</summary>
    public const int MaxCommandBytes = 160 - 3;

    /// <summary>The <c>sensor set &lt;key&gt; &lt;value&gt;</c> command, or null when the key or value is empty, has spaces
    /// or control characters, or the command is too long.</summary>
    public string? SetCommand(string value)
    {
        if (!IsToken(Key) || !IsToken(value)) return null;
        var command = $"sensor set {Key} {value}";
        return Encoding.UTF8.GetByteCount(command) <= MaxCommandBytes ? command : null;
    }

    private static bool IsToken(string s) => s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}

public class SensorSettingsException(string message) : Exception(message);

/// <summary>The sensor answered that it has no such setting.</summary>
public sealed class SensorSettingRejectedException() : SensorSettingsException(L.T("The sensor rejected this setting."));

/// <summary>Parses CommonCLI sensor replies (port of Swift <c>SensorSettingsResponseParser</c>, MeshCore One 1.7).</summary>
public static class SensorSettingsParser
{
    public const string ListCommand = "sensor list";
    public const string GetPrefix = "sensor get ";
    public const string RebootCommand = "reboot";

    public sealed record Page(int Total, IReadOnlyList<SensorSetting> Settings, int? Next);

    private static SensorSettingsException Invalid() => new(L.T("The sensor sent an unexpected reply."));

    public static Page List(string response, int offset)
    {
        response = response.Replace("\r\n", "\n");
        if (response.TrimEnd('\n') == "no custom var" && offset == 0) return new Page(0, [], null);
        var lines = response.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0 || !lines[0].EndsWith(" vars", StringComparison.Ordinal)
            || !int.TryParse(lines[0][..^" vars".Length], out var total) || total <= 0 || offset < 0 || offset >= total) throw Invalid();
        lines.RemoveAt(0);
        int? next = null;
        if (lines.Count > 0 && lines[^1].StartsWith("... next:", StringComparison.Ordinal))
        {
            if (!int.TryParse(lines[^1]["... next:".Length..], out var n)) throw Invalid();
            next = n;
            lines.RemoveAt(lines.Count - 1);
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var settings = new List<SensorSetting>();
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) throw Invalid();
            var key = line[..eq];
            if (!keys.Add(key)) throw Invalid();
            settings.Add(new SensorSetting(key, line[(eq + 1)..]));
        }
        var end = offset + settings.Count;
        if (settings.Count == 0 || end > total) throw Invalid();
        if (next is { } nx) { if (nx != end || nx <= offset || nx >= total) throw Invalid(); }
        else if (end != total) throw Invalid();
        return new Page(total, settings, next);
    }

    /// <summary>The value from a <c>sensor get</c> reply ("&gt; value"), or null for "null".</summary>
    public static string? Value(string response)
    {
        response = response.TrimEnd('\r', '\n');
        if (response == "null") return null;
        return response.StartsWith("> ", StringComparison.Ordinal) ? response[2..] : throw Invalid();
    }

    /// <summary>Throws unless a <c>sensor set</c> reply is "ok".</summary>
    public static void Accepted(string response)
    {
        response = response.TrimEnd('\r', '\n');
        if (response == "ok") return;
        if (response == "can't find custom var") throw new SensorSettingRejectedException();
        throw Invalid();
    }
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace MC1.Core.Mecp;

/// <summary>
/// A decoded MECP (Mesh Emergency Communication Protocol) message: <c>MECP/&lt;severity&gt;/&lt;codes…&gt; [free text]</c>.
/// Port of Swift <c>MECPMessage</c>/<c>MECPDecode</c> (MeshCore One 1.7).
/// </summary>
public sealed record MecpMessage(MecpSeverity Severity, IReadOnlyList<string> Codes, string? FreeText)
{
    /// <summary>UTF-8 budget a sender keeps to; decoding does not reject a longer string.</summary>
    public const int MaxUtf8Bytes = 200;

    internal MecpDecode.Extraction Extracted => MecpDecode.Extract(FreeText, Codes);
    public int? PersonCount => Extracted.PersonCount;
    public string? TimestampHhmm => Extracted.TimestampHhmm;
    public int? Eta => Extracted.Eta;
    public string? DisplayTail => Extracted.DisplayTail;
}

public enum MecpSeverity { Mayday = 0, Urgent = 1, Safety = 2, Routine = 3 }

/// <summary>The MECP wire grammar. A string that doesn't match decodes to null.</summary>
public static class MecpDecode
{
    private const string Prefix = "MECP/";
    private const int BodyLead = 2;
    private const string EtaCode = "R03";
    private const string PaxSuffix = "pax";

    public static MecpMessage? Decode(string? input)
    {
        if (input is null || !input.StartsWith(Prefix, StringComparison.Ordinal) || input.Length <= Prefix.Length) return null;
        var sev = input[Prefix.Length];
        if (sev is < '0' or > '3') return null;
        var severity = (MecpSeverity)(sev - '0');
        var bodyStart = Prefix.Length + BodyLead;
        var body = bodyStart < input.Length ? input[bodyStart..] : "";

        var codes = new List<string>();
        var tail = new List<string>();
        var readingTail = false;
        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!readingTail && IsCodeToken(token)) codes.Add(token);
            else { readingTail = true; tail.Add(token); }
        }
        return new MecpMessage(severity, codes, tail.Count == 0 ? null : string.Join(' ', tail));
    }

    internal readonly record struct Extraction(int? PersonCount, string? TimestampHhmm, int? Eta, string? DisplayTail);

    internal static Extraction Extract(string? freeText, IReadOnlyList<string> codes)
    {
        if (freeText is null) return default;
        var removals = new List<(int Start, int End)>();
        int? pax = null, eta = null;
        string? clock = null;
        if (FirstPax(freeText) is { } p) { pax = p.Count; removals.Add((p.Start, p.End)); }
        if (FirstTimestamp(freeText) is { } t && IsAcceptedClock(t.Digits)) { clock = t.Digits; removals.Add((t.Start, t.End)); }
        if (codes.Contains(EtaCode) && LeadingEta(freeText) is { } e) { eta = e.Minutes; removals.Add((e.Start, e.End)); }
        var text = freeText;
        foreach (var (start, end) in removals.OrderByDescending(r => r.Start)) text = text.Remove(start, end - start);
        var collapsed = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return new Extraction(pax, clock, eta, collapsed.Length == 0 ? null : collapsed);
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static bool IsCodeToken(string token) =>
        token.Length == 3 && token[0] is >= 'A' and <= 'Z' && IsAsciiDigit(token[1]) && IsAsciiDigit(token[2]);

    private static (int Count, int Start, int End)? FirstPax(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!IsAsciiDigit(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && IsAsciiDigit(text[i])) i++;
            if (string.CompareOrdinal(text, i, PaxSuffix, 0, PaxSuffix.Length) != 0) continue;
            if (!int.TryParse(text.AsSpan(start, i - start), out var count)) return null;
            return (count, start, i + PaxSuffix.Length);
        }
        return null;
    }

    private static (string Digits, int Start, int End)? FirstTimestamp(string text)
    {
        var search = 0;
        while (search < text.Length && text.IndexOf('@', search) is var at && at >= 0)
        {
            var cursor = at + 1;
            var count = 0;
            while (cursor < text.Length && count < 4 && IsAsciiDigit(text[cursor])) { cursor++; count++; }
            var atBoundary = cursor == text.Length || text[cursor] == ' ';
            if (count == 4 && atBoundary) return (text.Substring(at + 1, 4), at, cursor);
            search = at + 1;
        }
        return null;
    }

    private static (int Minutes, int Start, int End)? LeadingEta(string text)
    {
        if (text.Length == 0 || !IsAsciiDigit(text[0])) return null;
        var i = 0;
        while (i < text.Length && IsAsciiDigit(text[i])) i++;
        if (i < text.Length && text[i] != ' ') return null;
        return int.TryParse(text.AsSpan(0, i), out var minutes) ? (minutes, 0, i) : null;
    }

    private static bool IsAcceptedClock(string digits) =>
        digits.Length == 4 && int.TryParse(digits[..2], out var h) && int.TryParse(digits[2..], out var m) && h <= 23 && m <= 59;
}

/// <summary>
/// Reads a decoded MECP message as phrases in the app's language, from the MECP language packs (CC BY 4.0, see
/// licenses/MECP-LANGUAGES-CC-BY-4.0.txt). Port of Swift <c>MECPCatalog</c>.
/// </summary>
public static class MecpCatalog
{
    private const string EnglishStem = "en";
    private const string EtaCode = "R03";
    private const string ShelterCode = "H07";
    private static readonly string[] Stems = ["en", "de", "es", "fr", "it", "nl", "pl", "pt"];
    private static readonly ConcurrentDictionary<string, LanguagePack?> Packs = new();

    private sealed record LanguagePack(Dictionary<string, string> Severity, Dictionary<string, string> Codes, string? UnknownCode);

    /// <summary>The reading, one part per line, or null when <paramref name="text"/> isn't MECP.</summary>
    public static string? Reading(string? text, string? language = null) =>
        Lines(text, language) is { } lines ? string.Join('\n', lines) : null;

    /// <summary>The reading on one line (chat list, notifications), or the text itself when it isn't MECP.</summary>
    public static string DisplayText(string text, string? language = null) =>
        Lines(text, language) is { } lines ? string.Join(' ', lines) : text;

    public static bool IsMecp(string? text) => MecpDecode.Decode(text) is not null;

    private static IReadOnlyList<string>? Lines(string? text, string? language)
    {
        if (MecpDecode.Decode(text) is not { } message) return null;
        if (Pack(EnglishStem) is not { } english) return null;
        var stem = StemFor(language ?? L.Code);
        var pack = Pack(stem) ?? english;
        var parts = new List<string>();
        var sevKey = ((int)message.Severity).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if ((pack.Severity.GetValueOrDefault(sevKey) ?? english.Severity.GetValueOrDefault(sevKey)) is { Length: > 0 } label) parts.Add(label);
        foreach (var code in message.Codes) parts.Add(Phrase(code, message, pack, english));
        if (message.PersonCount is { } count) parts.Add(L.Plural(count, "{0} person", "{0} people"));
        if (message.TimestampHhmm is { } hhmm) parts.Add($"{hhmm[..2]}:{hhmm[2..]} UTC");
        if (message.DisplayTail is { } tail) parts.Add(tail);
        return parts.Count == 0 ? null : parts;
    }

    private static string Phrase(string code, MecpMessage message, LanguagePack pack, LanguagePack english)
    {
        if ((pack.Codes.GetValueOrDefault(code) ?? english.Codes.GetValueOrDefault(code)) is { } template)
        {
            if (code == EtaCode && message.Eta is { } eta && ReplaceBracket(template, inner => $"{eta} {inner}") is { } withEta) return withEta;
            if (code == ShelterCode && message.PersonCount is { } n && ReplaceBracket(template, _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } withCount) return withCount;
            return template;
        }
        return $"{code}: {pack.UnknownCode ?? english.UnknownCode ?? "Unknown code"}";
    }

    private static string? ReplaceBracket(string template, Func<string, string> replacement)
    {
        var open = template.IndexOf('[');
        if (open < 0) return null;
        var close = template.IndexOf(']', open + 1);
        if (close < 0) return null;
        var inner = template[(open + 1)..close];
        return template[..open] + replacement(inner) + template[(close + 1)..];
    }

    private static string StemFor(string language)
    {
        var stem = language.Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return Stems.Contains(stem) ? stem : EnglishStem;
    }

    private static LanguagePack? Pack(string stem) => Packs.GetOrAdd(stem, Load);

    private static LanguagePack? Load(string stem)
    {
        try
        {
            using var stream = typeof(MecpCatalog).Assembly.GetManifestResourceStream($"MC1.Core.Mecp.{stem}.json");
            if (stream is null) return null;
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var severity = new Dictionary<string, string>();
            if (root.TryGetProperty("severities", out var sev))
                foreach (var p in sev.EnumerateObject())
                    if (p.Value.TryGetProperty("local", out var local) && local.GetString() is { } s) severity[p.Name] = s;
            var codes = new Dictionary<string, string>();
            if (root.TryGetProperty("codes", out var cs))
                foreach (var p in cs.EnumerateObject())
                    if (p.Value.GetString() is { } s) codes[p.Name] = s;
            string? unknown = root.TryGetProperty("ui", out var ui) && ui.TryGetProperty("unknown_code", out var u) ? u.GetString() : null;
            return new LanguagePack(severity, codes, unknown);
        }
        catch (Exception) { return null; }
    }
}

using System.Text.Json.Nodes;

namespace MC1.Core.Maps;

/// <summary>
/// Shows the vector map's place names in the app's language (port of the iPhone app's MapLabelLocalizer): the
/// OpenMapTiles name labels prefer <c>name:&lt;lang&gt;</c> and fall back to the local <c>name</c>. English keeps
/// the style as published; road numbers and other labels that aren't the stock name expression are left alone.
/// </summary>
public static class MapLabelLocalizer
{
    public const string DefaultLanguageCode = "en";

    /// <summary>The vector source of OpenFreeMap's styles.</summary>
    public const string SourceId = "openmaptiles";

    private const string LocalNameField = "name";
    private const string EnglishNameField = "name_en";
    private const string LegacyGermanNameField = "name_de";

    /// <summary>The name expressions OpenFreeMap's styles use: English with local fallback, alone or with a non-Latin second line.</summary>
    private static readonly JsonNode[] OriginalNameExpressions = BuildOriginals();

    private static JsonNode[] BuildOriginals()
    {
        static JsonArray Fallback() => new("coalesce", new JsonArray("get", EnglishNameField), new JsonArray("get", LocalNameField));
        JsonNode Bilingual(string separator) => new JsonArray(
            "case", new JsonArray("has", "name:nonlatin"),
            new JsonArray("concat", new JsonArray("get", "name:latin"), separator, new JsonArray("get", "name:nonlatin")),
            Fallback());
        return [Fallback(), Bilingual(" "), Bilingual("\n")];
    }

    /// <summary>The OpenStreetMap language suffix for an app language ("zh-Hans" → "zh").</summary>
    public static string NameLanguage(string languageCode) => languageCode.Split('-')[0].ToLowerInvariant();

    /// <summary>The label expression for a language: <c>["coalesce", ["get", "name:es"], ["get", "name"]]</c>.</summary>
    public static JsonArray Expression(string languageCode)
    {
        var lang = NameLanguage(languageCode);
        var fields = new List<string> { "name:" + lang };
        if (lang == "de") fields.Add(LegacyGermanNameField);
        fields.Add(LocalNameField);
        var expr = new JsonArray("coalesce");
        foreach (var f in fields) expr.Add(new JsonArray("get", f));
        return expr;
    }

    /// <summary>Whether a layer's labels change with the language.</summary>
    public static bool IsLocalizable(JsonNode? textField, string? source) =>
        source == SourceId && textField is not null && OriginalNameExpressions.Any(o => JsonNode.DeepEquals(o, textField));

    /// <summary>The <c>text-field</c> to use for a symbol layer in <paramref name="languageCode"/>, or null to keep its own.</summary>
    public static JsonNode? Localize(JsonNode? textField, string? source, string languageCode)
    {
        if (NameLanguage(languageCode) == DefaultLanguageCode) return null;
        return IsLocalizable(textField, source) ? Expression(languageCode) : null;
    }
}

using System.Globalization;

namespace MC1.Core.Utilities;

/// <summary>
/// Countries, states and provinces the user can pick for radio preset recommendations (ported from MC1Services
/// RegionalAreas). "Region" here means where the user is — not a mesh flood-scope region.
/// </summary>
public static class RegionalAreas
{
    /// <summary>A country in the picker. <see cref="Name"/> is in the app's language.</summary>
    public sealed record Country(string Id, string EnglishName, IReadOnlyList<Subdivision>? Subdivisions)
    {
        public string Name => L.T(EnglishName);
        public override string ToString() => Name;
    }

    /// <summary>A state, province or territory (ISO 3166-2 id such as "CA-BC").</summary>
    public sealed record Subdivision(string Id, string EnglishName, IReadOnlySet<string> NormalizedNames)
    {
        public string Name => L.T(EnglishName);
        public override string ToString() => Name;
    }

    /// <summary>What the second picker is called: "State" (US, Australia) or "Province" (Canada).</summary>
    public enum AdministrativeAreaKind { State, Province }

    public static AdministrativeAreaKind AdministrativeAreaKindFor(string? countryCode) =>
        countryCode == "CA" ? AdministrativeAreaKind.Province : AdministrativeAreaKind.State;

    public static readonly IReadOnlyList<Subdivision> UsSubdivisions =
    [
        Us("AL", "Alabama"), Us("AK", "Alaska"), Us("AZ", "Arizona"), Us("AR", "Arkansas"), Us("CA", "California"),
        Us("CO", "Colorado"), Us("CT", "Connecticut"), Us("DE", "Delaware"),
        Us("DC", "District of Columbia", "washington dc", "washington d.c."),
        Us("FL", "Florida"), Us("GA", "Georgia"), Us("HI", "Hawaii"), Us("ID", "Idaho"), Us("IL", "Illinois"),
        Us("IN", "Indiana"), Us("IA", "Iowa"), Us("KS", "Kansas"), Us("KY", "Kentucky"), Us("LA", "Louisiana"),
        Us("ME", "Maine"), Us("MD", "Maryland"), Us("MA", "Massachusetts"), Us("MI", "Michigan"), Us("MN", "Minnesota"),
        Us("MS", "Mississippi"), Us("MO", "Missouri"), Us("MT", "Montana"), Us("NE", "Nebraska"), Us("NV", "Nevada"),
        Us("NH", "New Hampshire"), Us("NJ", "New Jersey"), Us("NM", "New Mexico"), Us("NY", "New York"),
        Us("NC", "North Carolina"), Us("ND", "North Dakota"), Us("OH", "Ohio"), Us("OK", "Oklahoma"), Us("OR", "Oregon"),
        Us("PA", "Pennsylvania"), Us("RI", "Rhode Island"), Us("SC", "South Carolina"), Us("SD", "South Dakota"),
        Us("TN", "Tennessee"), Us("TX", "Texas"), Us("UT", "Utah"), Us("VT", "Vermont"), Us("VA", "Virginia"),
        Us("WA", "Washington"), Us("WV", "West Virginia"), Us("WI", "Wisconsin"), Us("WY", "Wyoming"),
    ];

    public static readonly IReadOnlyList<Subdivision> AuSubdivisions =
    [
        Au("ACT", "Australian Capital Territory"), Au("NSW", "New South Wales"), Au("NT", "Northern Territory"),
        Au("QLD", "Queensland"), Au("SA", "South Australia"), Au("TAS", "Tasmania"), Au("VIC", "Victoria"),
        Au("WA", "Western Australia"),
    ];

    public static readonly IReadOnlyList<Subdivision> CaSubdivisions =
    [
        Ca("AB", "Alberta"), Ca("BC", "British Columbia"), Ca("MB", "Manitoba"), Ca("NB", "New Brunswick"),
        Ca("NL", "Newfoundland and Labrador", "newfoundland"), Ca("NS", "Nova Scotia"),
        Ca("NT", "Northwest Territories", "nwt"), Ca("NU", "Nunavut"), Ca("ON", "Ontario"),
        Ca("PE", "Prince Edward Island", "pei"), Ca("QC", "Quebec"), Ca("SK", "Saskatchewan"),
        Ca("YT", "Yukon", "yukon territory"),
    ];

    /// <summary>
    /// ISO country → continent. Mexico and Africa are intentionally absent (no recommendation); South America is CL and
    /// BR; Costa Rica is North America (US band).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RadioRegion> Continents = new Dictionary<string, RadioRegion>
    {
        ["US"] = RadioRegion.NorthAmerica, ["CA"] = RadioRegion.NorthAmerica, ["CR"] = RadioRegion.NorthAmerica,
        ["CL"] = RadioRegion.SouthAmerica, ["BR"] = RadioRegion.SouthAmerica,
        ["GB"] = RadioRegion.Europe, ["IE"] = RadioRegion.Europe, ["DE"] = RadioRegion.Europe, ["FR"] = RadioRegion.Europe,
        ["IT"] = RadioRegion.Europe, ["ES"] = RadioRegion.Europe, ["PT"] = RadioRegion.Europe, ["NL"] = RadioRegion.Europe,
        ["BE"] = RadioRegion.Europe, ["CH"] = RadioRegion.Europe, ["AT"] = RadioRegion.Europe, ["CZ"] = RadioRegion.Europe,
        ["PL"] = RadioRegion.Europe, ["DK"] = RadioRegion.Europe, ["SE"] = RadioRegion.Europe, ["NO"] = RadioRegion.Europe,
        ["FI"] = RadioRegion.Europe, ["GR"] = RadioRegion.Europe, ["HU"] = RadioRegion.Europe, ["SK"] = RadioRegion.Europe,
        ["RO"] = RadioRegion.Europe,
        ["AU"] = RadioRegion.Oceania, ["NZ"] = RadioRegion.Oceania,
        ["VN"] = RadioRegion.Asia, ["TH"] = RadioRegion.Asia, ["MY"] = RadioRegion.Asia, ["SG"] = RadioRegion.Asia,
        ["PH"] = RadioRegion.Asia, ["ID"] = RadioRegion.Asia, ["JP"] = RadioRegion.Asia, ["KR"] = RadioRegion.Asia,
    };

    /// <summary>The countries of the picker, in the iPhone app's order (sort with <see cref="CountriesByName"/>).</summary>
    public static readonly IReadOnlyList<Country> Countries =
    [
        new("US", "United States", UsSubdivisions),
        new("CA", "Canada", CaSubdivisions),
        new("CR", "Costa Rica", null),
        new("CL", "Chile", null),
        new("BR", "Brazil", null),
        new("AU", "Australia", AuSubdivisions),
        new("NZ", "New Zealand", null),
        new("GB", "United Kingdom", null),
        new("IE", "Ireland", null),
        new("DE", "Germany", null),
        new("FR", "France", null),
        new("IT", "Italy", null),
        new("ES", "Spain", null),
        new("PT", "Portugal", null),
        new("NL", "Netherlands", null),
        new("BE", "Belgium", null),
        new("CH", "Switzerland", null),
        new("AT", "Austria", null),
        new("CZ", "Czechia", null),
        new("PL", "Poland", null),
        new("DK", "Denmark", null),
        new("SE", "Sweden", null),
        new("NO", "Norway", null),
        new("FI", "Finland", null),
        new("GR", "Greece", null),
        new("HU", "Hungary", null),
        new("SK", "Slovakia", null),
        new("RO", "Romania", null),
        new("VN", "Vietnam", null),
        new("TH", "Thailand", null),
        new("MY", "Malaysia", null),
        new("SG", "Singapore", null),
        new("PH", "Philippines", null),
        new("ID", "Indonesia", null),
        new("JP", "Japan", null),
        new("KR", "South Korea", null),
    ];

    /// <summary>The Southern California counties of the WCMesh preset (normalised: lowercase, no "county").</summary>
    public static readonly IReadOnlySet<string> SoCalCounties = new HashSet<string>
    {
        "los angeles", "orange", "san diego", "riverside", "san bernardino",
        "ventura", "imperial", "kern", "santa barbara", "san luis obispo",
    };

    /// <summary>Normalised US county names by state (only states with county presets).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> UsCounties = new Dictionary<string, IReadOnlySet<string>>
    {
        ["US-CA"] = SoCalCounties,
    };

    /// <summary>The countries sorted by their name in the app's language.</summary>
    public static IReadOnlyList<Country> CountriesByName() =>
        Countries.OrderBy(c => c.Name, StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase)).ToList();

    public static Country? FindCountry(string? code) => code is null ? null : Countries.FirstOrDefault(c => c.Id == code);

    /// <summary>Whether a state/province picker is shown (the country has more than one).</summary>
    public static bool ShowsSubdivisionPicker(string? countryCode) => SubdivisionsFor(countryCode).Count > 1;

    /// <summary>The states/provinces of a country sorted by name in the app's language (empty when it has no picker).</summary>
    public static IReadOnlyList<Subdivision> SubdivisionsFor(string? countryCode) =>
        FindCountry(countryCode)?.Subdivisions is { } list
            ? list.OrderBy(s => s.Name, StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase)).ToList()
            : [];

    /// <summary>The ISO 3166-2 code for a lowercased state/province name or abbreviation ("pei" → "CA-PE"), or null.</summary>
    public static string? MatchSubdivision(string country, string? normalized)
    {
        if (normalized is null || FindCountry(country)?.Subdivisions is not { } list) return null;
        return list.FirstOrDefault(s => s.NormalizedNames.Contains(normalized))?.Id;
    }

    /// <summary>The county key when (country, state, name) is in the catalogue (US counties have no ISO code).</summary>
    public static string? MatchCounty(string country, string? state, string? normalized)
    {
        if (country != "US" || state is null || normalized is null) return null;
        return UsCounties.TryGetValue(state, out var counties) && counties.Contains(normalized) ? normalized : null;
    }

    /// <summary>The English name of a state or province ("US-CA" → "California"), or null when unknown.</summary>
    public static string? SubdivisionEnglishName(string code) =>
        UsSubdivisions.Concat(AuSubdivisions).Concat(CaSubdivisions).FirstOrDefault(s => s.Id == code)?.EnglishName;

    /// <summary>The name of a state or province in the app's language, or null when unknown.</summary>
    public static string? SubdivisionDisplayName(string code) => SubdivisionEnglishName(code) is { } name ? L.T(name) : null;

    /// <summary>The name of a country in the app's language.</summary>
    public static string CountryDisplayName(string code)
    {
        if (FindCountry(code) is { } c) return c.Name;
        try { return new RegionInfo(code).DisplayName; } catch { return code; }
    }

    /// <summary>
    /// The place to show: the state alone for the US and Canada ("British Columbia"), "State, Country" elsewhere
    /// ("Queensland, Australia"), the country when no state is picked.
    /// </summary>
    public static string DisplayName(RegionSelection region)
    {
        var country = CountryDisplayName(region.CountryCode);
        if (region.AdministrativeAreaCode is not { } admin) return country;
        var state = SubdivisionDisplayName(admin) ?? admin;
        return region.CountryCode is "US" or "CA" ? state : $"{state}, {country}";
    }

    /// <summary>True when the country has a state/province picker and none is picked yet.</summary>
    public static bool IsIncomplete(RegionSelection? region) =>
        region is null || (ShowsSubdivisionPicker(region.CountryCode) && region.AdministrativeAreaCode is null);

    /// <summary>
    /// An ISO country code as the presets know it: upper case, "UK" as "GB", and US territories (Puerto Rico, the US
    /// Virgin Islands, Guam…) as the US — they use the US band. Null for nothing or a non-country ("IV").
    /// </summary>
    public static string? NormalizeCountry(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToUpperInvariant();
        if (c.Length != 2 || c == "IV" || !c.All(char.IsAsciiLetterUpper)) return null;
        return c switch
        {
            "PR" or "VI" or "GU" or "AS" or "MP" or "UM" => "US",
            "UK" => "GB",
            _ => c,
        };
    }

    /// <summary>This PC's country (Windows' region setting), or null.</summary>
    public static string? PcCountry()
    {
        try { return NormalizeCountry(RegionInfo.CurrentRegion.TwoLetterISORegionName); } catch { return null; }
    }

    /// <summary>
    /// The area to recommend presets for: the country and state/province saved in the app, or else this PC's country.
    /// A saved state that doesn't belong to the country is ignored. Null when nothing is known.
    /// </summary>
    public static RegionSelection? Selection(string? savedCountry, string? savedArea)
    {
        var country = NormalizeCountry(savedCountry) ?? PcCountry();
        if (country is null) return null;
        var area = savedArea is { Length: > 0 } a && savedCountry is not null && a.StartsWith(country + "-", StringComparison.OrdinalIgnoreCase)
            ? a.ToUpperInvariant() : null;
        return new RegionSelection(country, area);
    }

    private static Subdivision Us(string code, string name, params string[] extra) => Make("US", code, name, extra);
    private static Subdivision Au(string code, string name, params string[] extra) => Make("AU", code, name, extra);
    private static Subdivision Ca(string code, string name, params string[] extra) => Make("CA", code, name, extra);

    private static Subdivision Make(string country, string code, string name, string[] extra)
    {
        var names = new HashSet<string> { name.ToLowerInvariant(), code.ToLowerInvariant() };
        foreach (var e in extra) names.Add(e.ToLowerInvariant());
        return new Subdivision($"{country}-{code}", name, names);
    }
}

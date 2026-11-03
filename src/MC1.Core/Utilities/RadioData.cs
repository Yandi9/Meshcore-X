using System.Globalization;
using System.Text.RegularExpressions;

namespace MC1.Core.Utilities;

public enum RadioRegion { NorthAmerica, SouthAmerica, Europe, Oceania, Asia }

/// <summary>
/// Where a preset is used (the iPhone app's <c>PresetAvailability</c>): a whole continent, some countries, some states or
/// provinces of one country (ISO 3166-2 codes such as "CA-BC"), or some counties of one US state (normalised names).
/// The recommendation picks the most specific one that matches the user's area.
/// </summary>
public abstract record PresetAvailability
{
    public sealed record Continent(RadioRegion Region) : PresetAvailability;
    public sealed record Countries(IReadOnlySet<string> Codes) : PresetAvailability;
    public sealed record SubRegions(string Country, IReadOnlySet<string> Areas) : PresetAvailability;
    public sealed record Counties(string Country, string State, IReadOnlySet<string> Keys) : PresetAvailability;
}

/// <summary>Where the user is, for picking radio presets (the iPhone app's <c>RegionSelection</c>).</summary>
/// <param name="CountryCode">ISO 3166-1 alpha-2, e.g. "CA".</param>
/// <param name="AdministrativeAreaCode">ISO 3166-2 state or province, e.g. "CA-BC".</param>
/// <param name="CountyKey">Normalised US county name, e.g. "los angeles".</param>
public sealed record RegionSelection(string CountryCode, string? AdministrativeAreaCode = null, string? CountyKey = null);

public sealed record RadioPreset(
    string Id, string Name, RadioRegion Region, double FrequencyMHz, double BandwidthKHz, byte SpreadingFactor, byte CodingRate,
    PresetAvailability Availability, string? RepeatSectionHeader = null, int? PathHashSize = null, int RecommendationPriority = 100)
{
    /// <summary>The countries this preset is meant for (empty for a continent-wide preset).</summary>
    public IReadOnlyList<string> Countries => Availability switch
    {
        PresetAvailability.Countries c => c.Codes.Order().ToList(),
        PresetAvailability.SubRegions s => [s.Country],
        PresetAvailability.Counties c => [c.Country],
        _ => [],
    };

    /// <summary>The states/provinces (ISO 3166-2) of a sub-region or county preset, else empty.</summary>
    public IReadOnlyList<string> Areas => Availability switch
    {
        PresetAvailability.SubRegions s => s.Areas.Order().ToList(),
        PresetAvailability.Counties c => [c.State],
        _ => [],
    };

    public string Summary => string.Create(CultureInfo.InvariantCulture, $"{FrequencyMHz:0.000} MHz · BW {BandwidthKHz:0.#} kHz · SF{SpreadingFactor} · CR{CodingRate}");
    /// <summary>The region's name in the app's language (for display only).</summary>
    public string RegionName => Region switch
    {
        RadioRegion.NorthAmerica => L.T("North America"),
        RadioRegion.SouthAmerica => L.T("South America"),
        RadioRegion.Europe => L.T("Europe"),
        RadioRegion.Oceania => L.T("Oceania"),
        _ => L.T("Asia"),
    };
}

/// <summary>Community radio presets (ported from MC1Services RadioPresets — same ids, names, values and areas).</summary>
public static class RadioPresets
{
    private static PresetAvailability.Countries In(params string[] codes) => new(codes.ToHashSet());
    private static PresetAvailability.SubRegions Areas(string country, params string[] areas) => new(country, areas.ToHashSet());

    public static readonly IReadOnlyList<RadioPreset> All =
    [
        // Oceania
        new("au-915", "Australia", RadioRegion.Oceania, 915.800, 250, 10, 5, In("AU")),
        new("au-narrow", "Australia (Narrow)", RadioRegion.Oceania, 916.575, 62.5, 7, 7, In("AU")),
        new("au-mid", "Australia (Mid)", RadioRegion.Oceania, 915.075, 125, 9, 5, In("AU")),
        new("au-sa-wa", "Australia: SA, WA", RadioRegion.Oceania, 923.125, 62.5, 8, 8, Areas("AU", "AU-SA", "AU-WA")),
        new("au-qld", "Australia: QLD", RadioRegion.Oceania, 923.125, 62.5, 8, 5, Areas("AU", "AU-QLD")),
        new("nz-lr", "New Zealand (Gisborne)", RadioRegion.Oceania, 917.375, 250, 11, 5, In("NZ"), PathHashSize: 1),
        new("nz-narrow", "New Zealand (Narrow)", RadioRegion.Oceania, 917.375, 62.5, 7, 5, In("NZ"), PathHashSize: 2),

        // Europe
        new("eu-narrow", "EU/UK (Narrow)", RadioRegion.Europe, 869.618, 62.5, 8, 8, new PresetAvailability.Continent(RadioRegion.Europe), RecommendationPriority: 110),
        new("eu-lr", "EU/UK (Deprecated)", RadioRegion.Europe, 869.525, 250, 11, 5, new PresetAvailability.Continent(RadioRegion.Europe)),
        new("cz-narrow", "Czech Republic (Narrow)", RadioRegion.Europe, 869.432, 62.5, 7, 5, In("CZ"), PathHashSize: 2),
        new("eu-433-lr", "EU 433MHz (Long Range)", RadioRegion.Europe, 433.650, 250, 11, 5, new PresetAvailability.Continent(RadioRegion.Europe)),
        new("eu-433-narrow", "EU 433MHz (Narrow)", RadioRegion.Europe, 433.650, 62.5, 8, 8, new PresetAvailability.Continent(RadioRegion.Europe)),
        new("pt-433", "Portugal 433", RadioRegion.Europe, 433.375, 62.5, 9, 6, In("PT")),
        new("pt-868", "Portugal 868", RadioRegion.Europe, 869.618, 62.5, 7, 6, In("PT"), RecommendationPriority: 110),
        new("ch", "Switzerland", RadioRegion.Europe, 869.618, 62.5, 8, 8, In("CH")),
        new("hu", "Hungary", RadioRegion.Europe, 869.618, 62.5, 7, 5, In("HU"), PathHashSize: 2),
        new("nl", "Netherlands", RadioRegion.Europe, 869.618, 62.5, 7, 5, In("NL"), RecommendationPriority: 110),
        new("nl-li", "Netherlands (Limburg)", RadioRegion.Europe, 869.618, 62.5, 8, 8, In("NL"), PathHashSize: 2),
        new("sk", "Slovakia", RadioRegion.Europe, 869.618, 62.5, 7, 5, In("SK"), PathHashSize: 2),

        // North America
        new("us-ca", "USA", RadioRegion.NorthAmerica, 910.525, 62.5, 7, 5, In("US"), RecommendationPriority: 110),
        new("ca", "Canada", RadioRegion.NorthAmerica, 910.525, 62.5, 7, 5, In("CA"), PathHashSize: 3, RecommendationPriority: 110),
        new("ca-bc", "Canada: BC", RadioRegion.NorthAmerica, 909.000, 62.5, 7, 5, Areas("CA", "CA-BC")),
        new("cr", "Costa Rica", RadioRegion.NorthAmerica, 910.525, 125, 11, 5, In("CR")),
        new("wcmesh", "WCMesh (SoCal)", RadioRegion.NorthAmerica, 927.875, 62.5, 7, 5,
            new PresetAvailability.Counties("US", "US-CA", RegionalAreas.SoCalCounties), PathHashSize: 3),
        new("phillymesh", "PhillyMesh", RadioRegion.NorthAmerica, 919.500, 500, 10, 5, Areas("US", "US-PA", "US-NJ", "US-DE", "US-MD"), PathHashSize: 2),
        new("lvmesh", "LVMesh", RadioRegion.NorthAmerica, 910.525, 500, 10, 5, Areas("US", "US-PA", "US-NJ"), PathHashSize: 2),

        // South America (Chile: the MeshChile network's community settings, https://meshchile.cl)
        new("cl", "Chile", RadioRegion.SouthAmerica, 927.875, 62.5, 8, 5, In("CL")),
        new("br", "Brazil", RadioRegion.SouthAmerica, 923.125, 62.5, 8, 8, In("BR")),

        // Asia
        new("vn-narrow", "Vietnam (Narrow)", RadioRegion.Asia, 920.250, 62.5, 8, 5, In("VN"), RecommendationPriority: 110),
        new("vn", "Vietnam (Deprecated)", RadioRegion.Asia, 920.250, 250, 11, 5, In("VN")),
    ];

    /// <summary>
    /// Repeat-mode frequencies (they must match the firmware's allowed set exactly). Repeat mode only applies the
    /// frequency; the bandwidth/SF/CR here are inert.
    /// </summary>
    public static readonly IReadOnlyList<RadioPreset> RepeatPresets =
    [
        new("repeat-433", "433 MHz", RadioRegion.Europe, 433.000, 62.5, 9, 8, new PresetAvailability.Continent(RadioRegion.Europe), RepeatSectionHeader: "EU/Asia"),
        new("repeat-869", "869 MHz", RadioRegion.Europe, 869.495, 62.5, 8, 8, new PresetAvailability.Continent(RadioRegion.Europe), RepeatSectionHeader: "EU"),
        new("repeat-918", "918 MHz", RadioRegion.NorthAmerica, 918.000, 62.5, 7, 8, new PresetAvailability.Continent(RadioRegion.NorthAmerica), RepeatSectionHeader: "US/AU/NZ"),
    ];

    /// <summary>The continents to list first for a country (the others follow), like the iPhone app.</summary>
    public static IReadOnlyList<RadioRegion> RegionsForCountry(string? country) => country switch
    {
        "US" or "CA" or "CR" => [RadioRegion.NorthAmerica, RadioRegion.Europe, RadioRegion.Oceania, RadioRegion.Asia],
        "AU" or "NZ" => [RadioRegion.Oceania, RadioRegion.NorthAmerica, RadioRegion.Europe, RadioRegion.Asia],
        "GB" or "DE" or "FR" or "IT" or "ES" or "PT" or "CH" or "CZ" or "IE" or "NL" or "BE" or "AT" or "HU" or "SK" =>
            [RadioRegion.Europe, RadioRegion.NorthAmerica, RadioRegion.Oceania, RadioRegion.Asia],
        "VN" or "TH" or "MY" or "SG" or "PH" or "ID" => [RadioRegion.Asia, RadioRegion.Oceania, RadioRegion.Europe, RadioRegion.NorthAmerica],
        "CL" or "BR" => [RadioRegion.SouthAmerica, RadioRegion.NorthAmerica, RadioRegion.Europe, RadioRegion.Oceania, RadioRegion.Asia],
        _ => Enum.GetValues<RadioRegion>(),
    };

    /// <summary>Every preset, the continents that matter for <paramref name="country"/> first, then by name.</summary>
    public static IReadOnlyList<RadioPreset> PresetsForCountry(string? country)
    {
        var order = RegionsForCountry(RegionalAreas.NormalizeCountry(country)).ToList();
        return All.OrderBy(p => order.IndexOf(p.Region) < 0 ? order.Count : order.IndexOf(p.Region)).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every preset ordered for this PC's country.</summary>
    public static IReadOnlyList<RadioPreset> PresetsForCurrentLocale() => PresetsForCountry(RegionalAreas.PcCountry());

    /// <summary>
    /// Every preset for the picker, ordered for <paramref name="region"/>: the presets of its country (the recommended one
    /// first, then the ones meant for its state or province), then the rest by continent. Nothing is hidden — a PC can't
    /// tell the county, so area presets stay pickable everywhere.
    /// </summary>
    public static IReadOnlyList<RadioPreset> PresetsFor(RegionSelection? region)
    {
        var byContinent = PresetsForCountry(region?.CountryCode ?? RegionalAreas.PcCountry()).ToList();
        if (region is null) return byContinent;
        var recommended = Recommended(region);
        var head = PresetsIn(region)
            .OrderBy(p => p.Id == recommended?.Id ? 0 : IsSelectable(p, region) ? 1 : 2)
            .ThenBy(p => byContinent.IndexOf(p))
            .ToList();
        return [.. head, .. byContinent.Where(p => !head.Contains(p))];
    }

    public static IReadOnlyList<RadioPreset> Matching(double freqMHz, double bwKHz, byte sf, byte cr) =>
        All.Where(p => Math.Abs(p.FrequencyMHz - freqMHz) < 0.1 && Math.Abs(p.BandwidthKHz - bwKHz) < 1.0 && p.SpreadingFactor == sf && p.CodingRate == cr).ToList();

    /// <summary>Stable recommendation order: higher priority first, then by id (as on the iPhone).</summary>
    private static readonly IReadOnlyList<RadioPreset> RecommendationOrder =
        All.OrderByDescending(p => p.RecommendationPriority).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The most specific community preset for <paramref name="region"/>: county → state/province → country → continent.
    /// Null for places no preset covers (e.g. Bermuda).
    /// </summary>
    public static RadioPreset? Recommended(RegionSelection region)
    {
        var country = region.CountryCode;
        if (region.AdministrativeAreaCode is { } admin && region.CountyKey is { } county
            && RecommendationOrder.FirstOrDefault(p => p.Availability is PresetAvailability.Counties c && c.Country == country && c.State == admin && c.Keys.Contains(county)) is { } byCounty)
            return byCounty;
        if (region.AdministrativeAreaCode is { } area
            && RecommendationOrder.FirstOrDefault(p => p.Availability is PresetAvailability.SubRegions s && s.Country == country && s.Areas.Contains(area)) is { } byArea)
            return byArea;
        if (RecommendationOrder.FirstOrDefault(p => p.Availability is PresetAvailability.Countries c && c.Codes.Contains(country)) is { } byCountry)
            return byCountry;
        if (RegionalAreas.Continents.TryGetValue(country, out var continent)
            && RecommendationOrder.FirstOrDefault(p => p.Availability is PresetAvailability.Continent c && c.Region == continent) is { } byContinent)
            return byContinent;
        return null;
    }

    /// <summary>
    /// The presets of the region's country (its area and county presets included), or of its continent when the
    /// country has none of its own (the iPhone's <c>presets(for:)</c>).
    /// </summary>
    public static IReadOnlyList<RadioPreset> PresetsIn(RegionSelection region)
    {
        var country = region.CountryCode;
        var own = All.Where(p => p.Availability switch
        {
            PresetAvailability.Counties c => c.Country == country,
            PresetAvailability.SubRegions s => s.Country == country,
            PresetAvailability.Countries c => c.Codes.Contains(country),
            _ => false,
        }).ToList();
        if (own.Count > 0) return own;
        if (!RegionalAreas.Continents.TryGetValue(country, out var continent)) return [];
        return All.Where(p => p.Availability is PresetAvailability.Continent c && c.Region == continent).ToList();
    }

    /// <summary>
    /// Whether the preset is meant for <paramref name="region"/> on the iPhone: county presets only for their counties,
    /// area presets only for their states/provinces; country and continent presets always.
    /// </summary>
    public static bool IsSelectable(RadioPreset preset, RegionSelection? region) => preset.Availability switch
    {
        PresetAvailability.Counties c => region is { CountyKey: { } key } && region.CountryCode == c.Country && region.AdministrativeAreaCode == c.State && c.Keys.Contains(key),
        PresetAvailability.SubRegions s => region is { AdministrativeAreaCode: { } admin } && region.CountryCode == s.Country && s.Areas.Contains(admin),
        _ => true,
    };

    /// <summary>
    /// The id of the preset recommended for a country (ISO code) and, optionally, a state or province ("CA-BC").
    /// US territories (Puerto Rico, Guam…) count as the US.
    /// </summary>
    public static string? RecommendedFor(string? country, string? area = null)
    {
        var code = RegionalAreas.NormalizeCountry(country);
        if (code is null) return null;
        var admin = area is { Length: > 0 } && area.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase) ? area.ToUpperInvariant() : null;
        return Recommended(new RegionSelection(code, admin))?.Id;
    }

    /// <summary>
    /// The preset the radio is on, like the iPhone app: the one last applied if it still matches, then the one
    /// recommended for the area (<paramref name="country"/>/<paramref name="area"/>, or this PC's country), then the first
    /// match (presets can share settings, e.g. USA and Canada). Null when the radio uses custom settings.
    /// </summary>
    public static RadioPreset? Resolve(double freqMHz, double bwKHz, byte sf, byte cr, string? preferredId, string? country = null, string? area = null)
    {
        var matches = Matching(freqMHz, bwKHz, sf, cr);
        if (matches.Count == 0) return null;
        if (preferredId is not null && matches.FirstOrDefault(p => p.Id == preferredId) is { } preferred) return preferred;
        if (RecommendedFor(country ?? RegionalAreas.PcCountry(), country is null ? null : area) is { } rec
            && matches.FirstOrDefault(p => p.Id == rec) is { } recommended) return recommended;
        return matches[0];
    }

    /// <summary>The repeat-mode preset for a frequency (repeat mode only works on these exact frequencies).</summary>
    public static RadioPreset? MatchingRepeat(double freqMHz) => RepeatPresets.FirstOrDefault(p => Math.Abs(p.FrequencyMHz - freqMHz) < 0.0005);

    /// <summary>"USA · 910.525 MHz · BW 62.5 kHz · SF7 · CR5" (or "Custom · …", in the app's language).</summary>
    public static string Describe(RadioPreset? preset, double freqMHz, double bwKHz, byte sf, byte cr) =>
        string.Create(CultureInfo.InvariantCulture, $"{preset?.Name ?? L.T("Custom")} · {freqMHz:0.000} MHz · BW {bwKHz:0.##} kHz · SF{sf} · CR{cr}");
}

/// <summary>Open-circuit-voltage battery curves (11 points, 100%→0%).</summary>
public static class OcvPresets
{
    public sealed record Preset(string Id, string Name, int[] Curve, bool IsDevice);

    public static readonly IReadOnlyList<Preset> All =
    [
        new("liIon", "Li-Ion (Default)", [4190, 4050, 3990, 3890, 3800, 3720, 3630, 3530, 3420, 3300, 3100], false),
        new("liFePO4", "LiFePO4", [3400, 3350, 3320, 3290, 3270, 3260, 3250, 3230, 3200, 3120, 3000], false),
        new("leadAcid", "Lead Acid", [2120, 2090, 2070, 2050, 2030, 2010, 1990, 1980, 1970, 1960, 1950], false),
        new("alkaline", "Alkaline", [1580, 1400, 1350, 1300, 1280, 1250, 1230, 1190, 1150, 1100, 1000], false),
        new("niMH", "NiMH", [1400, 1300, 1280, 1270, 1260, 1250, 1240, 1230, 1210, 1150, 1000], false),
        new("lto", "LTO", [2770, 2650, 2540, 2420, 2300, 2180, 2060, 1940, 1800, 1680, 1550], false),
        new("trackerT1000E", "Tracker T1000-E", [4190, 4042, 3957, 3885, 3820, 3776, 3746, 3725, 3696, 3644, 3100], true),
        new("heltecPocket5000", "Heltec Pocket 5000", [4300, 4240, 4120, 4000, 3888, 3800, 3740, 3698, 3655, 3580, 3400], true),
        new("heltecPocket10000", "Heltec Pocket 10000", [4100, 4060, 3960, 3840, 3729, 3625, 3550, 3500, 3420, 3345, 3100], true),
        new("seeedWioTracker", "Seeed WIO Tracker", [4200, 3876, 3826, 3763, 3713, 3660, 3573, 3485, 3422, 3359, 3300], true),
        new("seeedSolarNode", "Seeed Solar Node", [4200, 3986, 3922, 3812, 3734, 3645, 3527, 3420, 3281, 3087, 2786], true),
        new("r1Neo", "R1 Neo", [4120, 4020, 4000, 3940, 3870, 3820, 3750, 3630, 3550, 3450, 3100], true),
        new("wisMeshTag", "WisMesh Tag", [4160, 4020, 3940, 3870, 3810, 3760, 3740, 3720, 3680, 3620, 2990], true),
        new("lilyGoTBeam1W", "LilyGo T-Beam 1W", [7950, 7850, 7750, 7580, 7440, 7310, 7150, 7005, 6860, 6685, 6000], true),
        new("thinkNodeM6", "ThinkNode M6", [4080, 3990, 3935, 3880, 3825, 3770, 3715, 3660, 3605, 3550, 3450], true),
    ];

    public static Preset Default => All[0];

    public static Preset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    public static string? ForManufacturer(string model) => model switch
    {
        "Seeed Tracker T1000-e" or "Seeed Tracker T1000-E" => "trackerT1000E",
        "Seeed Wio Tracker L1" => "seeedWioTracker",
        "Seeed SenseCap Solar" => "seeedSolarNode",
        "RAK WisMesh Tag" => "wisMeshTag",
        "LilyGo T-Beam 1W" => "lilyGoTBeam1W",
        "Elecrow ThinkNode M6" => "thinkNodeM6",
        _ => null,
    };

    public static int LinearPercent(int millivolts) => (int)Math.Clamp((millivolts / 1000.0 - 3.0) / 1.2 * 100, 0, 100);

    /// <summary>Interpolates the battery percentage on an 11-point OCV curve.</summary>
    public static int Percent(int millivolts, int[]? curve)
    {
        if (curve is not { Length: 11 }) return LinearPercent(millivolts);
        if (millivolts >= curve[0]) return 100;
        if (millivolts <= curve[10]) return 0;
        for (var i = 0; i < 10; i++)
        {
            int upper = curve[i], lower = curve[i + 1];
            if (millivolts >= lower)
            {
                var seg = (double)(millivolts - lower) / (upper - lower);
                return (10 - i - 1) * 10 + (int)Math.Round(seg * 10, MidpointRounding.AwayFromZero);
            }
        }
        return 0;
    }

    public static int[]? ParseCustom(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var parts = csv.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        var list = new List<int>();
        foreach (var p in parts) if (int.TryParse(p, out var v) && v is >= 1000 and <= 99999) list.Add(v);
        return list.Count == 11 ? list.ToArray() : null;
    }
}

public enum ClearanceStatus { Clear, Marginal, PartialObstruction, Blocked }

public sealed record ElevationSample(double Latitude, double Longitude, double Elevation, double DistanceFromAMeters);
public sealed record ObstructionPoint(double DistanceFromAMeters, double ObstructionHeightMeters, double FresnelClearancePercent);

public sealed record PathAnalysisResult(
    double DistanceMeters, double FreeSpacePathLoss, double PeakDiffractionLoss, double TotalPathLoss,
    ClearanceStatus ClearanceStatus, double WorstClearancePercent, IReadOnlyList<ObstructionPoint> ObstructionPoints,
    double FrequencyMHz, double RefractionK)
{
    /// <summary>
    /// False when there was no usable data (too few terrain samples, an unusable frequency): the calculator then returns
    /// a zero distance, so missing data isn't mistaken for a blocked path.
    /// </summary>
    public bool IsValid => DistanceMeters > 0;

    public ObstructionPoint? WorstObstructionPoint => ObstructionPoints.Count == 0 ? null : ObstructionPoints.MinBy(o => o.FresnelClearancePercent);
}

/// <summary>RF line-of-sight maths (Fresnel zone, earth bulge, FSPL, knife-edge diffraction), as on the iPhone.</summary>
public static class RfCalculator
{
    public const double SpeedOfLight = 299_792_458;
    public const double EarthRadiusKm = 6371;
    public const double ClearThreshold = 80;
    public const double MarginalThreshold = 60;
    private const int MinimumProfileSampleCount = 3;
    /// <summary>64 ULPs of 1.0 (Swift's <c>64 * .ulpOfOne</c>): below this the two points are the same or antipodal.</summary>
    private const double AngularDegeneracyTolerance = 64 * 2.220446049250313e-16;

    /// <summary>The wavelength in metres, or 0 for an unusable frequency (zero, negative, not finite, or out of range).</summary>
    public static double Wavelength(double fMHz)
    {
        if (!double.IsFinite(fMHz) || fMHz <= 0) return 0;
        var hz = fMHz * 1_000_000;
        if (!double.IsFinite(hz) || hz <= 0) return 0;
        var result = SpeedOfLight / hz;
        return double.IsFinite(result) && result > 0 ? result : 0;
    }

    /// <summary>Whether <paramref name="fMHz"/> can be used for the analysis (it gives a finite, positive wavelength).</summary>
    public static bool IsUsableFrequency(double fMHz) => Wavelength(fMHz) > 0;

    public static double FresnelRadius(double fMHz, double dA, double dB)
    {
        var lambda = Wavelength(fMHz);
        if (lambda <= 0 || dA <= 0 || dB <= 0) return 0;
        return Math.Sqrt(lambda * dA * dB / (dA + dB));
    }

    public static double EarthBulge(double dA, double dB, double k) =>
        dA <= 0 || dB <= 0 || k <= 0 ? 0 : dA * dB / (2 * k * EarthRadiusKm * 1000);

    public static double PathLoss(double dMeters, double fMHz) =>
        dMeters <= 0 || Wavelength(fMHz) <= 0 ? 0 : 20 * Math.Log10(dMeters) + 20 * Math.Log10(fMHz) - 27.55;

    public static double DiffractionLoss(double h, double dA, double dB, double fMHz)
    {
        var lambda = Wavelength(fMHz);
        if (lambda <= 0 || dA <= 0 || dB <= 0) return 0;
        var v = h * Math.Sqrt(2 * (dA + dB) / (lambda * dA * dB));
        if (v <= -0.78) return 0;
        var s = v - 0.1;
        return 6.9 + 20 * Math.Log10(Math.Sqrt(s * s + 1) + s);
    }

    /// <summary>True for a finite latitude in [-90, 90] and longitude in [-180, 180].</summary>
    public static bool IsValidCoordinate(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

    /// <summary>The great-circle surface distance in metres (haversine), or NaN for invalid coordinates.</summary>
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        if (!IsValidCoordinate(lat1, lon1) || !IsValidCoordinate(lat2, lon2)) return double.NaN;
        var r = EarthRadiusKm * 1000;
        var p1 = lat1 * Math.PI / 180;
        var p2 = lat2 * Math.PI / 180;
        var dp = (lat2 - lat1) * Math.PI / 180;
        var dl = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        a = Math.Clamp(a, 0, 1);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// The point at <paramref name="fraction"/> (0…1) along the shortest great-circle arc from A to B. Null for invalid
    /// input and for antipodal points (no unique arc).
    /// </summary>
    public static (double Lat, double Lon)? InterpolateCoordinate(double lat1, double lon1, double lat2, double lon2, double fraction)
    {
        if (!IsValidCoordinate(lat1, lon1) || !IsValidCoordinate(lat2, lon2) || !double.IsFinite(fraction) || fraction is < 0 or > 1) return null;
        var (sx, sy, sz) = UnitVector(lat1, lon1);
        var (ex, ey, ez) = UnitVector(lat2, lon2);
        var dot = Math.Clamp(sx * ex + sy * ey + sz * ez, -1, 1);
        var cx = sy * ez - sz * ey;
        var cy = sz * ex - sx * ez;
        var cz = sx * ey - sy * ex;
        var crossLength = Hypot(Hypot(cx, cy), cz);
        if (!(crossLength > AngularDegeneracyTolerance || dot >= 0)) return null;
        if (fraction == 0) return (lat1, lon1);
        if (fraction == 1) return (lat2, lon2);
        if (crossLength <= AngularDegeneracyTolerance) return (lat1, lon1);
        var angle = Math.Atan2(crossLength, dot);
        var startWeight = Math.Sin((1 - fraction) * angle) / crossLength;
        var endWeight = Math.Sin(fraction * angle) / crossLength;
        var x = startWeight * sx + endWeight * ex;
        var y = startWeight * sy + endWeight * ey;
        var z = startWeight * sz + endWeight * ez;
        return (Math.Atan2(z, Hypot(x, y)) * 180 / Math.PI, Math.Atan2(y, x) * 180 / Math.PI);
    }

    private static (double X, double Y, double Z) UnitVector(double lat, double lon)
    {
        var p = lat * Math.PI / 180;
        var l = lon * Math.PI / 180;
        return (Math.Cos(p) * Math.Cos(l), Math.Cos(p) * Math.Sin(l), Math.Sin(p));
    }

    /// <summary>sqrt(a² + b²) without overflow or underflow (like C's hypot).</summary>
    private static double Hypot(double a, double b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        if (a < b) (a, b) = (b, a);
        if (a == 0) return 0;
        var r = b / a;
        return a * Math.Sqrt(1 + r * r);
    }

    /// <summary>
    /// Evenly spaced points along the great-circle route from A to B (endpoints included). Empty when there is no route:
    /// invalid coordinates or antipodal points.
    /// </summary>
    public static IReadOnlyList<(double Lat, double Lon)> SamplePath(double lat1, double lon1, double lat2, double lon2, int count)
    {
        count = Math.Max(2, count);
        var points = new List<(double Lat, double Lon)>(count);
        for (var i = 0; i < count; i++)
        {
            if (InterpolateCoordinate(lat1, lon1, lat2, lon2, (double)i / (count - 1)) is not { } p) return [];
            points.Add(p);
        }
        return points;
    }

    public static int SampleCountForDistance(double meters) => meters switch
    {
        < 1000 => 20,
        < 5000 => 50,
        < 20000 => 80,
        _ => 100,
    };

    /// <summary>
    /// Analyses a whole path A → B. Distances are measured from A itself (origin 0). Needs at least three samples (the
    /// endpoints alone say nothing about the terrain between them) and a usable frequency; otherwise the result is not
    /// <see cref="PathAnalysisResult.IsValid"/>.
    /// </summary>
    public static PathAnalysisResult Analyze(IReadOnlyList<ElevationSample> profile, double heightA, double heightB, double fMHz, double k) =>
        AnalyzeCore(profile, heightA, heightB, fMHz, k, 0);

    /// <summary>Analyses one segment of a longer profile; its distances start at the segment's first sample.</summary>
    public static PathAnalysisResult AnalyzeSegment(IReadOnlyList<ElevationSample> profile, double startHeight, double endHeight, double fMHz, double k) =>
        AnalyzeCore(profile, startHeight, endHeight, fMHz, k, profile.Count > 0 ? profile[0].DistanceFromAMeters : 0);

    private static PathAnalysisResult AnalyzeCore(IReadOnlyList<ElevationSample> profile, double heightA, double heightB, double fMHz, double k, double origin)
    {
        if (profile.Count < MinimumProfileSampleCount || Wavelength(fMHz) <= 0) return Empty(fMHz, k);
        var first = profile[0];
        var last = profile[^1];
        var length = last.DistanceFromAMeters - origin;
        if (!(length > 0)) return Empty(fMHz, k);
        var startH = first.Elevation + heightA;
        var endH = last.Elevation + heightB;
        var fspl = PathLoss(length, fMHz);
        var worst = double.PositiveInfinity;
        var peakDiff = 0.0;
        var obstructions = new List<ObstructionPoint>();
        // Every sample between the endpoints counts, however close to an end it is.
        for (var i = 1; i < profile.Count - 1; i++)
        {
            var s = profile[i];
            var dA = s.DistanceFromAMeters - origin;
            var dB = length - dA;
            var los = startH + dA / length * (endH - startH);
            var terrain = s.Elevation + EarthBulge(dA, dB, k);
            var fr = FresnelRadius(fMHz, dA, dB);
            if (!double.IsFinite(fr) || fr <= 0) return Empty(fMHz, k);
            var clearance = los - terrain;
            var pct = clearance / fr * 100;
            if (!double.IsFinite(pct)) return Empty(fMHz, k);
            worst = Math.Min(worst, pct);
            var obstructionH = terrain - los;
            if (obstructionH > -fr) peakDiff = Math.Max(peakDiff, DiffractionLoss(obstructionH, dA, dB, fMHz));
            if (pct < MarginalThreshold) obstructions.Add(new ObstructionPoint(s.DistanceFromAMeters, obstructionH, pct));
        }
        return new PathAnalysisResult(length, fspl, peakDiff, fspl + peakDiff, StatusFor(worst), worst, obstructions, fMHz, k);
    }

    /// <summary>The clearance status for the worst first-Fresnel-zone clearance (%).</summary>
    public static ClearanceStatus StatusFor(double worstClearancePercent) =>
        worstClearancePercent >= ClearThreshold ? ClearanceStatus.Clear
        : worstClearancePercent >= MarginalThreshold ? ClearanceStatus.Marginal
        : worstClearancePercent >= 0 ? ClearanceStatus.PartialObstruction
        : ClearanceStatus.Blocked;

    private static PathAnalysisResult Empty(double f, double k) => new(0, 0, 0, 0, ClearanceStatus.Blocked, 0, [], f, k);

    /// <summary>
    /// A signed clearance percentage with up to three significant digits, in the given (default: current) culture —
    /// "-50%", "167%", "-0.00222%". Never clamped: below 0% the terrain blocks the line of sight.
    /// </summary>
    public static string FormatClearancePercent(double percent, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (!double.IsFinite(percent)) return "–";
        var rounded = RoundToSignificant(percent, 3);
        var number = rounded.ToString("#,##0.##########", culture);
        if (number == "-0") number = "0";
        // The invariant culture would put a space before the sign; write it like en-US ("12%") instead.
        var pattern = culture.Name.Length == 0 ? 1 : culture.NumberFormat.PercentPositivePattern;
        var sign = culture.NumberFormat.PercentSymbol;
        // Patterns 0 ("n %") and 1 ("n%") are the common ones; keep the culture's spacing.
        return pattern == 0 ? $"{number} {sign}" : $"{number}{sign}";
    }

    private static double RoundToSignificant(double value, int digits)
    {
        if (value == 0) return 0;
        var magnitude = (int)Math.Floor(Math.Log10(Math.Abs(value))) + 1;
        var decimals = digits - magnitude;
        if (decimals >= 0) return Math.Round(value, Math.Min(15, decimals), MidpointRounding.ToEven);
        var scale = Math.Pow(10, -decimals);
        return Math.Round(value / scale, MidpointRounding.ToEven) * scale;
    }

    /// <summary>Approximate LoRa sensitivity (dBm) for SF/BW, used for link-budget hints.</summary>
    public static double LoraSensitivity(int sf, double bwKHz)
    {
        var snrLimit = sf switch { 5 => -2.5, 6 => -5, 7 => -7.5, 8 => -10, 9 => -12.5, 10 => -15, 11 => -17.5, _ => -20 };
        return -174 + 10 * Math.Log10(bwKHz * 1000) + 6 + snrLimit;
    }
}

public abstract record CliResponse
{
    public sealed record Ok : CliResponse;
    public sealed record Error(string Message) : CliResponse;
    public sealed record UnknownCommand(string Message) : CliResponse;
    public sealed record Version(string Value) : CliResponse;
    public sealed record DeviceTime(string Value) : CliResponse;
    public sealed record Name(string Value) : CliResponse;
    public sealed record Radio(double Frequency, double Bandwidth, int SpreadingFactor, int CodingRate) : CliResponse;
    public sealed record TxPower(int Value) : CliResponse;
    public sealed record RepeatMode(bool On) : CliResponse;
    public sealed record AdvertInterval(int Minutes) : CliResponse;
    public sealed record FloodAdvertInterval(int Hours) : CliResponse;
    public sealed record FloodMax(int Hops) : CliResponse;
    public sealed record Latitude(double Value) : CliResponse;
    public sealed record Longitude(double Value) : CliResponse;
    public sealed record OwnerInfo(string Value) : CliResponse;
    public sealed record Raw(string Value) : CliResponse;

    private static readonly HashSet<string> Structured =
        ["get radio", "get tx", "get repeat", "get advert.interval", "get flood.advert.interval", "get flood.max", "get lat", "get lon", "clock"];

    public static CliResponse Parse(string text, string? query = null)
    {
        var t = text.Trim();
        if (t.StartsWith("> ")) t = t[2..];
        else if (t == ">") t = "";
        if (t == "OK" || t.StartsWith("OK - ")) return new Ok();
        if (t.StartsWith("error", StringComparison.OrdinalIgnoreCase) || t.StartsWith("ERR:"))
            return t.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ? new UnknownCommand(t) : new Error(t);
        if (t.StartsWith("MeshCore v") || (t.StartsWith('v') && t.Contains('('))) return new Version(t);
        var inv = CultureInfo.InvariantCulture;
        switch (query)
        {
            case "ver": return new Version(t);
            case "get name": return new Name(t);
            case "get owner.info": return new OwnerInfo(t);
            case "clock" when t.Contains("UTC") || (t.Contains(':') && t.Contains('/')): return new DeviceTime(t);
            case "get radio":
                var parts = t.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length >= 4 && double.TryParse(parts[0], NumberStyles.Float, inv, out var f) && double.TryParse(parts[1], NumberStyles.Float, inv, out var bw)
                    && int.TryParse(parts[2], out var sf) && int.TryParse(parts[3], out var cr))
                    return new Radio(f, bw, sf, cr);
                break;
            case "get tx":
                var m = Regex.Match(t, @"max=(-?\d+)");
                if (m.Success) return new TxPower(int.Parse(m.Groups[1].Value, inv));
                m = Regex.Match(t, @"^(-?\d+)(?:dBm|\s|$)");
                if (m.Success) return new TxPower(int.Parse(m.Groups[1].Value, inv));
                break;
            case "get repeat":
                if (t.Equals("on", StringComparison.OrdinalIgnoreCase)) return new RepeatMode(true);
                if (t.Equals("off", StringComparison.OrdinalIgnoreCase)) return new RepeatMode(false);
                break;
            case "get advert.interval" when int.TryParse(t, out var ai): return new AdvertInterval(ai);
            case "get flood.advert.interval" when int.TryParse(t, out var fi): return new FloodAdvertInterval(fi);
            case "get flood.max" when int.TryParse(t, out var fm): return new FloodMax(fm);
            case "get lat" when double.TryParse(t, NumberStyles.Float, inv, out var lat): return new Latitude(lat);
            case "get lon" when double.TryParse(t, NumberStyles.Float, inv, out var lon): return new Longitude(lon);
        }
        return new Raw(t);
    }

    public static bool IsStructuredQuery(string q) => Structured.Contains(q);

    /// <summary>Splits the "XX|" echo prefix used to correlate CLI replies.</summary>
    public static (string Prefix, string Body)? SplitEchoedPrefix(string text)
    {
        if (text.Length > 3 && Regex.IsMatch(text[..3], "^[0-9A-F]{2}\\|$")) return (text[..3], text[3..]);
        return null;
    }

    /// <summary>"clock sync" is rewritten to "time &lt;epoch&gt;" like the iOS app does.</summary>
    public static string RewriteCommand(string command, DateTimeOffset? now = null)
    {
        var normalized = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return normalized == "clock sync" ? "time " + MeshCore.Bytes.EpochSeconds32(now ?? DateTimeOffset.UtcNow) : command;
    }

    public static DateTimeOffset? ParseClock(string text)
    {
        var m = Regex.Match(text, @"(\d{1,2}:\d{2}) - (\d{1,2}/\d{1,2}/\d{4}) UTC");
        if (!m.Success) return null;
        return DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}", "H:mm d/M/yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? new DateTimeOffset(d, TimeSpan.Zero) : null;
    }
}

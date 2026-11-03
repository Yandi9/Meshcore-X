using System.Globalization;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;
using Xunit;

namespace MC1.Core.Tests;

/// <summary>
/// WS5 — tools, charts and radio presets ported from MeshCore One v1.5.1 → v1.6.2: line-of-sight maths (the Swift
/// RFScientificRegressionTests / ElevationServiceGeometryTests / LOSFormattersTests numbers), status chart filtering,
/// trace cancellation, and the preset table with Canadian provinces.
/// </summary>
public class ToolsPortTests
{
    // ───────────────────────── Line of sight (RFScientificRegressionTests) ─────────────────────────

    private static ElevationSample Sample(double distance, double elevation = 0) =>
        new(0, distance / 6_371_000 * 180 / Math.PI, elevation, distance);

    private static List<ElevationSample> Profile(double distance, int? ridgeIndex = null, double ridgeHeight = 0) =>
        Enumerable.Range(0, 11).Select(i => Sample(i * distance / 10, i == ridgeIndex ? ridgeHeight : 0)).ToList();

    private static PathAnalysisResult Analyze(IReadOnlyList<ElevationSample> samples, double startHeight = 7, double endHeight = 7, double frequency = 906, double k = 1) =>
        RfCalculator.Analyze(samples, startHeight, endHeight, frequency, k);

    [Fact]
    public void FlatTenKilometerReference()
    {
        var r = Analyze(Profile(10000), 50, 50);
        Assert.True(r.IsValid);
        Assert.Equal(ClearanceStatus.Clear, r.ClearanceStatus);
        Assert.True(Math.Abs(r.WorstClearancePercent - 167.02001632263816) < 1e-9);
        Assert.True(Math.Abs(r.FreeSpacePathLoss - 111.59034717541964) < 0.0023);
        Assert.Equal(0, r.PeakDiffractionLoss);
    }

    [Fact]
    public void SingleRidgeReference()
    {
        var r = Analyze(Profile(10000, 5, 100));
        Assert.True(r.IsValid);
        Assert.Equal(ClearanceStatus.Blocked, r.ClearanceStatus);
        Assert.True(Math.Abs(r.WorstClearancePercent + 330.16700191225306) < 1e-9);
        Assert.True(Math.Abs(r.PeakDiffractionLoss - 26.219716530826638) < 1e-10);
    }

    [Fact]
    public void UnequalEndpointHeightsReference()
    {
        var r = Analyze(Profile(10000), 5, 50);
        Assert.True(Math.Abs(r.WorstClearancePercent - 50.95688560947363) < 1e-9);
        Assert.True(Math.Abs(r.PeakDiffractionLoss - 0.3968418683880186) < 1e-10);
    }

    [Fact]
    public void ShortAndLongFlatPathReferences()
    {
        var shortPath = Analyze(Profile(1000));
        Assert.Equal(ClearanceStatus.Marginal, shortPath.ClearanceStatus);
        Assert.True(Math.Abs(shortPath.WorstClearancePercent - 76.74724578644971) < 1e-9);
        var longPath = Analyze(Profile(100_000), 10, 10);
        Assert.Equal(ClearanceStatus.Blocked, longPath.ClearanceStatus);
        Assert.True(Math.Abs(longPath.WorstClearancePercent + 204.72317459077038) < 1e-9);
    }

    [Theory]
    [InlineData(19.219103128499036, ClearanceStatus.PartialObstruction, ClearanceStatus.Marginal)]
    [InlineData(24.971465710598515, ClearanceStatus.Marginal, ClearanceStatus.Clear)]
    public void ThresholdSides(double height, ClearanceStatus below, ClearanceStatus above)
    {
        var samples = Profile(10000);
        Assert.Equal(below, Analyze(samples, height - 1e-6, height - 1e-6).ClearanceStatus);
        Assert.Equal(above, Analyze(samples, height + 1e-6, height + 1e-6).ClearanceStatus);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    [InlineData(100_000.0)]
    public void ReversalHeightAndDatumInvariants(double distance)
    {
        var samples = Profile(distance, 3, 100);
        var result = Analyze(samples, 5, 50);
        var reversed = samples.AsEnumerable().Reverse().Select(s => Sample(distance - s.DistanceFromAMeters, s.Elevation)).ToList();
        var reverseResult = Analyze(reversed, 50, 5);
        Assert.True(Math.Abs(result.WorstClearancePercent - reverseResult.WorstClearancePercent) < 1e-7);
        Assert.True(Math.Abs(result.TotalPathLoss - reverseResult.TotalPathLoss) < 1e-9);
        var raised = Analyze(samples, 15, 60);
        Assert.True(raised.WorstClearancePercent >= result.WorstClearancePercent);
        Assert.True(raised.PeakDiffractionLoss <= result.PeakDiffractionLoss);
        var shifted = samples.Select(s => Sample(s.DistanceFromAMeters, s.Elevation - 430)).ToList();
        Assert.True(Math.Abs(Analyze(shifted, 5, 50).WorstClearancePercent - result.WorstClearancePercent) < 1e-7);
    }

    [Fact]
    public void InteriorRidgeWithinOneMeterIsEvaluated()
    {
        var r = Analyze([Sample(0), Sample(0.5, 100), Sample(5), Sample(10)]);
        Assert.True(r.IsValid);
        Assert.Equal(ClearanceStatus.Blocked, r.ClearanceStatus);
        Assert.Equal(0.5, r.WorstObstructionPoint?.DistanceFromAMeters);
    }

    [Fact]
    public void EndpointsAloneDoNotAssertClearance()
    {
        var r = Analyze([Sample(0), Sample(100_000)], 10, 10);
        Assert.False(r.IsValid);
        Assert.Equal(0, r.DistanceMeters);
        Assert.NotEqual(ClearanceStatus.Clear, r.ClearanceStatus);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)] // Swift's leastNonzeroMagnitude
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-906.0)]
    public void InvalidFrequenciesAreRejected(double frequency)
    {
        Assert.Equal(0, RfCalculator.Wavelength(frequency));
        Assert.False(RfCalculator.IsUsableFrequency(frequency));
        Assert.Equal(0, RfCalculator.FresnelRadius(frequency, 500, 500));
        Assert.Equal(0, RfCalculator.PathLoss(1000, frequency));
        Assert.False(Analyze(Profile(1000), frequency: frequency).IsValid);
    }

    [Fact]
    public void AntipodalDistanceRemainsFinite()
    {
        Assert.True(Math.Abs(RfCalculator.Distance(-13.89791609602186, -174.63650050114734, 13.89791609602186, 5.363499498852661) - 20_015_086.79602057) < 0.2);
        Assert.Null(RfCalculator.InterpolateCoordinate(-13.89791609602186, -174.63650050114734, 13.89791609602186, 5.363499498852661, 0.5));
        Assert.True(double.IsNaN(RfCalculator.Distance(91, 0, 0, 0)));
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    public void SphericalInterpolationIsReversibleAndEquidistant(double fraction)
    {
        var forward = RfCalculator.InterpolateCoordinate(70, 179.9, 71, -170, fraction)!.Value;
        var reverse = RfCalculator.InterpolateCoordinate(71, -170, 70, 179.9, 1 - fraction)!.Value;
        Assert.True(RfCalculator.Distance(forward.Lat, forward.Lon, reverse.Lat, reverse.Lon) < 1e-6);
        var total = RfCalculator.Distance(70, 179.9, 71, -170);
        Assert.True(Math.Abs(RfCalculator.Distance(70, 179.9, forward.Lat, forward.Lon) - fraction * total) < 1e-6);
    }

    [Fact]
    public void NearAntipodalInterpolationRemainsDefined()
    {
        var mid = RfCalculator.InterpolateCoordinate(0, 0, 0, 179.999999, 0.5)!.Value;
        Assert.True(Math.Abs(mid.Lat) < 1e-9);
        Assert.True(Math.Abs(mid.Lon - 89.9999995) < 1e-6);
    }

    // ───────────────────────── Elevation route (ElevationServiceGeometryTests) ─────────────────────────

    [Fact]
    public void DateLineProfileFollowsTheShortArcAndDetectsCurvatureObstruction()
    {
        var coordinates = RfCalculator.SamplePath(0, 179.9, 0, -179.9, 100);
        Assert.Equal(100, coordinates.Count);
        var profile = coordinates.Select(c => new ElevationSample(c.Lat, c.Lon, 0, RfCalculator.Distance(0, 179.9, c.Lat, c.Lon))).ToList();
        var length = profile[^1].DistanceFromAMeters;
        Assert.True(Math.Abs(length - 22238.985328912) < 0.000001);
        for (var i = 1; i < profile.Count; i++)
        {
            Assert.True(profile[i - 1].DistanceFromAMeters < profile[i].DistanceFromAMeters);
            Assert.True(Math.Abs(profile[i].DistanceFromAMeters - profile[i - 1].DistanceFromAMeters - length / 99) < 0.000001);
        }
        var r = RfCalculator.Analyze(profile, 7, 7, 906, 1);
        Assert.Equal(ClearanceStatus.Blocked, r.ClearanceStatus);
        Assert.True(r.WorstClearancePercent < 0);
    }

    [Fact]
    public void PolarRouteReachesTheSphericalMidpointAndReversesConsistently()
    {
        var coordinates = RfCalculator.SamplePath(89.9, -90, 89.9, 90, 3);
        var reversed = RfCalculator.SamplePath(89.9, 90, 89.9, -90, 3).Reverse().ToList();
        Assert.Equal(3, coordinates.Count);
        Assert.True(Math.Abs(coordinates[1].Lat - 90) < 0.000000001);
        for (var i = 0; i < 3; i++)
            Assert.True(RfCalculator.Distance(coordinates[i].Lat, coordinates[i].Lon, reversed[i].Lat, reversed[i].Lon) < 0.000001);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    [InlineData(100)]
    public void AmbiguousAntipodalPathsHaveNoTerrainRoute(int count)
    {
        Assert.Empty(RfCalculator.SamplePath(0, 0, 0, 180, count));
        Assert.Empty(ToolsService.ElevationSamplePoints(0, 0, 0, 180));
    }

    [Fact]
    public void ElevationPointsFollowTheGreatCircleAndKeepTheEndpoints()
    {
        var points = ToolsService.ElevationSamplePoints(18.2, -66.5, 18.4, -66.0);
        Assert.Equal(100, points.Count);
        Assert.Equal((18.2, -66.5), (points[0].Lat, points[0].Lon));
        Assert.Equal((18.4, -66.0), (points[^1].Lat, points[^1].Lon));
        Assert.Equal(0, points[0].Dist);
        Assert.True(Math.Abs(points[^1].Dist - RfCalculator.Distance(18.2, -66.5, 18.4, -66.0)) < 1e-6);
    }

    [Theory]
    [InlineData(180.0)]
    [InlineData(-180.0)]
    [InlineData(179.9999999)]
    public void ElevationRequestsNormalizeTheRoundedPositiveDateLine(double longitude)
    {
        Assert.Equal("-180.000000", ToolsService.FormatElevationLongitude(longitude));
        Assert.EndsWith("&longitude=-180.000000", ToolsService.ElevationRequestUrl([(0, longitude)]));
        Assert.Equal("179.999000", ToolsService.FormatElevationLongitude(179.999));
    }

    // ───────────────────────── Clearance formatting (LOSFormattersTests) ─────────────────────────

    [Theory]
    [InlineData(-50.0, "-50%")]
    [InlineData(167.0, "167%")]
    [InlineData(-0.0022239, "-0.00222%")]
    [InlineData(330.16700191225306, "330%")]
    [InlineData(76.74724578644971, "76.7%")]
    public void ClearanceKeepsItsSignAndRange(double percent, string expected) =>
        Assert.Equal(expected, RfCalculator.FormatClearancePercent(percent, CultureInfo.GetCultureInfo("en-US")));

    [Fact]
    public void ClearanceUsesTheCultureDecimalSeparatorAndPercentSpacing() =>
        Assert.Equal("-0,5 %", RfCalculator.FormatClearancePercent(-0.5, CultureInfo.GetCultureInfo("de-DE")));

    // ───────────────────────── Status charts (RadioMetricChartsTests) ─────────────────────────

    private static NodeSnapshotRecord Snap(double? snr = null, int? rssi = null, int? noise = null, int? battery = null) =>
        new() { LastSnr = snr, LastRssi = rssi, NoiseFloor = noise, BatteryMv = battery, PacketsSent = 1200 };

    [Theory]
    [InlineData(double.NaN, -90, -110)]
    [InlineData(-32.25, -90, -110)]
    [InlineData(32.0, -90, -110)]
    [InlineData(8.0, -257, -110)]
    [InlineData(8.0, 99, -110)]
    [InlineData(8.0, -90, -257)]
    [InlineData(8.0, -90, 99)]
    public void OneInvalidSignalExcludesTheWholeStatusCapture(double snr, int rssi, int noise)
    {
        Assert.False(RadioHistoryFilter.Includes(Snap(snr, rssi, noise)));
        Assert.Empty(RadioHistoryFilter.Chartable([Snap(snr, rssi, noise)]));
    }

    [Theory]
    [InlineData(-32.0, -256, 98)]
    [InlineData(31.75, 98, -256)]
    [InlineData(0.0, 0, 0)]
    public void DriverBoundariesKeepHighBatteryReadings(double snr, int rssi, int noise) =>
        Assert.True(RadioHistoryFilter.Includes(Snap(snr, rssi, noise, 35000)));

    [Fact]
    public void MissingSignalsAndZeroBatteryRemainChartable()
    {
        Assert.True(RadioHistoryFilter.Includes(Snap(battery: 0)));
        var suspect = Snap(2700, 8500, 8500, 0);
        Assert.False(RadioHistoryFilter.HasRadioData([suspect]));
        Assert.True(RadioHistoryFilter.HasRadioData([suspect, Snap()]));
    }

    [Fact]
    public async Task ChartHistoryOmitsSuspectCapturesButKeepsThemStored()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var repeater = app.Contacts.GetAll().First(c => c.ContactType == ContactType.Repeater);
        app.Db.SaveSnapshot(new NodeSnapshotRecord { RadioId = app.RadioId!, PublicKey = repeater.PublicKey, BatteryMv = 4000, LastSnr = 8, LastRssi = -90, NoiseFloor = -110 });
        app.Db.SaveSnapshot(new NodeSnapshotRecord { RadioId = app.RadioId!, PublicKey = repeater.PublicKey, BatteryMv = 0, LastSnr = 2700, LastRssi = 8500, NoiseFloor = 8500 });
        Assert.Equal(2, app.Remote.History(repeater, TimeSpan.FromDays(1)).Count);
        var charted = app.Remote.ChartHistory(repeater, TimeSpan.FromDays(1));
        Assert.Single(charted);
        Assert.Equal(-110, charted[0].NoiseFloor);
    }

    // ───────────────────────── Trace path (stale batches, stopped sessions) ─────────────────────────

    [Fact]
    public async Task CancelledTraceIsNotReportedAsATimeout()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var repeater = app.Contacts.GetAll().First(c => c.ContactType == ContactType.Repeater);
        var path = ToolsService.BuildTracePath([repeater.PublicKey[..1]], roundTrip: false);

        var ok = await app.Tools.RunTraceAsync(path, 0);
        Assert.True(ok.Success, ok.Error);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Tools.RunTraceAsync(path, 0, cts.Token));
    }

    [Fact]
    public async Task DisconnectingStopsAWaitingTraceAsCancellation()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var repeater = app.Contacts.GetAll().First(c => c.ContactType == ContactType.Repeater);
        var path = ToolsService.BuildTracePath([repeater.PublicKey[..1], repeater.PublicKey[..1], repeater.PublicKey[..1]], roundTrip: true);
        var run = app.Tools.RunTraceAsync(path, 0);
        await Task.Delay(300);
        await app.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ───────────────────────── Radio presets (RadioPresetRecommendationTests) ─────────────────────────

    private static RadioPreset Preset(string id) => RadioPresets.All.Single(p => p.Id == id);

    [Fact]
    public void PresetTableMatchesTheIPhone()
    {
        Assert.Equal(
            new[] { "au-915", "au-narrow", "au-mid", "au-sa-wa", "au-qld", "nz-lr", "nz-narrow",
             "eu-narrow", "eu-lr", "cz-narrow", "eu-433-lr", "eu-433-narrow", "pt-433", "pt-868", "ch", "hu", "nl", "nl-li", "sk",
             "us-ca", "ca", "ca-bc", "cr", "wcmesh", "phillymesh", "lvmesh", "cl", "br", "vn-narrow", "vn" },
            RadioPresets.All.Select(p => p.Id).ToArray());
        Assert.Equal(RadioPresets.All.Count, RadioPresets.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void PhillyMeshIsBackAt919_5()
    {
        var p = Preset("phillymesh");
        Assert.Equal(("PhillyMesh", 919.500, 500.0, (byte)10, (byte)5, (int?)2, RadioRegion.NorthAmerica),
            (p.Name, p.FrequencyMHz, p.BandwidthKHz, p.SpreadingFactor, p.CodingRate, p.PathHashSize, p.Region));
        Assert.Equal(new[] { "US" }, p.Countries);
        Assert.Equal(new[] { "US-DE", "US-MD", "US-NJ", "US-PA" }, p.Areas);
        Assert.Equal("phillymesh", RadioPresets.Resolve(919.5, 500, 10, 5, null, "US")!.Id);
    }

    [Fact]
    public void BritishColumbiaPresetCarriesTheExpectedParameters()
    {
        var p = Preset("ca-bc");
        Assert.Equal(("Canada: BC", 909.000, 62.5, (byte)7, (byte)5, (int?)null, RadioRegion.NorthAmerica),
            (p.Name, p.FrequencyMHz, p.BandwidthKHz, p.SpreadingFactor, p.CodingRate, p.PathHashSize, p.Region));
        Assert.Equal(new[] { "CA-BC" }, p.Areas);
        Assert.Equal("ca-bc", RadioPresets.Resolve(909, 62.5, 7, 5, null, "CA")!.Id);
    }

    [Fact]
    public void AustraliaAndCzechPresetsMatchTheIPhone()
    {
        Assert.Equal((916.575, 62.5, (byte)7, (byte)7), (Preset("au-narrow").FrequencyMHz, Preset("au-narrow").BandwidthKHz, Preset("au-narrow").SpreadingFactor, Preset("au-narrow").CodingRate));
        Assert.Equal(2, Preset("cz-narrow").PathHashSize);
        Assert.Equal(new[] { "AU-SA", "AU-WA" }, Preset("au-sa-wa").Areas);
        Assert.Equal(new[] { "AU-QLD" }, Preset("au-qld").Areas);
    }

    [Fact]
    public void CatalogPathHashSizeIsSetOnlyOnPresetsThatNameOne()
    {
        Assert.Equal(2, Preset("hu").PathHashSize);
        Assert.Equal(2, Preset("sk").PathHashSize);
        Assert.Equal(2, Preset("cz-narrow").PathHashSize);
        Assert.Equal(1, Preset("nz-lr").PathHashSize);
        Assert.Equal(2, Preset("nz-narrow").PathHashSize);
        Assert.Equal(2, Preset("phillymesh").PathHashSize);
        Assert.Equal(2, Preset("lvmesh").PathHashSize);
        Assert.Equal(3, Preset("ca").PathHashSize);
        Assert.Equal(3, Preset("wcmesh").PathHashSize);
        Assert.Equal(2, Preset("nl-li").PathHashSize);
        Assert.Null(Preset("nl").PathHashSize);
        Assert.Null(Preset("us-ca").PathHashSize);
        Assert.Null(Preset("cr").PathHashSize);
        Assert.Null(Preset("ca-bc").PathHashSize);
    }

    [Theory]
    [InlineData("CA", null, null, "ca")]
    [InlineData("CA", "CA-BC", null, "ca-bc")]
    [InlineData("CA", "CA-ON", null, "ca")]
    [InlineData("CA", "CA-QC", null, "ca")]
    [InlineData("CA", "CA-YT", null, "ca")]
    [InlineData("US", "US-CA", "los angeles", "wcmesh")]
    [InlineData("US", "US-CA", "sacramento", "us-ca")]
    [InlineData("US", "US-CA", null, "us-ca")]
    [InlineData("US", "US-PA", null, "lvmesh")]
    [InlineData("US", "US-NJ", null, "lvmesh")]
    [InlineData("US", "US-DE", null, "phillymesh")]
    [InlineData("US", "US-MD", null, "phillymesh")]
    [InlineData("US", "US-TX", null, "us-ca")]
    [InlineData("US", null, null, "us-ca")]
    [InlineData("AU", "AU-QLD", null, "au-qld")]
    [InlineData("AU", "AU-WA", null, "au-sa-wa")]
    [InlineData("AU", "AU-VIC", null, "au-915")]
    [InlineData("PT", null, null, "pt-868")]
    [InlineData("VN", null, null, "vn-narrow")]
    [InlineData("NL", null, null, "nl")]
    [InlineData("HU", null, null, "hu")]
    [InlineData("SK", null, null, "sk")]
    [InlineData("CR", null, null, "cr")]
    [InlineData("CL", null, null, "cl")]
    [InlineData("BR", null, null, "br")]
    [InlineData("DE", null, null, "eu-narrow")]
    [InlineData("NZ", null, null, "nz-lr")]
    public void RecommendationTiersMatchTheIPhone(string country, string? area, string? county, string expected) =>
        Assert.Equal(expected, RadioPresets.Recommended(new RegionSelection(country, area, county))?.Id);

    [Fact]
    public void PlacesWithoutPresetsGetNoRecommendation()
    {
        Assert.Null(RadioPresets.Recommended(new RegionSelection("BM")));
        Assert.Null(RadioPresets.Recommended(new RegionSelection("MX")));
        Assert.Null(RadioPresets.RecommendedFor(null));
        Assert.Null(RadioPresets.RecommendedFor("IV"));
    }

    [Fact]
    public void RecommendedForTakesAProvinceAndTreatsUsTerritoriesAsTheUs()
    {
        Assert.Equal("ca-bc", RadioPresets.RecommendedFor("ca", "ca-bc"));
        Assert.Equal("ca", RadioPresets.RecommendedFor("CA", "US-PA")); // a state of another country is ignored
        Assert.Equal("us-ca", RadioPresets.RecommendedFor("PR"));
        Assert.Equal("us-ca", RadioPresets.Resolve(910.525, 62.5, 7, 5, null, "PR")!.Id);
        Assert.Equal("ca", RadioPresets.Resolve(910.525, 62.5, 7, 5, null, "CA", "CA-ON")!.Id);
    }

    [Fact]
    public void PresetsInRegionMatchTheIPhoneMembership()
    {
        var canada = RadioPresets.PresetsIn(new RegionSelection("CA")).Select(p => p.Id).ToList();
        Assert.Contains("ca", canada);
        Assert.Contains("ca-bc", canada);
        Assert.DoesNotContain("us-ca", canada);
        Assert.DoesNotContain("phillymesh", canada);
        var texas = RadioPresets.PresetsIn(new RegionSelection("US", "US-TX")).Select(p => p.Id).ToList();
        Assert.Equal(new[] { "us-ca", "wcmesh", "phillymesh", "lvmesh" }, texas);
        var germany = RadioPresets.PresetsIn(new RegionSelection("DE")).Select(p => p.Id).ToList();
        Assert.Contains("eu-narrow", germany);
        Assert.DoesNotContain("us-ca", germany);
        Assert.DoesNotContain("eu-narrow", RadioPresets.PresetsIn(new RegionSelection("PT")).Select(p => p.Id));
    }

    [Fact]
    public void SelectabilityFollowsTheArea()
    {
        Assert.True(RadioPresets.IsSelectable(Preset("ca-bc"), new RegionSelection("CA", "CA-BC")));
        Assert.False(RadioPresets.IsSelectable(Preset("ca-bc"), new RegionSelection("CA", "CA-ON")));
        Assert.False(RadioPresets.IsSelectable(Preset("ca-bc"), null));
        Assert.True(RadioPresets.IsSelectable(Preset("wcmesh"), new RegionSelection("US", "US-CA", "los angeles")));
        Assert.False(RadioPresets.IsSelectable(Preset("wcmesh"), new RegionSelection("US", "US-CA")));
        Assert.True(RadioPresets.IsSelectable(Preset("us-ca"), null));
    }

    [Fact]
    public void PickerListsEveryPresetWithTheProvincesRecommendationFirst()
    {
        var bc = RadioPresets.PresetsFor(new RegionSelection("CA", "CA-BC"));
        Assert.Equal(RadioPresets.All.Count, bc.Count);
        Assert.Equal("ca-bc", bc[0].Id);
        Assert.Equal("ca", bc[1].Id);
        var ontario = RadioPresets.PresetsFor(new RegionSelection("CA", "CA-ON"));
        Assert.Equal("ca", ontario[0].Id);
        Assert.Equal("ca-bc", ontario[1].Id);
        Assert.Equal(RadioPresets.All.Count, RadioPresets.PresetsFor(null).Count);
    }

    // ───────────────────────── Regional areas (RegionalAreasTests) ─────────────────────────

    [Fact]
    public void CanadaListsEveryProvinceAndTerritory()
    {
        Assert.Equal(
            new HashSet<string> { "CA-AB", "CA-BC", "CA-MB", "CA-NB", "CA-NL", "CA-NS", "CA-NT", "CA-NU", "CA-ON", "CA-PE", "CA-QC", "CA-SK", "CA-YT" },
            RegionalAreas.CaSubdivisions.Select(s => s.Id).ToHashSet());
        Assert.Equal(51, RegionalAreas.UsSubdivisions.Count);
        Assert.Equal(8, RegionalAreas.AuSubdivisions.Count);
        Assert.True(RegionalAreas.ShowsSubdivisionPicker("CA"));
        Assert.True(RegionalAreas.ShowsSubdivisionPicker("US"));
        Assert.True(RegionalAreas.ShowsSubdivisionPicker("AU"));
        Assert.False(RegionalAreas.ShowsSubdivisionPicker("PT"));
        Assert.False(RegionalAreas.ShowsSubdivisionPicker(null));
        Assert.Equal(RegionalAreas.AdministrativeAreaKind.Province, RegionalAreas.AdministrativeAreaKindFor("CA"));
        Assert.Equal(RegionalAreas.AdministrativeAreaKind.State, RegionalAreas.AdministrativeAreaKindFor("US"));
        Assert.Equal(RegionalAreas.AdministrativeAreaKind.State, RegionalAreas.AdministrativeAreaKindFor("AU"));
    }

    [Theory]
    [InlineData("CA", "pei", "CA-PE")]
    [InlineData("CA", "nwt", "CA-NT")]
    [InlineData("CA", "newfoundland", "CA-NL")]
    [InlineData("CA", "yukon territory", "CA-YT")]
    [InlineData("CA", "british columbia", "CA-BC")]
    [InlineData("CA", "bc", "CA-BC")]
    [InlineData("US", "ca", "US-CA")]
    [InlineData("US", "pennsylvania", "US-PA")]
    [InlineData("AU", "qld", "AU-QLD")]
    [InlineData("US", "zz", null)]
    [InlineData("US", "pr", null)]
    public void MatchSubdivisionAcceptsNamesAndAliases(string country, string normalized, string? expected) =>
        Assert.Equal(expected, RegionalAreas.MatchSubdivision(country, normalized));

    [Fact]
    public void CountiesAndDisplayNames()
    {
        Assert.Equal("los angeles", RegionalAreas.MatchCounty("US", "US-CA", "los angeles"));
        Assert.Null(RegionalAreas.MatchCounty("US", "US-CA", "sacramento"));
        Assert.Null(RegionalAreas.MatchCounty("CA", "CA-ON", "york"));
        Assert.Null(RegionalAreas.MatchCounty("US", null, "los angeles"));
        Assert.Equal("British Columbia", RegionalAreas.DisplayName(new RegionSelection("CA", "CA-BC")));
        Assert.Equal("California", RegionalAreas.DisplayName(new RegionSelection("US", "US-CA")));
        Assert.Equal("Queensland, Australia", RegionalAreas.DisplayName(new RegionSelection("AU", "AU-QLD")));
        Assert.Equal("United States", RegionalAreas.DisplayName(new RegionSelection("US")));
        Assert.True(RegionalAreas.IsIncomplete(new RegionSelection("CA")));
        Assert.False(RegionalAreas.IsIncomplete(new RegionSelection("CA", "CA-BC")));
        Assert.False(RegionalAreas.IsIncomplete(new RegionSelection("PT")));
    }

    [Fact]
    public void ContinentsAndCountriesCoverTheSameCodes()
    {
        Assert.Equal(RegionalAreas.Continents.Keys.ToHashSet(), RegionalAreas.Countries.Select(c => c.Id).ToHashSet());
        Assert.False(RegionalAreas.Continents.ContainsKey("MX"));
        Assert.Equal(RadioRegion.NorthAmerica, RegionalAreas.Continents["CR"]);
    }

    [Fact]
    public void SavedAreaDrivesTheSelectionAndAStrayStateIsDropped()
    {
        Assert.Equal(new RegionSelection("CA", "CA-BC"), RegionalAreas.Selection("CA", "CA-BC"));
        Assert.Equal(new RegionSelection("CA"), RegionalAreas.Selection("CA", "US-PA"));
        Assert.Equal(new RegionSelection("US"), RegionalAreas.Selection("PR", null));
    }

    [Fact]
    public async Task RadioOnSharedSettingsResolvesByTheSavedProvince()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        // 910.525/62.5/7/5 is both USA and Canada: the saved country decides when nothing was applied from the app.
        app.Settings.Update(s => { s.PresetCountry = "CA"; s.PresetArea = "CA-ON"; });
        Assert.Equal("ca", app.Device.CurrentPreset()!.Id);
        app.Settings.Update(s => { s.PresetCountry = "US"; s.PresetArea = null; });
        Assert.Equal("us-ca", app.Device.CurrentPreset()!.Id);
        await app.Device.ApplyPresetAsync(Preset("ca-bc"));
        Assert.Equal(909.0, app.SelfInfo!.RadioFrequency, 3);
        Assert.Equal("ca-bc", app.Device.CurrentPreset()!.Id);
    }
}

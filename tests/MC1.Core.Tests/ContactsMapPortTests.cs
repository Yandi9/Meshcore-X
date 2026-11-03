using System.Text.Json;
using System.Text.Json.Nodes;
using MC1.Core.Maps;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;
using Xunit;

namespace MC1.Core.Tests;

/// <summary>WS4: contacts (nodes) and the map, ported from MeshCore One v1.5.1 → v1.6.2.</summary>
public class ContactsMapPortTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    private static ContactRecord Contact(string name, long lastAdvert = 0, long lastHeardMs = 0, int outPathLength = 0xFF, int? inboundHops = null,
        double lat = 0, double lon = 0, ContactType type = ContactType.Chat) =>
        new() { Name = name, PublicKey = [(byte)name[0], 1, 2, 3], LastAdvert = lastAdvert, LastHeard = lastHeardMs, OutPathLength = outPathLength,
            InboundHops = inboundHops, Latitude = lat, Longitude = lon, Type = (int)type };

    // MARK: Sort by last advert (25701306, cd2d78b8)

    [Fact]
    public void AdvertTimestampsBefore2020OrInTheFutureAreNotPlausible()
    {
        Assert.True(AdvertTimestamp.IsPlausible(Now.ToUnixTimeSeconds() - 60, Now));
        Assert.True(AdvertTimestamp.IsPlausible(Now.ToUnixTimeSeconds() + 299, Now)); // small drift
        Assert.False(AdvertTimestamp.IsPlausible(Now.ToUnixTimeSeconds() + 301, Now));
        Assert.False(AdvertTimestamp.IsPlausible(1_577_836_799, Now));
        Assert.True(AdvertTimestamp.IsPlausible(1_577_836_800, Now));
        Assert.False(AdvertTimestamp.IsPlausible(0, Now));
    }

    [Fact]
    public void NodesSortByLastAdvertAndFallBackToLastHeardWhenTheAdvertIsSkewed()
    {
        var nowS = Now.ToUnixTimeSeconds();
        var recent = Contact("Recent", lastAdvert: nowS - 3600);                                       // advert 1 h ago
        var future = Contact("Future", lastAdvert: nowS + 86400 * 365, lastHeardMs: (nowS - 600) * 1000); // clock ahead: heard 10 min ago
        var unset = Contact("Unset", lastAdvert: 12345, lastHeardMs: (nowS - 7200) * 1000);           // clock never set: heard 2 h ago
        var never = Contact("Never", lastAdvert: 0);
        var sorted = NodeList.Sort([never, unset, recent, future], NodeSortOrder.LastHeard, Now);
        Assert.Equal(["Future", "Recent", "Unset", "Never"], sorted.Select(c => c.Name));

        // The row shows the time it was sorted by.
        Assert.Equal((nowS - 3600) * 1000, NodeList.TimestampMs(recent, Now));
        Assert.Equal((nowS - 600) * 1000, NodeList.TimestampMs(future, Now));
        Assert.Equal((nowS - 7200) * 1000, NodeList.TimestampMs(unset, Now));
        Assert.Null(NodeList.TimestampMs(never, Now));
    }

    [Fact]
    public void NodesSortByNameDistanceAndHops()
    {
        var a = Contact("Charlie", outPathLength: 2, lat: 1.02, lon: 1.0);
        var b = Contact("alpha", outPathLength: 0xFF, inboundHops: 1, lat: 1.001, lon: 1.0);
        var c = Contact("Bravo", outPathLength: 0, lat: 0, lon: 0);
        var d = Contact("Delta", outPathLength: 0xFF);
        Assert.Equal(["alpha", "Bravo", "Charlie", "Delta"], NodeList.Sort([a, b, c, d], NodeSortOrder.Name, Now).Select(x => x.Name));
        // Located nodes first, nearest first; no location falls back to name.
        Assert.Equal(["alpha", "Charlie", "Bravo", "Delta"], NodeList.Sort([a, b, c, d], NodeSortOrder.Distance, Now, (1.0, 1.0)).Select(x => x.Name));
        // Flood-routed nodes use the advert's inbound hops; unknown last.
        Assert.Equal(["Bravo", "alpha", "Charlie", "Delta"], NodeList.Sort([a, b, c, d], NodeSortOrder.Hops, Now, (1.0, 1.0)).Select(x => x.Name));
        // Distance without a location of our own sorts by last heard.
        Assert.Equal(NodeList.Sort([a, b, c, d], NodeSortOrder.LastHeard, Now).Select(x => x.Name),
            NodeList.Sort([a, b, c, d], NodeSortOrder.Distance, Now).Select(x => x.Name));
    }

    [Fact]
    public async Task SyncedAdvertsAdvanceLastHeardButSkewedOnesDoNot()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob");
        var alice = app.Contacts.GetAll().First(c => c.Name == "Alice");
        // The initial sync already counted the radio's adverts as "heard".
        Assert.True(bob.LastHeard >= bob.LastAdvert * 1000 && bob.LastAdvert > 0);

        // Bob advertised while this PC wasn't listening; Alice's clock jumped a day ahead.
        var advert = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 20;
        f.Radio.UpdateContactForTest("Bob", lastAdvert: advert);
        var aliceHeard = alice.LastHeard;
        f.Radio.UpdateContactForTest("Alice", lastAdvert: advert + 86400);
        await app.SyncContactsAsync(force: false);
        Assert.Equal((long)advert * 1000, app.Db.GetContact(bob.Id)!.LastHeard);
        Assert.Equal(aliceHeard, app.Db.GetContact(alice.Id)!.LastHeard);

        // Last heard never goes back: an older advert keeps the newer time.
        f.Radio.UpdateContactForTest("Bob", lastAdvert: advert - 3600);
        await app.SyncContactsAsync(force: true);
        Assert.Equal((long)advert * 1000, app.Db.GetContact(bob.Id)!.LastHeard);
        // Local fields survive the batched save.
        app.Contacts.SetNickname(app.Db.GetContact(bob.Id)!, "Bobby");
        await app.SyncContactsAsync(force: true);
        Assert.Equal("Bobby", app.Db.GetContact(bob.Id)!.Nickname);
    }

    // MARK: Favourites confirmed with the radio (7ef0596d, 8ba4940d)

    [Fact]
    public async Task FavoritesRoundTripThroughTheRadio()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob");
        var changes = 0;
        app.DataChanged += c => { if (c.Kind == DataKind.Contacts) Interlocked.Increment(ref changes); };

        var saved = await app.Contacts.SetFavoriteAsync(bob, true);
        Assert.True(saved.IsFavorite);
        Assert.True(app.Db.GetContact(bob.Id)!.IsFavorite);
        Assert.True((await app.Session!.GetContactAsync(bob.PublicKey))!.Flags.HasFlag(ContactFlags.Favorite));
        Assert.True(changes > 0, "lists refresh after the favourite is saved");

        // Changed on the radio by another app: the next sync brings it here.
        f.Radio.UpdateContactForTest("Bob", favorite: false);
        await app.SyncContactsAsync(force: false);
        Assert.False(app.Db.GetContact(bob.Id)!.IsFavorite);

        // A radio that doesn't take the change: nothing is saved here.
        f.Radio.IgnoreContactFlagChangesForTest = true;
        await Assert.ThrowsAsync<MessageServiceException>(() => app.Contacts.SetFavoriteAsync(app.Db.GetContact(bob.Id)!, true));
        Assert.False(app.Db.GetContact(bob.Id)!.IsFavorite);
    }

    [Fact]
    public async Task FavoriteAndSyncRunOneAtATime()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = app.Contacts.GetAll().First(c => c.Name == "Alice");
        // A sync started together with the favourite can't save its older snapshot over it.
        var sync = app.SyncContactsAsync(force: true);
        var fav = app.Contacts.SetFavoriteAsync(alice, true);
        await Task.WhenAll(sync, fav);
        Assert.True(app.Db.GetContact(alice.Id)!.IsFavorite);
        await app.SyncContactsAsync(force: true);
        Assert.True(app.Db.GetContact(alice.Id)!.IsFavorite);
    }

    // MARK: Discovered paths saved on the radio (7be05b55)

    [Fact]
    public async Task DiscoveredPathIsStoredOnTheRadioAndSurvivesSync()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob");
        Assert.True(bob.IsFloodRouted);
        var saved = new TaskCompletionSource<ContactRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Contacts.PathDiscovered += (c, _) => { if (c.Id == bob.Id) saved.TrySetResult(c); };

        await app.Contacts.DiscoverPathAsync(bob);
        var updated = await saved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, updated.HopCount);
        Assert.False(app.Db.GetContact(bob.Id)!.IsFloodRouted);
        var onRadio = (await app.Session!.GetContactAsync(bob.PublicKey))!;
        Assert.Equal(1, onRadio.PathHopCount);
        Assert.Equal(updated.OutPath, onRadio.OutPath[..onRadio.PathByteLength]);

        // A full contact sync keeps it (the radio has it now).
        await app.SyncContactsAsync(force: true);
        var afterSync = app.Db.GetContact(bob.Id)!;
        Assert.Equal(1, afterSync.HopCount);
        Assert.Equal(updated.OutPath, afterSync.OutPath[..afterSync.OutPath.Length]);
    }

    [Fact]
    public async Task ZeroHopDiscoveredPathStaysDirectAndReservedLengthsAreNotWritten()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob");
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Contacts.PathDiscovered += (c, _) => { if (c.Id == bob.Id) saved.TrySetResult(); };

        // A reserved length byte (hash mode 3): nothing written, path unchanged.
        app.Contacts.HandlePathDiscovery(new PathInfo(bob.PublicKey[..6], 0xC1, [0xAA], 0, []));
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(app.Db.GetContact(bob.Id)!.IsFloodRouted);

        saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Contacts.HandlePathDiscovery(new PathInfo(bob.PublicKey[..6], 0, [], 0, []));
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var direct = app.Db.GetContact(bob.Id)!;
        Assert.Equal(0, direct.OutPathLength);
        Assert.False(direct.IsFloodRouted);
        Assert.Equal(0, (await app.Session!.GetContactAsync(bob.PublicKey))!.OutPathLength);
    }

    // MARK: Advanced map filters (4a38cb73)

    [Fact]
    public void LastHeardRangesFilterByAgeAndRepairCrossedBounds()
    {
        var twoHours = MapLastHeardRange.Within(new MapDuration(2, MapDurationUnit.Hours));
        Assert.True(twoHours.IsActive);
        Assert.True(twoHours.Contains(Now.AddMinutes(-119), Now));
        Assert.False(twoHours.Contains(Now.AddMinutes(-121), Now));
        Assert.False(twoHours.Contains(null, Now));                 // never heard: hidden while on
        Assert.True(twoHours.Contains(Now.AddHours(3), Now));        // clock skew counts as just heard
        Assert.True(MapLastHeardRange.Any.Contains(null, Now));

        var older = MapLastHeardRange.Any.WithMinAge(new MapDuration(1, MapDurationUnit.Days));
        Assert.False(older.Contains(Now.AddHours(-23), Now));
        Assert.True(older.Contains(Now.AddDays(-2), Now));

        // Setting a minimum above the maximum moves the maximum up; and the other way round.
        var r = twoHours.WithMinAge(new MapDuration(3, MapDurationUnit.Hours));
        Assert.Equal(r.MinAge, r.MaxAge);
        var r2 = MapLastHeardRange.Any.WithMinAge(new MapDuration(3, MapDurationUnit.Days)).WithMaxAge(new MapDuration(30, MapDurationUnit.Minutes));
        Assert.Equal(new MapDuration(30, MapDurationUnit.Minutes), r2.MinAge);
        Assert.Equal(new MapDuration(9999, MapDurationUnit.Hours), new MapDuration(100_000, MapDurationUnit.Hours));
        Assert.Contains(twoHours, MapLastHeardRange.Presets);
    }

    [Fact]
    public void HopRangesClampRepairAndTreatUnknownAsHidden()
    {
        Assert.False(MapHopRange.Any.IsActive);
        Assert.True(MapHopRange.Direct.Contains(0));
        Assert.False(MapHopRange.Direct.Contains(1));
        Assert.False(MapHopRange.Direct.Contains(null));
        Assert.True(MapHopRange.Any.Contains(null));
        var r = MapHopRange.Create(2, 5);
        Assert.True(r.Contains(2) && r.Contains(5));
        Assert.False(r.Contains(1) || r.Contains(6));
        Assert.Equal(MapHopRange.Create(7, 7), r.WithMin(7));        // max moves up
        Assert.Equal(MapHopRange.Create(1, 1), r.WithMax(1));        // min moves down
        Assert.Null(MapHopRange.Create(0, 63).Max);                  // 63 = no limit
        Assert.False(MapHopRange.Create(0, 63).IsActive);
        Assert.Equal(63, MapHopRange.Create(80, null).Min);
        Assert.Equal(MapHopRange.Create(0, 2), MapHopRange.Presets[3]);
    }

    [Fact]
    public void AdvancedFiltersCombineAndShowAsChips()
    {
        var filter = new MapAdvancedFilter { LastHeard = MapLastHeardRange.Within(new MapDuration(15, MapDurationUnit.Minutes)), Hops = MapHopRange.Direct };
        Assert.Equal([MapFilterDimension.LastHeard, MapFilterDimension.Hops], filter.ActiveDimensions);
        Assert.Equal("Heard ≤ 15m ago", filter.ChipText(MapFilterDimension.LastHeard));
        Assert.Equal("Direct only", filter.ChipText(MapFilterDimension.Hops));
        Assert.Equal("Hops 1–3", MapHopRange.Create(1, 3).ChipText);
        Assert.Equal("Hops ≥ 2", MapHopRange.Create(2, null).ChipText);
        Assert.Equal("Heard 1h–2d ago", new MapLastHeardRange { MinAge = new(1, MapDurationUnit.Hours), MaxAge = new(2, MapDurationUnit.Days) }.ChipText);
        Assert.Equal("Showing nodes last heard within the last 15m.", filter.LastHeard.SummaryText);
        Assert.Equal("≤ 2", MapHopRange.Presets[3].PresetLabel);

        Assert.True(filter.Allows(Now.AddMinutes(-5), 0, Now));
        Assert.False(filter.Allows(Now.AddMinutes(-5), 1, Now));
        Assert.False(filter.Allows(Now.AddMinutes(-30), 0, Now));
        var cleared = filter.Cleared(MapFilterDimension.Hops);
        Assert.Equal([MapFilterDimension.LastHeard], cleared.ActiveDimensions);
        Assert.False(cleared.Cleared(MapFilterDimension.LastHeard).IsActive);
    }

    [Fact]
    public void MapNodeFilterUsesHeardTimesAndHopsLikeTheNodesList()
    {
        var nowS = Now.ToUnixTimeSeconds();
        var types = new MapNodeFilter.TypeToggles(true, true, false, true);
        var filter = new MapAdvancedFilter { LastHeard = MapLastHeardRange.Within(new MapDuration(1, MapDurationUnit.Hours)), Hops = MapHopRange.Create(0, 1) };
        var near = Contact("Near", lastAdvert: nowS - 600, outPathLength: 1, lat: 1, lon: 1);
        var floodHeard = Contact("Flood", lastHeardMs: (nowS - 60) * 1000, inboundHops: 1, lat: 1, lon: 1); // flood: advert's hops
        var stale = Contact("Stale", lastAdvert: nowS - 7200, outPathLength: 0, lat: 1, lon: 1);
        var far = Contact("Far", lastAdvert: nowS - 60, outPathLength: 3, lat: 1, lon: 1);
        var room = Contact("Room", lastAdvert: nowS - 60, outPathLength: 0, lat: 1, lon: 1, type: ContactType.Room);
        var blocked = Contact("Blocked", lastAdvert: nowS - 60, outPathLength: 0, lat: 1, lon: 1);
        blocked.IsBlocked = true;
        var shown = new[] { near, floodHeard, stale, far, room, blocked }.Where(c => MapNodeFilter.Shows(c, types, filter, Now)).Select(c => c.Name);
        Assert.Equal(["Near", "Flood"], shown);

        var heard = new DiscoveredNodeRecord { Name = "Disc", LastHeard = (nowS - 60) * 1000, OutPathLength = 0, Latitude = 1, Longitude = 1 };
        var floodDisc = new DiscoveredNodeRecord { Name = "Disc2", LastHeard = (nowS - 60) * 1000, OutPathLength = 0xFF, Latitude = 1, Longitude = 1 };
        Assert.True(MapNodeFilter.Shows(heard, types, filter, Now));
        Assert.False(MapNodeFilter.Shows(floodDisc, types, filter, Now));  // unknown hops
        Assert.False(MapNodeFilter.Shows(heard, types with { Discovered = false }, filter, Now));
        Assert.True(MapNodeFilter.Shows(floodDisc, types, new MapAdvancedFilter(), Now));
    }

    [Fact]
    public void MapFilterIsStoredInTheSettingsAndRepairedOnLoad()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc1filter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "settings.json");
            var store = new SettingsStore(path);
            var filter = new MapAdvancedFilter { LastHeard = new MapLastHeardRange { MinAge = new(2, MapDurationUnit.Days) }, Hops = MapHopRange.Create(1, 4) };
            store.Update(s => s.MapFilter = filter);
            Assert.Equal(filter, new SettingsStore(path).Current.MapFilter);
            Assert.DoesNotContain("ChipText", File.ReadAllText(path));

            // An older settings file (no filter) and an edited one (crossed, out-of-range values).
            File.WriteAllText(path, "{\"MapLayer\":\"Dark\"}");
            Assert.False(new SettingsStore(path).Current.MapFilter.IsActive);
            File.WriteAllText(path, """{"MapFilter":{"LastHeard":{"MinAge":{"Value":5,"Unit":"Days"},"MaxAge":{"Value":1,"Unit":"Hours"}},"Hops":{"Min":9,"Max":2}}}""");
            var repaired = new SettingsStore(path).Current.MapFilter.Sanitized();
            Assert.Equal(repaired.LastHeard.MinAge, repaired.LastHeard.MaxAge);
            Assert.Equal(MapHopRange.Create(9, 9), repaired.Hops);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // MARK: Offline maps (f6a3690b, 02abdb3b, c30be5c2, d6da297a)

    [Fact]
    public void OfflineAreasCountTilesAcrossTheAntimeridianAndWholeWorld()
    {
        // A view across 180°: west 170 → east −170 (drawn as 190).
        var across = GeoBounds.FromView(10, -10, 170, 190);
        Assert.True(across.CrossesAntimeridian);
        Assert.Equal(20, across.LongitudeSpan, 6);
        var cols = OfflineMapPolicy.Columns(across, 4).ToList();
        Assert.Equal([15, 0], cols);              // the last and first column, not the whole world in between
        // A view wider than the world counts each tile once.
        var world = GeoBounds.FromView(85, -85, -400, 500);
        Assert.Equal(1, OfflineMapPolicy.TileCount(world, 0));
        Assert.Equal(1L << 10, OfflineMapPolicy.TileCount(world, 5));
        // A tiny area: one tile per zoom.
        var tiny = new GeoBounds(1.000001, 1, 1, 1.000001);
        var std = OfflineMapPolicy.Summary(tiny, "Standard", 16);
        Assert.Equal(16, std.TileCount);
        Assert.True(std.PermitsDownload);
    }

    [Fact]
    public void OfflineDetailEstimatesAndLimitsPerLayer()
    {
        var tiny = new GeoBounds(1.000001, 1, 1, 1.000001);
        // Dark (vector): map zoom Z is drawn from data zoom Z−1, so zoom 15 saves data zooms 0…14 (15 files).
        Assert.Equal(15, OfflineMapPolicy.Summary(tiny, "Dark", 15).TileCount);
        Assert.Equal(15, OfflineMapPolicy.Summary(tiny, "Dark", 19).TileCount);   // clamped to the most detailed data
        Assert.Equal(60_000 + 10 * 10_000 + 15_000 + 25_000 + 45_000 + 70_000 + 100_000, OfflineMapPolicy.Summary(tiny, "Dark", 15).EstimatedBytes);
        Assert.Equal(OfflineMapPolicy.VectorTileLimit, OfflineMapPolicy.Summary(tiny, "Dark", 15).TileLimit);
        // Lower detail: fewer files and bytes.
        Assert.True(OfflineMapPolicy.Summary(tiny, "Standard", 10).EstimatedBytes < OfflineMapPolicy.Summary(tiny, "Standard", 16).EstimatedBytes);
        // No downloads of the topographic layer.
        Assert.False(OfflineMapPolicy.Profile("Topo").AllowsDownload);
        Assert.True(OfflineMapPolicy.Profile("Dark").AllowsDownload);

        // A large area goes over the provider's limit at full detail; the most detailed allowed level stays within it.
        var region = GeoBounds.FromView(48, 45, -124, -116);
        Assert.False(OfflineMapPolicy.Summary(region, "Dark", 15).PermitsDownload);
        var best = OfflineMapPolicy.MostDetailedPermitted(region, "Dark");
        Assert.True(OfflineMapPolicy.Summary(region, "Dark", best).PermitsDownload);
        Assert.False(OfflineMapPolicy.Summary(region, "Dark", best + 1).PermitsDownload);
        Assert.Equal("Local (zoom 12)", OfflineMapPolicy.DetailName(12));
        Assert.Equal("Overview (zoom 5)", OfflineMapPolicy.DetailName(5));
        Assert.Equal("Detailed (zoom 13)", OfflineMapPolicy.DetailName(13));
    }

    // MARK: Map labels in the app's language (54d7b7e9)

    private static JsonObject BundledDarkStyle()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "MC1.Windows", "Assets", "Map", "openfreemap-dark.json");
            if (File.Exists(path)) return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        }
        throw new FileNotFoundException("openfreemap-dark.json");
    }

    [Theory]
    [InlineData("es", new[] { "name:es", "name" })]
    [InlineData("de", new[] { "name:de", "name_de", "name" })]
    [InlineData("pt", new[] { "name:pt", "name" })]
    public void DarkMapNameLabelsUseTheAppLanguage(string lang, string[] fields)
    {
        var expected = new JsonArray("coalesce");
        foreach (var field in fields) expected.Add(new JsonArray("get", field));
        var symbols = BundledDarkStyle()["layers"]!.AsArray().OfType<JsonObject>().Where(l => (string?)l["type"] == "symbol").ToList();
        var nameLayers = new[] { "water_name", "highway_name_other", "place_other", "place_suburb", "place_village", "place_town", "place_city",
            "place_city_large", "place_state", "place_country_other", "place_country_minor", "place_country_major" };
        foreach (var layer in symbols)
        {
            var text = layer["layout"]?["text-field"];
            var localized = MapLabelLocalizer.Localize(text, (string?)layer["source"], lang);
            if (nameLayers.Contains((string?)layer["id"])) Assert.True(JsonNode.DeepEquals(expected, localized), $"{layer["id"]} should use {lang}");
            else Assert.Null(localized); // road numbers (ref) and arrows keep their own text
            Assert.Null(MapLabelLocalizer.Localize(text, (string?)layer["source"], "en")); // English: unchanged
        }
    }

    [Fact]
    public void OnlyStockOpenMapTilesNameLabelsAreLocalized()
    {
        var stock = JsonNode.Parse("""["coalesce",["get","name_en"],["get","name"]]""");
        Assert.NotNull(MapLabelLocalizer.Localize(stock, "openmaptiles", "fr"));
        Assert.Null(MapLabelLocalizer.Localize(stock, "contacts", "fr"));                        // not the basemap
        Assert.Null(MapLabelLocalizer.Localize(JsonNode.Parse("\"{name}\""), "openmaptiles", "fr")); // unrecognized
        Assert.Equal("""["coalesce",["get","name:zh"],["get","name"]]""", MapLabelLocalizer.Expression("zh-Hans").ToJsonString());
    }
}

namespace MC1.Core.Maps;

/// <summary>
/// A geographic rectangle. Longitudes are in −180…180; <see cref="West"/> greater than <see cref="East"/> means the
/// area crosses the antimeridian. Latitudes are clamped to what Web-Mercator maps can show.
/// </summary>
public readonly record struct GeoBounds(double North, double South, double West, double East)
{
    public const double MaxLatitude = 85.0511287798066;

    /// <summary>
    /// Bounds from a map's edges as drawn: <paramref name="west"/> and <paramref name="east"/> may run past ±180 when
    /// the view wraps around the world. A view wider than the world becomes the whole world.
    /// </summary>
    public static GeoBounds FromView(double north, double south, double west, double east)
    {
        if (north < south) (north, south) = (south, north);
        north = Math.Clamp(north, -MaxLatitude, MaxLatitude);
        south = Math.Clamp(south, -MaxLatitude, MaxLatitude);
        var width = east - west;
        if (width < 0) width += 360;
        if (width >= 360) return new GeoBounds(north, south, -180, 180);
        var w = NormalizeLongitude(west);
        var e = NormalizeLongitude(w + width);
        if (e == -180 && width > 0) e = 180;
        return new GeoBounds(north, south, w, e);
    }

    public static double NormalizeLongitude(double lon)
    {
        lon = ((lon + 180) % 360 + 360) % 360 - 180;
        return lon;
    }

    /// <summary>Width in degrees of longitude (0…360).</summary>
    public double LongitudeSpan => East >= West ? East - West : East + 360 - West;

    public bool CrossesAntimeridian => East < West;
}

/// <summary>What can be saved for offline use from one map layer, and how big it gets.</summary>
/// <param name="MinZoom">Lowest map zoom saved (the map never shows zoom 0).</param>
/// <param name="MaxZoom">Highest detail that can be chosen (map zoom).</param>
/// <param name="IsVector">Saved as vector data (drawn on this PC at any zoom) instead of map images.</param>
/// <param name="BytesPerTile">Average stored bytes of one file per zoom (vector: per data zoom).</param>
/// <param name="SupportingBytes">Files saved once per layer (style, tile index).</param>
/// <param name="TileLimit">Most files one download may fetch (the provider's limit), or null for none.</param>
public sealed record OfflineMapProfile(
    string LayerId,
    bool AllowsDownload,
    int MinZoom,
    int MaxZoom,
    bool IsVector,
    IReadOnlyDictionary<int, long> BytesPerTile,
    long DefaultBytesPerTile,
    long SupportingBytes,
    int? TileLimit);

/// <summary>Size and limits of an offline map download.</summary>
/// <param name="TileCount">Files to download (map images, or vector tiles for a vector layer).</param>
public sealed record OfflineMapSummary(string LayerId, int MaxZoom, long TileCount, long EstimatedBytes, int? TileLimit)
{
    public bool PermitsDownload => TileLimit is not { } limit || TileCount <= limit;
}

/// <summary>Detail levels, tile counts and size estimates for offline map downloads (port of OfflineMapDownloadPolicy).</summary>
public static class OfflineMapPolicy
{
    /// <summary>The OpenFreeMap vector data ends at zoom 14; the map draws closer zooms from it.</summary>
    public const int VectorDataMaxZoom = 14;

    /// <summary>OpenFreeMap's limit for one download, counted in vector tiles (as on the iPhone).</summary>
    public const int VectorTileLimit = 3000;

    /// <summary>The most map images one download of a raster layer fetches.</summary>
    public const int RasterTileLimit = 30_000;

    /// <summary>Estimates above this get a "large download" warning.</summary>
    public const long LargeDownloadBytes = 500_000_000;

    // Average stored bytes per file. Approximate: real sizes vary a lot with how built-up an area is.
    private static readonly Dictionary<int, long> OsmPng = new()
    {
        [10] = 15_000, [11] = 18_000, [12] = 22_000, [13] = 25_000, [14] = 30_000, [15] = 35_000, [16] = 40_000, [17] = 45_000,
    };
    private static readonly Dictionary<int, long> SatelliteJpeg = new()
    {
        [10] = 18_000, [11] = 20_000, [12] = 22_000, [13] = 24_000, [14] = 26_000, [15] = 28_000, [16] = 30_000, [17] = 32_000,
    };
    // OpenMapTiles vector tiles (by data zoom), the iPhone app's coefficients.
    private static readonly Dictionary<int, long> OpenMapTiles = new()
    {
        [10] = 15_000, [11] = 25_000, [12] = 45_000, [13] = 70_000, [14] = 100_000,
    };

    public static IReadOnlyList<OfflineMapProfile> Profiles { get; } =
    [
        new("Standard", true, 1, 16, false, OsmPng, 10_000, 0, RasterTileLimit),
        // Vector data: the map draws zoom Z from data zoom Z−1 (512-px tiles), so zoom 15 is the most detailed data.
        new("Dark", true, 1, VectorDataMaxZoom + 1, true, OpenMapTiles, 10_000, 60_000, VectorTileLimit),
        new("Satellite", true, 1, 17, false, SatelliteJpeg, 10_000, 0, RasterTileLimit),
        // OpenTopoMap's terms don't clearly allow bulk downloads: viewing only (saved areas keep working).
        new("Topo", false, 1, 15, false, OsmPng, 10_000, 0, RasterTileLimit),
    ];

    public static OfflineMapProfile Profile(string? layerId) => Profiles.FirstOrDefault(p => p.LayerId == layerId) ?? Profiles[0];

    /// <summary>The vector data zoom a vector map draws its zoom <paramref name="mapZoom"/> from.</summary>
    public static int VectorDataZoom(int mapZoom) => Math.Clamp(mapZoom - 1, 0, VectorDataMaxZoom);

    /// <summary>The zooms whose files a download up to <paramref name="maxZoom"/> saves (vector: data zooms).</summary>
    public static IEnumerable<int> DownloadZooms(OfflineMapProfile p, int maxZoom)
    {
        maxZoom = Math.Clamp(maxZoom, p.MinZoom, p.MaxZoom);
        return p.IsVector
            ? Enumerable.Range(p.MinZoom, maxZoom - p.MinZoom + 1).Select(VectorDataZoom).Distinct()
            : Enumerable.Range(p.MinZoom, maxZoom - p.MinZoom + 1);
    }

    public static OfflineMapSummary Summary(GeoBounds area, string layerId, int maxZoom)
    {
        var p = Profile(layerId);
        maxZoom = Math.Clamp(maxZoom, p.MinZoom, p.MaxZoom);
        long count = 0, bytes = p.SupportingBytes;
        foreach (var z in DownloadZooms(p, maxZoom))
        {
            var tiles = TileCount(area, z);
            count += tiles;
            bytes += tiles * (p.BytesPerTile.TryGetValue(z, out var b) ? b : p.DefaultBytesPerTile);
        }
        return new OfflineMapSummary(p.LayerId, maxZoom, count, bytes, p.TileLimit);
    }

    /// <summary>The most detailed zoom whose download stays within the layer's limit (at least the lowest zoom).</summary>
    public static int MostDetailedPermitted(GeoBounds area, string layerId)
    {
        var p = Profile(layerId);
        for (var z = p.MaxZoom; z > p.MinZoom; z--)
            if (Summary(area, layerId, z).PermitsDownload) return z;
        return p.MinZoom;
    }

    /// <summary>"Overview (zoom 4)" … "Detailed (zoom 15)": the name of a detail level, in map zooms.</summary>
    public static string DetailName(int mapZoom) => (mapZoom - 1) switch
    {
        <= 4 => L.F("Overview (zoom {0})", mapZoom),
        <= 8 => L.F("Regional (zoom {0})", mapZoom),
        <= 11 => L.F("Local (zoom {0})", mapZoom),
        _ => L.F("Detailed (zoom {0})", mapZoom),
    };

    // MARK: Tiles

    /// <summary>Tile columns covering the area at zoom <paramref name="z"/> (each column once, wrapping past 180°).</summary>
    public static IEnumerable<int> Columns(GeoBounds area, int z)
    {
        var n = 1 << z;
        var (first, count) = ColumnSpan(area, z);
        for (long i = 0; i < count; i++) yield return (int)(((first + i) % n + n) % n);
    }

    private static (long First, long Count) ColumnSpan(GeoBounds area, int z)
    {
        var n = 1L << z;
        var width = Math.Min(area.LongitudeSpan, 360);
        var first = (long)Math.Floor((area.West + 180) / 360 * n);
        var last = (long)Math.Floor((area.West + width + 180) / 360 * n);
        return (first, Math.Min(n, last - first + 1));
    }

    /// <summary>First and last tile row covering the area at zoom <paramref name="z"/>.</summary>
    public static (int First, int Last) Rows(GeoBounds area, int z)
    {
        var a = TileY(area.North, z);
        var b = TileY(area.South, z);
        return (Math.Min(a, b), Math.Max(a, b));
    }

    public static long TileCount(GeoBounds area, int z)
    {
        var (y0, y1) = Rows(area, z);
        return ColumnSpan(area, z).Count * (y1 - y0 + 1);
    }

    public static IEnumerable<(int Z, int X, int Y)> Tiles(GeoBounds area, IEnumerable<int> zooms)
    {
        foreach (var z in zooms)
        {
            var (y0, y1) = Rows(area, z);
            foreach (var x in Columns(area, z))
                for (var y = y0; y <= y1; y++)
                    yield return (z, x, y);
        }
    }

    private static int TileY(double latitude, int z)
    {
        var n = 1 << z;
        var lat = Math.Clamp(latitude, -GeoBounds.MaxLatitude, GeoBounds.MaxLatitude) * Math.PI / 180;
        var y = Math.Floor((1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * n);
        return (int)Math.Clamp(y, 0, n - 1);
    }
}

/// <summary>An offline map download that hasn't finished (kept in the settings so it can be resumed after a restart).</summary>
public sealed class OfflineMapDownloadJob
{
    public string LayerId { get; set; } = "Standard";
    public double North { get; set; }
    public double South { get; set; }
    public double West { get; set; }
    public double East { get; set; }
    public int MaxZoom { get; set; }
    /// <summary>The user stopped it (or it ended with failed tiles): wait for Resume instead of continuing by itself.</summary>
    public bool Paused { get; set; }
    /// <summary>Tiles that couldn't be downloaded the last time it ran.</summary>
    public int FailedTiles { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public GeoBounds Area
    {
        get => new(North, South, West, East);
        set { North = value.North; South = value.South; West = value.West; East = value.East; }
    }
}

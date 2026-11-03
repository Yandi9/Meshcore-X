using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MC1.Core.Maps;
using MC1.Core.Services;

namespace MC1.Windows.Services;

/// <param name="IsDark">A dark basemap: markers' labels are drawn dark on it whatever the app's theme.</param>
public sealed record MapLayerInfo(string Id, string Name, string UrlTemplate, string Attribution, int MaxZoom, string[] Subdomains, int MaxOfflineZoom,
    bool IsVector = false, string? Background = null, bool IsDark = false)
{
    /// <summary>What can be saved of this layer for offline use.</summary>
    public OfflineMapProfile Offline => OfflineMapPolicy.Profile(Id);
}

/// <summary>Raster map tiles with a memory LRU + disk cache (%APPDATA%\MeshCore\tiles).</summary>
public sealed class TileService
{
    public static TileService Instance { get; } = new();

    public static readonly IReadOnlyList<MapLayerInfo> Layers =
    [
        new("Standard", "Standard", "https://tile.openstreetmap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors", 19, [], 16),
        // Vector tiles drawn on this PC with OpenFreeMap's "dark" style (tiles.openfreemap.org/styles/dark). Offline it
        // keeps the vector data (zoom 14 at most) and draws every zoom from it.
        new("Dark", "Dark", VectorTiles.OpenFreeMapSource.StyleUrl, "OpenFreeMap © OpenMapTiles Data from OpenStreetMap", 19, [], OfflineMapPolicy.VectorDataMaxZoom + 1,
            IsVector: true, Background: "#0C0C0C", IsDark: true),
        new("Satellite", "Satellite", "https://server.arcgisonline.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}", "Imagery © Esri, Maxar, Earthstar Geographics", 19, [], 17),
        // View only: no offline downloads (OpenTopoMap's terms don't clearly allow them); areas saved earlier still show.
        new("Topo", "Topographic", "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors, SRTM · © OpenTopoMap (CC-BY-SA)", 17, ["a", "b", "c"], 0),
    ];

    public static MapLayerInfo Layer(string? id) => Layers.FirstOrDefault(l => l.Id == id) ?? Layers[0];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(6, 6);
    private readonly ConcurrentDictionary<string, Task> _inflight = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _memory = new();
    private readonly LinkedList<(string Key, Bitmap Bitmap)> _lru = new();
    private readonly object _lock = new();
    private const int MemoryCapacity = 400;
    private readonly ConcurrentDictionary<string, DateTime> _failed = new();

    private TileService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCore-Windows/1.0 (+https://github.com/meshcore-dev)");
    }

    /// <summary>Raised on the UI thread when a tile finished loading.</summary>
    public event Action? TileLoaded;

    public bool OfflineOnly { get; set; }

    /// <summary>
    /// The cache folder of a layer's map images. The vector map draws its place names in the app's language, so its
    /// images are kept per language ("Dark-es"); the vector data they're drawn from is shared.
    /// </summary>
    public static string CacheLayer(string layer)
    {
        if (Layer(layer) is not { IsVector: true } l) return layer;
        var lang = MapLabelLocalizer.NameLanguage(L.Code);
        return lang == MapLabelLocalizer.DefaultLanguageCode ? l.Id : $"{l.Id}-{lang}";
    }

    private static string Key(string layer, int z, int x, int y) => $"{CacheLayer(layer)}/{z}/{x}/{y}";

    private static string CachePath(string layer, int z, int x, int y) => Path.Combine(AppPaths.TileCache, CacheLayer(layer), z.ToString(), x.ToString(), y + ".tile");

    /// <summary>Writes a cache file through a temporary file, so an interrupted write never leaves half a tile behind.</summary>
    internal static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private string Url(MapLayerInfo l, int z, int x, int y)
    {
        var url = l.UrlTemplate.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
        if (l.Subdomains.Length > 0) url = url.Replace("{s}", l.Subdomains[(x + y) % l.Subdomains.Length]);
        return url;
    }

    /// <summary>Returns the tile if it is in memory; otherwise starts loading it and returns null.</summary>
    public Bitmap? GetTile(string layer, int z, int x, int y)
    {
        var key = Key(layer, z, x, y);
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }
        if (_failed.TryGetValue(key, out var when) && DateTime.UtcNow - when < TimeSpan.FromMinutes(2)) return null;
        _inflight.GetOrAdd(key, _ => Task.Run(() => LoadAsync(layer, z, x, y, key)));
        return null;
    }

    /// <summary>Returns a lower-zoom ancestor that is already in memory, used as a blurry placeholder.</summary>
    public (Bitmap Bitmap, int Dz, int Ox, int Oy)? GetFallback(string layer, int z, int x, int y)
    {
        for (var dz = 1; dz <= 4 && z - dz >= 0; dz++)
        {
            var px = x >> dz;
            var py = y >> dz;
            lock (_lock)
            {
                if (_memory.TryGetValue(Key(layer, z - dz, px, py), out var node))
                    return (node.Value.Bitmap, dz, x - (px << dz), y - (py << dz));
            }
        }
        return null;
    }

    private async Task LoadAsync(string layer, int z, int x, int y, string key)
    {
        try
        {
            var bytes = await FetchBytesAsync(layer, z, x, y, OfflineOnly).ConfigureAwait(false);
            if (bytes is null) { _failed[key] = DateTime.UtcNow; return; }
            Bitmap bmp;
            try
            {
                using var ms = new MemoryStream(bytes);
                bmp = new Bitmap(ms);
            }
            catch
            {
                // A damaged cache file (e.g. from an older interrupted download): drop it so it's fetched again.
                try { File.Delete(CachePath(layer, z, x, y)); } catch { /* in use */ }
                throw;
            }
            lock (_lock)
            {
                if (!_memory.ContainsKey(key))
                {
                    var node = _lru.AddFirst((key, bmp));
                    _memory[key] = node;
                    while (_lru.Count > MemoryCapacity)
                    {
                        var last = _lru.Last!;
                        _lru.RemoveLast();
                        _memory.Remove(last.Value.Key);
                    }
                }
            }
            Dispatcher.UIThread.Post(() => TileLoaded?.Invoke(), DispatcherPriority.Background);
        }
        catch
        {
            _failed[key] = DateTime.UtcNow;
        }
        finally { _inflight.TryRemove(key, out _); }
    }

    private async Task<byte[]?> FetchBytesAsync(string layer, int z, int x, int y, bool offlineOnly, CancellationToken ct = default)
    {
        var path = CachePath(layer, z, x, y);
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (Layer(layer) is { IsVector: true })
        {
            // Drawn here from vector tiles (cached separately), then kept like any other tile.
            var png = await VectorTiles.OpenFreeMapSource.Dark.RenderTileAsync(z, x, y, offlineOnly, ct, L.Code).ConfigureAwait(false);
            if (png is null) return null;
            await WriteAtomicAsync(path, png, ct).ConfigureAwait(false);
            return png;
        }
        if (offlineOnly) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var l = Layer(layer);
            using var resp = await _http.GetAsync(Url(l, z, x, y), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            await WriteAtomicAsync(path, bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Saves an area of a layer for offline use, from zoom 1 up to <paramref name="maxZoom"/>. Files already saved are
    /// skipped, so running it again after an interruption continues where it stopped. A vector layer saves its vector
    /// data (drawn later, at any zoom). Returns how many files couldn't be downloaded.
    /// </summary>
    public async Task<int> DownloadAreaAsync(string layer, GeoBounds area, int maxZoom, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var info = Layer(layer);
        var profile = info.Offline;
        if (!profile.AllowsDownload) throw new InvalidOperationException(L.T("This map layer can't be downloaded for offline use."));
        var jobs = OfflineMapPolicy.Tiles(area, OfflineMapPolicy.DownloadZooms(profile, maxZoom)).ToList();
        var total = jobs.Count;
        var done = 0;
        var failed = 0;
        if (info.IsVector) await VectorTiles.OpenFreeMapSource.Dark.PrepareOfflineAsync(ct).ConfigureAwait(false);
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (j, token) =>
        {
            try
            {
                var ok = info.IsVector
                    ? await VectorTiles.OpenFreeMapSource.Dark.SaveVectorTileAsync(j.Z, j.X, j.Y, token).ConfigureAwait(false)
                    : File.Exists(CachePath(layer, j.Z, j.X, j.Y)) || await FetchBytesAsync(layer, j.Z, j.X, j.Y, false, token).ConfigureAwait(false) is not null;
                if (!ok) Interlocked.Increment(ref failed);
            }
            catch (OperationCanceledException) { throw; }
            catch { Interlocked.Increment(ref failed); }
            var d = Interlocked.Increment(ref done);
            if (d % 8 == 0 || d == total) progress?.Report((d, total));
        }).ConfigureAwait(false);
        return failed;
    }

    /// <summary>Free space on the drive holding the map cache, or null when unknown.</summary>
    public static long? AvailableBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppPaths.TileCache));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    public static long CacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(AppPaths.TileCache)) return 0;
            return new DirectoryInfo(AppPaths.TileCache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    public void ClearCache()
    {
        lock (_lock) { _memory.Clear(); _lru.Clear(); }
        _failed.Clear();
        VectorTiles.OpenFreeMapSource.Dark.Reset();
        try { if (Directory.Exists(AppPaths.TileCache)) Directory.Delete(AppPaths.TileCache, true); } catch { /* in use */ }
    }
}

/// <summary>Web-Mercator helpers (256 px tiles).</summary>
public static class MapMath
{
    public const double TileSize = 256;
    public const double MaxLatitude = 85.05112878;

    public static (double X, double Y) ToPixel(double lat, double lon, double zoom)
    {
        lat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        var scale = TileSize * Math.Pow(2, zoom);
        var x = (lon + 180.0) / 360.0 * scale;
        var sin = Math.Sin(lat * Math.PI / 180);
        var y = (0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI)) * scale;
        return (x, y);
    }

    public static (double Lat, double Lon) FromPixel(double x, double y, double zoom)
    {
        var scale = TileSize * Math.Pow(2, zoom);
        var lon = x / scale * 360.0 - 180.0;
        var n = Math.PI - 2.0 * Math.PI * y / scale;
        var lat = 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        return (lat, lon);
    }

    public static (int X, int Y) TileXY(double lat, double lon, int z)
    {
        var (px, py) = ToPixel(lat, lon, z);
        var max = (1 << z) - 1;
        return (Math.Clamp((int)(px / TileSize), 0, max), Math.Clamp((int)(py / TileSize), 0, max));
    }

    /// <summary>Zoom that fits the bounds into a viewport.</summary>
    public static double FitZoom(double north, double south, double west, double east, double width, double height, double padding = 60)
    {
        for (var z = 18.0; z >= 1; z -= 0.25)
        {
            var (x0, y0) = ToPixel(north, west, z);
            var (x1, y1) = ToPixel(south, east, z);
            if (Math.Abs(x1 - x0) + padding * 2 <= width && Math.Abs(y1 - y0) + padding * 2 <= height) return z;
        }
        return 1;
    }
}

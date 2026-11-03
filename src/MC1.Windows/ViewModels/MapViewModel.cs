using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Maps;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MC1.Windows.Controls;
using MC1.Windows.Services;
using MeshCore;

namespace MC1.Windows.ViewModels;

/// <summary>A chip over the map for one active advanced filter (click to edit, ✕ to remove).</summary>
public sealed record MapFilterChip(MapFilterDimension Dimension, string Text)
{
    public string IconKey => Dimension == MapFilterDimension.LastHeard ? "Icon.ClockOutline" : "Icon.Hop";
    public string RemoveTip => L.F("Remove filter: {0}", Text);
}

/// <summary>A detail level offered for an offline download ("Local (zoom 12)").</summary>
public sealed record MapDetailOption(int Zoom, string Name);

public sealed partial class MapViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly Debouncer _reload;
    private readonly Debouncer _persist;
    private MapMarker? _pin;
    private List<MapMarker> _pathMarkers = new();
    private CancellationTokenSource? _downloadCts;
    private readonly Debouncer _estimate;
    /// <summary>The running download is being thrown away (offline maps cleared): don't keep it as paused.</summary>
    private bool _discardDownload;
    private readonly Avalonia.Threading.DispatcherTimer _filterClock;
    /// <summary>Rolling "last heard" windows are re-checked on this cadence, so nodes age out without new traffic.</summary>
    private static readonly TimeSpan FilterClockInterval = TimeSpan.FromSeconds(60);

    public MapViewModel(MainWindowViewModel main)
    {
        _main = main;
        var s = Core.Settings.Current;
        _centerLatitude = s.MapLatitude;
        _centerLongitude = s.MapLongitude;
        _zoom = s.MapZoom;
        _layerId = TileService.Layer(s.MapLayer).Id;
        _showChat = s.MapShowChat;
        _showRepeaters = s.MapShowRepeaters;
        _showRooms = s.MapShowRooms;
        _showDiscovered = s.MapShowDiscovered;
        _filter = (s.MapFilter ?? new MapAdvancedFilter()).Sanitized();
        _reload = new Debouncer(TimeSpan.FromMilliseconds(300), Reload);
        _persist = new Debouncer(TimeSpan.FromSeconds(1), Persist);
        _estimate = new Debouncer(TimeSpan.FromMilliseconds(250), UpdateEstimate);
        _filterClock = new Avalonia.Threading.DispatcherTimer { Interval = FilterClockInterval };
        _filterClock.Tick += (_, _) => Reload();
        Track(() => _filterClock.Stop());
        OnData(c => { if (c.Kind is DataKind.Contacts or DataKind.Discovered or DataKind.Radio) _reload.Trigger(); });
        RebuildFilterChips();
        UpdateFilterClock();
        LoadPendingDownload();
    }

    public ObservableCollection<MapMarker> Markers { get; } = new();
    public ObservableCollection<MapPolyline> Lines { get; } = new();
    /// <summary>
    /// The map layers for the layer picker, with their names in the app's language. The <see cref="MapLayerInfo.Id"/> (what's
    /// stored in Settings.MapLayer and bound as the picker's value) stays the English id.
    /// </summary>
    public IReadOnlyList<MapLayerInfo> Layers { get; } = TileService.Layers.Select(l => l with { Name = LayerName(l) }).ToList();

    private static string LayerName(MapLayerInfo layer) => layer.Id switch
    {
        "Standard" => L.T("Standard"),
        "Dark" => L.T("Dark"),
        "Satellite" => L.T("Satellite"),
        "Topo" => L.T("Topographic"),
        _ => layer.Name,
    };
    public bool Metric => Core.Settings.Current.UseMetricUnits;

    /// <summary>Provided by the view: visible bounds (north, south, west, east) and viewport size.</summary>
    public Func<(double North, double South, double West, double East)>? VisibleBounds { get; set; }
    /// <summary>Provided by the view: the area inside the offline-download frame.</summary>
    public Func<GeoBounds>? SelectionArea { get; set; }
    public Func<(double Width, double Height)>? ViewportSize { get; set; }

    [ObservableProperty] private double _centerLatitude;
    [ObservableProperty] private double _centerLongitude;
    [ObservableProperty] private double _zoom;
    [ObservableProperty] private string _layerId;
    [ObservableProperty] private bool _showChat;
    [ObservableProperty] private bool _showRepeaters;
    [ObservableProperty] private bool _showRooms;
    [ObservableProperty] private bool _showDiscovered;
    [ObservableProperty] private bool _showLabels = true;
    [ObservableProperty] private MapMarker? _selectedMarker;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _pointerText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasPath;
    [ObservableProperty] private string? _pathSummary;

    // selection panel
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _selName = "";
    [ObservableProperty] private string _selType = "";
    [ObservableProperty] private string _selDetail = "";
    [ObservableProperty] private string _selCoords = "";
    [ObservableProperty] private bool _selIsContact;
    [ObservableProperty] private bool _selIsDiscovered;
    [ObservableProperty] private bool _selCanMessage;
    [ObservableProperty] private bool _selCanManage;

    // advanced filters
    [ObservableProperty] private MapAdvancedFilter _filter;
    public ObservableCollection<MapFilterChip> FilterChips { get; } = new();
    [ObservableProperty] private bool _hasFilterChips;

    // offline download
    [ObservableProperty] private bool _isDownloadPanelOpen;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadText = "";
    /// <summary>The detail (highest zoom) to save, in map zooms.</summary>
    [ObservableProperty] private int _downloadMaxZoom = 14;
    public ObservableCollection<MapDetailOption> DetailOptions { get; } = new();
    [ObservableProperty] private MapDetailOption? _selectedDetail;
    [ObservableProperty] private string _downloadEstimate = "";
    [ObservableProperty] private string _downloadTiles = "";
    /// <summary>Why the area can't be (or should think twice before being) downloaded; null when fine.</summary>
    [ObservableProperty] private string? _downloadWarning;
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _layerAllowsDownload = true;
    [ObservableProperty] private string _cacheSize = "";
    /// <summary>The frame of the area to save is drawn (panel open, nothing downloading).</summary>
    [ObservableProperty] private bool _showSelection;
    /// <summary>The area being (or last) downloaded, outlined on the map while the panel is open.</summary>
    [ObservableProperty] private GeoBounds? _downloadArea;
    /// <summary>A paused or interrupted download that can be resumed.</summary>
    [ObservableProperty] private bool _hasPendingDownload;
    [ObservableProperty] private string _pendingDownloadText = "";

    partial void OnCenterLatitudeChanged(double value) { _persist.Trigger(); if (IsDownloadPanelOpen) _estimate.Trigger(); }
    partial void OnCenterLongitudeChanged(double value) { _persist.Trigger(); if (IsDownloadPanelOpen) _estimate.Trigger(); }
    partial void OnZoomChanged(double value) { _persist.Trigger(); if (IsDownloadPanelOpen) _estimate.Trigger(); }
    partial void OnLayerIdChanged(string value)
    {
        _persist.Trigger();
        if (IsDownloadPanelOpen) RefreshDetailOptions();
    }
    partial void OnShowChatChanged(bool value) { _persist.Trigger(); Reload(); }
    partial void OnShowRepeatersChanged(bool value) { _persist.Trigger(); Reload(); }
    partial void OnShowRoomsChanged(bool value) { _persist.Trigger(); Reload(); }
    partial void OnShowDiscoveredChanged(bool value) { _persist.Trigger(); Reload(); }
    partial void OnDownloadMaxZoomChanged(int value) => UpdateEstimate();
    partial void OnSelectedDetailChanged(MapDetailOption? value) { if (value is not null) DownloadMaxZoom = value.Zoom; }
    partial void OnIsDownloadPanelOpenChanged(bool value) => UpdateSelectionFrame();
    partial void OnIsDownloadingChanged(bool value) { UpdateSelectionFrame(); UpdateEstimate(); }

    partial void OnFilterChanged(MapAdvancedFilter value)
    {
        _persist.Trigger();
        RebuildFilterChips();
        UpdateFilterClock();
        Reload();
    }

    private void Persist() => Core.Settings.Update(s =>
    {
        s.MapLatitude = CenterLatitude;
        s.MapLongitude = CenterLongitude;
        s.MapZoom = Zoom;
        s.MapLayer = LayerId;
        s.MapShowChat = ShowChat;
        s.MapShowRepeaters = ShowRepeaters;
        s.MapShowRooms = ShowRooms;
        s.MapShowDiscovered = ShowDiscovered;
        s.MapFilter = Filter;
    });

    private bool _shownOnce;

    public void OnShown()
    {
        Reload();
        OnPropertyChanged(nameof(Metric));
        ResumeInterruptedDownload();
        if (!_shownOnce && Markers.Count > 0)
        {
            _shownOnce = true;
            // First visit this session: if nothing is in view (e.g. the default world view), frame the nodes.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (VisibleBounds?.Invoke() is { } b && !Markers.Any(m => m.Latitude <= b.North && m.Latitude >= b.South && m.Longitude >= b.West && m.Longitude <= b.East)) FitAll();
                else if (Zoom < 6) FitAll();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    public void Reload()
    {
        var selId = SelectedMarker?.Id;
        var list = new List<MapMarker>();
        var contacts = Core.Contacts.GetAll();
        var now = DateTimeOffset.UtcNow;
        var types = new MapNodeFilter.TypeToggles(ShowChat, ShowRepeaters, ShowRooms, ShowDiscovered);
        // With an advanced filter on, the summary also counts what it hides.
        var unfiltered = 0;
        foreach (var c in contacts.Where(c => c.HasLocation && !c.IsBlocked))
        {
            if (!MapNodeFilter.AllowsType(c.ContactType, types)) continue;
            unfiltered++;
            if (!MapNodeFilter.Shows(c, types, Filter, now)) continue;
            list.Add(new MapMarker
            {
                Id = "c:" + c.Id,
                Latitude = c.Latitude,
                Longitude = c.Longitude,
                Label = c.DisplayName,
                Kind = c.ContactType switch { ContactType.Repeater => MapMarkerKind.Repeater, ContactType.Room => MapMarkerKind.Room, ContactType.Sensor => MapMarkerKind.Sensor, _ => MapMarkerKind.Chat },
                Color = Formatters.ContactColor(c.ContactType),
                Badge = c.IsFavorite ? "★" : null,
                Emoji = Formatters.AvatarEmoji(c.DisplayName),
                Tag = c,
            });
        }
        var known = contacts.Select(c => c.PublicKeyHex).ToHashSet();
        if (ShowDiscovered)
        {
            foreach (var d in Core.Contacts.GetDiscovered().Where(d => d.HasLocation && !known.Contains(d.PublicKeyHex)))
            {
                unfiltered++;
                if (!MapNodeFilter.Shows(d, types, Filter, now)) continue;
                list.Add(new MapMarker
                {
                    Id = "d:" + d.Id,
                    Latitude = d.Latitude,
                    Longitude = d.Longitude,
                    Label = d.Name,
                    Kind = MapMarkerKind.Discovered,
                    Color = Formatters.ContactColor(d.ContactType),
                    Emoji = Formatters.AvatarEmoji(d.Name),
                    Tag = d,
                });
            }
        }
        if (Core.SelfInfo is { } self && (self.Latitude != 0 || self.Longitude != 0))
        {
            list.Add(new MapMarker { Id = "self", Latitude = self.Latitude, Longitude = self.Longitude, Label = L.F("{0} (you)", self.Name), Kind = MapMarkerKind.Self, Color = "#2463EB", Tag = self });
        }
        foreach (var pm in _pathMarkers)
        {
            // A hop that sits on a node already shown only needs its sequence badge.
            var dup = list.Any(m => Math.Abs(m.Latitude - pm.Latitude) < 1e-6 && Math.Abs(m.Longitude - pm.Longitude) < 1e-6);
            list.Add(dup ? new MapMarker { Id = pm.Id, Latitude = pm.Latitude, Longitude = pm.Longitude, Label = "", Kind = pm.Kind, Color = pm.Color, Badge = pm.Badge } : pm);
        }
        if (_pin is not null) list.Add(_pin);
        Markers.Clear();
        foreach (var m in list) Markers.Add(m);
        var located = list.Count(m => m.Kind is not (MapMarkerKind.Self or MapMarkerKind.Pin or MapMarkerKind.Hop));
        Summary = Filter.IsActive
            ? L.F("{0} of {1} nodes with location match the filters", located, unfiltered)
            : L.Plural(located, "{0} node with location", "{0} nodes with location");
        if (selId is not null) SelectedMarker = list.FirstOrDefault(m => m.Id == selId);
    }

    partial void OnSelectedMarkerChanged(MapMarker? value)
    {
        HasSelection = value is not null;
        SelIsContact = value?.Tag is ContactRecord;
        SelIsDiscovered = value?.Tag is DiscoveredNodeRecord;
        SelCanMessage = value?.Tag is ContactRecord { ContactType: ContactType.Chat or ContactType.Room or ContactType.Sensor };
        SelCanManage = value?.Tag is ContactRecord { ContactType: ContactType.Repeater or ContactType.Room or ContactType.Sensor };
        if (value is null) return;
        SelName = value.Label;
        SelCoords = Formatters.Coordinates(value.Latitude, value.Longitude);
        var distance = DistanceFromSelf(value.Latitude, value.Longitude);
        switch (value.Tag)
        {
            case ContactRecord c:
                SelType = Formatters.TypeName(c.ContactType);
                SelDetail = $"{L.F("Last advert {0}", Formatters.AgoSeconds(c.LastAdvert))} · {c.RouteDescription}" + (distance is { } d ? " · " + L.F("{0} away", Formatters.Distance(d)) : "");
                break;
            case DiscoveredNodeRecord n:
                SelType = Formatters.TypeName(n.ContactType) + " · " + L.T("not in contacts");
                SelDetail = L.F("Heard {0}", Formatters.AgoMs(n.LastHeard)) + (distance is { } d2 ? " · " + L.F("{0} away", Formatters.Distance(d2)) : "");
                break;
            case SelfInfo:
                SelType = L.T("Your radio");
                SelDetail = L.T("Position advertised by your companion radio");
                break;
            default:
                SelType = value.Kind == MapMarkerKind.Hop ? L.T("Path hop") : L.T("Location");
                SelDetail = distance is { } d3 ? L.F("{0} from your radio", Formatters.Distance(d3)) : "";
                break;
        }
    }

    private (double Lat, double Lon)? SelfLocation =>
        Core.SelfInfo is { } s && (s.Latitude != 0 || s.Longitude != 0) ? (s.Latitude, s.Longitude) : null;

    private double? DistanceFromSelf(double lat, double lon) =>
        SelfLocation is { } me ? RfCalculator.Distance(me.Lat, me.Lon, lat, lon) : null;

    public void CenterOn(double lat, double lon, double zoom, string label)
    {
        _pin = new MapMarker { Id = "pin", Latitude = lat, Longitude = lon, Label = label, Kind = MapMarkerKind.Pin, Color = "#E5484D" };
        Reload();
        CenterLatitude = lat;
        CenterLongitude = lon;
        Zoom = zoom;
        SelectedMarker = Markers.FirstOrDefault(m => Math.Abs(m.Latitude - lat) < 1e-7 && Math.Abs(m.Longitude - lon) < 1e-7 && m.Kind != MapMarkerKind.Pin) ?? _pin;
    }

    /// <summary>Draws a route through the given repeater hashes (ending at this radio).</summary>
    public void ShowPath(List<byte[]> hops)
    {
        _pathMarkers = new();
        var points = new List<(double Lat, double Lon)>();
        var unresolved = 0;
        for (var i = 0; i < hops.Count; i++)
        {
            var hash = hops[i];
            if (Core.Contacts.ResolveHopLocation(hash) is { } loc)
            {
                points.Add(loc);
                _pathMarkers.Add(new MapMarker
                {
                    Id = $"hop:{i}",
                    Latitude = loc.Lat,
                    Longitude = loc.Lon,
                    Label = Core.Contacts.ResolveHopName(hash) ?? Convert.ToHexString(hash),
                    Kind = MapMarkerKind.Hop,
                    Color = "#7C3AED",
                    Badge = (i + 1).ToString(),
                });
            }
            else unresolved++;
        }
        if (SelfLocation is { } me) points.Add(me);
        Lines.Clear();
        if (points.Count >= 2) Lines.Add(new MapPolyline { Points = points, Color = "#7C3AED", Thickness = 3.5 });
        HasPath = true;
        PathSummary = L.Plural(hops.Count, "{0} hop", "{0} hops") + (unresolved > 0 ? " · " + L.F("{0} without a known location", unresolved) : "");
        Reload();
        if (points.Count > 0) FitPoints(points);
    }

    /// <summary>Shows a straight A→B link (line of sight tool).</summary>
    public void ShowSegment((double Lat, double Lon, string Label) a, (double Lat, double Lon, string Label) b)
    {
        _pathMarkers =
        [
            new MapMarker { Id = "seg:a", Latitude = a.Lat, Longitude = a.Lon, Label = a.Label, Kind = MapMarkerKind.Hop, Color = "#E5484D", Badge = "A" },
            new MapMarker { Id = "seg:b", Latitude = b.Lat, Longitude = b.Lon, Label = b.Label, Kind = MapMarkerKind.Hop, Color = "#E5484D", Badge = "B" },
        ];
        Lines.Clear();
        Lines.Add(new MapPolyline { Points = [(a.Lat, a.Lon), (b.Lat, b.Lon)], Color = "#E5484D", Thickness = 3, Dashed = true });
        HasPath = true;
        PathSummary = $"{a.Label} → {b.Label} · {Formatters.Distance(RfCalculator.Distance(a.Lat, a.Lon, b.Lat, b.Lon))}";
        Reload();
        FitPoints([(a.Lat, a.Lon), (b.Lat, b.Lon)]);
    }

    [RelayCommand]
    private void ClearPath()
    {
        _pathMarkers.Clear();
        Lines.Clear();
        HasPath = false;
        PathSummary = null;
        _pin = null;
        Reload();
    }

    private void FitPoints(IReadOnlyCollection<(double Lat, double Lon)> pts)
    {
        if (pts.Count == 0) return;
        var north = pts.Max(p => p.Lat);
        var south = pts.Min(p => p.Lat);
        var west = pts.Min(p => p.Lon);
        var east = pts.Max(p => p.Lon);
        CenterLatitude = (north + south) / 2;
        CenterLongitude = (west + east) / 2;
        var (w, h) = ViewportSize?.Invoke() ?? (900, 600);
        Zoom = pts.Count == 1 ? 13 : Math.Min(15, MapMath.FitZoom(north, south, west, east, w, h, 110));
    }

    [RelayCommand]
    private void FitAll() => FitPoints(Markers.Select(m => (m.Latitude, m.Longitude)).ToList());

    [RelayCommand]
    private void CenterOnMe()
    {
        if (SelfLocation is { } me)
        {
            CenterLatitude = me.Lat;
            CenterLongitude = me.Lon;
            Zoom = Math.Max(Zoom, 12);
        }
        else _main.ShowToast(L.T("No location"), L.T("Your radio has no position set. Set one on the Radio page."));
    }

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(19, Math.Round(Zoom) + 1);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(1, Math.Round(Zoom) - 1);
    [RelayCommand] private void SetLayer(string id) => LayerId = id;
    [RelayCommand] private void CloseSelection() => SelectedMarker = null;

    [RelayCommand]
    private void RunSearch()
    {
        var q = Search.Trim();
        if (q.Length == 0) return;
        var hit = Markers.FirstOrDefault(m => m.Label.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (hit is null) { _main.ShowToast(L.T("Not found"), L.F("No node named \"{0}\" has a location.", q)); return; }
        CenterLatitude = hit.Latitude;
        CenterLongitude = hit.Longitude;
        Zoom = Math.Max(Zoom, 13);
        SelectedMarker = hit;
    }

    [RelayCommand]
    private void MessageSelected()
    {
        if (SelectedMarker?.Tag is not ContactRecord c) return;
        if (c.ContactType == ContactType.Room) { _ = RoomLoginFlow.JoinAsync(c); return; }
        _main.Navigate(Page.Chats);
        _main.Chats.OpenByKeyForce(MessageService.DmKey(c.Id));
    }

    [RelayCommand]
    private void ShowSelectedContact()
    {
        if (SelectedMarker?.Tag is not ContactRecord c) return;
        _main.Navigate(Page.Contacts);
        _main.Contacts.Select(c.Id);
    }

    [RelayCommand]
    private async Task ManageSelected()
    {
        if (SelectedMarker?.Tag is not ContactRecord c) return;
        if (!Core.IsConnected) { await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio first.")); return; }
        await AppHost.Dialogs.ShowDialog(new NodeManagementViewModel(c), L.F("Manage {0}", c.DisplayName), 920, 740);
    }

    [RelayCommand]
    private async Task AddSelected()
    {
        if (SelectedMarker?.Tag is not DiscoveredNodeRecord n) return;
        await Guard(async () =>
        {
            var rec = await Core.Contacts.AddDiscoveredAsync(n);
            _main.ShowToast(L.T("Contact added"), L.F("{0} was added to your radio.", rec.DisplayName));
            Reload();
        }, L.T("Add contact"));
    }

    [RelayCommand]
    private async Task CopySelectedCoordinates()
    {
        if (SelectedMarker is not { } m) return;
        await AppHost.Dialogs.CopyToClipboard(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{m.Latitude:0.000000}, {m.Longitude:0.000000}"));
        _main.ShowToast(L.T("Copied"), L.T("Coordinates copied to clipboard."));
    }

    [RelayCommand]
    private void LineOfSightToSelected()
    {
        if (SelectedMarker is not { } m) return;
        _main.Navigate(Page.Tools);
        _main.Tools.OpenLineOfSight(SelfLocation, (m.Latitude, m.Longitude), m.Label);
    }

    [RelayCommand]
    private void TraceToSelected()
    {
        if (SelectedMarker?.Tag is not ContactRecord c) return;
        _main.Navigate(Page.Tools);
        _main.Tools.OpenTraceFor(c);
    }

    public void LineOfSightTo(double lat, double lon)
    {
        _main.Navigate(Page.Tools);
        _main.Tools.OpenLineOfSight(SelfLocation, (lat, lon), L.T("Map point"));
    }

    public async Task SetRadioLocation(double lat, double lon)
    {
        if (!Core.IsConnected) { await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio first.")); return; }
        if (!await AppHost.Dialogs.Confirm(L.T("Set radio location?"), L.F("Set your radio's advertised position to {0}?", Formatters.Coordinates(lat, lon)), L.T("Set location"))) return;
        await Guard(async () =>
        {
            await Core.Device.SetLocationAsync(lat, lon);
            _main.ShowToast(L.T("Location updated"), L.T("Send an advert so others see your new position."));
            Reload();
        }, L.T("Set location"));
    }

    // ---- advanced filters ----

    private void RebuildFilterChips()
    {
        FilterChips.Clear();
        foreach (var d in Filter.ActiveDimensions) FilterChips.Add(new MapFilterChip(d, Filter.ChipText(d)));
        HasFilterChips = FilterChips.Count > 0;
    }

    private void UpdateFilterClock()
    {
        if (Filter.LastHeard.IsActive) { if (!_filterClock.IsEnabled) _filterClock.Start(); }
        else _filterClock.Stop();
    }

    [RelayCommand]
    private async Task ShowAdvancedFilters() =>
        await AppHost.Dialogs.ShowDialog(new MapFiltersViewModel(this), L.T("Advanced Filters"), 500, 660);

    [RelayCommand]
    private void RemoveFilter(MapFilterChip chip) => Filter = Filter.Cleared(chip.Dimension);

    // ---- offline maps ----

    private string LayerTitle(string id) => Layers.FirstOrDefault(l => l.Id == id)?.Name ?? id;

    [RelayCommand]
    private void ToggleDownloadPanel()
    {
        IsDownloadPanelOpen = !IsDownloadPanelOpen;
        if (IsDownloadPanelOpen)
        {
            RefreshDetailOptions();
            RefreshCacheSize();
        }
    }

    private void RefreshCacheSize() =>
        _ = Task.Run(TileService.CacheSizeBytes).ContinueWith(t => Ui(() => CacheSize = Formatters.Bytes(t.Result)));

    private void UpdateSelectionFrame()
    {
        ShowSelection = IsDownloadPanelOpen && !IsDownloading && LayerAllowsDownload;
        DownloadArea = IsDownloadPanelOpen && (IsDownloading || HasPendingDownload) ? Core.Settings.Current.OfflineMapDownload?.Area : null;
    }

    /// <summary>Detail levels of the current layer; picks the most detailed one the framed area allows.</summary>
    private void RefreshDetailOptions()
    {
        var profile = OfflineMapPolicy.Profile(LayerId);
        LayerAllowsDownload = profile.AllowsDownload;
        DetailOptions.Clear();
        if (profile.AllowsDownload)
            for (var z = profile.MinZoom; z <= profile.MaxZoom; z++) DetailOptions.Add(new MapDetailOption(z, OfflineMapPolicy.DetailName(z)));
        var best = SelectionArea is { } area && profile.AllowsDownload ? OfflineMapPolicy.MostDetailedPermitted(area(), LayerId) : profile.MaxZoom;
        SelectedDetail = DetailOptions.FirstOrDefault(o => o.Zoom == best) ?? DetailOptions.LastOrDefault();
        UpdateSelectionFrame();
        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        if (!IsDownloadPanelOpen) return;
        var profile = OfflineMapPolicy.Profile(LayerId);
        if (!profile.AllowsDownload)
        {
            DownloadEstimate = "";
            DownloadTiles = "";
            DownloadWarning = L.T("Topographic maps can't be downloaded for offline use. Areas you saved before still work offline.");
            CanDownload = false;
            return;
        }
        if (SelectionArea is not { } selection) return;
        var summary = OfflineMapPolicy.Summary(selection(), LayerId, DownloadMaxZoom);
        DownloadEstimate = L.F("Approximate saved map size: ~{0}", Formatters.Bytes(summary.EstimatedBytes));
        DownloadTiles = L.Plural(summary.TileCount, "{0:N0} file to download", "{0:N0} files to download");
        var available = TileService.AvailableBytes();
        var fits = available is not { } free || summary.EstimatedBytes <= free;
        DownloadWarning = !summary.PermitsDownload ? L.T("Reduce detail or select a smaller area to stay within the map provider's download limit.")
            : !fits ? L.T("Reduce detail or select a smaller area to fit the available storage.")
            : summary.EstimatedBytes > OfflineMapPolicy.LargeDownloadBytes ? L.T("Large download area. This may take a while and use significant storage.")
            : null;
        CanDownload = summary.PermitsDownload && fits && !IsDownloading;
    }

    [RelayCommand]
    private async Task DownloadVisibleArea()
    {
        if (SelectionArea is not { } selection || IsDownloading) return;
        var profile = OfflineMapPolicy.Profile(LayerId);
        if (!profile.AllowsDownload) return;
        var area = selection();
        var summary = OfflineMapPolicy.Summary(area, LayerId, DownloadMaxZoom);
        if (!summary.PermitsDownload)
        {
            await AppHost.Dialogs.ShowError(L.T("Area too large"), L.T("Reduce detail or select a smaller area to stay within the map provider's download limit."));
            return;
        }
        if (TileService.AvailableBytes() is { } free && summary.EstimatedBytes > free)
        {
            await AppHost.Dialogs.ShowError(L.T("Not enough storage"), L.T("Reduce detail or select a smaller area to fit the available storage."));
            return;
        }
        var job = new OfflineMapDownloadJob { LayerId = LayerId, Area = area, MaxZoom = summary.MaxZoom };
        Core.Settings.Update(s => s.OfflineMapDownload = job);
        await RunDownloadAsync(job);
    }

    /// <summary>Runs (or continues) a download. Saved files are skipped, so a resumed download picks up where it stopped.</summary>
    private async Task RunDownloadAsync(OfflineMapDownloadJob job)
    {
        if (IsDownloading) return;
        _discardDownload = false;
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        HasPendingDownload = false;
        DownloadProgress = 0;
        DownloadText = L.T("Starting download…");
        UpdateSelectionFrame();
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                DownloadProgress = 100.0 * p.Done / Math.Max(1, p.Total);
                DownloadText = L.F("Downloading {0:N0} / {1:N0}…", p.Done, p.Total);
            });
            var failed = await TileService.Instance.DownloadAreaAsync(job.LayerId, job.Area, job.MaxZoom, progress, _downloadCts.Token);
            if (failed == 0)
            {
                Core.Settings.Update(s => s.OfflineMapDownload = null);
                DownloadText = L.F("Done — the area of the {0} map is saved on this PC.", LayerTitle(job.LayerId));
            }
            else
            {
                SavePaused(job, failed);
                DownloadText = L.Plural(failed, "Done — {0:N0} tile could not be downloaded.", "Done — {0:N0} tiles could not be downloaded.");
            }
        }
        catch (OperationCanceledException)
        {
            SavePaused(job, job.FailedTiles);
            DownloadText = L.T("Download paused. You can resume it later, even after restarting.");
        }
        catch (Exception ex)
        {
            SavePaused(job, job.FailedTiles);
            DownloadText = L.F("Download failed: {0}", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            _downloadCts = null;
            LoadPendingDownload();
            CacheSize = Formatters.Bytes(await Task.Run(TileService.CacheSizeBytes));
        }
    }

    private void SavePaused(OfflineMapDownloadJob job, int failed)
    {
        if (_discardDownload) return;
        job.Paused = true;
        job.FailedTiles = failed;
        Core.Settings.Update(s => s.OfflineMapDownload = job);
    }

    private void LoadPendingDownload()
    {
        var job = Core.Settings.Current.OfflineMapDownload;
        HasPendingDownload = job is not null && !IsDownloading;
        PendingDownloadText = job is null ? ""
            : !OfflineMapPolicy.Profile(job.LayerId).AllowsDownload ? L.T("A download of the topographic map can't continue: those maps can't be downloaded any more.")
            : job.FailedTiles > 0 ? L.Plural(job.FailedTiles, "The last download of the {1} map missed {0:N0} tile. Resume to try again.", "The last download of the {1} map missed {0:N0} tiles. Resume to try again.", LayerTitle(job.LayerId))
            : job.Paused ? L.F("The download of the {0} map is paused.", LayerTitle(job.LayerId))
            : L.F("The download of the {0} map was interrupted.", LayerTitle(job.LayerId));
        UpdateSelectionFrame();
    }

    /// <summary>A download cut short by closing the app continues when the map is opened again (unless the user paused it).</summary>
    private void ResumeInterruptedDownload()
    {
        if (IsDownloading || Core.Settings.Current.OfflineMapDownload is not { Paused: false } job) return;
        if (!OfflineMapPolicy.Profile(job.LayerId).AllowsDownload) { LoadPendingDownload(); return; }
        if (!IsDownloadPanelOpen) ToggleDownloadPanel();
        _ = RunDownloadAsync(job);
    }

    [RelayCommand]
    private async Task ResumeDownload()
    {
        if (IsDownloading || Core.Settings.Current.OfflineMapDownload is not { } job) return;
        if (!OfflineMapPolicy.Profile(job.LayerId).AllowsDownload)
        {
            DiscardDownload();
            DownloadText = L.T("Topographic maps can't be downloaded for offline use. Areas you saved before still work offline.");
            return;
        }
        job.Paused = false;
        job.FailedTiles = 0;
        Core.Settings.Update(s => s.OfflineMapDownload = job);
        await RunDownloadAsync(job);
    }

    /// <summary>Forgets a paused download (what it saved stays until the offline maps are cleared).</summary>
    [RelayCommand]
    private void DiscardDownload()
    {
        Core.Settings.Update(s => s.OfflineMapDownload = null);
        DownloadText = "";
        LoadPendingDownload();
    }

    [RelayCommand] private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private async Task ClearTileCache()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Clear offline maps?"), L.T("Deletes all downloaded and cached map tiles."), L.T("Clear"), true)) return;
        _discardDownload = true;
        _downloadCts?.Cancel();
        TileService.Instance.ClearCache();
        Core.Settings.Update(s => s.OfflineMapDownload = null);
        LoadPendingDownload();
        CacheSize = Formatters.Bytes(0);
        DownloadText = L.T("Offline maps cleared.");
    }
}

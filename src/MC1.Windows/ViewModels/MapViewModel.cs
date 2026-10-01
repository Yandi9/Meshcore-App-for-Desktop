using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MC1.Windows.Controls;
using MC1.Windows.Services;
using MeshCore;

namespace MC1.Windows.ViewModels;

public sealed partial class MapViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly Debouncer _reload;
    private readonly Debouncer _persist;
    private MapMarker? _pin;
    private List<MapMarker> _pathMarkers = new();
    private CancellationTokenSource? _downloadCts;

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
        _reload = new Debouncer(TimeSpan.FromMilliseconds(300), Reload);
        _persist = new Debouncer(TimeSpan.FromSeconds(1), Persist);
        OnData(c => { if (c.Kind is DataKind.Contacts or DataKind.Discovered or DataKind.Radio) _reload.Trigger(); });
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

    // offline download
    [ObservableProperty] private bool _isDownloadPanelOpen;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadText = "";
    [ObservableProperty] private int _downloadMinZoom = 8;
    [ObservableProperty] private int _downloadMaxZoom = 14;
    [ObservableProperty] private string _downloadEstimate = "";
    [ObservableProperty] private string _cacheSize = "";

    partial void OnCenterLatitudeChanged(double value) => _persist.Trigger();
    partial void OnCenterLongitudeChanged(double value) => _persist.Trigger();
    partial void OnZoomChanged(double value) => _persist.Trigger();
    partial void OnLayerIdChanged(string value) { _persist.Trigger(); UpdateEstimate(); }
    partial void OnShowChatChanged(bool value) { _persist.Trigger(); Reload(); }
    partial void OnShowRepeatersChanged(bool value) { _persist.Trigger(); Reload(); }
    partial void OnShowRoomsChanged(bool value) { _persist.Trigger(); Reload(); }
    partial void OnShowDiscoveredChanged(bool value) { _persist.Trigger(); Reload(); }
    partial void OnDownloadMinZoomChanged(int value) => UpdateEstimate();
    partial void OnDownloadMaxZoomChanged(int value) => UpdateEstimate();

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
    });

    private bool _shownOnce;

    public void OnShown()
    {
        Reload();
        OnPropertyChanged(nameof(Metric));
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
        foreach (var c in contacts.Where(c => c.HasLocation && !c.IsBlocked))
        {
            var include = c.ContactType switch
            {
                ContactType.Repeater => ShowRepeaters,
                ContactType.Room => ShowRooms,
                _ => ShowChat,
            };
            if (!include) continue;
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
        Summary = L.Plural(located, "{0} node with location", "{0} nodes with location");
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

    // ---- offline tiles ----

    [RelayCommand]
    private void ToggleDownloadPanel()
    {
        IsDownloadPanelOpen = !IsDownloadPanelOpen;
        if (IsDownloadPanelOpen)
        {
            DownloadMinZoom = Math.Max(1, (int)Zoom - 2);
            DownloadMaxZoom = Math.Min(TileService.Layer(LayerId).MaxOfflineZoom, (int)Zoom + 3);
            UpdateEstimate();
            _ = Task.Run(() => TileService.CacheSizeBytes()).ContinueWith(t => Ui(() => CacheSize = Formatters.Bytes(t.Result)));
        }
    }

    private void UpdateEstimate()
    {
        if (VisibleBounds is null) return;
        var (n, s, w, e) = VisibleBounds();
        var max = TileService.Layer(LayerId).MaxOfflineZoom;
        if (DownloadMaxZoom > max) DownloadMaxZoom = max;
        if (DownloadMinZoom > DownloadMaxZoom) DownloadMinZoom = DownloadMaxZoom;
        var count = TileService.CountTiles(n, s, w, e, DownloadMinZoom, DownloadMaxZoom);
        DownloadEstimate = L.Plural(count, "{0:N0} tile (≈{1}) · max zoom for this layer: {2}", "{0:N0} tiles (≈{1}) · max zoom for this layer: {2}", Formatters.Bytes(count * 25_000L), max);
    }

    [RelayCommand]
    private async Task DownloadVisibleArea()
    {
        if (VisibleBounds is null || IsDownloading) return;
        var (n, s, w, e) = VisibleBounds();
        var count = TileService.CountTiles(n, s, w, e, DownloadMinZoom, DownloadMaxZoom);
        if (count > 30_000)
        {
            await AppHost.Dialogs.ShowError(L.T("Area too large"), L.Plural(count, "That would download {0:N0} tile. Zoom in or lower the maximum zoom (limit {1:N0} tiles).", "That would download {0:N0} tiles. Zoom in or lower the maximum zoom (limit {1:N0} tiles).", 30_000));
            return;
        }
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadText = L.F("Downloading {0:N0} / {1:N0}…", 0, count);
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                DownloadProgress = 100.0 * p.Done / Math.Max(1, p.Total);
                DownloadText = L.F("Downloading {0:N0} / {1:N0}…", p.Done, p.Total);
            });
            var failed = await TileService.Instance.DownloadAreaAsync(LayerId, n, s, w, e, DownloadMinZoom, DownloadMaxZoom, progress, _downloadCts.Token);
            DownloadText = failed == 0
                ? L.Plural(count, "Done — {0:N0} tile available offline.", "Done — {0:N0} tiles available offline.")
                : L.Plural(failed, "Done — {0:N0} tile could not be downloaded.", "Done — {0:N0} tiles could not be downloaded.");
        }
        catch (OperationCanceledException) { DownloadText = L.T("Download cancelled."); }
        catch (Exception ex) { DownloadText = L.F("Download failed: {0}", ex.Message); }
        finally
        {
            IsDownloading = false;
            _downloadCts = null;
            CacheSize = Formatters.Bytes(await Task.Run(TileService.CacheSizeBytes));
        }
    }

    [RelayCommand] private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private async Task ClearTileCache()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Clear offline maps?"), L.T("Deletes all downloaded and cached map tiles."), L.T("Clear"), true)) return;
        TileService.Instance.ClearCache();
        CacheSize = Formatters.Bytes(0);
        DownloadText = L.T("Offline maps cleared.");
    }
}

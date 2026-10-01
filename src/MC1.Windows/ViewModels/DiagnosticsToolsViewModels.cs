using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Windows.Controls;
using MeshCore;

namespace MC1.Windows.ViewModels;

// ───────────────────────────── RX log ─────────────────────────────

public sealed class RxLogRow
{
    public RxLogRow(RxLogRecord r)
    {
        Record = r;
        Time = DateTimeOffset.FromUnixTimeMilliseconds(r.ReceivedAt).LocalDateTime.ToString(Formatters.Use24h ? "HH:mm:ss" : "h:mm:ss tt");
        Type = r.Payload.DisplayName();
        Route = r.Route.DisplayName();
        Hops = r.PathLength == 0xFF ? "–" : r.HopCount.ToString();
        Snr = r.Snr is { } s ? $"{s:0.#}" : "–";
        Rssi = r.Rssi is { } rs ? rs.ToString() : "–";
        Region = r.RegionScope;
        Summary = r.Payload switch
        {
            PayloadType.GroupText when r.Decrypt == DecryptStatus.Success => $"[{r.ChannelName}] {r.DecodedText}",
            PayloadType.GroupText => r.ChannelName is not null ? L.F("[{0}] (couldn't decrypt)", r.ChannelName) : L.T("Channel message (unknown channel)"),
            PayloadType.TextMessage when r.Decrypt == DecryptStatus.Success => L.F("From {0}: {1}", r.FromContactName, r.DecodedText),
            PayloadType.TextMessage => L.T("Direct message (not for you)"),
            PayloadType.Advert => r.FromContactName is { } n ? L.F("Advert from {0}", n) : L.T("Advert"),
            PayloadType.Ack => L.T("Acknowledgement"),
            PayloadType.Path => L.T("Path return"),
            PayloadType.Trace => L.T("Trace"),
            PayloadType.Request => L.T("Request"),
            PayloadType.Response => L.T("Response"),
            PayloadType.AnonRequest => L.T("Anonymous request (login)"),
            PayloadType.Control => L.T("Control"),
            _ => L.Plural(r.RawPayload.Length, "{0} byte", "{0} bytes"),
        };
        TypeColor = r.Payload switch
        {
            PayloadType.GroupText => "#2463EB",
            PayloadType.TextMessage => "#7C3AED",
            PayloadType.Advert => "#22A06B",
            PayloadType.Ack => "#6B7280",
            PayloadType.Trace or PayloadType.Path => "#F59E0B",
            _ => "#0EA5E9",
        };
    }

    public RxLogRecord Record { get; }
    public string Time { get; }
    public string Type { get; }
    public string TypeColor { get; }
    public string Route { get; }
    public string Hops { get; }
    public string Snr { get; }
    public string Rssi { get; }
    public string? Region { get; }
    public string Summary { get; }
}

public enum RxFilter { All, Messages, Channel, Direct, Adverts, Other }

public sealed partial class RxLogViewModel : ToolPage
{
    private readonly MainWindowViewModel _main;
    private readonly List<RxLogRow> _all = new();
    private readonly Debouncer _refilter;

    public RxLogViewModel(MainWindowViewModel main)
    {
        _main = main;
        _refilter = new Debouncer(TimeSpan.FromMilliseconds(150), ApplyFilter);
        Action<MC1.Core.Models.RxLogRecord> onEntry = r => Ui(() =>
        {
            if (Paused) { PendingCount++; return; }
            _all.Insert(0, new RxLogRow(r));
            if (_all.Count > 2000) _all.RemoveAt(_all.Count - 1);
            if (Matches(_all[0])) { Rows.Insert(0, _all[0]); if (Rows.Count > 2000) Rows.RemoveAt(Rows.Count - 1); }
            CountText = L.Plural(_all.Count, "{0} packet", "{0} packets");
        });
        Core.RxLog.EntryAdded += onEntry;
        Track(() => Core.RxLog.EntryAdded -= onEntry);
    }

    public ObservableCollection<RxLogRow> Rows { get; } = new();
    public IReadOnlyList<RxFilter> Filters { get; } = Enum.GetValues<RxFilter>();

    [ObservableProperty] private RxFilter _filter;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private RxLogRow? _selected;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _loggingEnabled;

    public override void OnShown()
    {
        LoggingEnabled = Core.Settings.Current.RxLogEnabled;
        Reload();
    }

    partial void OnLoggingEnabledChanged(bool value)
    {
        if (Core.Settings.Current.RxLogEnabled != value) Core.Settings.Update(s => s.RxLogEnabled = value);
    }

    partial void OnPausedChanged(bool value)
    {
        if (!value) { PendingCount = 0; Reload(); }
    }

    partial void OnFilterChanged(RxFilter value) => ApplyFilter();
    partial void OnSearchChanged(string value) => _refilter.Trigger();

    partial void OnSelectedChanged(RxLogRow? value)
    {
        if (value is null) { DetailText = ""; return; }
        var r = value.Record;
        // Label / value rows; the labels are padded to one column (at least as wide as the English layout).
        var rows = new List<(string Label, string Value)>
        {
            (L.T("Received"), $"{DateTimeOffset.FromUnixTimeMilliseconds(r.ReceivedAt).LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}"),
            (L.T("Type"), $"{value.Type} (v{r.PayloadVersion})"),
            (L.T("Route"), r.RegionScope is { } rg ? L.F("{0} · region {1}", value.Route, rg)
                : r.TransportCode is { Length: > 0 } tc ? L.F("{0} · transport {1}", value.Route, Convert.ToHexString(tc)) : value.Route),
            (L.T("Signal"), $"SNR {value.Snr} dB · RSSI {value.Rssi} dBm"),
        };
        if (r.PathLength != 0xFF && r.HopCount > 0)
        {
            var hops = PathEncoding.HopHexes(r.PathNodes, r.HashSize).Select(h => Core.Contacts.ResolveHopName(Convert.FromHexString(h)) is { } n ? $"{h} ({n})" : h);
            rows.Add((L.T("Path"), string.Join(" → ", hops)));
        }
        else rows.Add((L.T("Path"), r.PathLength == 0 ? L.T("direct (0 hops)") : "–"));
        if (r.ChannelName is not null) rows.Add((L.T("Channel"), r.ChannelName));
        if (r.FromContactName is not null) rows.Add((L.T("From"), r.FromContactName));
        if (r.SenderTimestamp is { } ts) rows.Add((L.T("Sent at"), $"{DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime:yyyy-MM-dd HH:mm:ss}"));
        if (r.DecodedText is not null) rows.Add((L.T("Text"), r.DecodedText));
        rows.Add((L.T("Decrypt"), r.Decrypt.ToString()));
        rows.Add((L.T("Hash"), r.PacketHash));
        var width = Math.Max(11, rows.Max(x => x.Label.Length) + 2);
        var sb = new StringBuilder();
        foreach (var (label, text) in rows) sb.AppendLine(label.PadRight(width) + text);
        sb.AppendLine();
        sb.AppendLine(L.T("Raw packet:"));
        sb.AppendLine(HexDump(r.RawPayload));
        DetailText = sb.ToString();
    }

    private static string HexDump(byte[] data)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += 16)
        {
            var chunk = data.AsSpan(i, Math.Min(16, data.Length - i));
            sb.Append($"{i:X4}  ");
            for (var j = 0; j < 16; j++) sb.Append(j < chunk.Length ? $"{chunk[j]:X2} " : "   ");
            sb.Append(' ');
            foreach (var b in chunk) sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void Reload()
    {
        _all.Clear();
        foreach (var r in Core.RxLog.Recent(2000)) _all.Add(new RxLogRow(r));
        CountText = L.Plural(_all.Count, "{0} packet", "{0} packets");
        ApplyFilter();
    }

    private bool Matches(RxLogRow row)
    {
        var p = row.Record.Payload;
        var ok = Filter switch
        {
            RxFilter.Messages => p is PayloadType.GroupText or PayloadType.TextMessage,
            RxFilter.Channel => p == PayloadType.GroupText,
            RxFilter.Direct => p == PayloadType.TextMessage,
            RxFilter.Adverts => p == PayloadType.Advert,
            RxFilter.Other => p is not (PayloadType.GroupText or PayloadType.TextMessage or PayloadType.Advert),
            _ => true,
        };
        if (!ok) return false;
        var q = Search.Trim();
        return q.Length == 0 || row.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) || row.Record.PacketHash.StartsWith(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in _all.Where(Matches)) Rows.Add(r);
    }

    [RelayCommand]
    private async Task Clear()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Clear RX log?"), L.T("Deletes every logged packet for this radio."), L.T("Clear"), true)) return;
        Core.RxLog.Clear();
        Reload();
        Selected = null;
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = await AppHost.Dialogs.PickSaveFile(L.T("Export RX log"), $"rxlog-{DateTime.Now:yyyyMMdd-HHmm}.csv", "csv", L.T("CSV file"));
        if (path is null) return;
        var sb = new StringBuilder("time,type,route,hops,snr,rssi,region,path,channel,from,text,hash,raw\n");
        foreach (var row in _all.AsEnumerable().Reverse())
        {
            var r = row.Record;
            string Esc(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
            sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(r.ReceivedAt).ToString("o")).Append(',')
              .Append(row.Type).Append(',').Append(row.Route).Append(',').Append(row.Hops).Append(',')
              .Append(r.Snr?.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(r.Rssi).Append(',')
              .Append(Esc(r.RegionScope)).Append(',').Append(Convert.ToHexString(r.PathNodes)).Append(',')
              .Append(Esc(r.ChannelName)).Append(',').Append(Esc(r.FromContactName)).Append(',').Append(Esc(r.DecodedText)).Append(',')
              .Append(r.PacketHash).Append(',').Append(Convert.ToHexString(r.RawPayload)).Append('\n');
        }
        await File.WriteAllTextAsync(path, sb.ToString());
        _main.ShowToast(L.T("Exported"), L.Plural(_all.Count, "{0} packet saved.", "{0} packets saved."));
    }

    [RelayCommand]
    private async Task CopyDetail()
    {
        if (DetailText.Length > 0) await AppHost.Dialogs.CopyToClipboard(DetailText);
    }
}

// ───────────────────────────── Noise floor ─────────────────────────────

public sealed partial class NoiseFloorViewModel : ToolPage
{
    private readonly List<ChartPoint> _noise = new();
    private readonly List<ChartPoint> _rssi = new();
    private readonly List<ChartPoint> _snr = new();

    public NoiseFloorViewModel(MainWindowViewModel main)
    {
        Action<NoiseReading> onReading = r => Ui(() => Add(r));
        Core.Tools.NoiseReadingAdded += onReading;
        Track(() => Core.Tools.NoiseReadingAdded -= onReading);
        OnStatus(() => Ui(() => IsMonitoring = Core.Tools.IsNoiseMonitoring));
    }

    public ObservableCollection<ChartSeries> SignalSeries { get; } = new();
    public ObservableCollection<ChartSeries> SnrSeries { get; } = new();
    public IReadOnlyList<string> Intervals { get; } =
        [L.Plural(1, "{0} second", "{0} seconds"), L.Plural(2, "{0} second", "{0} seconds"), L.Plural(5, "{0} second", "{0} seconds"), L.Plural(10, "{0} second", "{0} seconds")];

    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private int _intervalIndex = 1;
    [ObservableProperty] private string _currentNoise = "–";
    [ObservableProperty] private string _quality = "";
    [ObservableProperty] private string _qualityColor = "#6B7280";
    [ObservableProperty] private string _currentRssi = "–";
    [ObservableProperty] private string _currentSnr = "–";
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private int _revision;
    [ObservableProperty] private string? _status;

    public override void OnShown() => IsMonitoring = Core.Tools.IsNoiseMonitoring;

    private TimeSpan Interval => IntervalIndex switch { 0 => TimeSpan.FromSeconds(1), 2 => TimeSpan.FromSeconds(5), 3 => TimeSpan.FromSeconds(10), _ => TimeSpan.FromSeconds(2) };

    partial void OnIntervalIndexChanged(int value)
    {
        if (IsMonitoring) Core.Tools.StartNoiseMonitor(Interval);
    }

    [RelayCommand]
    private void Toggle()
    {
        if (Core.Tools.IsNoiseMonitoring) { Core.Tools.StopNoiseMonitor(); IsMonitoring = false; return; }
        if (!Core.IsConnected) { Status = L.T("Connect to your radio first."); return; }
        Status = null;
        Core.Tools.StartNoiseMonitor(Interval);
        IsMonitoring = true;
    }

    [RelayCommand]
    private void Reset()
    {
        _noise.Clear(); _rssi.Clear(); _snr.Clear();
        Rebuild();
        Stats = "";
    }

    private void Add(NoiseReading r)
    {
        var x = r.Time.ToUnixTimeMilliseconds();
        _noise.Add(new ChartPoint(x, r.NoiseFloor));
        _rssi.Add(new ChartPoint(x, r.LastRssi));
        _snr.Add(new ChartPoint(x, r.LastSnr));
        const int max = 600;
        if (_noise.Count > max) { _noise.RemoveAt(0); _rssi.RemoveAt(0); _snr.RemoveAt(0); }
        CurrentNoise = $"{r.NoiseFloor} dBm";
        Quality = ToolsService.NoiseQuality(r.NoiseFloor);
        // Colour from the English rating key (the shown text is translated).
        QualityColor = ToolsService.NoiseQualityKey(r.NoiseFloor) switch { "Excellent" => "#22A06B", "Good" => "#65A30D", "Fair" => "#F59E0B", _ => "#E5484D" };
        CurrentRssi = $"{r.LastRssi} dBm";
        CurrentSnr = $"{r.LastSnr:0.#} dB";
        Stats = L.Plural(_noise.Count, "Noise floor min {1:0} · avg {2:0.#} · max {3:0} dBm over {0} sample", "Noise floor min {1:0} · avg {2:0.#} · max {3:0} dBm over {0} samples",
            _noise.Min(p => p.Y), _noise.Average(p => p.Y), _noise.Max(p => p.Y));
        Rebuild();
    }

    private void Rebuild()
    {
        SignalSeries.Clear();
        SignalSeries.Add(new ChartSeries { Name = L.T("Noise floor"), Color = "#E5484D", Points = _noise.ToList(), Fill = true });
        SignalSeries.Add(new ChartSeries { Name = L.T("Last RSSI"), Color = "#2463EB", Points = _rssi.ToList() });
        SnrSeries.Clear();
        SnrSeries.Add(new ChartSeries { Name = L.T("Last SNR"), Color = "#22A06B", Points = _snr.ToList(), Fill = true });
        Revision++;
    }
}

// ───────────────────────────── Node discovery ─────────────────────────────

public sealed partial class DiscoveredNeighbourRow : ObservableObject
{
    public required DiscoveredNeighbour Node { get; init; }
    public string Name => Node.Name ?? Node.Type switch
    {
        ContactType.Repeater => L.T("Unknown repeater"),
        ContactType.Room => L.T("Unknown room server"),
        ContactType.Sensor => L.T("Unknown sensor"),
        _ => L.T("Unknown companion"),
    };
    public string KeyText => Convert.ToHexString(Node.PublicKey.Length > 8 ? Node.PublicKey[..8] : Node.PublicKey);
    public string IconKey => Formatters.ContactIcon(Node.Type);
    public string? Emoji => Formatters.AvatarEmoji(Node.Name);
    public string Color => Formatters.ContactColor(Node.Type);
    public string Signal => L.F("SNR {0:0.#} dB (they heard you {1:0.#} dB) · RSSI {2} dBm", Node.Snr, Node.SnrIn, Node.Rssi);
    public bool IsKnown => Node.Name is not null;
    [ObservableProperty] private bool _added;
}

public sealed partial class NodeDiscoveryViewModel : ToolPage
{
    private readonly MainWindowViewModel _main;
    private CancellationTokenSource? _cts;

    public NodeDiscoveryViewModel(MainWindowViewModel main) => _main = main;

    public ObservableCollection<DiscoveredNeighbourRow> Results { get; } = new();

    [ObservableProperty] private bool _includeRepeaters = true;
    [ObservableProperty] private bool _includeRooms = true;
    [ObservableProperty] private bool _includeSensors = true;
    [ObservableProperty] private int _listenSeconds = 10;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private double _progress;

    [RelayCommand]
    private async Task Scan()
    {
        if (!Core.IsConnected) { Status = L.T("Connect to your radio first."); return; }
        if (!IncludeRepeaters && !IncludeRooms && !IncludeSensors) { Status = L.T("Pick at least one node type."); return; }
        _cts = new CancellationTokenSource();
        Results.Clear();
        IsScanning = true;
        Status = L.T("Listening for replies…");
        Progress = 0;
        var started = DateTime.UtcNow;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => Progress = Math.Min(100, (DateTime.UtcNow - started).TotalSeconds / ListenSeconds * 100);
        timer.Start();
        try
        {
            var found = await Core.Tools.DiscoverNodesAsync(IncludeRepeaters, IncludeRooms, IncludeSensors, TimeSpan.FromSeconds(ListenSeconds),
                n => Ui(() => Results.Add(new DiscoveredNeighbourRow { Node = n })), _cts.Token);
            Status = found.Count == 0 ? L.T("No nodes replied. Discovery only reaches nodes in direct radio range (zero hop).") : L.Plural(found.Count, "{0} node replied.", "{0} nodes replied.");
        }
        catch (Exception ex) { Status = L.F("Discovery failed: {0}", ex.Message); }
        finally { timer.Stop(); Progress = 100; IsScanning = false; _cts = null; }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private async Task Add(DiscoveredNeighbourRow row)
    {
        await Guard(async () =>
        {
            if (row.Node.PublicKey.Length < 32)
            {
                await AppHost.Dialogs.ShowError(L.T("Can't add yet"), L.T("Only part of this node's key is known. Wait for its advert, then add it from Contacts → Discover."));
                return;
            }
            await Core.Contacts.AddAsync(row.Node.PublicKey, row.Node.Name ?? Convert.ToHexString(row.Node.PublicKey[..4]), row.Node.Type, outPathLength: 0, outPath: []);
            row.Added = true;
            _main.ShowToast(L.T("Contact added"), L.F("{0} was added as a direct neighbour.", row.Name));
        }, L.T("Add node"));
    }
}

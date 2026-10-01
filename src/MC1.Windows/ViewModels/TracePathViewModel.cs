using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Windows.ViewModels;

public sealed record TraceHopEntry(byte[] Hash, string Label, ContactRecord? Contact)
{
    public string HashText => Convert.ToHexString(Hash);
}

public sealed record TraceResultRow(int Index, string Label, string HashText, double Snr, string SnrText, double Quality, string Color);

public sealed record TraceBatchRun(int Run, bool Success, int RoundTripMs, string Summary);

public sealed partial class TracePathViewModel : ToolPage
{
    private readonly MainWindowViewModel _main;
    private CancellationTokenSource? _cts;
    private TraceRunResult? _lastResult;

    public TracePathViewModel(MainWindowViewModel main)
    {
        _main = main;
        var reload = new Debouncer(TimeSpan.FromMilliseconds(150), Reload);
        OnData(c => { if (c.Kind is DataKind.Contacts or DataKind.TracePaths or DataKind.Radio) reload.Trigger(); });
    }

    public ObservableCollection<ContactRecord> Repeaters { get; } = new();
    public ObservableCollection<TraceHopEntry> Hops { get; } = new();
    public ObservableCollection<TraceResultRow> Results { get; } = new();
    public ObservableCollection<TracePathRecord> SavedPaths { get; } = new();
    public ObservableCollection<TraceBatchRun> BatchRuns { get; } = new();
    public IReadOnlyList<int> BatchOptions { get; } = [1, 3, 5, 10];
    public IReadOnlyList<string> HashModes { get; } = [L.Plural(1, "{0} byte", "{0} bytes"), L.Plural(2, "{0} byte", "{0} bytes"), L.Plural(4, "{0} byte", "{0} bytes")];

    [ObservableProperty] private string _repeaterSearch = "";
    [ObservableProperty] private bool _roundTrip = true;
    [ObservableProperty] private int _hashMode;
    [ObservableProperty] private bool _canChangeHashMode;
    [ObservableProperty] private int _batchCount = 1;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _pathText = "";
    [ObservableProperty] private string? _distanceText;
    [ObservableProperty] private string? _batchSummary;

    private int HashSize => 1 << HashMode;

    public override void OnShown() { Reload(); UpdatePathText(); }

    partial void OnRepeaterSearchChanged(string value) => Reload();
    partial void OnRoundTripChanged(bool value) => UpdatePathText();
    partial void OnHashModeChanged(int value)
    {
        // Re-derive hop hashes at the new width from the contact keys where possible.
        var rebuilt = Hops.Select(h => h.Contact is { } c ? h with { Hash = c.PublicKey.Prefix(HashSize) } : h).ToList();
        Hops.Clear();
        foreach (var h in rebuilt) Hops.Add(h);
        UpdatePathText();
    }

    private void Reload()
    {
        var q = RepeaterSearch.Trim();
        Repeaters.Clear();
        foreach (var c in Core.Contacts.GetAll()
                     .Where(c => c.ContactType is ContactType.Repeater or ContactType.Room)
                     .Where(c => q.Length == 0 || c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || c.PublicKeyHex.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(c => c.IsFavorite).ThenBy(c => c.DisplayName))
            Repeaters.Add(c);
        SavedPaths.Clear();
        foreach (var p in Core.Tools.SavedPaths()) SavedPaths.Add(p);
        var caps = Core.Capabilities;
        CanChangeHashMode = caps is { FirmwareVersion: >= 11 };
        if (!CanChangeHashMode && caps is not null && Hops.Count == 0) HashMode = Math.Min(2, (int)caps.PathHashMode);
    }

    public void StartWith(ContactRecord c)
    {
        Reload();
        Hops.Clear();
        AddHop(c);
    }

    [RelayCommand]
    private void AddHop(ContactRecord c)
    {
        Hops.Add(new TraceHopEntry(c.PublicKey.Prefix(HashSize), c.DisplayName, c));
        UpdatePathText();
    }

    [RelayCommand]
    private async Task AddManualHop()
    {
        var input = await AppHost.Dialogs.Prompt(L.T("Add hops by hash"), L.Plural(HashSize * 2, "Enter repeater hash codes separated by commas ({0} hex character each), e.g. A1,B2,C3.",
            "Enter repeater hash codes separated by commas ({0} hex characters each), e.g. A1,B2,C3."), "");
        if (string.IsNullOrWhiteSpace(input)) return;
        var tokens = input.Split([',', ' ', '>'], StringSplitOptions.RemoveEmptyEntries);
        var widths = tokens.Select(t => t.Length).Distinct().ToList();
        if (widths.Count == 1 && widths[0] is 2 or 4 or 8 && widths[0] / 2 != HashSize && CanChangeHashMode)
            HashMode = widths[0] switch { 2 => 0, 4 => 1, _ => 2 };
        foreach (var t in tokens)
        {
            var bytes = Bytes.FromHex(t);
            if (bytes is null || bytes.Length != HashSize)
            {
                await AppHost.Dialogs.ShowError(L.T("Invalid hash"), L.F("\"{0}\" is not a {1}-byte hex code.", t, HashSize));
                return;
            }
            var contact = Core.Contacts.GetAll().FirstOrDefault(c => c.PublicKey.StartsWith(bytes) && c.ContactType is ContactType.Repeater or ContactType.Room);
            Hops.Add(new TraceHopEntry(bytes, contact?.DisplayName ?? Core.Contacts.ResolveHopName(bytes) ?? t.ToUpperInvariant(), contact));
        }
        UpdatePathText();
    }

    [RelayCommand]
    private void RemoveHop(TraceHopEntry hop) { Hops.Remove(hop); UpdatePathText(); }

    [RelayCommand]
    private void MoveHopUp(TraceHopEntry hop)
    {
        var i = Hops.IndexOf(hop);
        if (i > 0) { Hops.Move(i, i - 1); UpdatePathText(); }
    }

    [RelayCommand]
    private void MoveHopDown(TraceHopEntry hop)
    {
        var i = Hops.IndexOf(hop);
        if (i >= 0 && i < Hops.Count - 1) { Hops.Move(i, i + 1); UpdatePathText(); }
    }

    [RelayCommand]
    private void ClearHops()
    {
        Hops.Clear();
        Results.Clear();
        BatchRuns.Clear();
        HasResult = false;
        Status = null;
        BatchSummary = null;
        UpdatePathText();
    }

    private byte[] FullPath() => ToolsService.BuildTracePath(Hops.Select(h => h.Hash).ToList(), RoundTrip);

    private void UpdatePathText()
    {
        var path = FullPath();
        PathText = path.Length == 0 ? L.T("Add at least one repeater") : string.Join(" → ", new[] { L.T("You") }.Concat(PathEncoding.HopHexes(path, HashSize)).Append(RoundTrip ? L.T("You") : L.T("end")));
        // distance through known positions
        var pts = new List<(double Lat, double Lon)>();
        if (Core.SelfInfo is { } s && (s.Latitude != 0 || s.Longitude != 0)) pts.Add((s.Latitude, s.Longitude));
        foreach (var h in Hops)
        {
            if (h.Contact is { HasLocation: true } c) pts.Add((c.Latitude, c.Longitude));
            else if (Core.Contacts.ResolveHopLocation(h.Hash) is { } loc) pts.Add(loc);
        }
        double total = 0;
        for (var i = 1; i < pts.Count; i++) total += RfCalculator.Distance(pts[i - 1].Lat, pts[i - 1].Lon, pts[i].Lat, pts[i].Lon);
        DistanceText = pts.Count >= 2 ? L.F("Outbound distance ≈ {0}", Formatters.Distance(total)) : null;
    }

    [RelayCommand]
    private async Task Run()
    {
        if (Hops.Count == 0) { Status = L.T("Add at least one repeater to the path."); return; }
        if (!Core.IsConnected) { Status = L.T("Connect to your radio first."); return; }
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Results.Clear();
        BatchRuns.Clear();
        BatchSummary = null;
        HasResult = false;
        var path = FullPath();
        var successes = new List<TraceRunResult>();
        try
        {
            for (var run = 1; run <= BatchCount && !_cts.IsCancellationRequested; run++)
            {
                Status = BatchCount > 1 ? L.F("Tracing… run {0} of {1}", run, BatchCount) : L.T("Tracing…");
                var r = await Core.Tools.RunTraceAsync(path, (byte)HashMode, _cts.Token);
                _lastResult = r;
                if (r.Success) successes.Add(r);
                if (BatchCount > 1)
                    BatchRuns.Add(new TraceBatchRun(run, r.Success, r.RoundTripMs, r.Success ? L.F("{0} ms · min SNR {1:0.#} dB", r.RoundTripMs, r.Hops.Min(h => h.Snr)) : r.Error ?? L.T("failed")));
                ShowResult(r);
                if (run < BatchCount) await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
            }
            if (BatchCount > 1)
            {
                BatchSummary = successes.Count == 0 ? L.Plural(BatchCount, "0 / {0} trace succeeded", "0 / {0} traces succeeded")
                    : L.F("{0} / {1} succeeded · avg {2:0} ms · avg worst-hop SNR {3:0.#} dB", successes.Count, BatchCount, successes.Average(s => s.RoundTripMs), successes.Average(s => s.Hops.Min(h => h.Snr)));
            }
        }
        catch (OperationCanceledException) { Status = L.T("Trace cancelled."); }
        catch (Exception ex) { Status = L.F("Trace failed: {0}", ex.Message); }
        finally { IsRunning = false; _cts = null; }
    }

    private void ShowResult(TraceRunResult r)
    {
        Results.Clear();
        if (!r.Success) { Status = r.Error ?? L.T("Trace failed"); HasResult = false; return; }
        var i = 0;
        foreach (var h in r.Hops)
        {
            i++;
            var quality = Math.Clamp((h.Snr + 15) / 25.0, 0, 1);
            var color = h.Snr >= 5 ? "#22A06B" : h.Snr >= -5 ? "#F59E0B" : "#E5484D";
            Results.Add(new TraceResultRow(i, h.Label, h.Hash is { } b ? Convert.ToHexString(b) : "", h.Snr, $"{h.Snr:0.##} dB", quality, color));
        }
        HasResult = true;
        Status = L.F("Trace completed in {0} ms", r.RoundTripMs);
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task SavePath()
    {
        if (Hops.Count == 0) return;
        var name = await AppHost.Dialogs.Prompt(L.T("Save path"), L.T("Name for this trace path:"), string.Join(" → ", Hops.Select(h => h.Label)).Utf8Prefix(60));
        if (string.IsNullOrWhiteSpace(name)) return;
        await Guard(() => { Core.Tools.SavePath(name.Trim(), FullPath(), HashSize, _lastResult is { Success: true } ? _lastResult : null); return Task.CompletedTask; }, L.T("Save path"));
        Reload();
    }

    [RelayCommand]
    private void LoadPath(TracePathRecord p)
    {
        Hops.Clear();
        Results.Clear();
        HasResult = false;
        var size = Math.Max(1, p.HashSize);
        if (CanChangeHashMode || size == HashSize) HashMode = size switch { 2 => 1, 4 => 2, _ => 0 };
        var hops = PathEncoding.HopHexes(p.Path, size).Select(Convert.FromHexString).ToList();
        // Saved paths store the full (possibly mirrored) route; detect and collapse a mirrored return.
        var n = hops.Count;
        var mirrored = n >= 3 && n % 2 == 1 && Enumerable.Range(0, n / 2).All(i => hops[i].SequenceEqual(hops[n - 1 - i]));
        RoundTrip = mirrored;
        var outbound = mirrored ? hops.Take(n / 2 + 1).ToList() : hops;
        var contacts = Core.Contacts.GetAll();
        foreach (var h in outbound)
        {
            var c = contacts.FirstOrDefault(x => x.PublicKey.StartsWith(h) && x.ContactType is ContactType.Repeater or ContactType.Room);
            Hops.Add(new TraceHopEntry(h, c?.DisplayName ?? Core.Contacts.ResolveHopName(h) ?? Convert.ToHexString(h), c));
        }
        UpdatePathText();
        Status = L.F("Loaded \"{0}\"", p.Name) + (p.LastRunAt is { } at ? " · " + L.F("last run {0}", Formatters.AgoMs(at)) : "");
    }

    [RelayCommand]
    private async Task RunSaved(TracePathRecord p)
    {
        LoadPath(p);
        await Run();
    }

    [RelayCommand]
    private async Task DeleteSaved(TracePathRecord p)
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Delete saved path?"), L.F("\"{0}\" will be removed.", p.Name), L.T("Delete"), true)) return;
        Core.Tools.DeletePath(p);
        Reload();
    }

    [RelayCommand]
    private void ShowOnMap()
    {
        if (Hops.Count == 0) return;
        _main.Navigate(Page.Map);
        _main.Map.ShowPath(Hops.Select(h => h.Hash).ToList());
    }

    [RelayCommand]
    private async Task CopyPath()
    {
        await AppHost.Dialogs.CopyToClipboard(string.Join(",", PathEncoding.HopHexes(FullPath(), HashSize)));
        Status = L.T("Path copied to clipboard.");
    }
}

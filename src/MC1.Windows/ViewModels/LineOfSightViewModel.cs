using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Windows.ViewModels;

public sealed record LosPlace(string Name, double Lat, double Lon)
{
    public override string ToString() => Name;
}

public sealed partial class LineOfSightViewModel : ToolPage
{
    private readonly MainWindowViewModel _main;
    private CancellationTokenSource? _cts;

    public LineOfSightViewModel(MainWindowViewModel main)
    {
        _main = main;
        var s = Core.Settings.Current;
        _heightA = s.LosHeightA;
        _heightB = s.LosHeightB;
        _refractionK = s.LosRefractionK;
        _frequencyMHz = Core.SelfInfo?.RadioFrequency is > 0 and var f ? f : 910.525;
    }

    public ObservableCollection<LosPlace> Places { get; } = new();
    public IReadOnlyList<double> KFactors { get; } = [1.0, 1.333, 1.5, 2.0];
    public bool Metric => Core.Settings.Current.UseMetricUnits;

    [ObservableProperty] private string _nameA = L.T("Point A");
    [ObservableProperty] private string _latA = "";
    [ObservableProperty] private string _lonA = "";
    [ObservableProperty] private string _nameB = L.T("Point B");
    [ObservableProperty] private string _latB = "";
    [ObservableProperty] private string _lonB = "";
    [ObservableProperty] private double _heightA;
    [ObservableProperty] private double _heightB;
    [ObservableProperty] private double _frequencyMHz;
    [ObservableProperty] private double _refractionK;
    [ObservableProperty] private LosPlace? _pickA;
    [ObservableProperty] private LosPlace? _pickB;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private IReadOnlyList<ElevationSample>? _profile;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private string _verdictColor = "#22A06B";
    [ObservableProperty] private string _distanceText = "";
    [ObservableProperty] private string _fsplText = "";
    [ObservableProperty] private string _diffractionText = "";
    [ObservableProperty] private string _totalLossText = "";
    [ObservableProperty] private string _clearanceText = "";
    [ObservableProperty] private string _marginText = "";
    [ObservableProperty] private string _bearingText = "";
    [ObservableProperty] private string _elevationText = "";

    private List<ElevationSample>? _raw;

    public override void OnShown()
    {
        OnPropertyChanged(nameof(Metric));
        Places.Clear();
        if (Core.SelfInfo is { } s && (s.Latitude != 0 || s.Longitude != 0)) Places.Add(new LosPlace(L.F("{0} (your radio)", s.Name), s.Latitude, s.Longitude));
        foreach (var c in Core.Contacts.GetAll().Where(c => c.HasLocation).OrderBy(c => c.ContactType != ContactType.Repeater).ThenBy(c => c.DisplayName))
            Places.Add(new LosPlace($"{c.DisplayName} · {Formatters.TypeName(c.ContactType)}", c.Latitude, c.Longitude));
        if (Core.SelfInfo?.RadioFrequency is > 0 and var f && !HasResult) FrequencyMHz = f;
        if (LatA.Length == 0 && Places.Count > 0 && Core.SelfInfo is { Latitude: not 0 }) PickA = Places[0];
    }

    partial void OnPickAChanged(LosPlace? value)
    {
        if (value is null) return;
        NameA = value.Name.Split(" · ")[0];
        LatA = value.Lat.ToString("0.000000", CultureInfo.InvariantCulture);
        LonA = value.Lon.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    partial void OnPickBChanged(LosPlace? value)
    {
        if (value is null) return;
        NameB = value.Name.Split(" · ")[0];
        LatB = value.Lat.ToString("0.000000", CultureInfo.InvariantCulture);
        LonB = value.Lon.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    partial void OnHeightAChanged(double value) => Recompute();
    partial void OnHeightBChanged(double value) => Recompute();
    partial void OnFrequencyMHzChanged(double value) => Recompute();
    partial void OnRefractionKChanged(double value) => Recompute();

    public void SetPoints((double Lat, double Lon)? from, (double Lat, double Lon) to, string toLabel)
    {
        OnShown();
        if (from is { } a)
        {
            NameA = Core.SelfName is { Length: > 0 } n ? n : L.T("Your radio");
            LatA = a.Lat.ToString("0.000000", CultureInfo.InvariantCulture);
            LonA = a.Lon.ToString("0.000000", CultureInfo.InvariantCulture);
        }
        NameB = toLabel;
        LatB = to.Lat.ToString("0.000000", CultureInfo.InvariantCulture);
        LonB = to.Lon.ToString("0.000000", CultureInfo.InvariantCulture);
        if (from is not null) _ = Analyze();
    }

    private static bool TryCoord(string text, double min, double max, out double v) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

    [RelayCommand]
    private void Swap()
    {
        (NameA, NameB) = (NameB, NameA);
        (LatA, LatB) = (LatB, LatA);
        (LonA, LonB) = (LonB, LonA);
        (HeightA, HeightB) = (HeightB, HeightA);
        if (_raw is not null)
        {
            var total = _raw[^1].DistanceFromAMeters;
            _raw = _raw.AsEnumerable().Reverse().Select(s => s with { DistanceFromAMeters = total - s.DistanceFromAMeters }).ToList();
            Recompute();
        }
    }

    [RelayCommand]
    private async Task PasteA() => await PasteInto(true);

    [RelayCommand]
    private async Task PasteB() => await PasteInto(false);

    private async Task PasteInto(bool a)
    {
        var text = await AppHost.Dialogs.ReadClipboard();
        if (string.IsNullOrWhiteSpace(text)) return;
        (double Lat, double Lon)? p = MeshCoreUrl.ParseMap(text.Trim());
        if (p is null)
        {
            var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && TryCoord(parts[0], -90, 90, out var la) && TryCoord(parts[1], -180, 180, out var lo)) p = (la, lo);
        }
        if (p is not { } pos) { Status = L.T("The clipboard doesn't contain coordinates."); return; }
        var lat = pos.Lat.ToString("0.000000", CultureInfo.InvariantCulture);
        var lon = pos.Lon.ToString("0.000000", CultureInfo.InvariantCulture);
        if (a) { LatA = lat; LonA = lon; } else { LatB = lat; LonB = lon; }
    }

    [RelayCommand]
    private async Task Analyze()
    {
        if (!TryCoord(LatA, -90, 90, out var la) || !TryCoord(LonA, -180, 180, out var loa) || !TryCoord(LatB, -90, 90, out var lb) || !TryCoord(LonB, -180, 180, out var lob))
        {
            Status = L.T("Enter valid coordinates for both points (decimal degrees).");
            return;
        }
        if (RfCalculator.Distance(la, loa, lb, lob) < 10) { Status = L.T("The two points are too close together."); return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Status = L.T("Fetching terrain elevation…");
        try
        {
            _raw = (await Core.Tools.FetchElevationProfileAsync(la, loa, lb, lob, ct: _cts.Token)).ToList();
            Core.Settings.Update(s => { s.LosHeightA = HeightA; s.LosHeightB = HeightB; s.LosRefractionK = RefractionK; });
            Recompute();
            var bearing = Bearing(la, loa, lb, lob);
            BearingText = $"{bearing:0}° ({Compass(bearing)})";
            Status = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Uses an already-fetched elevation profile (e.g. from a test or cache).</summary>
    public void ApplyProfile(IReadOnlyList<ElevationSample> profile)
    {
        _raw = profile.ToList();
        Recompute();
    }

    private void Recompute()
    {
        if (_raw is not { Count: >= 2 }) return;
        var result = RfCalculator.Analyze(_raw, HeightA, HeightB, FrequencyMHz, RefractionK);
        Profile = _raw.ToList();
        HasResult = true;
        (Verdict, VerdictColor) = result.ClearanceStatus switch
        {
            ClearanceStatus.Clear => (L.T("Clear line of sight"), "#22A06B"),
            ClearanceStatus.Marginal => (L.T("Marginal — Fresnel zone partly obstructed"), "#F59E0B"),
            ClearanceStatus.PartialObstruction => (L.T("Partially obstructed"), "#F97316"),
            _ => (L.T("Blocked by terrain"), "#E5484D"),
        };
        DistanceText = Formatters.Distance(result.DistanceMeters);
        FsplText = $"{result.FreeSpacePathLoss:0.0} dB";
        DiffractionText = $"{result.PeakDiffractionLoss:0.0} dB";
        TotalLossText = $"{result.TotalPathLoss:0.0} dB";
        ClearanceText = L.F("{0:0}% of first Fresnel zone", result.WorstClearancePercent);
        var min = _raw.Min(s => s.Elevation);
        var max = _raw.Max(s => s.Elevation);
        ElevationText = Metric ? $"{min:0}–{max:0} m" : $"{min * 3.28084:0}–{max * 3.28084:0} ft";
        if (Core.SelfInfo is { } self)
        {
            var sens = RfCalculator.LoraSensitivity(self.RadioSpreadingFactor, self.RadioBandwidth);
            var margin = self.TxPower + 4 - result.TotalPathLoss - sens; // assumes ~2 dBi antennas at each end
            MarginText = L.F("{0:0} dB (at {1} dBm, SF{2}/{3:0.#} kHz, 2 dBi antennas)", margin, self.TxPower, self.RadioSpreadingFactor, self.RadioBandwidth);
        }
        else MarginText = L.T("Connect a radio to estimate the link budget");
    }

    private static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180;
        var p2 = lat2 * Math.PI / 180;
        var dl = (lon2 - lon1) * Math.PI / 180;
        var y = Math.Sin(dl) * Math.Cos(p2);
        var x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>Compass point abbreviation (translated: e.g. "O" for east in German).</summary>
    private static string Compass(double b) => ((int)Math.Round(b / 45) % 8) switch
    {
        0 => L.T("N"),
        1 => L.T("NE"),
        2 => L.T("E"),
        3 => L.T("SE"),
        4 => L.T("S"),
        5 => L.T("SW"),
        6 => L.T("W"),
        _ => L.T("NW"),
    };

    [RelayCommand]
    private void ShowOnMap()
    {
        if (!TryCoord(LatA, -90, 90, out var la) || !TryCoord(LonA, -180, 180, out var loa) || !TryCoord(LatB, -90, 90, out var lb) || !TryCoord(LonB, -180, 180, out var lob)) return;
        _main.Navigate(Page.Map);
        _main.Map.ShowSegment((la, loa, NameA), (lb, lob, NameB));
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MC1.Core.Utilities;

public enum RadioRegion { NorthAmerica, SouthAmerica, Europe, Oceania, Asia }

public sealed record RadioPreset(
    string Id, string Name, RadioRegion Region, double FrequencyMHz, double BandwidthKHz, byte SpreadingFactor, byte CodingRate,
    string? RepeatSectionHeader = null, int? PathHashSize = null, string[]? Countries = null)
{
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

/// <summary>Community radio presets (ported from MC1Services RadioPresets).</summary>
public static class RadioPresets
{
    public static readonly IReadOnlyList<RadioPreset> All =
    [
        new("au-915", "Australia", RadioRegion.Oceania, 915.800, 250, 10, 5, Countries: ["AU"]),
        new("au-narrow", "Australia (Narrow)", RadioRegion.Oceania, 916.575, 62.5, 7, 8, Countries: ["AU"]),
        new("au-mid", "Australia (Mid)", RadioRegion.Oceania, 915.075, 125, 9, 5, Countries: ["AU"]),
        new("au-sa-wa", "Australia: SA, WA", RadioRegion.Oceania, 923.125, 62.5, 8, 8, Countries: ["AU"]),
        new("au-qld", "Australia: QLD", RadioRegion.Oceania, 923.125, 62.5, 8, 5, Countries: ["AU"]),
        new("nz-lr", "New Zealand (Gisborne)", RadioRegion.Oceania, 917.375, 250, 11, 5, PathHashSize: 1, Countries: ["NZ"]),
        new("nz-narrow", "New Zealand (Narrow)", RadioRegion.Oceania, 917.375, 62.5, 7, 5, PathHashSize: 2, Countries: ["NZ"]),
        new("eu-narrow", "EU/UK (Narrow)", RadioRegion.Europe, 869.618, 62.5, 8, 8),
        new("eu-lr", "EU/UK (Deprecated)", RadioRegion.Europe, 869.525, 250, 11, 5),
        new("cz-narrow", "Czech Republic (Narrow)", RadioRegion.Europe, 869.432, 62.5, 7, 5, Countries: ["CZ"]),
        new("eu-433-lr", "EU 433MHz (Long Range)", RadioRegion.Europe, 433.650, 250, 11, 5),
        new("eu-433-narrow", "EU 433MHz (Narrow)", RadioRegion.Europe, 433.650, 62.5, 8, 8),
        new("pt-433", "Portugal 433", RadioRegion.Europe, 433.375, 62.5, 9, 6, Countries: ["PT"]),
        new("pt-868", "Portugal 868", RadioRegion.Europe, 869.618, 62.5, 7, 6, Countries: ["PT"]),
        new("ch", "Switzerland", RadioRegion.Europe, 869.618, 62.5, 8, 8, Countries: ["CH"]),
        new("hu", "Hungary", RadioRegion.Europe, 869.618, 62.5, 7, 5, PathHashSize: 2, Countries: ["HU"]),
        new("nl", "Netherlands", RadioRegion.Europe, 869.618, 62.5, 7, 5, Countries: ["NL"]),
        new("nl-li", "Netherlands (Limburg)", RadioRegion.Europe, 869.618, 62.5, 8, 8, PathHashSize: 2, Countries: ["NL"]),
        new("sk", "Slovakia", RadioRegion.Europe, 869.618, 62.5, 7, 5, PathHashSize: 2, Countries: ["SK"]),
        new("us-ca", "USA", RadioRegion.NorthAmerica, 910.525, 62.5, 7, 5, Countries: ["US"]),
        new("ca", "Canada", RadioRegion.NorthAmerica, 910.525, 62.5, 7, 5, PathHashSize: 3, Countries: ["CA"]),
        new("cr", "Costa Rica", RadioRegion.NorthAmerica, 910.525, 125, 11, 5, Countries: ["CR"]),
        new("wcmesh", "WCMesh (SoCal)", RadioRegion.NorthAmerica, 927.875, 62.5, 7, 5, PathHashSize: 3, Countries: ["US"]),
        new("lvmesh", "LVMesh", RadioRegion.NorthAmerica, 910.525, 500, 10, 5, PathHashSize: 2, Countries: ["US"]),
        new("cl", "Chile", RadioRegion.SouthAmerica, 927.875, 62.5, 8, 5, Countries: ["CL"]),
        new("br", "Brazil", RadioRegion.SouthAmerica, 923.125, 62.5, 8, 8, Countries: ["BR"]),
        new("vn-narrow", "Vietnam (Narrow)", RadioRegion.Asia, 920.250, 62.5, 8, 5, Countries: ["VN"]),
        new("vn", "Vietnam (Deprecated)", RadioRegion.Asia, 920.250, 250, 11, 5, Countries: ["VN"]),
    ];

    public static readonly IReadOnlyList<RadioPreset> RepeatPresets =
    [
        new("repeat-433", "433 MHz", RadioRegion.Europe, 433.000, 62.5, 9, 8, RepeatSectionHeader: "EU/Asia"),
        new("repeat-869", "869 MHz", RadioRegion.Europe, 869.495, 62.5, 8, 8, RepeatSectionHeader: "EU"),
        new("repeat-918", "918 MHz", RadioRegion.NorthAmerica, 918.000, 62.5, 7, 8, RepeatSectionHeader: "US/AU/NZ"),
    ];

    public static IReadOnlyList<RadioRegion> RegionsForCountry(string? country) => country switch
    {
        "US" or "CA" or "CR" => [RadioRegion.NorthAmerica, RadioRegion.Europe, RadioRegion.Oceania, RadioRegion.Asia, RadioRegion.SouthAmerica],
        "AU" or "NZ" => [RadioRegion.Oceania, RadioRegion.NorthAmerica, RadioRegion.Europe, RadioRegion.Asia, RadioRegion.SouthAmerica],
        "GB" or "DE" or "FR" or "IT" or "ES" or "PT" or "CH" or "CZ" or "IE" or "NL" or "BE" or "AT" or "HU" or "SK" =>
            [RadioRegion.Europe, RadioRegion.NorthAmerica, RadioRegion.Oceania, RadioRegion.Asia, RadioRegion.SouthAmerica],
        "VN" or "TH" or "MY" or "SG" or "PH" or "ID" => [RadioRegion.Asia, RadioRegion.Oceania, RadioRegion.Europe, RadioRegion.NorthAmerica, RadioRegion.SouthAmerica],
        "CL" or "BR" => [RadioRegion.SouthAmerica, RadioRegion.NorthAmerica, RadioRegion.Europe, RadioRegion.Oceania, RadioRegion.Asia],
        _ => Enum.GetValues<RadioRegion>(),
    };

    public static IReadOnlyList<RadioPreset> PresetsForCurrentLocale()
    {
        string? country = null;
        try { country = RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch { /* invariant */ }
        var order = RegionsForCountry(country).ToList();
        return All.OrderBy(p => order.IndexOf(p.Region) < 0 ? 99 : order.IndexOf(p.Region)).ThenBy(p => p.Name).ToList();
    }

    public static IReadOnlyList<RadioPreset> Matching(double freqMHz, double bwKHz, byte sf, byte cr) =>
        All.Where(p => Math.Abs(p.FrequencyMHz - freqMHz) < 0.1 && Math.Abs(p.BandwidthKHz - bwKHz) < 1.0 && p.SpreadingFactor == sf && p.CodingRate == cr).ToList();

    /// <summary>The recommended preset for a country (ISO code), used when several presets share the radio's settings.</summary>
    public static string? RecommendedFor(string? country) => country?.ToUpperInvariant() switch
    {
        "US" or "PR" or "VI" or "GU" or "AS" or "MP" or "UM" => "us-ca",
        "CA" => "ca",
        "CR" => "cr",
        "AU" => "au-915",
        "NZ" => "nz-narrow",
        "GB" or "IE" or "DE" or "FR" or "IT" or "ES" or "BE" or "AT" or "DK" or "SE" or "NO" or "FI" or "PL" => "eu-narrow",
        "CZ" => "cz-narrow",
        "PT" => "pt-868",
        "CH" => "ch",
        "HU" => "hu",
        "NL" => "nl",
        "SK" => "sk",
        "CL" => "cl",
        "BR" => "br",
        "VN" => "vn-narrow",
        _ => null,
    };

    private static string? CurrentCountry()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch { return null; }
    }

    /// <summary>
    /// The preset the radio is on, like the iPhone app: the one last applied if it still matches, then the one
    /// recommended for this PC's country, then the first match (presets can share settings, e.g. USA and Canada).
    /// Null when the radio uses custom settings.
    /// </summary>
    public static RadioPreset? Resolve(double freqMHz, double bwKHz, byte sf, byte cr, string? preferredId, string? country = null)
    {
        var matches = Matching(freqMHz, bwKHz, sf, cr);
        if (matches.Count == 0) return null;
        if (preferredId is not null && matches.FirstOrDefault(p => p.Id == preferredId) is { } preferred) return preferred;
        if (RecommendedFor(country ?? CurrentCountry()) is { } rec && matches.FirstOrDefault(p => p.Id == rec) is { } recommended) return recommended;
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
    double FrequencyMHz, double RefractionK);

/// <summary>RF line-of-sight maths (Fresnel zone, earth bulge, FSPL, knife-edge diffraction).</summary>
public static class RfCalculator
{
    public const double SpeedOfLight = 299_792_458;
    public const double EarthRadiusKm = 6371;
    public const double ClearThreshold = 80;
    public const double MarginalThreshold = 60;

    public static double Wavelength(double fMHz) => fMHz <= 0 ? 0 : SpeedOfLight / (fMHz * 1_000_000);

    public static double FresnelRadius(double fMHz, double dA, double dB)
    {
        if (fMHz <= 0 || dA <= 0 || dB <= 0) return 0;
        return Math.Sqrt(Wavelength(fMHz) * dA * dB / (dA + dB));
    }

    public static double EarthBulge(double dA, double dB, double k) =>
        dA <= 0 || dB <= 0 || k <= 0 ? 0 : dA * dB / (2 * k * EarthRadiusKm * 1000);

    public static double PathLoss(double dMeters, double fMHz) =>
        dMeters <= 0 || fMHz <= 0 ? 0 : 20 * Math.Log10(dMeters) + 20 * Math.Log10(fMHz) - 27.55;

    public static double DiffractionLoss(double h, double dA, double dB, double fMHz)
    {
        if (dA <= 0 || dB <= 0 || fMHz <= 0) return 0;
        var lambda = Wavelength(fMHz);
        var v = h * Math.Sqrt(2 * (dA + dB) / (lambda * dA * dB));
        if (v <= -0.78) return 0;
        var s = v - 0.1;
        return 6.9 + 20 * Math.Log10(Math.Sqrt(s * s + 1) + s);
    }

    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        var r = EarthRadiusKm * 1000;
        var p1 = lat1 * Math.PI / 180;
        var p2 = lat2 * Math.PI / 180;
        var dp = (lat2 - lat1) * Math.PI / 180;
        var dl = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Evenly spaced great-circle-ish sample points between A and B (linear interpolation is fine at mesh ranges).</summary>
    public static IReadOnlyList<(double Lat, double Lon)> SamplePath(double lat1, double lon1, double lat2, double lon2, int count)
    {
        count = Math.Max(2, count);
        return Enumerable.Range(0, count).Select(i =>
        {
            var t = (double)i / (count - 1);
            return (lat1 + (lat2 - lat1) * t, lon1 + (lon2 - lon1) * t);
        }).ToList();
    }

    public static int SampleCountForDistance(double meters) => meters switch
    {
        < 1000 => 20,
        < 5000 => 50,
        < 20000 => 80,
        _ => 100,
    };

    public static PathAnalysisResult Analyze(IReadOnlyList<ElevationSample> profile, double heightA, double heightB, double fMHz, double k)
    {
        if (profile.Count < 2) return Empty(fMHz, k);
        var first = profile[0];
        var last = profile[^1];
        var origin = first.DistanceFromAMeters;
        var length = last.DistanceFromAMeters - origin;
        if (length <= 0) return Empty(fMHz, k);
        var startH = first.Elevation + heightA;
        var endH = last.Elevation + heightB;
        var fspl = PathLoss(length, fMHz);
        var worst = double.PositiveInfinity;
        var peakDiff = 0.0;
        var obstructions = new List<ObstructionPoint>();
        foreach (var s in profile)
        {
            var dA = s.DistanceFromAMeters - origin;
            var dB = length - dA;
            if (dA <= 1 || dB <= 1) continue;
            var los = startH + dA / length * (endH - startH);
            var terrain = s.Elevation + EarthBulge(dA, dB, k);
            var fr = FresnelRadius(fMHz, dA, dB);
            var clearance = los - terrain;
            var pct = fr > 0 ? clearance / fr * 100 : clearance > 0 ? 100 : 0;
            worst = Math.Min(worst, pct);
            var obstructionH = terrain - los;
            if (obstructionH > -fr) peakDiff = Math.Max(peakDiff, DiffractionLoss(obstructionH, dA, dB, fMHz));
            if (pct < MarginalThreshold) obstructions.Add(new ObstructionPoint(s.DistanceFromAMeters, obstructionH, pct));
        }
        if (double.IsPositiveInfinity(worst)) worst = 100;
        var status = worst >= ClearThreshold ? ClearanceStatus.Clear : worst >= MarginalThreshold ? ClearanceStatus.Marginal
            : worst >= 0 ? ClearanceStatus.PartialObstruction : ClearanceStatus.Blocked;
        return new PathAnalysisResult(length, fspl, peakDiff, fspl + peakDiff, status, worst, obstructions, fMHz, k);
    }

    private static PathAnalysisResult Empty(double f, double k) => new(0, 0, 0, 0, ClearanceStatus.Blocked, 0, [], f, k);

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

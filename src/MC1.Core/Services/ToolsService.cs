using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

public sealed record TraceHopResult(byte[]? Hash, string Label, double Snr);

public sealed record TraceRunResult(bool Success, IReadOnlyList<TraceHopResult> Hops, int RoundTripMs, string? Error, byte[] Path, int HashSize);

public sealed record DiscoveredNeighbour(byte[] PublicKey, ContactType Type, double SnrIn, double Snr, int Rssi, string? Name, DateTimeOffset HeardAt);

public sealed record NoiseReading(DateTimeOffset Time, short NoiseFloor, sbyte LastRssi, double LastSnr);

/// <summary>Network tools: trace path, zero-hop node discovery, noise floor monitor, elevation lookups.</summary>
public sealed class ToolsService
{
    private readonly MeshApp _app;
    private readonly HttpClient _http;
    private CancellationTokenSource? _noiseCts;

    public ToolsService(MeshApp app)
    {
        _app = app;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCore-Windows/1.0");
    }

    internal void OnDisconnected() => StopNoiseMonitor();

    // MARK: Trace path

    /// <summary>Builds the full trace path (outbound, then the mirrored return unless it ends at a repeater that answers).</summary>
    public static byte[] BuildTracePath(IReadOnlyList<byte[]> outboundHops, bool roundTrip)
    {
        var hops = new List<byte[]>(outboundHops);
        if (roundTrip) hops.AddRange(outboundHops.Reverse().Skip(1));
        return hops.SelectMany(h => h).ToArray();
    }

    public async Task<TraceRunResult> RunTraceAsync(byte[] path, byte hashMode, CancellationToken ct = default)
    {
        var session = _app.RequireSession();
        var hashSize = 1 << hashMode;
        var tag = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        var tcs = new TaskCompletionSource<TraceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(MeshEvent e) { if (e is MeshEvent.TraceData t && t.Info.Tag == tag) tcs.TrySetResult(t.Info); }
        _app.RadioEvent += OnEvent;
        var started = DateTime.UtcNow;
        try
        {
            MessageSentInfo sent;
            try { sent = await session.SendTraceAsync(tag, 0, hashMode, path, ct).ConfigureAwait(false); }
            catch (Exception ex) { return new TraceRunResult(false, [], 0, L.F("Couldn't send trace: {0}", ex.Message), path, hashSize); }
            var timeout = sent.SuggestedTimeoutMs == 0 ? 30 : Math.Clamp(sent.SuggestedTimeoutMs / 1000.0 * 1.2 + 8, 5, 60);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeout), ct)).ConfigureAwait(false);
            if (done != tcs.Task) return new TraceRunResult(false, [], 0, L.T("No response (timed out)"), path, hashSize);
            var info = await tcs.Task.ConfigureAwait(false);
            var rtt = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            var hops = info.Path.Select((n, i) => new TraceHopResult(n.HashBytes,
                n.HashBytes is null ? (i == info.Path.Count - 1 ? L.T("Back to you") : L.T("Destination")) : (_app.Contacts.ResolveHopName(n.HashBytes) ?? Convert.ToHexString(n.HashBytes)),
                n.Snr)).ToList();
            return new TraceRunResult(true, hops, rtt, null, path, hashSize);
        }
        finally { _app.RadioEvent -= OnEvent; }
    }

    public IReadOnlyList<TracePathRecord> SavedPaths() => _app.RadioId is { } id ? _app.Db.GetTracePaths(id) : [];

    public TracePathRecord SavePath(string name, byte[] path, int hashSize, TraceRunResult? lastResult = null)
    {
        var rec = new TracePathRecord
        {
            RadioId = _app.RequireRadioId(), Name = name, Path = path, HashSize = hashSize,
            LastRunAt = lastResult is null ? null : Time.Now(),
            LastResultJson = lastResult is null ? null : JsonSerializer.Serialize(lastResult.Hops.Select(h => new { h.Label, h.Snr })),
        };
        _app.Db.SaveTracePath(rec);
        _app.Notify(DataKind.TracePaths);
        return rec;
    }

    public void DeletePath(TracePathRecord p) { _app.Db.DeleteTracePath(p.Id); _app.Notify(DataKind.TracePaths); }

    // MARK: Node discovery

    /// <summary>Broadcasts a zero-hop discovery request and collects replies for <paramref name="listen"/>.</summary>
    public async Task<IReadOnlyList<DiscoveredNeighbour>> DiscoverNodesAsync(bool repeaters, bool rooms, bool sensors, TimeSpan listen, Action<DiscoveredNeighbour>? onFound = null, CancellationToken ct = default)
    {
        var session = _app.RequireSession();
        byte filter = 0;
        if (repeaters) filter |= 1 << (int)ContactType.Repeater;
        if (rooms) filter |= 1 << (int)ContactType.Room;
        if (sensors) filter |= 1 << (int)ContactType.Sensor;
        var results = new List<DiscoveredNeighbour>();
        uint tag = 0;
        var names = _app.RadioId is { } rid ? _app.Db.GetContacts(rid) : [];
        void OnEvent(MeshEvent e)
        {
            if (e is not MeshEvent.DiscoverResponseEvent { Response: var r } || (tag != 0 && r.Tag.ReadUInt32LE(0) != tag)) return;
            var type = Enum.IsDefined(typeof(ContactType), r.NodeType) ? (ContactType)r.NodeType : ContactType.Repeater;
            var name = names.FirstOrDefault(c => c.PublicKey.StartsWith(r.PublicKey))?.DisplayName;
            var n = new DiscoveredNeighbour(r.PublicKey, type, r.SnrIn, r.Snr, r.Rssi, name, DateTimeOffset.Now);
            lock (results)
            {
                if (results.Any(x => x.PublicKey.SequenceEquals(n.PublicKey))) return;
                results.Add(n);
            }
            onFound?.Invoke(n);
        }
        _app.RadioEvent += OnEvent;
        try
        {
            tag = await session.SendNodeDiscoverRequestAsync(filter, prefixOnly: false, ct: ct).ConfigureAwait(false);
            await Task.Delay(listen, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { _app.RadioEvent -= OnEvent; }
        lock (results) return results.ToList();
    }

    // MARK: Noise floor

    public event Action<NoiseReading>? NoiseReadingAdded;
    public bool IsNoiseMonitoring => _noiseCts is not null;

    public void StartNoiseMonitor(TimeSpan? interval = null)
    {
        StopNoiseMonitor();
        var cts = new CancellationTokenSource();
        _noiseCts = cts;
        var every = interval ?? TimeSpan.FromSeconds(1.5);
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var s = _app.Session;
                    if (s is { IsRunning: true })
                    {
                        var r = await s.GetStatsRadioAsync(cts.Token).ConfigureAwait(false);
                        NoiseReadingAdded?.Invoke(new NoiseReading(DateTimeOffset.Now, r.NoiseFloor, r.LastRssi, r.LastSnr));
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _app.Log.Debug("Noise", ex.Message); }
                try { await Task.Delay(every, cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        });
    }

    public void StopNoiseMonitor()
    {
        _noiseCts?.Cancel();
        _noiseCts = null;
    }

    /// <summary>The noise floor rating as a fixed English key ("Excellent", "Good", "Fair", "Poor") for comparisons.</summary>
    public static string NoiseQualityKey(short nf) => nf switch
    {
        <= -100 => "Excellent",
        <= -90 => "Good",
        <= -80 => "Fair",
        _ => "Poor",
    };

    /// <summary>The noise floor rating to show (in the app's language).</summary>
    public static string NoiseQuality(short nf) => NoiseQualityKey(nf) switch
    {
        "Excellent" => L.T("Excellent"),
        "Good" => L.T("Good"),
        "Fair" => L.T("Fair"),
        _ => L.T("Poor"),
    };

    // MARK: Elevation (line of sight)

    /// <summary>Terrain elevations from Open-Meteo (max 100 points per request).</summary>
    public async Task<IReadOnlyList<ElevationSample>> FetchElevationProfileAsync(double lat1, double lon1, double lat2, double lon2, IReadOnlyList<(double Lat, double Lon)>? via = null, CancellationToken ct = default)
    {
        var waypoints = new List<(double Lat, double Lon)> { (lat1, lon1) };
        if (via is not null) waypoints.AddRange(via);
        waypoints.Add((lat2, lon2));
        var total = 0.0;
        for (var i = 1; i < waypoints.Count; i++) total += RfCalculator.Distance(waypoints[i - 1].Lat, waypoints[i - 1].Lon, waypoints[i].Lat, waypoints[i].Lon);
        var count = RfCalculator.SampleCountForDistance(total);
        var points = new List<(double Lat, double Lon, double Dist)>();
        var acc = 0.0;
        for (var i = 1; i < waypoints.Count; i++)
        {
            var seg = RfCalculator.Distance(waypoints[i - 1].Lat, waypoints[i - 1].Lon, waypoints[i].Lat, waypoints[i].Lon);
            var n = Math.Max(2, (int)Math.Round(count * (total <= 0 ? 1 : seg / total)));
            var samples = RfCalculator.SamplePath(waypoints[i - 1].Lat, waypoints[i - 1].Lon, waypoints[i].Lat, waypoints[i].Lon, n);
            for (var j = i == 1 ? 0 : 1; j < samples.Count; j++)
                points.Add((samples[j].Lat, samples[j].Lon, acc + seg * j / (samples.Count - 1)));
            acc += seg;
        }
        if (points.Count > 100)
        {
            var step = (points.Count - 1) / 99.0;
            points = Enumerable.Range(0, 100).Select(i => points[(int)Math.Round(i * step)]).ToList();
        }
        var inv = CultureInfo.InvariantCulture;
        var url = "https://api.open-meteo.com/v1/elevation?latitude=" + string.Join(",", points.Select(p => p.Lat.ToString("0.000000", inv))) +
                  "&longitude=" + string.Join(",", points.Select(p => p.Lon.ToString("0.000000", inv)));
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var resp = await _http.GetFromJsonAsync<ElevationResponse>(url, ct).ConfigureAwait(false);
                if (resp?.Elevation is not { } elev || elev.Count != points.Count) throw new InvalidDataException(L.T("Unexpected elevation response"));
                return points.Select((p, i) => new ElevationSample(p.Lat, p.Lon, elev[i], p.Dist)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                await Task.Delay(500 * (1 << attempt), ct).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException(L.F("Couldn't fetch terrain data: {0}", last?.Message), last);
    }

    private sealed record ElevationResponse(List<double>? Elevation);
}

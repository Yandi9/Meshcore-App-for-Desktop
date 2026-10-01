using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Avalonia.Platform;
using MC1.Core.Services;

namespace MC1.Windows.Services.VectorTiles;

/// <summary>
/// OpenFreeMap vector tiles drawn with the "dark" style (https://tiles.openfreemap.org/styles/dark).
/// The style and the tile address list are fetched once and cached; vector tiles are cached on disk so zooming in
/// past zoom 14 and redrawing never downloads them again. A copy of the style ships with the app for offline use.
/// </summary>
public sealed class OpenFreeMapSource
{
    public const string StyleUrl = "https://tiles.openfreemap.org/styles/dark";
    private const string BundledStyle = "avares://MeshCoreOne/Assets/Map/openfreemap-dark.json";
    private static readonly TimeSpan StyleMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan TileJsonMaxAge = TimeSpan.FromDays(1);

    public static OpenFreeMapSource Dark { get; } = new();

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _init = new(1, 1);
    private readonly SemaphoreSlim _downloads = new(6, 6);
    private readonly Dictionary<string, Task<VectorTile?>> _parsed = new();
    private readonly LinkedList<string> _parsedOrder = new();
    private MapStyle? _style;
    private string? _tileTemplate;
    private int _maxZoom = 14;

    private OpenFreeMapSource()
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCore-Windows/1.0 (+https://github.com/meshcore-dev)");
    }

    private static string Folder => Path.Combine(AppPaths.TileCache, "_openfreemap");

    /// <summary>Renders the 256-px raster tile z/x/y as PNG bytes, or null when the data can't be had.</summary>
    public async Task<byte[]?> RenderTileAsync(int z, int x, int y, bool offlineOnly, CancellationToken ct = default)
    {
        var style = await GetStyleAsync(offlineOnly, ct).ConfigureAwait(false);
        var styleZoom = Math.Max(0, z - 1);
        var dataZ = Math.Min(styleZoom, _maxZoom);
        var k = z - dataZ;
        var dx = x >> k;
        var dy = y >> k;
        var tile = await GetVectorTileAsync(dataZ, dx, dy, offlineOnly, ct).ConfigureAwait(false);
        if (tile is null) return null;
        var ox = x - (dx << k);
        var oy = y - (dy << k);
        return await Task.Run(() => VectorTileRenderer.RenderPng(style, tile, styleZoom, k, ox, oy), ct).ConfigureAwait(false);
    }

    // MARK: Style

    public async Task<MapStyle> GetStyleAsync(bool offlineOnly = false, CancellationToken ct = default)
    {
        if (_style is not null) return _style;
        await _init.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_style is not null) return _style;
            var path = Path.Combine(Folder, "dark-style.json");
            string? json = null;
            var fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < StyleMaxAge;
            if (!fresh && !offlineOnly)
            {
                try
                {
                    json = await _http.GetStringAsync(StyleUrl, ct).ConfigureAwait(false);
                    MapStyle.Parse(json); // validate before caching
                    Directory.CreateDirectory(Folder);
                    await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { json = null; }
            }
            if (json is null && File.Exists(path)) json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (json is null)
            {
                using var s = AssetLoader.Open(new Uri(BundledStyle));
                using var reader = new StreamReader(s);
                json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            var style = MapStyle.Parse(json);
            _maxZoom = Math.Clamp(style.SourceMaxZoom, 0, 16);
            _style = style;
            return style;
        }
        finally { _init.Release(); }
    }

    /// <summary>The vector tile URL template, from the style or its TileJSON (e.g. …/planet/20250901_001001_pt/{z}/{x}/{y}.pbf).</summary>
    private async Task<string?> GetTileTemplateAsync(CancellationToken ct)
    {
        if (_tileTemplate is not null) return _tileTemplate;
        var style = await GetStyleAsync(false, ct).ConfigureAwait(false);
        if (style.SourceTiles.Count > 0) return _tileTemplate = style.SourceTiles[0];
        if (style.SourceUrl is not { } url) return null;
        var path = Path.Combine(Folder, "tilejson.json");
        string? json = null;
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TileJsonMaxAge)
            json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        if (json is null)
        {
            try
            {
                json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                Directory.CreateDirectory(Folder);
                await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (File.Exists(path)) json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
        }
        if (json is null || JsonNode.Parse(json) is not JsonObject tj || tj["tiles"] is not JsonArray tiles || tiles.Count == 0) return null;
        if (tj["maxzoom"] is JsonValue mz && mz.TryGetValue<double>(out var m)) _maxZoom = Math.Clamp((int)m, 0, 16);
        return _tileTemplate = (string?)tiles[0];
    }

    // MARK: Vector tiles

    private Task<VectorTile?> GetVectorTileAsync(int z, int x, int y, bool offlineOnly, CancellationToken ct)
    {
        var key = $"{z}/{x}/{y}";
        lock (_parsed)
        {
            if (_parsed.TryGetValue(key, out var existing) && !existing.IsFaulted && !existing.IsCanceled && !(existing.IsCompletedSuccessfully && existing.Result is null)) return existing;
            var task = LoadVectorTileAsync(z, x, y, offlineOnly, ct);
            _parsed[key] = task;
            _parsedOrder.AddLast(key);
            // Parsed tiles are shared by the 4+ raster tiles drawn from them; keep a few dozen.
            while (_parsedOrder.Count > 48)
            {
                _parsed.Remove(_parsedOrder.First!.Value);
                _parsedOrder.RemoveFirst();
            }
            return task;
        }
    }

    private async Task<VectorTile?> LoadVectorTileAsync(int z, int x, int y, bool offlineOnly, CancellationToken ct)
    {
        var bytes = await GetPbfAsync(z, x, y, offlineOnly, ct).ConfigureAwait(false);
        if (bytes is null) return null;
        return await Task.Run(() => MvtReader.Read(Ungzip(bytes)), ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> GetPbfAsync(int z, int x, int y, bool offlineOnly, CancellationToken ct)
    {
        var path = Path.Combine(Folder, "pbf", z.ToString(), x.ToString(), y + ".pbf");
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (offlineOnly) return null;
        var template = await GetTileTemplateAsync(ct).ConfigureAwait(false);
        if (template is null) return null;
        await _downloads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var url = template.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            // Empty ocean/desert tiles come back as 204 / empty: draw them as just the background.
            if (resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return [];
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        finally { _downloads.Release(); }
    }

    private static byte[] Ungzip(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B) return data;
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Forgets cached style/tile addresses (after the tile cache is cleared).</summary>
    public void Reset()
    {
        lock (_parsed) { _parsed.Clear(); _parsedOrder.Clear(); }
        _style = null;
        _tileTemplate = null;
    }
}

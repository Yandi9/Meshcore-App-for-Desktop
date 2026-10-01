using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Services;

public sealed record LinkPreview(string? Title, string Host, Bitmap? Image);

/// <summary>Fetches page titles/OpenGraph images and inline images for chat bubbles (cached in the DB and on disk).</summary>
public sealed partial class LinkPreviewService
{
    public static LinkPreviewService Instance { get; } = new();

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _images = new();
    private readonly SemaphoreSlim _gate = new(4, 4);

    private LinkPreviewService()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MeshCore/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,image/*;q=0.9,*/*;q=0.5");
    }

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](og:title|twitter:title)[""'][^>]*content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegex();

    [GeneratedRegex(@"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]*(?:property|name)\s*=\s*[""'](og:title|twitter:title)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegexReversed();

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](og:image|twitter:image)[""'][^>]*content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRegex();

    [GeneratedRegex(@"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    private static bool IsSafe(Uri u)
    {
        if (u.Scheme is not ("http" or "https")) return false;
        if (u.IsLoopback) return false;
        if (IPAddress.TryParse(u.Host, out var ip))
        {
            var b = ip.GetAddressBytes();
            if (b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127 || (b[0] == 169 && b[1] == 254))) return false;
        }
        return true;
    }

    public async Task<LinkPreview?> GetPreviewAsync(MessageRecord m, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsSafe(uri)) return null;
        var core = AppHost.Core;
        if (m.LinkPreviewFetched)
        {
            Bitmap? cachedImage = null;
            if (m.LinkPreviewImage is { Length: > 0 } bytes)
            {
                try { cachedImage = Bitmap.DecodeToWidth(new MemoryStream(bytes), 480); } catch { /* ignore */ }
            }
            return new LinkPreview(m.LinkPreviewTitle, uri.Host, cachedImage);
        }
        await _gate.WaitAsync();
        try
        {
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) { core.Db.SetLinkPreview(m.Id, url, null, null); return null; }
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.Contains("html")) { core.Db.SetLinkPreview(m.Id, url, null, null); return null; }
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var buffer = new byte[256 * 1024];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read))) > 0) read += n;
            var html = Encoding.UTF8.GetString(buffer, 0, read);
            var title = FirstGroup(OgTitleRegex(), html, 2) ?? FirstGroup(OgTitleRegexReversed(), html, 1) ?? FirstGroup(TitleRegex(), html, 1);
            if (title is not null) title = WebUtility.HtmlDecode(title.Trim());
            var imageUrl = FirstGroup(OgImageRegex(), html, 2);
            byte[]? imageBytes = null;
            Bitmap? image = null;
            if (imageUrl is not null && Uri.TryCreate(uri, WebUtility.HtmlDecode(imageUrl), out var imgUri) && IsSafe(imgUri))
            {
                try
                {
                    imageBytes = await DownloadCapped(imgUri, 2 * 1024 * 1024);
                    if (imageBytes is not null) image = Bitmap.DecodeToWidth(new MemoryStream(imageBytes), 480);
                }
                catch { imageBytes = null; image = null; }
            }
            core.Db.SetLinkPreview(m.Id, url, title, imageBytes);
            return new LinkPreview(title, uri.Host, image);
        }
        catch (Exception ex)
        {
            core.Log.Debug("LinkPreview", $"{url}: {ex.Message}");
            return null;
        }
        finally { _gate.Release(); }
    }

    private static string? FirstGroup(Regex r, string html, int group)
    {
        var m = r.Match(html);
        return m.Success ? m.Groups[group].Value : null;
    }

    private async Task<byte[]?> DownloadCapped(Uri uri, int max)
    {
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return null;
        if (resp.Content.Headers.ContentLength > max) return null;
        await using var s = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max) return null;
        }
        return ms.ToArray();
    }

    /// <summary>Downloads (and disk-caches) an image for inline display.</summary>
    public Task<Bitmap?> LoadImageAsync(string url, int decodeWidth) => _images.GetOrAdd(url + "|" + decodeWidth, _ => LoadImageCore(url, decodeWidth));

    private async Task<Bitmap?> LoadImageCore(string url, int decodeWidth)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsSafe(uri)) return null;
        var cacheDir = AppPaths.ImageCache;
        Directory.CreateDirectory(cacheDir);
        var file = Path.Combine(cacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32]);
        byte[]? bytes = null;
        if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
        else
        {
            await _gate.WaitAsync();
            try { bytes = await DownloadCapped(uri, 8 * 1024 * 1024); }
            catch { bytes = null; }
            finally { _gate.Release(); }
            if (bytes is not null) await File.WriteAllBytesAsync(file, bytes);
        }
        if (bytes is null) return null;
        try { return Bitmap.DecodeToWidth(new MemoryStream(bytes), decodeWidth); }
        catch { return null; }
    }
}

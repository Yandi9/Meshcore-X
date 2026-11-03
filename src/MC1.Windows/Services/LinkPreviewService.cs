using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Services;

public sealed record LinkPreview(string? Title, string Host, Bitmap? Image);

/// <summary>An inline picture, or why there is none.</summary>
/// <param name="ServesPage">The image link is really a web page (imgur, prnt.sc…): show a link preview instead.</param>
public sealed record InlineImageResult(Bitmap? Image, bool ServesPage);

/// <summary>
/// Fetches page titles/OpenGraph images and inline images for chat bubbles (cached in the DB and on disk). Like the
/// iPhone app: everything runs off the UI thread so messages show at once, redirects are checked hop by hop, preview
/// pages and their images are fetched over https, and downloads stop as soon as they pass their size limit.
/// </summary>
public sealed partial class LinkPreviewService
{
    public static LinkPreviewService Instance { get; } = new();

    /// <summary>Only the top of a page is read (its title and image tags are there).</summary>
    private const int PageBytes = 256 * 1024;
    private const int PreviewImageBytes = 2 * 1024 * 1024;
    private const int InlineImageBytes = 8 * 1024 * 1024;

    private readonly SafeWebFetcher _web = new(userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MeshCore/1.0");
    private readonly ConcurrentDictionary<string, Task<InlineImageResult>> _images = new();
    private readonly SemaphoreSlim _gate = new(4, 4);

    private LinkPreviewService() { }

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](og:title|twitter:title)[""'][^>]*content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegex();

    [GeneratedRegex(@"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]*(?:property|name)\s*=\s*[""'](og:title|twitter:title)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegexReversed();

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](og:image|twitter:image)[""'][^>]*content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRegex();

    [GeneratedRegex(@"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    private static bool IsImage(string type) => type.StartsWith("image/", StringComparison.Ordinal);
    private static bool IsPage(string type) => type.Contains("html", StringComparison.Ordinal);

    /// <summary>The preview for a message's first link (from the database when it was fetched before).</summary>
    public Task<LinkPreview?> GetPreviewAsync(MessageRecord m, string url) => Task.Run(() => GetPreviewCoreAsync(m, url));

    private async Task<LinkPreview?> GetPreviewCoreAsync(MessageRecord m, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !SafeWebFetcher.IsAllowedUrl(uri)) return null;
        var core = AppHost.Core;
        if (m.LinkPreviewFetched)
            return new LinkPreview(m.LinkPreviewTitle, uri.Host, Decode(m.LinkPreviewImage, 480));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var page = await _web.GetAsync(uri, upgradeToHttps: true, PageBytes, IsPage, truncate: true).ConfigureAwait(false);
            if (!page.IsOk) { core.Db.SetLinkPreview(m.Id, url, null, null); return null; }
            var html = Encoding.UTF8.GetString(page.Body!);
            var title = FirstGroup(OgTitleRegex(), html, 2) ?? FirstGroup(OgTitleRegexReversed(), html, 1) ?? FirstGroup(TitleRegex(), html, 1);
            if (title is not null) title = WebUtility.HtmlDecode(title.Trim());
            var imageUrl = FirstGroup(OgImageRegex(), html, 2);
            byte[]? imageBytes = null;
            if (imageUrl is not null && Uri.TryCreate(page.FinalUri ?? uri, WebUtility.HtmlDecode(imageUrl), out var imgUri))
            {
                // Preview images are fetched over https too (an http og:image is upgraded).
                var img = await _web.GetAsync(imgUri, upgradeToHttps: true, PreviewImageBytes, IsImage).ConfigureAwait(false);
                if (img.IsOk) imageBytes = img.Body;
            }
            var image = Decode(imageBytes, 480);
            if (image is null) imageBytes = null;
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

    private static Bitmap? Decode(byte[]? bytes, int width)
    {
        if (bytes is not { Length: > 0 }) return null;
        try { return Bitmap.DecodeToWidth(new MemoryStream(bytes), width); }
        catch { return null; }
    }

    private static string? FirstGroup(Regex r, string html, int group)
    {
        var m = r.Match(html);
        return m.Success ? m.Groups[group].Value : null;
    }

    /// <summary>Downloads (and disk-caches) an image for inline display.</summary>
    public async Task<Bitmap?> LoadImageAsync(string url, int decodeWidth) => (await LoadInlineImageAsync(url, decodeWidth).ConfigureAwait(false)).Image;

    /// <summary>Like <see cref="LoadImageAsync"/>, also telling when the link is a web page rather than a picture.</summary>
    public Task<InlineImageResult> LoadInlineImageAsync(string url, int decodeWidth)
    {
        var key = url + "|" + decodeWidth;
        var task = _images.GetOrAdd(key, _ => Task.Run(() => LoadImageCore(url, decodeWidth)));
        // A page (or a failure) may be worth another try later; only pictures stay cached in memory.
        _ = task.ContinueWith(t => { if (t.IsFaulted || t.Result.Image is null) _images.TryRemove(key, out _); }, TaskScheduler.Default);
        return task;
    }

    private async Task<InlineImageResult> LoadImageCore(string url, int decodeWidth)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !SafeWebFetcher.IsAllowedUrl(uri)) return new(null, false);
        var cacheDir = AppPaths.ImageCache;
        Directory.CreateDirectory(cacheDir);
        var file = Path.Combine(cacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32]);
        byte[]? bytes = null;
        if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        else
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Redirects are checked like the first address; the limit holds whatever Content-Length says.
                var r = await _web.GetAsync(uri, upgradeToHttps: false, InlineImageBytes, IsImage, htmlMeansPage: true).ConfigureAwait(false);
                if (r.Outcome == WebFetchOutcome.ServesHtml) return new(null, true);
                bytes = r.IsOk ? r.Body : null;
            }
            catch { bytes = null; }
            finally { _gate.Release(); }
            if (bytes is not null) await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
        }
        return new(Decode(bytes, decodeWidth), false);
    }
}

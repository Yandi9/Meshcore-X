using System.Text.Json;

namespace MC1.Core.Utilities;

/// <summary>What a CoreScope site said about itself when its link was checked.</summary>
public sealed record CoreScopeProbe(bool IsCoreScope, string? SiteName, string? Error)
{
    public bool Reached => Error is null;
}

/// <summary>
/// The link to a CoreScope site — the MeshCore network analyzer that many states and regions host for their mesh
/// (live packets, map, nodes, channels, analytics). Once a link is saved, the app shows that site in its own tab.
/// </summary>
public static class CoreScopeLink
{
    public const string DefaultName = "CoreScope";
    private const int MaxNameLength = 60;

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCore-for-Windows");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    });

    /// <summary>
    /// Turns what was pasted into a site address: "corescope.example.org" → "https://corescope.example.org/".
    /// Paths and in-site links (…/#/map) are kept, so the tab opens there. Returns false with a reason to show.
    /// </summary>
    public static bool TryNormalize(string? input, out string url, out string? error)
    {
        url = "";
        error = null;
        var text = input?.Trim().Trim('<', '>', '"', '\'', ' ') ?? "";
        if (text.Length == 0)
        {
            error = L.T("Paste the link to your CoreScope first.");
            return false;
        }
        if (text.Any(char.IsWhiteSpace))
        {
            error = L.T("That doesn't look like a link. Copy the address from your browser's address bar.");
            return false;
        }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text.TrimStart('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            error = L.T("That isn't a web link. It should look like https://corescope.example.org.");
            return false;
        }
        if (uri.UserInfo.Length > 0)
        {
            error = L.T("Links with a user name or password in them can't be used.");
            return false;
        }
        url = uri.AbsoluteUri;
        return true;
    }

    /// <summary>The site's address without the in-site part: where its API lives.</summary>
    public static Uri SiteRoot(Uri link)
    {
        var path = link.AbsolutePath;
        var cut = path.LastIndexOf('/');
        path = cut >= 0 ? path[..(cut + 1)] : "/";
        return new UriBuilder(link.Scheme, link.Host, link.Port, path).Uri;
    }

    /// <summary>CoreScope's description of itself (name and colours): <c>/api/config/theme</c>.</summary>
    public static Uri ThemeApi(Uri link) => new(SiteRoot(link), "api/config/theme");

    /// <summary>Reads the site's name from its theme answer; null when the answer isn't CoreScope's.</summary>
    public static bool TryParseTheme(string json, out string? siteName)
    {
        siteName = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("branding", out var branding)
                || branding.ValueKind != JsonValueKind.Object) return false;
            if (branding.TryGetProperty("siteName", out var name) && name.ValueKind == JsonValueKind.String)
                siteName = CleanName(name.GetString());
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? CleanName(string? name)
    {
        var s = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (s.Length == 0) return null;
        return s.Length <= MaxNameLength ? s : s[..(MaxNameLength - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Asks the site what it is. The link is used either way; this only finds the site's name and lets the app say
    /// when the link doesn't seem to lead to a CoreScope.
    /// </summary>
    public static async Task<CoreScopeProbe> ProbeAsync(string url, HttpClient? http = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new(false, null, L.T("That isn't a web link."));
        try
        {
            using var response = await (http ?? SharedHttp.Value).GetAsync(ThemeApi(uri), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, null, null);
            if (response.Content.Headers.ContentLength is > 512 * 1024) return new(false, null, null);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return TryParseTheme(json, out var name) ? new(true, name, null) : new(false, null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, L.T("The site didn't answer in time."));
        }
        catch (HttpRequestException ex)
        {
            return new(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Pages that belong to the linked site and stay in the tab (the same host, with or without "www.", over http or
    /// https). Anything else opens in the web browser.
    /// </summary>
    public static bool IsSameSite(string siteUrl, string? target)
    {
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) || !Uri.TryCreate(target, UriKind.Absolute, out var t)) return false;
        if (t.Scheme is "about" or "data" or "blob") return true; // the page's own internals
        if (t.Scheme is not ("http" or "https")) return false;
        static string Bare(string h) => h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        if (!string.Equals(Bare(site.IdnHost), Bare(t.IdnHost), StringComparison.OrdinalIgnoreCase)) return false;
        return site.IsDefaultPort && t.IsDefaultPort || site.Port == t.Port;
    }

    /// <summary>Short form for labels: "corescope.example.org".</summary>
    public static string DisplayHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}") : url;

    /// <summary>The tab's title: the site's own name, or "CoreScope".</summary>
    public static string TabTitle(string? siteName) => string.IsNullOrWhiteSpace(siteName) ? DefaultName : siteName.Trim();
}

/// <summary>Saving and removing the CoreScope link (the app's tab follows these settings).</summary>
public static class CoreScopeSettings
{
    /// <summary>Saves the pasted link; false (with the reason) when it isn't a usable web link.</summary>
    public static bool Link(MC1.Core.Services.SettingsStore store, string? input, out string url, out string? error)
    {
        if (!CoreScopeLink.TryNormalize(input, out url, out error)) return false;
        var link = url;
        if (store.Current.CoreScopeUrl != link)
            store.Update(s =>
            {
                // The name belongs to the site it was read from.
                if (!SameSiteRoot(s.CoreScopeUrl, link)) s.CoreScopeName = null;
                s.CoreScopeUrl = link;
            });
        return true;
    }

    public static void Unlink(MC1.Core.Services.SettingsStore store)
    {
        if (store.Current.CoreScopeUrl is null && store.Current.CoreScopeName is null) return;
        store.Update(s =>
        {
            s.CoreScopeUrl = null;
            s.CoreScopeName = null;
        });
    }

    /// <summary>Checks the linked site and remembers its name (only while it's still the linked site).</summary>
    public static async Task<CoreScopeProbe> RefreshNameAsync(MC1.Core.Services.SettingsStore store, HttpClient? http = null, CancellationToken ct = default)
    {
        if (store.Current.CoreScopeUrl is not { } url) return new(false, null, null);
        var probe = await CoreScopeLink.ProbeAsync(url, http, ct).ConfigureAwait(false);
        if (probe.IsCoreScope && store.Current.CoreScopeUrl == url && store.Current.CoreScopeName != probe.SiteName)
            store.Update(s => s.CoreScopeName = probe.SiteName);
        return probe;
    }

    private static bool SameSiteRoot(string? a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && CoreScopeLink.SiteRoot(x) == CoreScopeLink.SiteRoot(y);
}

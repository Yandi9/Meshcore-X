using System.Net;
using System.Text;
using MC1.Core.Services;
using MC1.Core.Utilities;
using Xunit;

namespace MC1.Core.Tests;

/// <summary>The CoreScope link: what's accepted, the site check, and what's saved.</summary>
public sealed class CoreScopeTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string ThemeJson = """{"branding":{"siteName":"Colorado Mesh","tagline":"Real-time MeshCore LoRa mesh network analyzer"},"theme":{"accent":"#4a9eff"}}""";

    [Theory]
    [InlineData("corescope.example.org", "https://corescope.example.org/")]
    [InlineData("  https://corescope.example.org  ", "https://corescope.example.org/")]
    [InlineData("<https://corescope.example.org/>", "https://corescope.example.org/")]
    [InlineData("https://corescope.example.org/#/map", "https://corescope.example.org/#/map")]
    [InlineData("http://192.168.1.50:3000", "http://192.168.1.50:3000/")]
    [InlineData("HTTPS://CoreScope.Example.org/#/packets", "https://corescope.example.org/#/packets")]
    public void PastedLinksBecomeSiteAddresses(string pasted, string expected)
    {
        Assert.True(CoreScopeLink.TryNormalize(pasted, out var url, out var error), error);
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("my state corescope")]
    [InlineData("ftp://corescope.example.org")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@corescope.example.org")]
    public void OtherTextIsRefusedWithAReason(string? pasted)
    {
        Assert.False(CoreScopeLink.TryNormalize(pasted, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TheSiteIsAskedAboutItselfAtItsRoot()
    {
        Assert.Equal("https://corescope.example.org/api/config/theme",
            CoreScopeLink.ThemeApi(new Uri("https://corescope.example.org/#/map")).AbsoluteUri);
        Assert.Equal("https://example.org/mesh/api/config/theme",
            CoreScopeLink.ThemeApi(new Uri("https://example.org/mesh/?x=1#/nodes")).AbsoluteUri);
        Assert.Equal("http://192.168.1.50:3000/api/config/theme",
            CoreScopeLink.ThemeApi(new Uri("http://192.168.1.50:3000/")).AbsoluteUri);
    }

    [Fact]
    public void TheSitesNameIsReadFromItsTheme()
    {
        Assert.True(CoreScopeLink.TryParseTheme(ThemeJson, out var name));
        Assert.Equal("Colorado Mesh", name);

        Assert.True(CoreScopeLink.TryParseTheme("""{"branding":{}}""", out name));
        Assert.Null(name);
        Assert.Equal("CoreScope", CoreScopeLink.TabTitle(name));

        var longName = new string('x', 200);
        Assert.True(CoreScopeLink.TryParseTheme("{\"branding\":{\"siteName\":\"  " + longName + "  \"}}", out name));
        Assert.True(name!.Length <= 60);
        Assert.EndsWith("…", name);

        Assert.False(CoreScopeLink.TryParseTheme("""{"status":"ok"}""", out _));
        Assert.False(CoreScopeLink.TryParseTheme("<html>not json</html>", out _));
        Assert.False(CoreScopeLink.TryParseTheme("[1,2]", out _));
    }

    [Theory]
    [InlineData("https://corescope.example.org/", "https://corescope.example.org/#/packets", true)]
    [InlineData("https://corescope.example.org/", "http://corescope.example.org/nodes", true)]
    [InlineData("https://corescope.example.org/", "https://www.corescope.example.org/", true)]
    [InlineData("https://www.corescope.example.org/", "https://corescope.example.org/", true)]
    [InlineData("https://corescope.example.org/", "about:blank", true)]
    [InlineData("https://corescope.example.org/", "https://github.com/Kpa-clawbot/CoreScope", false)]
    [InlineData("https://corescope.example.org/", "https://evil.example/?u=corescope.example.org", false)]
    [InlineData("https://corescope.example.org/", "mailto:someone@example.org", false)]
    [InlineData("http://192.168.1.50:3000/", "http://192.168.1.50:3000/#/map", true)]
    [InlineData("http://192.168.1.50:3000/", "http://192.168.1.50:8080/", false)]
    [InlineData("https://corescope.example.org/", null, false)]
    public void OnlyTheLinkedSiteStaysInTheTab(string site, string? target, bool stays) =>
        Assert.Equal(stays, CoreScopeLink.IsSameSite(site, target));

    [Fact]
    public async Task ACoreScopeSiteGivesItsName()
    {
        var handler = new FakeHandler(_ => Json(ThemeJson));
        using var http = new HttpClient(handler);
        var probe = await CoreScopeLink.ProbeAsync("https://corescope.example.org/#/map", http);
        Assert.True(probe.IsCoreScope);
        Assert.True(probe.Reached);
        Assert.Equal("Colorado Mesh", probe.SiteName);
        Assert.Equal("https://corescope.example.org/api/config/theme", Assert.Single(handler.Asked).AbsoluteUri);
    }

    [Fact]
    public async Task OtherSitesAndUnreachableSitesAreToldApart()
    {
        using (var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
        {
            var probe = await CoreScopeLink.ProbeAsync("https://example.org/", http);
            Assert.False(probe.IsCoreScope);
            Assert.True(probe.Reached);
        }
        using (var http = new HttpClient(new FakeHandler(_ => Json("""{"hello":"world"}"""))))
        {
            var probe = await CoreScopeLink.ProbeAsync("https://example.org/", http);
            Assert.False(probe.IsCoreScope);
            Assert.True(probe.Reached);
        }
        using (var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("No such host is known."))))
        {
            var probe = await CoreScopeLink.ProbeAsync("https://nowhere.example/", http);
            Assert.False(probe.IsCoreScope);
            Assert.False(probe.Reached);
            Assert.Contains("No such host", probe.Error);
        }
    }

    [Fact]
    public async Task LinkingSavesTheSiteAndItsName()
    {
        var path = Path.Combine(Path.GetTempPath(), "mc1-corescope-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var changes = 0;
            store.Changed += () => changes++;

            Assert.False(CoreScopeSettings.Link(store, "not a link", out _, out var error));
            Assert.NotNull(error);
            Assert.Null(store.Current.CoreScopeUrl);
            Assert.Equal(0, changes);

            Assert.True(CoreScopeSettings.Link(store, "corescope.example.org", out var url, out _));
            Assert.Equal("https://corescope.example.org/", url);
            Assert.Equal(url, store.Current.CoreScopeUrl);
            Assert.Equal(1, changes);

            // Linking the same site again changes nothing.
            Assert.True(CoreScopeSettings.Link(store, "https://corescope.example.org/", out _, out _));
            Assert.Equal(1, changes);

            using var http = new HttpClient(new FakeHandler(_ => Json(ThemeJson)));
            var probe = await CoreScopeSettings.RefreshNameAsync(store, http);
            Assert.True(probe.IsCoreScope);
            Assert.Equal("Colorado Mesh", store.Current.CoreScopeName);

            // A start page on the same site keeps the name; another site forgets it.
            Assert.True(CoreScopeSettings.Link(store, "https://corescope.example.org/#/map", out _, out _));
            Assert.Equal("Colorado Mesh", store.Current.CoreScopeName);
            Assert.True(CoreScopeSettings.Link(store, "https://texas.example.net", out _, out _));
            Assert.Null(store.Current.CoreScopeName);

            // Both survive a restart.
            var reopened = new SettingsStore(path);
            Assert.Equal("https://texas.example.net/", reopened.Current.CoreScopeUrl);

            CoreScopeSettings.Unlink(store);
            Assert.Null(store.Current.CoreScopeUrl);
            Assert.Null(store.Current.CoreScopeName);
            Assert.Null(new SettingsStore(path).Current.CoreScopeUrl);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task ANameArrivingAfterTheLinkChangedIsNotSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), "mc1-corescope-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.True(CoreScopeSettings.Link(store, "https://first.example.org", out _, out _));
            using var http = new HttpClient(new FakeHandler(request =>
            {
                // The user links another site while the first one is being asked.
                CoreScopeSettings.Link(store, "https://second.example.org", out _, out _);
                return Json(ThemeJson);
            }));
            await CoreScopeSettings.RefreshNameAsync(store, http);
            Assert.Equal("https://second.example.org/", store.Current.CoreScopeUrl);
            Assert.Null(store.Current.CoreScopeName);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { /* temp */ }
        }
    }
}

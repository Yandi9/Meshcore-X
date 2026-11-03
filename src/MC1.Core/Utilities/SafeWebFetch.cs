using System.Net;
using System.Net.Sockets;

namespace MC1.Core.Utilities;

public enum WebFetchOutcome
{
    Ok,
    /// <summary>The server answered with an error status.</summary>
    NotFound,
    /// <summary>The address (or a redirect) points at this PC or the local network, or isn't http(s).</summary>
    Blocked,
    /// <summary>The body is bigger than allowed (checked while it arrives, whatever Content-Length says).</summary>
    TooLarge,
    /// <summary>The content isn't of an accepted type.</summary>
    WrongType,
    /// <summary>An image address that serves a web page (imgur, prnt.sc…): show a link preview instead.</summary>
    ServesHtml,
    /// <summary>Too many redirects, a redirect back to itself, or a network error.</summary>
    Failed,
}

public sealed record WebFetchResult(WebFetchOutcome Outcome, byte[]? Body = null, string? MediaType = null, Uri? FinalUri = null)
{
    public bool IsOk => Outcome == WebFetchOutcome.Ok;
}

/// <summary>
/// Bounded GETs for link previews and inline images (like the iPhone app's preview fetcher): every redirect is
/// followed by hand and checked again, so a public link can't lead to this PC or the local network; preview pages
/// and their images are fetched over https; and the size limit is enforced while the data arrives.
/// </summary>
public sealed class SafeWebFetcher : IDisposable
{
    public const int MaxRedirects = 5;
    private readonly HttpClient _http;

    /// <param name="handler">For tests; normally <see cref="CreateHandler"/> (which also checks the addresses a name resolves to).</param>
    public SafeWebFetcher(HttpMessageHandler? handler = null, TimeSpan? timeout = null, string? userAgent = null)
    {
        _http = new HttpClient(handler ?? CreateHandler()) { Timeout = timeout ?? TimeSpan.FromSeconds(12) };
        if (userAgent is not null) _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,image/*;q=0.9,*/*;q=0.5");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A handler that follows no redirects itself and connects only to public addresses.</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = ConnectToPublicAddressAsync,
    };

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        // Through a proxy the connection goes to the proxy, which may well be on the local network.
        var viaProxy = ctx.InitialRequestMessage.RequestUri is { } u && !string.Equals(u.IdnHost, host, StringComparison.OrdinalIgnoreCase);
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || (!viaProxy && addresses.Any(IsPrivateOrReserved)))
            throw new HttpRequestException($"Blocked: {host} is a local or reserved address");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>http(s) only, with a host that isn't this PC or a private/reserved IP address.</summary>
    public static bool IsAllowedUrl(Uri u)
    {
        if (!u.IsAbsoluteUri || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host)) return false;
        var host = u.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (u.IsLoopback) return false;
        return !(IPAddress.TryParse(u.HostNameType == UriHostNameType.IPv6 ? u.Host.Trim('[', ']') : u.Host, out var ip) && IsPrivateOrReserved(ip));
    }

    public static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                   || b[0] >= 224;                                  // multicast, reserved, broadcast
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(ip) || IPAddress.IPv6None.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) return true;
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC                            // unique local fc00::/7
                   || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)       // link-local fe80::/10
                   || b[0] == 0xFF                                  // multicast
                   || ip.IsIPv6SiteLocal;
        }
        return true;
    }

    /// <summary>The same address over https (port 80 dropped, so the upgraded request doesn't go to the http port).</summary>
    public static Uri HttpsUpgrade(Uri u)
    {
        if (!u.IsAbsoluteUri || u.Scheme is not ("http" or "https")) return u;
        var b = new UriBuilder(u) { Scheme = Uri.UriSchemeHttps };
        if (u.Port == 80 || u.IsDefaultPort) b.Port = -1;
        return b.Uri;
    }

    /// <param name="upgradeToHttps">Fetch (and follow redirects) over https — for link preview pages and their images.</param>
    /// <param name="byteLimit">Largest body accepted.</param>
    /// <param name="truncate">Keep the first <paramref name="byteLimit"/> bytes instead of failing (web pages: the title is near the top).</param>
    /// <param name="acceptsType">Accepted media types (e.g. html for a page, image/* for a picture).</param>
    /// <param name="htmlMeansPage">An HTML answer is <see cref="WebFetchOutcome.ServesHtml"/> (an image link that is really a page).</param>
    public async Task<WebFetchResult> GetAsync(Uri url, bool upgradeToHttps, long byteLimit, Func<string, bool> acceptsType,
        bool truncate = false, bool htmlMeansPage = false, CancellationToken ct = default)
    {
        var current = upgradeToHttps ? HttpsUpgrade(url) : url;
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (!IsAllowedUrl(current)) return new(WebFetchOutcome.Blocked, FinalUri: current);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) return new(WebFetchOutcome.Failed, FinalUri: current);
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (upgradeToHttps) next = HttpsUpgrade(next);
                    // Following the URL that just answered would only repeat that answer.
                    if (Uri.Compare(next, current, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0)
                        return new(WebFetchOutcome.Failed, FinalUri: current);
                    current = next;
                    continue;
                }
                if (status is < 200 or >= 300) return new(WebFetchOutcome.NotFound, FinalUri: current);
                var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                if (htmlMeansPage && type is "text/html" or "application/xhtml+xml") return new(WebFetchOutcome.ServesHtml, MediaType: type, FinalUri: current);
                if (!acceptsType(type)) return new(WebFetchOutcome.WrongType, MediaType: type, FinalUri: current);
                if (!truncate && response.Content.Headers.ContentLength > byteLimit) return new(WebFetchOutcome.TooLarge, MediaType: type, FinalUri: current);
                var body = await ReadCappedAsync(response.Content, byteLimit, truncate, ct).ConfigureAwait(false);
                return body is null ? new(WebFetchOutcome.TooLarge, MediaType: type, FinalUri: current) : new(WebFetchOutcome.Ok, body, type, current);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UriFormatException)
        {
            return new(WebFetchOutcome.Failed, FinalUri: current);
        }
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; null when there is more (unless truncating).</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long limit, bool truncate, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var room = limit + 1 - ms.Length; // one byte past the limit tells "too large" from "exactly the limit"
            if (room <= 0) break;
            var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, room)), ct).ConfigureAwait(false);
            if (n == 0) break;
            ms.Write(buffer, 0, n);
        }
        if (ms.Length <= limit) return ms.ToArray();
        if (!truncate) return null;
        var all = ms.GetBuffer();
        return all.AsSpan(0, (int)limit).ToArray();
    }
}

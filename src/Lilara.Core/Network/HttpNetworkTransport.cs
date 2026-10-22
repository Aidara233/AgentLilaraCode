using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Network;

/// <summary>
/// 联网传输的实现：Tavily 搜索 + 抓网页。
///
/// <para>⚠️ <b>所有安全判据都住在这里，不在工具里</b>：工具只负责把结果说成人话；
/// 「这个 URL 能不能碰」只有一处判据。测试注入 <see cref="HttpMessageHandler"/> 与
/// 地址解析委托，因此整张表可以在**不连公网**的情况下钉住。</para>
///
/// <para>⚠️ <b>DNS rebinding 的第二道闸在连接回调里</b>：发请求前解析一次并校验，
/// 连接时再解析一次、连到**已校验的那个 IP**。只做前者的话，攻击者可以让第一次解析
/// 给公网地址、第二次给 <c>127.0.0.1</c>，而 <see cref="HttpClient"/> 自己解析的正是第二次。</para>
/// </summary>
public sealed class HttpNetworkTransport : INetworkTransport
{
    private static readonly HttpRequestOptionsKey<IReadOnlyList<string>> AllowDomainsKey =
        new("lilara.network.allowDomains");

    private static readonly HttpClient Shared = CreateClient();

    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public HttpNetworkTransport() : this(Shared, Dns.GetHostAddressesAsync) { }

    internal HttpNetworkTransport(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
        : this(new HttpClient(handler, disposeHandler: false), resolve ?? Dns.GetHostAddressesAsync) { }

    private HttpNetworkTransport(
        HttpClient http, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        _http = http;
        _resolve = resolve;
    }

    // ---------------- search ----------------

    public async Task<NetworkSearchResult> SearchAsync(
        NetworkConfig config, string query, CancellationToken ct)
    {
        var s = NetworkStore.Normalize(config).Search;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(s.TimeoutSeconds));

        using var req = new HttpRequestMessage(HttpMethod.Post, s.Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);

        // ⚠️ 搜索端点来自**配置**（用户明确填的），不是模型给的 URL ⇒ 它不受
        // 「只许公网」那条闸管：本机网关 / 内网搜索服务都是合法部署。把它当成
        // 「显式白名单」，连接回调就不会把 127.0.0.1 拒掉。
        // （`web_fetch` 的 URL 来自模型，那道闸仍然生效。）
        if (!Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
            throw new NetworkException($"搜索端点 `{s.Endpoint}` 不是合法的 http / https 绝对 URL。");

        req.Options.Set(AllowDomainsKey, new[] { endpointUri.Host });
        req.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                query,
                max_results = s.MaxResults,
                search_depth = "basic",
                include_answer = true,
            }, Json.Options),
            Encoding.UTF8, "application/json");

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var (bytes, _) = await ReadLimitedAsync(resp.Content, NetworkLimits.MaxSearchResponseBytes, cts.Token);
            var text = Encoding.UTF8.GetString(bytes);

            if (!resp.IsSuccessStatusCode)
                throw new NetworkException(DescribeStatus((int)resp.StatusCode, text));

            return ParseSearch(text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NetworkException($"搜索超时（{s.TimeoutSeconds} 秒）。");
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException($"搜索请求失败：{ex.Message}");
        }
    }

    private static NetworkSearchResult ParseSearch(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new NetworkException("搜索端点返回的不是 JSON。");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new NetworkException("搜索端点返回的 JSON 不是对象。");

            var answer = root.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;

            var hits = new List<NetworkSearchHit>();
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var url = Str(item, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    var title = Str(item, "title");
                    var score = item.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number
                        ? sc.GetDouble()
                        : (double?)null;

                    hits.Add(new NetworkSearchHit(
                        string.IsNullOrWhiteSpace(title) ? url : title!, url!, Str(item, "content"), score));
                }
            }

            return new NetworkSearchResult(answer, hits);
        }
    }

    private static string? Str(JsonElement element, string key) =>
        element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string DescribeStatus(int status, string body)
    {
        var excerpt = body.Length > 300 ? body[..300] + "…" : body;

        return status switch
        {
            401 or 403 => "搜索 key 被拒绝（401 / 403）。检查 Tavily key 是否填对。",
            429 => "搜索被限流（429）。稍后再试，或降低请求频率。",
            432 or 433 => $"Tavily 账户限制（{status}）。检查额度 / 套餐。",
            >= 500 => $"搜索服务暂时不可用（{status}）。",
            _ => $"搜索请求失败（{status}）：{excerpt}",
        };
    }

    // ---------------- fetch ----------------

    public async Task<NetworkFetchResult> FetchAsync(
        NetworkConfig config, string url, CancellationToken ct)
    {
        var f = NetworkStore.Normalize(config).Fetch;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(f.TimeoutSeconds));

        var requested = ParseUri(url);
        var current = requested;

        for (var hop = 0; ; hop++)
        {
            await ValidateHostAsync(current, f, cts.Token);

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Options.Set(AllowDomainsKey, f.AllowDomains);
            req.Headers.UserAgent.ParseAdd("Lilara/1.0 (+web_fetch)");

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new NetworkException($"抓取超时（{f.TimeoutSeconds} 秒）：{current}");
            }
            catch (HttpRequestException ex)
            {
                throw new NetworkException($"抓取失败：{ex.Message}");
            }

            using (resp)
            {
                var status = (int)resp.StatusCode;

                if (status is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } location)
                {
                    if (hop >= NetworkLimits.MaxRedirects)
                        throw new NetworkException(
                            $"重定向超过 {NetworkLimits.MaxRedirects} 跳，停止。最后一跳：{current}");

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                        throw new NetworkException($"重定向到了非 http(s) 地址：{next}");
                    current = next;
                    continue;
                }

                var mediaType = resp.Content.Headers.ContentType?.MediaType;

                if (!IsTextual(mediaType))
                {
                    // ⚠️ 不读二进制正文：那正是「一次 fetch 把内存吃光」的入口。
                    return new NetworkFetchResult(
                        requested.ToString(), current.ToString(), status, mediaType, null,
                        $"（这个 URL 返回的不是文本（content-type = {mediaType ?? "未知"}），没有取出内容。）",
                        false, 0);
                }

                var (bytes, truncated) = await ReadLimitedAsync(resp.Content, f.MaxBytes, cts.Token);
                var (title, text) = DecodeBody(bytes, resp.Content.Headers.ContentType);

                return new NetworkFetchResult(
                    requested.ToString(), current.ToString(), status, mediaType, title, text, truncated, bytes.Length);
            }
        }
    }

    private static Uri ParseUri(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new NetworkException($"`{raw}` 不是一个绝对 URL。");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new NetworkException($"只允许 http / https，拒绝 `{uri.Scheme}://`。");

        if (uri.Host.Length == 0)
            throw new NetworkException($"`{raw}` 没有主机名。");

        return uri;
    }

    private async Task ValidateHostAsync(Uri uri, NetworkFetchConfig config, CancellationToken ct)
    {
        var allowlisted = AddressPolicy.IsAllowlisted(uri.Host, config.AllowDomains);

        // 非白名单只许默认端口：一个公网域名上的 8080 常常是路由器 / 管理面，
        // 而「看起来是公网」不表示那个端口真是给公众用的。白名单是显式同意，不设这条。
        if (!allowlisted && !uri.IsDefaultPort)
            throw new NetworkException(
                $"非白名单域名只允许默认端口（http 80 / https 443），拒绝 {uri.Host}:{uri.Port}。"
                + "如果这是你要的本机 / 内网服务，把域名写进联网配置的 allowDomains。");

        IPAddress[] addresses;
        try
        {
            addresses = await _resolve(uri.Host, ct);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new NetworkException($"解析域名 {uri.Host} 失败：{ex.Message}");
        }

        if (addresses.Length == 0)
            throw new NetworkException($"域名 {uri.Host} 没有解析出任何地址。");

        if (!allowlisted && addresses.Any(ip => !AddressPolicy.IsPublic(ip)))
            throw new NetworkException(
                $"`{uri.Host}` 解析到了内网 / 本机地址，拒绝抓取。"
                + "如果这是你要的本机 / 内网服务，把域名写进联网配置的 allowDomains。");
    }

    // ---------------- shared ----------------

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,      // 逐跳校验，见 FetchAsync
            UseProxy = false,               // 系统代理不受这条闸管，一律不走
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = ConnectValidatedAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        // 超时由每次请求的 CTS 管（搜索与抓取各有自己的秒数）。
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// 连接期再校验一次——**连到已校验的 IP**，而不是让 <see cref="HttpClient"/> 再解析一次。
    /// 白名单从请求的 Options 里取（handler 是共享的，配置是每请求的）。
    /// </summary>
    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var allow = context.InitialRequestMessage.Options.TryGetValue(AllowDomainsKey, out var list)
            ? list
            : [];
        var allowlisted = AddressPolicy.IsAllowlisted(context.DnsEndPoint.Host, allow);

        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var chosen = addresses.FirstOrDefault(ip => AddressPolicy.Allows(ip, allowlisted));

        if (chosen is null)
            throw new NetworkException($"`{context.DnsEndPoint.Host}` 解析到了内网 / 本机地址，拒绝连接。");

        var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(chosen, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsTextual(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Title, string Text) DecodeBody(byte[] bytes, MediaTypeHeaderValue? type)
    {
        var text = DecodeText(bytes, type?.CharSet);

        return type?.MediaType is { } mediaType
               && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            ? HtmlText.ToText(text)
            : (null, text);
    }

    private static string DecodeText(byte[] bytes, string? charset)
    {
        var encoding = Encoding.UTF8;

        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // 不认识的字符集 ⇒ UTF-8（替换字符是**看得见**的失真，比抛好）。
            }
        }

        return encoding.GetString(bytes);
    }

    /// <summary>读正文，最多 <paramref name="cap"/> 字节；多读一个字节用来判断「是不是真截断了」。</summary>
    private static async Task<(byte[] Bytes, bool Truncated)> ReadLimitedAsync(
        HttpContent content, int cap, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var limit = (long)cap + 1;

        while (buffer.Length < limit)
        {
            var want = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }

        var all = buffer.ToArray();
        return all.Length <= cap ? (all, false) : (all[..cap], true);
    }
}

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Network;

namespace Lilara.Core.Tests;

/// <summary>
/// 传输层的安全与容量判据。⚠️ **一条都不打真网络**：handler 与地址解析都是注入口。
/// </summary>
public class NetworkTransportTests
{
    private static readonly Func<string, CancellationToken, Task<IPAddress[]>> PublicDns =
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static readonly Func<string, CancellationToken, Task<IPAddress[]>> LoopbackDns =
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") });

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }

    private static NetworkConfig Config(
        IReadOnlyList<string>? allow = null, int timeoutSeconds = 20, int maxBytes = 1024 * 1024) =>
        new()
        {
            Enabled = true,
            Search = new NetworkSearchConfig { ApiKey = "k", TimeoutSeconds = timeoutSeconds },
            Fetch = new NetworkFetchConfig
            {
                TimeoutSeconds = timeoutSeconds, MaxBytes = maxBytes, AllowDomains = allow ?? [],
            },
        };

    private static HttpResponseMessage Text(
        string body, string mediaType = "text/plain", HttpStatusCode status = HttpStatusCode.OK,
        string? charset = "utf-8")
    {
        var content = new StringContent(body, Encoding.UTF8, mediaType);
        content.Headers.ContentType!.CharSet = charset;
        return new HttpResponseMessage(status) { Content = content };
    }

    private static HttpResponseMessage Binary(byte[] bytes, string mediaType = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // ---- 地址表 ----

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]   // 云元数据
    [InlineData("100.64.0.1", false)]        // CGNAT
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]  // IPv4-mapped 不能换个写法就绕过去
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void AddressPolicy_ClassifiesAddresses(string ip, bool isPublic)
    {
        Assert.Equal(isPublic, AddressPolicy.IsPublic(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("example.test", "example.test", true)]
    [InlineData("a.example.test", "example.test", true)]
    [InlineData("notexample.test", "example.test", false)]
    [InlineData("EXAMPLE.TEST", "example.test", true)]
    [InlineData("x", ".x", true)]
    public void Allowlist_MatchesExactAndSubdomains(string host, string allow, bool expected)
    {
        Assert.Equal(expected, AddressPolicy.IsAllowlisted(host, [allow]));
    }

    // ---- fetch 安全闸 ----

    [Fact]
    public async Task Fetch_OnlyHttpAndHttps()
    {
        var transport = new HttpNetworkTransport(new StubHandler((_, _) => Task.FromResult(Text("x"))), PublicDns);

        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            transport.FetchAsync(Config(), "file:///etc/passwd", default));

        Assert.Contains("只允许 http", ex.Message);
    }

    [Fact]
    public async Task Fetch_PrivateResolution_IsRefused()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Text("secret")));
        var transport = new HttpNetworkTransport(handler, LoopbackDns);

        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            transport.FetchAsync(Config(), "http://localhost/x", default));

        Assert.Contains("内网", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_AllowlistedPrivateHost_IsAllowed()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Text("ok")));
        var transport = new HttpNetworkTransport(handler, LoopbackDns);

        var result = await transport.FetchAsync(
            Config(allow: ["127.0.0.1"]), "http://127.0.0.1:8080/x", default);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task Fetch_NonDefaultPort_RequiresAllowlist()
    {
        var transport = new HttpNetworkTransport(
            new StubHandler((_, _) => Task.FromResult(Text("x"))), PublicDns);

        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            transport.FetchAsync(Config(), "https://example.test:8080/", default));

        Assert.Contains("默认端口", ex.Message);
    }

    [Fact]
    public async Task Fetch_RedirectToPrivate_IsRefusedPerHop()
    {
        var handler = new StubHandler((request, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("http://127.0.0.1/secret");
            return Task.FromResult(r);
        });

        var resolve = new Func<string, CancellationToken, Task<IPAddress[]>>((host, _) =>
            Task.FromResult(host == "127.0.0.1"
                ? new[] { IPAddress.Loopback }
                : new[] { IPAddress.Parse("93.184.216.34") }));

        var transport = new HttpNetworkTransport(handler, resolve);

        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            transport.FetchAsync(Config(), "https://example.test/start", default));

        Assert.Contains("内网", ex.Message);
        Assert.Equal(1, handler.Calls);     // 第一跳之后就被拦下
    }

    [Fact]
    public async Task Fetch_RedirectCap_IsEnforced()
    {
        var handler = new StubHandler((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://example.test/again");
            return Task.FromResult(r);
        });

        var transport = new HttpNetworkTransport(handler, PublicDns);

        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            transport.FetchAsync(Config(), "https://example.test/start", default));

        Assert.Contains("重定向超过", ex.Message);
    }

    [Fact]
    public async Task Fetch_OversizeBody_IsTruncatedAndSaidSo()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Text(new string('x', 10_000))));
        var transport = new HttpNetworkTransport(handler, PublicDns);

        var result = await transport.FetchAsync(
            Config(maxBytes: NetworkLimits.MinFetchBytes), "https://example.test/big", default);

        Assert.True(result.Truncated);
        Assert.Equal(NetworkLimits.MinFetchBytes, result.BytesRead);
        Assert.Equal(NetworkLimits.MinFetchBytes, result.Text.Length);
    }

    [Fact]
    public async Task Fetch_Html_StripsMarkupAndKeepsTitle()
    {
        const string html =
            "<html><head><title>标题</title><script>bad()</script></head>"
            + "<body><p>Hello &amp; <b>world</b></p></body></html>";

        var transport = new HttpNetworkTransport(
            new StubHandler((_, _) => Task.FromResult(Text(html, "text/html"))), PublicDns);

        var result = await transport.FetchAsync(Config(), "https://example.test/", default);

        Assert.Equal("标题", result.Title);
        Assert.Contains("Hello & world", result.Text);
        Assert.DoesNotContain("bad()", result.Text);
        Assert.DoesNotContain("<p>", result.Text);
    }

    [Fact]
    public async Task Fetch_NonText_DoesNotReadBody()
    {
        var transport = new HttpNetworkTransport(
            new StubHandler((_, _) => Task.FromResult(Binary([1, 2, 3, 4]))), PublicDns);

        var result = await transport.FetchAsync(Config(), "https://example.test/blob", default);

        Assert.Contains("不是文本", result.Text);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task Fetch_Timeout_IsReported()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Text("late");
        });

        var transport = new HttpNetworkTransport(handler, PublicDns);

        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            transport.FetchAsync(Config(timeoutSeconds: 1), "https://example.test/slow", default));

        Assert.Contains("超时", ex.Message);
    }

    // ---- search ----

    [Fact]
    public async Task Search_ParsesAnswerAndHits()
    {
        const string json = """
            {"answer":"A","results":[{"title":"T1","url":"https://a.test","content":"C1","score":0.9}]}
            """;

        var transport = new HttpNetworkTransport(
            new StubHandler((_, _) => Task.FromResult(Text(json, "application/json"))), PublicDns);

        var result = await transport.SearchAsync(Config(), "q", default);

        Assert.Equal("A", result.Answer);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("T1", hit.Title);
        Assert.Equal("https://a.test", hit.Url);
        Assert.Equal("C1", hit.Snippet);
    }

    [Fact]
    public async Task Search_401_SaysKeyIsRejected()
    {
        var transport = new HttpNetworkTransport(
            new StubHandler((_, _) => Task.FromResult(
                Text("{\"error\":\"bad key\"}", "application/json", HttpStatusCode.Unauthorized))),
            PublicDns);

        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            transport.SearchAsync(Config(), "q", default));

        Assert.Contains("key", ex.Message);
    }

    [Fact]
    public async Task Search_Timeout_IsReported()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Text("{}", "application/json");
        });

        var transport = new HttpNetworkTransport(handler, PublicDns);

        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            transport.SearchAsync(Config(timeoutSeconds: 1), "q", default));

        Assert.Contains("超时", ex.Message);
    }
}

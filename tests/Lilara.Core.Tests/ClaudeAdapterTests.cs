using System.Net;
using Anthropic.Exceptions;
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class ClaudeAdapterTests
{
    // 协议正确的 provider；构造适配器不发任何网络请求。
    private static ProviderDefinition Provider => new()
    {
        Id = "claude",
        Protocol = ClaudeAdapter.ProtocolId,
        ApiKey = "sk-test-not-a-real-key",
        DefaultModel = "claude-opus-5",
        Modality = "vision",
    };

    // 空时间线会让 Build 在发请求之前抛 ArgumentException。
    private static ModelRequest EmptyTimelineRequest => new()
    {
        ModelId = "claude-opus-5",
        MaxTokens = 1024,
        Protocol = Protocols.ClaudeMessages,
        Effort = "high",
        SystemBlocks = [],
        Timeline = [],
    };

    private static HttpRequestException Inner() => new("connection refused");

    // ---- 构造期守卫（离线） ----

    [Fact]
    public void Constructor_RejectsWrongProtocol()
    {
        var wrong = new ProviderDefinition
        {
            Id = "openai",
            Protocol = Protocols.OpenAiChat,
            ApiKey = "x",
            DefaultModel = "gpt",
            Modality = "vision",
        };

        var ex = Assert.Throws<ArgumentException>(() => new ClaudeAdapter(wrong));

        Assert.Contains("claude-messages", ex.Message);
    }

    [Fact]
    public void Constructor_AcceptsClaudeProtocol()
    {
        var adapter = new ClaudeAdapter(Provider);

        Assert.NotNull(adapter);
    }

    [Fact]
    public void Constructor_RejectsNullProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new ClaudeAdapter(null!));
    }

    // ---- 认证方式：后端怪癖，归提供商配置（离线：只构造） ----
    // 官方只认 x-api-key（SDK 的 ApiKey），本机网关只认 Authorization: Bearer（SDK 的 AuthToken）。

    [Fact]
    public void Constructor_UsesBearerWhenAuthSchemeSaysSo()
    {
        var provider = new ProviderDefinition
        {
            Id = "gw", Protocol = ClaudeAdapter.ProtocolId,
            ApiKey = "sk-x", DefaultModel = "m", Modality = "vision",
            AuthScheme = AuthSchemes.Bearer,   // ← 网关要 Bearer
        };

        var adapter = new ClaudeAdapter(provider);

        Assert.Equal(AuthSchemes.Bearer, adapter.AuthScheme);
    }

    [Fact]
    public void Constructor_DefaultsToApiKeyWhenUnset()
    {
        var provider = new ProviderDefinition
        {
            Id = "official", Protocol = ClaudeAdapter.ProtocolId,
            ApiKey = "sk-x", DefaultModel = "m", Modality = "vision",
        };

        Assert.Equal(AuthSchemes.ApiKey, new ClaudeAdapter(provider).AuthScheme);
    }

    // ---- 端点解析（不联网：只构造，绝不 CallAsync） ----
    // 环境变量接管本身不是错，「悄悄」接管才是：忽略 ANTHROPIC_BASE_URL
    // 会把占位串当真 key 发去官方地址（401）。

    [Fact]
    public void Constructor_ResolvesConfiguredEndpoint()
    {
        var adapter = new ClaudeAdapter(Provider with { Endpoint = "http://example.invalid" });

        // 接口上的 Endpoint 可空（占位适配器没有）；真适配器必须给得出。
        Assert.NotNull(adapter.Endpoint);
        Assert.Equal("http://example.invalid", adapter.Endpoint!.Url);
        Assert.Equal(EndpointSource.Configured, adapter.Endpoint.Source);
    }

    // 本机环境变量指向本地代理，故未配端点时也不是官方地址；只断言来源可辨识。
    [Fact]
    public void Constructor_AlwaysResolvesAnEndpointWithAKnownSource()
    {
        var adapter = new ClaudeAdapter(Provider);

        Assert.NotNull(adapter.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(adapter.Endpoint!.Url));
        Assert.True(Enum.IsDefined(adapter.Endpoint.Source));
    }

    // ---- CallAsync 的失败包装（离线：Build 在发请求之前就抛了） ----

    // 这条不联网：Build 必须在 try 里面，否则这里冒出来的会是裸的 ArgumentException。
    [Fact]
    public async Task CallAsync_WrapsRequestBuilderFailures()
    {
        var adapter = new ClaudeAdapter(Provider);

        var ex = await Assert.ThrowsAsync<ModelCallFailedException>(
            () => adapter.CallAsync(EmptyTimelineRequest, CancellationToken.None));

        // 内层保留原始失败，外层是调用方唯一要认的类型。
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    // ---- ShouldWrap：取消 vs 失败的判据（纯函数，离线） ----

    [Fact]
    public void ShouldWrap_WrapsOrdinaryFailure()
    {
        Assert.True(ClaudeAdapter.ShouldWrap(new ArgumentException("boom"), CancellationToken.None));
    }

    // HttpClient 超时以 TaskCanceledException 到达，但调用方的 token 没取消——这是真失败，必须包。
    [Fact]
    public void ShouldWrap_WrapsTimeoutThatLooksLikeCancellation()
    {
        Assert.True(ClaudeAdapter.ShouldWrap(new TaskCanceledException("timeout"), CancellationToken.None));
    }

    // 调用方自己取消（Ctrl-C）要原样冒出，不报成「调用失败」。
    [Fact]
    public void ShouldWrap_DoesNotWrapCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(ClaudeAdapter.ShouldWrap(new OperationCanceledException(cts.Token), cts.Token));
    }

    // ---- Describe：每一条错误文案（纯函数，离线：直接构造 SDK 异常） ----

    [Fact]
    public void Describe_MapsBadRequest()
    {
        var ex = new AnthropicBadRequestException(Inner())
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = "{}",
        };

        Assert.Contains("400", ClaudeAdapter.Describe(ex));
    }

    [Fact]
    public void Describe_MapsUnauthorized()
    {
        var ex = new AnthropicUnauthorizedException(Inner())
        {
            StatusCode = HttpStatusCode.Unauthorized,
            ResponseBody = "{}",
        };

        Assert.Contains("401", ClaudeAdapter.Describe(ex));
    }

    [Fact]
    public void Describe_MapsForbidden()
    {
        var ex = new AnthropicForbiddenException(Inner())
        {
            StatusCode = HttpStatusCode.Forbidden,
            ResponseBody = "{}",
        };

        Assert.Contains("403", ClaudeAdapter.Describe(ex));
    }

    [Fact]
    public void Describe_MapsNotFound()
    {
        var ex = new AnthropicNotFoundException(Inner())
        {
            StatusCode = HttpStatusCode.NotFound,
            ResponseBody = "{}",
        };

        Assert.Contains("404", ClaudeAdapter.Describe(ex));
    }

    [Fact]
    public void Describe_MapsRateLimit()
    {
        var ex = new AnthropicRateLimitException(Inner())
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            ResponseBody = "{}",
        };

        Assert.Contains("429", ClaudeAdapter.Describe(ex));
    }

    [Fact]
    public void Describe_Maps5xx()
    {
        var ex = new Anthropic5xxException(Inner())
        {
            StatusCode = HttpStatusCode.InternalServerError,
            ResponseBody = "{}",
        };

        Assert.Contains("5xx", ClaudeAdapter.Describe(ex));
    }

    // 认不出来的异常退化成通用文案，但带上原始 message。
    [Fact]
    public void Describe_FallsBackForUnknownException()
    {
        var described = ClaudeAdapter.Describe(new InvalidOperationException("totally unknown"));

        Assert.Contains("totally unknown", described);
    }
}

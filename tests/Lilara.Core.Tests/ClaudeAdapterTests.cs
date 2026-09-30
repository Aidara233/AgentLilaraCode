using System.Net;
using Anthropic.Exceptions;
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class ClaudeAdapterTests
{
    /// <summary>一个协议正确的 provider。构造它<b>不发任何网络请求</b>。</summary>
    private static ProviderDefinition Provider => new()
    {
        Id = "claude",
        Protocol = ClaudeAdapter.ProtocolId,
        ApiKey = "sk-test-not-a-real-key",
        DefaultModel = "claude-opus-5",
        Modality = "vision",
    };

    /// <summary>空时间线的请求：Build 会对它抛 ArgumentException，且**发生在发请求之前**。</summary>
    private static ModelRequest EmptyTimelineRequest => new()
    {
        ModelId = "claude-opus-5",
        MaxTokens = 1024,
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
            Protocol = "openai-chat",
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
    // ⚠️ 官方只认 x-api-key（SDK 的 ApiKey），本机网关只认 Authorization: Bearer
    // （SDK 的 AuthToken）。实测：ApiKey 打不通，AuthToken 通。构造不触网。

    [Fact]
    public void Constructor_UsesBearerWhenAuthSchemeSaysSo()
    {
        var provider = new ProviderDefinition
        {
            Id = "gw", Protocol = ClaudeAdapter.ProtocolId,
            ApiKey = "sk-x", DefaultModel = "m", Modality = "vision",
            AuthScheme = AuthSchemes.Bearer,   // ← 网关要 Bearer
        };

        // 构造不触网；断言的是「解析出的方案被选中」
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
    // ⚠️ 判据是「谁定的端点必须说得清」。
    // 环境变量接管本身不是错——**悄悄**接管才是：本机环境里 ANTHROPIC_BASE_URL
    // 指向一个本地代理，把它忽略掉反而会把 PROXY_MANAGED 这个占位串
    // 当成真 key 发去官方地址（已实测：401）。

    [Fact]
    public void Constructor_ResolvesConfiguredEndpoint()
    {
        var adapter = new ClaudeAdapter(Provider with { Endpoint = "http://example.invalid" });

        Assert.Equal("http://example.invalid", adapter.Endpoint.Url);
        Assert.Equal(EndpointSource.Configured, adapter.Endpoint.Source);
    }

    /// <summary>
    /// 没配端点时**也不是官方地址**（本机就是这种情况：环境变量指向本地代理）。
    /// 这里只断言「解析出某个来源且说得清」，不断言具体是哪一个——
    /// 那取决于跑测试的机器，正是 `EndpointResolver` 把那部分抽成可注入的纯函数。
    /// </summary>
    [Fact]
    public void Constructor_AlwaysResolvesAnEndpointWithAKnownSource()
    {
        var adapter = new ClaudeAdapter(Provider);

        Assert.False(string.IsNullOrWhiteSpace(adapter.Endpoint.Url));
        Assert.True(Enum.IsDefined(adapter.Endpoint.Source));
    }

    // ---- CallAsync 的失败包装（离线：Build 在发请求之前就抛了） ----

    /// <summary>
    /// ⚠️ 这个测试<b>不联网</b>：空时间线让 Build 在 Create 之前就抛，
    /// 走的是「包装失败」这条路，一个字节都没发出去。
    /// 它盯的是 Build 必须在 try 里面——否则这里冒出来的会是裸的 ArgumentException。
    /// </summary>
    [Fact]
    public async Task CallAsync_WrapsRequestBuilderFailures()
    {
        var adapter = new ClaudeAdapter(Provider);

        var ex = await Assert.ThrowsAsync<ModelCallFailedException>(
            () => adapter.CallAsync(EmptyTimelineRequest, CancellationToken.None));

        // 内层保留原始失败，外层是调用方唯一要认的类型
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    // ---- ShouldWrap：取消 vs 失败的判据（纯函数，离线） ----

    [Fact]
    public void ShouldWrap_WrapsOrdinaryFailure()
    {
        Assert.True(ClaudeAdapter.ShouldWrap(new ArgumentException("boom"), CancellationToken.None));
    }

    /// <summary>
    /// HttpClient 超时是以 TaskCanceledException 到达的——但调用方的 token 没被取消，
    /// 所以这是一次真失败，必须包。
    /// </summary>
    [Fact]
    public void ShouldWrap_WrapsTimeoutThatLooksLikeCancellation()
    {
        Assert.True(ClaudeAdapter.ShouldWrap(new TaskCanceledException("timeout"), CancellationToken.None));
    }

    /// <summary>用户 Ctrl-C：token 是调用方自己取消的，原样冒出去，不报成「调用失败」。</summary>
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

    /// <summary>认不出来的异常退化成通用文案，但**带上原始 message**，不丢信息。</summary>
    [Fact]
    public void Describe_FallsBackForUnknownException()
    {
        var described = ClaudeAdapter.Describe(new InvalidOperationException("totally unknown"));

        Assert.Contains("totally unknown", described);
    }
}

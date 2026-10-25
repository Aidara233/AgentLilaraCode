using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class OpenAiHttpTests
{
    private static ProviderDefinition Def(string protocol) => new()
    {
        Id = "gw", Protocol = protocol, ApiKey = "k",
        DefaultModel = "m", Modality = "vision",
    };

    [Fact]
    public void ClaudeAdapter_StillRejectsWrongProtocol()
    {
        Assert.Throws<ArgumentException>(() => new Lilara.Core.Protocol.Claude.ClaudeAdapter(Def(Protocols.OpenAiChat)));
    }

    [Fact]
    public void ChatAdapter_RejectsWrongProtocol()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OpenAiChatAdapter(Def(Protocols.ClaudeMessages)));
        Assert.Contains(Protocols.OpenAiChat, ex.Message);
    }

    [Fact]
    public void ResponsesAdapter_RejectsWrongProtocol()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OpenAiResponsesAdapter(Def(Protocols.ClaudeMessages)));
        Assert.Contains(Protocols.OpenAiResponses, ex.Message);
    }

    [Fact]
    public void ChatAdapter_RejectsNullProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenAiChatAdapter(null!));
    }

    [Fact]
    public void OpenAiHttp_DefaultAuthIsBearer()
    {
        // OpenAI 兼容端点默认用 Bearer，与 Claude 的 x-api-key 相反，故各测各的。
        Assert.Equal(AuthSchemes.Bearer, new OpenAiHttp(Def(Protocols.OpenAiChat)).AuthScheme);
    }

    [Fact]
    public void OpenAiHttp_HonoursExplicitApiKeyScheme()
    {
        var def = Def(Protocols.OpenAiChat) with { AuthScheme = AuthSchemes.ApiKey };
        Assert.Equal(AuthSchemes.ApiKey, new OpenAiHttp(def).AuthScheme);
    }

    // ---- ShouldWrap：取消 vs 失败的判据（纯函数，离线） ----
    // 这三条是唯一能离线测到的运行时行为；真值表与 ClaudeAdapterTests 那三条相同。

    [Fact]
    public void ShouldWrap_WrapsOrdinaryFailure()
    {
        Assert.True(OpenAiHttp.ShouldWrap(new ArgumentException("boom"), CancellationToken.None));
    }

    [Fact]
    public void ShouldWrap_WrapsTimeoutThatLooksLikeCancellation()
    {
        // HttpClient 超时也是 TaskCanceledException（真失败，必须包装）。
        // 判据是调用方的 token 有没有被取消，不是异常类型。
        Assert.True(OpenAiHttp.ShouldWrap(new TaskCanceledException("timeout"), CancellationToken.None));
    }

    [Fact]
    public void ShouldWrap_DoesNotWrapCallerCancellation()
    {
        // 用户按 Ctrl-C 要原样冒出，不报成「模型调用失败」。
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(OpenAiHttp.ShouldWrap(new OperationCanceledException(cts.Token), cts.Token));
    }

    // ---- 超时：按 provider 配置，由**这一次请求**的令牌管 ----

    // ⚠️ 这一条钉的是「超时**真的**按 provider 生效」。共用那个 `HttpClient` 的 `Timeout`
    // 必须关掉（`InfiniteTimeSpan`）：否则它会在 100 秒处先到，用户把 timeoutSeconds 调到
    // 600 也只是「600 秒以内的 100 秒超时」——而这正是改这一格要解决的那个毛病。
    [Fact]
    public async Task OpenAiHttp_UsesTheConfiguredTimeout_AndSaysSo()
    {
        var http = new OpenAiHttp(Def(Protocols.OpenAiChat) with { TimeoutSeconds = 1 });
        using var slow = new SlowHandler(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<ModelCallFailedException>(
            () => http.PostAsync("/v1/chat/completions", "{}", CancellationToken.None, slow));

        Assert.Contains("超时（1 秒）", ex.Message);
        Assert.True(ex.Transient, "超时是瞬态——重试策略读的就是这个标志");
    }

    // 反向：**调用方取消**不是超时。它要原样冒出去（`Cancellation.ShouldWrap` 那一关），
    // 否则一次 Ctrl-C 会被说成「模型调用超时」，还会被重试十次。
    [Fact]
    public async Task OpenAiHttp_DoesNotReportCallerCancellationAsATimeout()
    {
        var http = new OpenAiHttp(Def(Protocols.OpenAiChat) with { TimeoutSeconds = 60 });
        using var slow = new SlowHandler(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => http.PostAsync("/v1/chat/completions", "{}", cts.Token, slow));
    }

    /// <summary>一个**永不回**的传输：只为让超时那一路真的被走到（不联网、不睡满设定值）。</summary>
    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    // OpenAI 家族不读任何环境变量；走真实进程环境，因为要证的正是「真实环境里设了也不许赢」。
    [Fact]
    public void OpenAiHttp_IgnoresAnthropicBaseUrlFromTheRealEnvironment()
    {
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "http://should-not-be-used.invalid");

            var http = new OpenAiHttp(Def(Protocols.OpenAiChat));

            Assert.Equal(OpenAiHttp.OfficialUrl, http.Endpoint.Url);
            Assert.Equal(EndpointSource.Default, http.Endpoint.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", saved);
        }
    }
}

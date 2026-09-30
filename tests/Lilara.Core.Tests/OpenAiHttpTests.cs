using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
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
        Assert.Throws<ArgumentException>(() => new Adapters.Claude.ClaudeAdapter(Def(Protocols.OpenAiChat)));
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
        // ⚠️ OpenAI 兼容端点的通用约定就是 Bearer；
        // 缺省与 Claude 那套（x-api-key）相反，所以必须各测各的。
        Assert.Equal(AuthSchemes.Bearer, new OpenAiHttp(Def(Protocols.OpenAiChat)).AuthScheme);
    }

    [Fact]
    public void OpenAiHttp_HonoursExplicitApiKeyScheme()
    {
        var def = Def(Protocols.OpenAiChat) with { AuthScheme = AuthSchemes.ApiKey };
        Assert.Equal(AuthSchemes.ApiKey, new OpenAiHttp(def).AuthScheme);
    }

    // ---- ShouldWrap：取消 vs 失败的判据（纯函数，离线） ----
    // ⚠️ 这三条是**本任务唯一能离线测到的运行时行为**——其余全在 PostAsync 内部、无注入口。
    // ⚠️ 形状照抄 ClaudeAdapterTests 的三条同名测试（同一判据、同一张真值表）；
    //    它们必须在**本族**也测一遍，因为内联进 catch 过滤器后就再也测不到了。

    [Fact]
    public void ShouldWrap_WrapsOrdinaryFailure()
    {
        Assert.True(OpenAiHttp.ShouldWrap(new ArgumentException("boom"), CancellationToken.None));
    }

    [Fact]
    public void ShouldWrap_WrapsTimeoutThatLooksLikeCancellation()
    {
        // ⚠️ HttpClient 超时也是 TaskCanceledException——那是一次**真失败**，必须包装。
        // 判据是「调用方自己的 token 被取消了没有」，不是异常类型。
        Assert.True(OpenAiHttp.ShouldWrap(new TaskCanceledException("timeout"), CancellationToken.None));
    }

    [Fact]
    public void ShouldWrap_DoesNotWrapCallerCancellation()
    {
        // ⚠️ 用户按 Ctrl-C 要原样冒出去（03 §3 session.interrupt），不能被报成「模型调用失败」。
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(OpenAiHttp.ShouldWrap(new OperationCanceledException(cts.Token), cts.Token));
    }

    /// <summary>
    /// ⚠️ **实测复现过的真 bug**：控制器在本机环境里量到
    /// `ANTHROPIC_BASE_URL=http://127.0.0.1:15721` 会让**未配 endpoint 的 OpenAI provider**
    /// 把请求发到那个地址（`source=Environment`）。本测试钉住「OpenAI 家族不读任何环境变量」。
    /// ⚠️ 走真实的进程环境（`Environment.SetEnvironmentVariable`），因为要证的正是
    /// 「真实环境里设了也不许赢」——注入式读器证明不了这一点。
    /// </summary>
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

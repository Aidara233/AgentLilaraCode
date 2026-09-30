using Anthropic;
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.Claude;

/// <summary>
/// Claude Messages 适配器。走**官方 C# SDK**，不手搓 HTTP。
/// 职责薄得可怜：构造请求 → 发出去 → 映射回来。翻译逻辑全在两个纯函数里。
/// </summary>
public sealed class ClaudeAdapter : IModelAdapter
{
    public const string ProtocolId = "claude-messages";

    private readonly AnthropicClient _client;

    public ClaudeAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"ClaudeAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'",
                nameof(provider));

        _client = new AnthropicClient
        {
            ApiKey = provider.ApiKey,
            BaseUrl = ResolveBaseUrl(provider),
        };
    }

    /// <summary>没配端点时的官方地址。</summary>
    internal const string OfficialBaseUrl = "https://api.anthropic.com";

    /// <summary>
    /// 端点解析。⚠️ 抽成纯函数是为了能**离线**把「配了用配的 / 没配用官方的」这张表测干净。
    /// ⚠️ 返回的**绝不能是 null**：留空的话 SDK 会去读环境里的 `ANTHROPIC_BASE_URL`，
    /// 一个管理全部会话的无头进程就被环境变量悄悄决定了把数据发到哪。
    /// 实测（探针见 final-fix-report.md）：显式设 BaseUrl 能压过环境变量；不设则被压过。
    /// </summary>
    internal static string ResolveBaseUrl(ProviderDefinition provider) =>
        provider.Endpoint ?? OfficialBaseUrl;

    public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        // ⚠️ Build 必须在 try **里面**。
        // 它的三处 ArgumentException（时间线为空 / 消息为空 / 未知 effort）都是
        // 「这次调用失败了」的一种——让它们逃出包装，调用方就抓不到，
        // 于是一个 --effort xhihg 的拼写错误会以**未捕获异常**的形式崩掉，
        // 而不是给用户一句能读懂的话。
        try
        {
            var parameters = ClaudeRequestBuilder.Build(request);
            var response = await _client.Messages.Create(parameters, cancellationToken: ct);
            return ClaudeResponseMapper.Map(response);
        }
        catch (Exception ex) when (ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException(Describe(ex), ex);
        }
    }

    /// <summary>
    /// 这个异常该不该包成 <see cref="ModelCallFailedException"/>？
    /// ⚠️ **取消不是失败**：用户按 Ctrl-C 要原样冒出去（03 §3 `session.interrupt`、
    /// 01 §9 的工具取消钩子），不能被报成「模型调用失败」。
    /// 但反过来，`HttpClient` 超时**也**是以 `TaskCanceledException`（→ `OperationCanceledException`）
    /// 到达的，那是一次真失败，必须包。
    /// 所以判据是「**调用方自己的 token 被取消了没有**」，不是异常类型。
    /// 抽成纯函数是为了能把这张真值表离线测干净——它藏在 catch 过滤器里就没法测了。
    /// </summary>
    internal static bool ShouldWrap(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || !ct.IsCancellationRequested;

    /// <summary>把 SDK 的异常翻译成一句人话（Review Focus #6）。</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        Anthropic.Exceptions.AnthropicBadRequestException =>
            "请求被上游拒绝（400）。多半是参数不合法——检查模型 id 与 max tokens 的设置。",
        Anthropic.Exceptions.AnthropicUnauthorizedException =>
            "API key 无效或已吊销（401）。检查 `provider set` 里的 --key。",
        Anthropic.Exceptions.AnthropicForbiddenException =>
            "这个 key 没有访问该模型的权限（403）。",
        Anthropic.Exceptions.AnthropicNotFoundException =>
            "模型 id 不存在（404）。检查 --model 拼写。",
        Anthropic.Exceptions.AnthropicRateLimitException =>
            "被限流了（429）。等一会儿再试。",
        Anthropic.Exceptions.Anthropic5xxException =>
            "上游 5xx。这是临时的，重试即可。",
        _ => $"模型调用失败：{ex.Message}",
    };
}

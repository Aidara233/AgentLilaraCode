using Anthropic;
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.Claude;

/// <summary>
/// Claude Messages 适配器。走**官方 C# SDK**，不手搓 HTTP。
/// 职责薄得可怜：构造请求 → 发出去 → 映射回来。翻译逻辑全在两个纯函数里。
/// </summary>
public sealed class ClaudeAdapter : IModelAdapter
{
    public const string ProtocolId = Protocols.ClaudeMessages;

    private readonly AnthropicClient _client;

    /// <summary>
    /// 解析出来的端点（含**来源**）。
    /// ⚠️ 暴露出来是为了**能显示**——「静默接管」才是问题，「接管」本身不是。
    /// 命令面启动时必须把它印出来，见 `Program.cs`。
    /// </summary>
    public ResolvedEndpoint? Endpoint { get; }

    /// <inheritdoc />
    public string Protocol => ProtocolId;

    /// <summary>实际采用的认证方式（解析后的，不是原始配置）。</summary>
    public string AuthScheme { get; }

    public ClaudeAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // ⚠️ 两种认证方式走不同属性：ApiKey → x-api-key，AuthToken → Authorization: Bearer。
        // 本机网关只认后者（实测：ApiKey 打不通、AuthToken 通）。这是协议怪癖，不是可选优化。
        AuthScheme = provider.AuthScheme ?? AuthSchemes.Default;

        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"ClaudeAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'",
                nameof(provider));

        // ⚠️ 始终**显式**设 BaseUrl，哪怕是解析到官方地址。
        // 不设的话客户端会自己去读 ANTHROPIC_BASE_URL——那条路径绕过了
        // `Endpoint` 这个属性，于是「谁定的端点」就没人说得出来，正是要修的静默接管。
        // 解析顺序：provider 配置 > 环境变量 > 官方地址（`EndpointResolver`）。
        var endpoint = EndpointResolver.Resolve(provider.Endpoint);

        _client = AuthScheme == AuthSchemes.Bearer
            ? new AnthropicClient { AuthToken = provider.ApiKey, BaseUrl = endpoint.Url }
            : new AnthropicClient { ApiKey = provider.ApiKey, BaseUrl = endpoint.Url };

        Endpoint = endpoint;
    }

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

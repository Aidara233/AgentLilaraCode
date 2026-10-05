using Anthropic;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.Claude;

// Claude Messages 适配器：走官方 C# SDK，不手搓 HTTP。构造请求 → 发出去 → 映射回来，翻译逻辑全在两个纯函数里。
public sealed class ClaudeAdapter : IModelAdapter
{
    public const string ProtocolId = Protocols.ClaudeMessages;

    private readonly AnthropicClient _client;

    // 解析出来的端点（含来源）。暴露出来是为了能显示——命令面启动时必须把它印出来。
    public ResolvedEndpoint? Endpoint { get; }

    /// <inheritdoc />
    public string Protocol => ProtocolId;

    /// <summary>实际采用的认证方式（解析后的，不是原始配置）。</summary>
    public string AuthScheme { get; }

    public ClaudeAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // 两种认证方式走不同属性：ApiKey → x-api-key，AuthToken → Authorization: Bearer。
        // 本机网关只认后者（ApiKey 打不通、AuthToken 通）。这是协议怪癖，不是可选优化。
        AuthScheme = provider.AuthScheme ?? AuthSchemes.Default;

        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"ClaudeAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'",
                nameof(provider));

        // 始终显式设 BaseUrl，哪怕是解析到官方地址：不设的话客户端会自己去读 ANTHROPIC_BASE_URL，
        // 那条路径绕过 Endpoint 属性，「谁定的端点」就没人说得出来。
        var endpoint = EndpointResolver.Resolve(provider.Endpoint);

        _client = AuthScheme == AuthSchemes.Bearer
            ? new AnthropicClient { AuthToken = provider.ApiKey, BaseUrl = endpoint.Url }
            : new AnthropicClient { ApiKey = provider.ApiKey, BaseUrl = endpoint.Url };

        Endpoint = endpoint;
    }

    public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        // Build 必须在 try 里面：它的三处 ArgumentException（时间线为空 / 消息为空 / 未知 effort）
        // 都是「这次调用失败了」的一种，逃出包装会以未捕获异常崩掉，而不是给用户一句能读懂的话。
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

    // 转发到 Cancellation.ShouldWrap——判据只有一份，别在这里加逻辑。
    internal static bool ShouldWrap(Exception ex, CancellationToken ct) =>
        Cancellation.ShouldWrap(ex, ct);

    /// <summary>把 SDK 的异常翻译成一句人话。</summary>
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

        // 上游回了**不符合 Anthropic 规范的响应体**时，SDK 内部的 STJ 会抛这句
        //（实测：本机网关 401 回 `{"error":"Authentication required"}`——规范要求 `error` 是
        // **对象**、里面再放 `type`/`message`，而它是个**字符串**；SDK 去读 `.error.type` 就抛在这）。
        // ⚠️ 它不是 `AnthropicException` 子类（**认不出 HTTP 状态码**）⇒ 不接的话会落进兜底，
        // 把 STJ 的机器话原样吐给用户——「模型调用失败：The requested operation requires an
        // element of type 'Object'…」。那条消息里**没有一个字**能指向真正的原因。
        _ when ex.Message.Contains("requires an element of type", StringComparison.Ordinal) =>
            "上游回的响应体不符合 Anthropic 规范（没能读出错误的对象结构）。"
            + "多半是**端点不对**，或**认证没被接受**——本机网关通常只认 `--auth bearer`，"
            + "官方 anthropic.com 只认 `--auth api-key`。",

        _ => $"模型调用失败：{ex.Message}",
    };
}

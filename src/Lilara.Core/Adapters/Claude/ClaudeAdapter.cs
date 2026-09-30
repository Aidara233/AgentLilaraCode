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
        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"ClaudeAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'",
                nameof(provider));

        _client = new AnthropicClient { ApiKey = provider.ApiKey };
    }

    public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        var parameters = ClaudeRequestBuilder.Build(request);

        try
        {
            var response = await _client.Messages.Create(parameters, cancellationToken: ct);
            return ClaudeResponseMapper.Map(response);
        }
        catch (Exception ex)
        {
            throw new ModelCallFailedException(Describe(ex), ex);
        }
    }

    /// <summary>把 SDK 的异常翻译成一句人话（Review Focus #6）。</summary>
    private static string Describe(Exception ex) => ex switch
    {
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

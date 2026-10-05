using Lilara.Core.Kernel;
using Lilara.Core.Providers;

namespace Lilara.Core.Protocol.OpenAi;

/// <summary>OpenAI Responses 适配器：构造 → 发 → 映射。</summary>
public sealed class OpenAiResponsesAdapter : IModelAdapter
{
    public const string ProtocolId = Protocols.OpenAiResponses;

    private readonly OpenAiHttp _http;

    /// <summary>与 ClaudeAdapter.Endpoint 同理：能显示才谈得上没静默接管。</summary>
    public ResolvedEndpoint? Endpoint => _http.Endpoint;

    /// <inheritdoc />
    public string Protocol => ProtocolId;

    public OpenAiResponsesAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"OpenAiResponsesAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'", nameof(provider));

        _http = new OpenAiHttp(provider);
    }

    public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        // Build 必须在 try 里面：它的两处 ArgumentException（时间线为空 / 空消息）也是「调用失败」的一种，
        // 逃出去会变成未捕获异常。
        try
        {
            var body = OpenAiResponsesRequest.Build(request);
            var json = await _http.PostAsync(OpenAiResponsesRequest.Path, body, ct);
            return OpenAiResponsesResponse.Map(json);
        }
        catch (ModelCallFailedException) { throw; }               // 已经是人话，别套两层
        catch (Exception ex) when (OpenAiHttp.ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }
}

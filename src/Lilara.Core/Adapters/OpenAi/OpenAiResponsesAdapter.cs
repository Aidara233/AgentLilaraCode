using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>OpenAI Responses 适配器。⚠️ 薄壳：构造 → 发 → 映射。翻译全在两个纯函数里。</summary>
public sealed class OpenAiResponsesAdapter : IModelAdapter
{
    public const string ProtocolId = Protocols.OpenAiResponses;

    private readonly OpenAiHttp _http;

    /// <summary>⚠️ 与 <see cref="ClaudeAdapter.Endpoint"/> 同理：能显示才谈得上「没静默接管」。</summary>
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
        // ⚠️ Build 必须在 try **里面**，与 Claude 那条同形。
        // 它的两处 ArgumentException（时间线为空 / 空消息）都是「这次调用失败了」的一种——
        // 让它们逃出包装，调用方就抓不到，一个构造不出请求的输入会以**未捕获异常**崩掉，
        // 而不是给用户一句能读懂的话。
        try
        {
            var body = OpenAiResponsesRequest.Build(request);          // 纯函数：失败也是「调用失败」
            var json = await _http.PostAsync(OpenAiResponsesRequest.Path, body, ct);
            return OpenAiResponsesResponse.Map(json);                  // 纯函数
        }
        catch (ModelCallFailedException) { throw; }               // 已经是人话了，别套两层
        catch (Exception ex) when (OpenAiHttp.ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }
}

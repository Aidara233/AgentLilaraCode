using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

/// <summary>OpenAI Chat 适配器：构造 → 发 → 映射。</summary>
public sealed class OpenAiChatAdapter : IModelAdapter
{
    public const string ProtocolId = Protocols.OpenAiChat;

    private readonly OpenAiHttp _http;

    /// <summary>与 ClaudeAdapter.Endpoint 同理：能显示才谈得上没静默接管。</summary>
    public ResolvedEndpoint? Endpoint => _http.Endpoint;

    /// <inheritdoc />
    public string Protocol => ProtocolId;

    public OpenAiChatAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"OpenAiChatAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'", nameof(provider));

        _http = new OpenAiHttp(provider);
    }

    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
        CallAsync(request, ct, transport: null);

    /// <param name="transport">
    /// ⚠️ **只为可测性**：`null` = 生产路径（共用的 HttpClient）。测试传一个假 handler 进来，
    /// 让 `OpenAiHttp.PostAsync` 的状态码分支（含「这次失败值不值得重试」那个标志）被**真的跑到**。
    /// ⚠️ 它不是接口的一部分（`IModelAdapter` 只有一个两参数的重载）——生产调用点永远走上面那个。
    /// </param>
    public async Task<ModelReply> CallAsync(
        ModelRequest request, CancellationToken ct, HttpMessageHandler? transport)
    {
        // Build 必须在 try 里面：它的两处 ArgumentException（时间线为空 / 空消息）也是「调用失败」的一种，
        // 逃出去会变成未捕获异常。
        try
        {
            var body = OpenAiChatRequest.Build(request);
            var json = await _http.PostAsync(OpenAiChatRequest.Path, body, ct, transport);
            return OpenAiChatResponse.Map(json);
        }
        catch (ModelCallFailedException) { throw; }               // 已经是人话，别套两层
        catch (Exception ex) when (OpenAiHttp.ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }
}

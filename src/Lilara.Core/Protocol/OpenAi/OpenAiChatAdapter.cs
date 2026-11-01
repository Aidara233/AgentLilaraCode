using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

/// <summary>OpenAI Chat 适配器：构造 → 发 → 映射。</summary>
public sealed class OpenAiChatAdapter : IModelAdapter, IStreamingModelAdapter
{
    public const string ProtocolId = Protocols.OpenAiChat;

    private readonly OpenAiHttp _http;

    /// <summary>与 ClaudeAdapter.Endpoint 同理：能显示才谈得上没静默接管。</summary>
    public ResolvedEndpoint? Endpoint => _http.Endpoint;

    /// <inheritdoc />
    public string Protocol => ProtocolId;

    /// <inheritdoc />
    public bool ExplicitCache { get; }

    /// <summary>这个端点上要不要发 `reasoning_effort`（`effortParam: reasoning`）。默认不发。</summary>
    public bool SendEffort { get; }

    /// <summary>这个提供商是否接收增量（`provider.Streaming`）。</summary>
    public bool Streaming { get; }

    public OpenAiChatAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ExplicitCache = provider.CacheMode == CacheBreakpoint.Explicit;
        SendEffort = provider.EffortParam == EffortParam.Reasoning;
        Streaming = provider.Streaming;
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
            var body = OpenAiChatRequest.Build(request, ExplicitCache, SendEffort);
            var json = await _http.PostAsync(OpenAiChatRequest.Path, body, ct, transport);
            return OpenAiChatResponse.Map(json);
        }
        catch (ModelCallFailedException) { throw; }               // 已经是人话，别套两层
        catch (Exception ex) when (OpenAiHttp.ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ **增量只是转发**：真正交回去的是 <see cref="OpenAiChatStream.Finish"/> 拼出来、
    /// 再走**同一个** <see cref="OpenAiChatResponse.Map"/> 的那份 ⇒ 流式与非流式对同一段输出
    /// 给出逐字段相同的 <see cref="ModelReply"/>。这条等价性有测试钉着。
    /// </remarks>
    public async Task<ModelReply> StreamAsync(
        ModelRequest request, Action<ModelDelta>? onDelta, CancellationToken ct)
    {
        // 没开增量 / 没人要看 ⇒ 走原来那条路（请求体也因此逐字节不变）。
        if (!Streaming || onDelta is null) return await CallAsync(request, ct);

        try
        {
            var body = OpenAiChatRequest.Build(request, ExplicitCache, SendEffort, stream: true);
            var state = new ChatStreamState();

            // ⚠️ `onDelta` 在**读流的那个循环里**同步调用：它一抛（头断开之类），
            // 这次调用就该跟着停——不能把流读完再回头处理一个早就不需要的回复。
            await _http.PostStreamAsync(OpenAiChatRequest.Path, body, data =>
            {
                if (data == "[DONE]") return Task.CompletedTask;
                if (OpenAiChatStream.Apply(data, state) is { } delta) onDelta(delta);
                return Task.CompletedTask;
            }, ct);

            return OpenAiChatResponse.Map(OpenAiChatStream.Finish(state));
        }
        catch (ModelCallFailedException) { throw; }
        catch (Exception ex) when (OpenAiHttp.ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }
}

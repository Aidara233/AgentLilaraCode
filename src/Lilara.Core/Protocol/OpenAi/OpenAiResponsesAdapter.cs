using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

/// <summary>OpenAI Responses 适配器：构造 → 发 → 映射。</summary>
public sealed class OpenAiResponsesAdapter : IModelAdapter, IStreamingModelAdapter
{
    public const string ProtocolId = Protocols.OpenAiResponses;

    private readonly OpenAiHttp _http;

    /// <summary>与 ClaudeAdapter.Endpoint 同理：能显示才谈得上没静默接管。</summary>
    public ResolvedEndpoint? Endpoint => _http.Endpoint;

    /// <inheritdoc />
    public string Protocol => ProtocolId;

    /// <inheritdoc />
    public bool ExplicitCache { get; }

    /// <summary>这个端点上要不要发 `reasoning.effort`（`effortParam: reasoning`）。默认不发。</summary>
    public bool SendEffort { get; }

    /// <summary>这个端点上要不要 `include: ["reasoning.encrypted_content"]`（`reasoningInclude: include`）。默认不发。</summary>
    public bool IncludeReasoning { get; }

    /// <summary>这个提供商是否接收增量（`provider.Streaming`）。</summary>
    public bool Streaming { get; }

    public OpenAiResponsesAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ExplicitCache = provider.CacheMode == CacheBreakpoint.Explicit;
        SendEffort = provider.EffortParam == EffortParam.Reasoning;
        IncludeReasoning = provider.ReasoningInclude == ReasoningInclude.Mode;
        Streaming = provider.Streaming;
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
            var body = OpenAiResponsesRequest.Build(request, ExplicitCache, SendEffort, IncludeReasoning);
            var json = await _http.PostAsync(OpenAiResponsesRequest.Path, body, ct);
            return OpenAiResponsesResponse.Map(json);
        }
        catch (ModelCallFailedException) { throw; }               // 已经是人话，别套两层
        catch (Exception ex) when (OpenAiHttp.ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ 终态优先用上游的 `response.completed`（含 usage / 推理项 / 内置工具项），
    /// 见 <see cref="OpenAiResponsesStream.Finish"/>。
    /// </remarks>
    public async Task<ModelReply> StreamAsync(
        ModelRequest request, Action<ModelDelta>? onDelta, CancellationToken ct)
    {
        if (!Streaming || onDelta is null) return await CallAsync(request, ct);

        try
        {
            var body = OpenAiResponsesRequest.Build(
                request, ExplicitCache, SendEffort, IncludeReasoning, stream: true);
            var state = new ResponsesStreamState();

            await _http.PostStreamAsync(OpenAiResponsesRequest.Path, body, data =>
            {
                if (data == "[DONE]") return Task.CompletedTask;
                if (OpenAiResponsesStream.Apply(data, state) is { } delta) onDelta(delta);
                return Task.CompletedTask;
            }, ct);

            return OpenAiResponsesResponse.Map(OpenAiResponsesStream.Finish(state));
        }
        catch (ModelCallFailedException) { throw; }
        catch (Exception ex) when (OpenAiHttp.ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }
}

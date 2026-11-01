namespace Lilara.Core.Protocol;

// 上游协议适配器：内建焊死在代码里，不做插件——出去的协议是产品形状，不该可换。
public interface IModelAdapter
{
    // 解析出来的端点（含来源）。暴露出来是为了能显示。可空：占位适配器返回 null，调用方不要印。
    ResolvedEndpoint? Endpoint { get; }

    // 实际会说的协议（Protocols 里的常量）。发出去的请求必须用这里的，不是会话 spec 里的协议快照——
    // 两者会因 provider set --protocol 分叉。
    string Protocol { get; }

    /// <summary>
    /// 这个端点上**要不要标显式缓存断点**（`ProviderDefinition.CacheMode == "explicit"`）。
    ///
    /// <para>⚠️ <b>默认 <c>false</c> = 与今天逐字节相同</b>：三族对缓存的默认行为不一样
    /// （Anthropic 无隐式、OpenAI 有隐式但只有 5.6+ 认显式断点、DeepSeek 系自动）⇒ 这必须是
    /// **按端点开的开关**，不能默认打开。判据与位置见 <see cref="CacheBreakpoint"/>。</para>
    /// </summary>
    bool ExplicitCache => false;

    Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct);
}

/// <summary>可选的增量生成能力；适配器仍返回完整且权威的最终回复。</summary>
public interface IStreamingModelAdapter
{
    /// <summary>这个提供商是否开启了增量（`provider.Streaming`）。关闭时与既有调用路径逐字节相同。</summary>
    bool Streaming { get; }

    /// <summary>
    /// 发一次流式请求：`onDelta` 收到**只供显示**的正文增量，返回值仍是权威的完整回复。
    /// `onDelta` 为 null = 不关心增量（探针），此时可以直接走非流式路径。
    /// </summary>
    Task<ModelReply> StreamAsync(
        ModelRequest request, Action<ModelDelta>? onDelta, CancellationToken ct);
}

/// <summary>
/// 只供实时显示的文本增量。⚠️ 不含工具参数（半截 JSON 没有显示价值，
/// 也会让「参数逐字原文」那条纪律多一种形态）。
/// </summary>
/// <param name="BlockIndex">这一块在最终回复 `Blocks` 里的序号，头据此把增量放对位置。</param>
/// <param name="Kind">`text` / `reasoning`，与 <see cref="Kernel.Block"/> 的判别式同名。</param>
public sealed record ModelDelta(int BlockIndex, string Kind, string Text);

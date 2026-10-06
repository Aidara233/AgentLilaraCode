using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

// 一次模型调用要的全部东西：核心发全量 canonical，适配器负责翻译成具体协议。
public sealed record ModelRequest
{
    public required string ModelId { get; init; }
    public required int MaxTokens { get; init; }

    // 这次调用走哪条协议。能力表按它判断哪些块发得出去，必须先于块过滤存在。
    // 取值必须来自即将真正发送的那个适配器的 IModelAdapter.Protocol，不能取会话 spec 里的快照——
    // 两者会因 provider set --protocol 分叉，分叉会在能承载推理的链路上静默丢掉推理。
    public required string Protocol { get; init; }

    /// <summary>思考强度轴，映射到协议参数——绝不进上下文。</summary>
    public required string Effort { get; init; }

    /// <summary>
    /// 采样温度（`temperature` 轴）。`null` = **本次不发这个字段**。
    ///
    /// <para>⚠️ `null` 与「发一个默认值」**不是**同一件事：三个构造器都按
    /// 「`null` 就不写这个键」实现，所以没设过的会话发出去的请求**逐字节**与加这条之前相同。
    /// 这正是磁盘/线上格式能保持不变的原因。</para>
    ///
    /// <para>⚠️ 它由 `Axes.NumberOrNull` 从**轴**换算而来（不是从 spec）。</para>
    /// </summary>
    public required double? Temperature { get; init; }

    /// <summary>系统块。核心不带提示词，最小闭环恒为空；「块数恒定」说的是最多各 1 块，不是非有不可。</summary>
    public required IReadOnlyList<Block> SystemBlocks { get; init; }

    /// <summary>
    /// 工具表。⚠️ **它在 Claude 协议下位于前缀的最前端** ⇒ 改它是最贵的操作
    /// （`00` §7 判据 5：「只该在冷窗口做」）。所以这条**不是**给「这一步想给什么工具」用的旋钮，
    /// 而是会话级的静态属性。
    ///
    /// <para>⚠️ **空表是合法形态**：没有插件就是纯 chat（`04` §1.3 推论 1）。
    /// ⇒ 三个构造器都把「没有 tools 键」当作**不发这个字段**，与 <see cref="Temperature"/>
    /// 的 `null` 同一个形状——没配工具的会话，请求**逐字节**与加这条之前相同。</para>
    /// </summary>
    public required IReadOnlyList<ToolSpec> Tools { get; init; }

    public required IReadOnlyList<TimelineEvent> Timeline { get; init; }
}

/// <summary>模型回话的产物。</summary>
public sealed record ModelReply
{
    public required IReadOnlyList<Block> Blocks { get; init; }

    /// <summary>协议原样透传的停止原因，用于诊断。</summary>
    public required string StopReason { get; init; }

    /// <summary>安全分类器拒答。拒答时 Blocks 通常为空，核心必须当一等结果处理，不能当成「模型说了空话」。</summary>
    public required bool Refused { get; init; }
}

/// <summary>调用失败，带一句人话。</summary>
public sealed class ModelCallFailedException(string message, Exception? inner = null)
    : Exception(message, inner);

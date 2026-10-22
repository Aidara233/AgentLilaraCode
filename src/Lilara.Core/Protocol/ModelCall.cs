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

    /// <summary>
    /// **三个槽位各自的产物**（`01` §3.1 / §3.2）——系统块 · 首要块 · 尾块。
    ///
    /// <para>⚠️ <b>为什么是一格而不是三格</b>：它们的<b>来源判据是同一件事</b>
    /// （`SessionBlock.Render` 的三条路），而三格会让「哪一块从哪来」在类型上
    /// 分给三个地方各自回答。一个 <see cref="Session.PromptSlots"/> 里装着三块，
    /// 而**适配器那一侧仍然按位置取**（Claude：前两块进 `system`、尾块进消息；
    /// Chat：前两块进首条 system 消息、尾块进末条消息）——位置语义没有被这一格藏起来。</para>
    ///
    /// <para>⚠️ <b>尾块<b>不是</b>「一条永远追加的消息」</b>：它的语义是**替换**——
    /// 同一份 <see cref="Session.PromptSlots"/> 每次渲染都给出**一份**尾块，
    /// 各构造器把它<b>拼进</b>该去的地方（Claude 拼进末尾那条 user 消息），
    /// 所以「永远只有一份、不累积」这条性质由**构造器**保证，不靠调用方自觉。</para>
    ///
    /// <para>⚠️ <b>三块都可以是空的</b>（`04` §1.3 推论 1：裸核心 = 纯 chat）。
    /// 三个构造器都按「空 ⇒ 不发这个键 / 不加这个块」处理，
    /// 与 <see cref="Temperature"/> 的 `null`、<see cref="Tools"/> 的空表**同一条判据**：
    /// 没配过的东西，请求**逐字节**与加它之前相同。</para>
    /// </summary>
    public required Session.PromptSlots Slots { get; init; }

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

    /// <summary>上游报告的 token 用量；协议没有提供时为 null。</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>上游返回的模型标识；缺失时为 null。</summary>
    public string? ReportedModel { get; init; }

    /// <summary>上游响应标识；缺失时为 null。</summary>
    public string? ResponseId { get; init; }
}

/// <summary>调用失败，带一句人话。</summary>
/// <param name="Transient">
/// **再发一次有没有可能成功**——只由两个协议适配器在**看得见状态码 / 异常类型的那一层**打标，
/// 由 <see cref="RetryPolicy"/> 读取。⚠️ 默认 <c>false</c>：说不清的失败一律不重试
/// （方向是「少重试一次」，不是「多撞一次上游」）。
///
/// <para>⚠️ <b>它<b>不是</b>持久化字段</b>——这个异常从不上线、也不落盘，它只活在一次调用的栈里。</para>
/// </param>
public sealed class ModelCallFailedException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Transient { get; init; }
}

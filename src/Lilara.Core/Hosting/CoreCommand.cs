using Lilara.Core.Adapters;
using Lilara.Core.Canonical;

namespace Lilara.Core.Hosting;

/// <summary>
/// 一条命令（`03` §3 的子集）。
/// ⚠️ **这是「头能做什么」的类型化形状**，与文本无关——控制台把一行字解析成它，
/// 控制面把一帧 JSON 反序列化成它。两个前端走**同一个命令集**（`04` §1.1）。
/// </summary>
public abstract record CoreCommand
{
    /// <summary>
    /// 配置提供商。⚠️ 除 <paramref name="Id"/> 与 <paramref name="ApiKey"/> 外**都可空**——
    /// null 表示「本次没提到」，落盘时要**保留旧值**（整文件覆盖会抹掉手写配置）。
    /// </summary>
    public sealed record ProviderUpsert(
        string Id, string ApiKey, string? Model, string? Protocol, string? Endpoint, string? Auth)
        : CoreCommand;

    /// <summary>⚠️ **显式动作**——核心不替你决定该有个会话（`01` §8.1）。</summary>
    public sealed record SessionCreate : CoreCommand;

    public sealed record SessionActivate(string SessionId) : CoreCommand;

    /// <summary>用户消息。⚠️ 是**块列表**不是字符串——`03` §9.1 的附件就是这么进来的。</summary>
    public sealed record SessionSend(string SessionId, IReadOnlyList<Block> Blocks) : CoreCommand;

    public sealed record SessionStart(string SessionId) : CoreCommand;

    public sealed record SessionInterrupt(string SessionId) : CoreCommand;
}

/// <summary>
/// 回执（`03` §3）。⚠️ **只有两类**。
/// </summary>
public abstract record Ack
{
    public required string CommandId { get; init; }

    /// <summary>分配型命令（`session.create`）**必须**回传新身份，否则后续命令无从指涉。</summary>
    public sealed record Accepted : Ack
    {
        public string? SessionId { get; init; }

        /// <summary>
        /// 给**人**看的一句补充（如保存后的协议名）。
        /// ⚠️ 它**不是协议的一部分**——头不该依赖它，它只是省得前端重读一遍存储。
        /// </summary>
        public string? Note { get; init; }
    }

    /// <summary>
    /// ⚠️ **只用于「命令本身不合法」**：会话不存在、协议名未知、轴取值非法。
    /// **不用于业务结果**——「提供商没了」「模型 400」「上游 503」一个都不能走这里，
    /// 它们是**事件的失败**（`error` 事件），不是**命令的失败**。
    /// 搞混了的话，头会把一次网络抖动当成「你的命令写错了」。
    /// </summary>
    public sealed record Rejected : Ack
    {
        public required string Reason { get; init; }
    }
}

/// <summary>
/// 一次分派的产物。
/// ⚠️ <paramref name="Reply"/> 只在**同步完成模式**下非空（控制台用）——
/// 控制台没有视图，`04` §1.1 因此允许它「本地退化成同步拿回执」。
/// 控制面走异步模式：立刻拿到 <see cref="Ack"/>，模型回话经**事件**流出。
/// </summary>
public sealed record DispatchResult(Ack Ack, ModelReply? Reply = null)
{
    public bool IsRejected => Ack is Ack.Rejected;
}

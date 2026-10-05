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
    /// 配置提供商。⚠️ 除 <paramref name="Id"/> 外**都可空**——
    /// null 表示「本次没提到」，落盘时要**保留旧值**（整文件覆盖会抹掉手写配置）。
    ///
    /// <para>⚠️ <paramref name="ApiKey"/> 也是可空的，语义是**省略 = 保留已存的那个 key**
    /// （用户裁定 R-B：key 能改不能看 ⇒ 头读不到 key，不该逼它重发才能改协议）。
    /// ⚠️ 而**新** provider 省略 key 必须被拒（存一个发不出去的配置比拒绝更糟）——
    /// 那条语义在 dispatcher，**别照此在别处放行**。</para>
    /// </summary>
    public sealed record ProviderUpsert(
        string Id, string? ApiKey, string? Model, string? Protocol, string? Endpoint, string? Auth)
        : CoreCommand;

    /// <summary>
    /// 删掉一个提供商定义（移除 `<数据根>/providers/&lt;id&gt;.json`）。
    ///
    /// <para>⚠️ **`Id` 与 <see cref="ProviderUpsert.Id"/> 走的是同一个闸**
    /// （`CommandDispatcher.IsSafeProviderId`），而这条上更要紧：**删是不可逆的**。
    /// `Id` 决定删**哪个**文件——坏 id（`../escaped`）会让它在 `providers/` **之外**动手，
    /// 而那种删除**挽不回来**（`upsert` 用同一个 id 只是把文件写到怪地方，没毁掉别的）。</para>
    ///
    /// <para>⚠️ **不存在 ⇒ 拒绝，不是幂等成功**——判据在 dispatcher（两个前端共用）。</para>
    /// </summary>
    public sealed record ProviderDelete(string Id) : CoreCommand;

    /// <summary>⚠️ **显式动作**——核心不替你决定该有个会话（`01` §8.1）。</summary>
    public sealed record SessionCreate : CoreCommand;

    public sealed record SessionActivate(string SessionId) : CoreCommand;

    /// <summary>用户消息。⚠️ 是**块列表**不是字符串——`03` §9.1 的附件就是这么进来的。</summary>
    public sealed record SessionSend(string SessionId, IReadOnlyList<Block> Blocks) : CoreCommand;

    public sealed record SessionStart(string SessionId) : CoreCommand;

    public sealed record SessionInterrupt(string SessionId) : CoreCommand;

    /// <summary>
    /// 设置一条行为控制轴（`01` §3.5）。
    /// ⚠️ 取值校验在 dispatcher（**线上也要吃**），控制台只做文本拆词。
    /// </summary>
    public sealed record SessionSetAxis(string SessionId, string Axis, string Value) : CoreCommand;

    /// <summary>占用一片区域（`01` §12.2）。路径可以是相对的，按会话的工作目录解析。</summary>
    /// <param name="Scope">
    /// ⚠️ **名字与形状都照协议**：`03` §3 写的是 `claim.acquire(sessionId, scope)`，
    /// 而 `01` §12.4 的 release 形参写作 `scope: [...]` ⇒ 它是**列表**。
    /// ⚠️ 叫 `Path` 会与协议不符（头的将来客户端按 `scope` 发帧 ⇒ 「命令形状不对」）。
    /// </param>
    public sealed record ClaimAcquire(string SessionId, IReadOnlyList<string> Scope) : CoreCommand;

    /// <summary>释放。⚠️ 这是「模型失控时的逃生口」（`03` §3）——所以它**不检查**占用者是不是自己。</summary>
    /// <param name="Scope">⚠️ 同 <see cref="ClaimAcquire"/>：列表、名字照协议。</param>
    public sealed record ClaimRelease(string SessionId, IReadOnlyList<string> Scope) : CoreCommand;
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

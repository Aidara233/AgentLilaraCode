namespace Lilara.Core.Kernel;

// 会话持有的模型选择：解析结果的快照 + 指向定义的引用。
public sealed record ModelSelection
{
    // 指向 providers/ 里的定义。
    public required string ProviderId { get; init; }

    public required string ModelId { get; init; }

    // 快照：**最近一次设定模型时**供应商的协议是什么。建会话时写一次，
    // `session.configure` 换供应商时**跟着重新快照**——它必须与 ProviderId 同代。
    // ⚠️ 别拿它当「现在真的走哪条协议」：`Stepper` 取的是**适配器**的 Protocol，
    // 因为 provider 定义可以被 `provider set --protocol` 改掉，那样两者会分叉。
    public required string Protocol { get; init; }

    // 思考强度轴。纯请求参数，绝不进上下文。
    public required string Effort { get; init; }

    public required int MaxTokens { get; init; }
}

// ⚠️ **不是「创建时写一次，此后不可变」**——那句曾经是真的，`session.configure` 之后不再是。
// 它现在是 handle 上**两份可变数据之一**（另一份是 SessionState），改它**必须**走
// `SessionHandle.UpdateSpecAndPersist`（与 State 同一把锁、同一条纪律）。
// ⚠️ 记录本身仍是 `init`-only：改的是「谁持有它」，不是「它能不能改」。
public sealed record SessionSpec
{
    /// <summary>
    /// 磁盘格式版本。写盘时恒等于 <see cref="Json.FormatVersion"/>（默认值即当前版本，
    /// 与 `ReasoningBlock.Kind` 的做法同形）；读盘时由 `SessionStore` **显式校验**，
    /// 比自己新 ⇒ 明确拒绝，不是静默重解释。
    /// </summary>
    public int Version { get; init; } = Json.FormatVersion;

    public required string SessionId { get; init; }

    // 会话**出生地**，会话一旦建出来就不会再变（不像 Model 那样可改）。
    public required string ProjectKey { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    // 可改：`session.configure` 换绑供应商 / 换模型。⚠️ 改 ProviderId 后必须
    // `CoreHost.InvalidateSteppers()`，否则请求会走旧供应商的适配器（旧 key、旧端点）。
    public required ModelSelection Model { get; init; }

    /// <summary>
    /// 人给会话起的名字（`session.create --title`）。
    ///
    /// <para>⚠️ **只在建会话那一刻写一次，此后没有任何命令能改它**——
    /// 所以也**没有** `session.renamed` 事件可发，头刷新靠重拉快照。
    /// 「点一下给个临时名」那种做法要丢掉：没有改名命令 ⇒ 名字是**永久**的，
    /// 工具不该替用户起名。</para>
    ///
    /// <para>⚠️ **可空不是「磁盘格式不破」的要求**——那件事现在由**格式版本**承担
    /// （见 `Storage.FileFormat`）：缺字段 = 一份老文件，读的一方当场就知道。
    /// 可空在这里是**语义**：`null` 字面就是「没起名」，与「名叫空串」不是一回事。</para>
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// 归档时刻；`null` = 没归档。
    ///
    /// <para>⚠️ **归档是「原地标记」，不是「搬走」**：目录不动，`ListSessions` **照列**它。
    /// 理由是语义而不是省事——「怎么算一个项目下的会话」只该有一份实现，而归档会话
    /// 是**完整的**会话（`/state` 读得出、`activate` 载得进、`send` 发得动、`delete` 删得掉）。
    /// 若 `ListSessions` 开始滤它，**`/claims` 会跟着一起漏**（它也走 `ListSessions`，
    /// 而归档会话可能仍占着路径）⇒ 用户看到两份自相矛盾的真相。
    /// ⇒ **过滤挪到显示层**（头折叠、控制台不折叠——那是有意的两种视图）。</para>
    ///
    /// <para>⚠️ 可空是**语义**（`null` = 没归档），不再是「为了让老文件逐字节不变」——
    /// 那件事现在由**格式版本**承担。</para>
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; init; }
}

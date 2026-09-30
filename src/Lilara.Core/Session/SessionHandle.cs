using Lilara.Core.Canonical;
using Lilara.Core.Persistence;

namespace Lilara.Core.Session;

/// <summary>
/// 一个已激活会话在内存里的把手：数据 + 目录位置。
/// ⚠️ 它会变，但它是**数据**——没有循环、没有定时器（02 §5「激活即缓存」）。
/// </summary>
public sealed record SessionHandle
{
    public required string ProjectKey { get; init; }
    public required SessionSpec Spec { get; init; }
    public required SessionState State { get; set; }
    public required string Directory { get; init; }

    /// <summary>
    /// 轴的当前值。⚠️ **非空**——<see cref="Load"/> 已经把磁盘上的 null 补成默认值。
    /// 加一条轴时**只改 <see cref="Axes.Defaults"/>**：<see cref="Load"/> 是按默认值补齐的，
    /// 不是按硬编码名单，所以新轴对老会话自动生效。
    ///
    /// <para>⚠️ **`set` 而不是 `init`**：`session.setAxis`（Task 3）要改它，
    /// 而 T3 的处理器**不**走 `with`——`SessionHandle` 是可变的活对象，
    /// 换一个新实例会让激活表里那份**指向旧实例**（状态分叉）。
    /// 改属性 + <see cref="SaveAxes"/> 才是对的。</para>
    /// </summary>
    public required IReadOnlyDictionary<string, string> Axes { get; set; }

    public string SessionId => Spec.SessionId;
    public string SpecPath => Path.Combine(Directory, "spec.json");
    public string StatePath => Path.Combine(Directory, "state.json");
    public string HistoryPath => Path.Combine(Directory, "history.jsonl");

    // ---- 待办队列与「一把标志」（01 §6.1 / 02 §3.1） ----

    /// <summary>
    /// ⚠️ **内存队列，不落盘**——这是**刻意的偏离**，别当成漏了。
    /// `01` §6.1 / §12.8 要求队列进 `state.json`，但 `SessionState.Pending` 是
    /// `IReadOnlyList&lt;string&gt;`，**装不下块**（`03` §9.1 的 `session.send` 带附件块）。
    /// 改它的类型是一次**磁盘格式迁移**，而磁盘格式已冻结。
    /// ⇒ 最小实现只在内存排队：崩溃会丢掉未开跑的待办，而磁盘上**看不出来**
    /// （`Pending` 恒为空列表，不是「有而没写」）。
    /// </summary>
    private readonly Queue<IReadOnlyList<Block>> _pending = new();
    private readonly Lock _queueLock = new();

    /// <summary>
    /// 「这一步在跑」——`02` §3.1 说的「重入保护只剩**一把标志**」。
    /// ⚠️ 它是 `启动(s)` 的**去重**判据（`01` §6.1：「会话 Running？→ no-op」）。
    ///
    /// <para>⚠️ **必须是原子的 test-and-set，不能用 `volatile bool` + 读改写。**
    /// 第一版写的是 `!_running && (_running = true)`——那是经典的 read-then-write：
    /// 两个线程**都读到 false**，于是**都返回 true**，两次推进都被安排上。
    /// `volatile` 只保证**可见性**，不保证**原子性**。</para>
    ///
    /// <para>后果正是 Task 4 花大力气修掉的那个症状（「连发 N 条只调一次模型」失效，
    /// 变成串行地调两次）——而信号量会把它们**串起来**，所以看起来不像并发 bug，
    /// 只像「偶尔多烧一次钱」。可达路径：控制台（同步路）与头（后台路）同时对同一会话发送。</para>
    ///
    /// <para>⚠️ 纪律：**没实际跑出过复现**——这是靠**代码审查**定的（读改写不是原子操作，
    /// 这是语言语义，不需要测量）。唯一调用点在 `CommandDispatcher`。</para>
    /// </summary>
    private int _step;

    public bool IsRunning => Volatile.Read(ref _step) == 1;

    /// <summary>入队。⚠️ 调用方**已经**把这条消息追加进记录了（`01` §6.1 的次序）。</summary>
    public void Enqueue(IReadOnlyList<Block> blocks)
    {
        lock (_queueLock) _pending.Enqueue(blocks);
    }

    public int QueueDepth { get { lock (_queueLock) return _pending.Count; } }

    /// <summary>
    /// 取空队列（`01` §6.1：「否则取空队列 → 推进」）。
    /// ⚠️ **取空**是「合并是队列的副产品」那条的实现：连发 3 条时，
    /// 第一次取就把 3 条一起拿去渲染，于是只调一次模型。
    /// </summary>
    public IReadOnlyList<Block> DrainQueue()
    {
        lock (_queueLock)
        {
            if (_pending.Count == 0) return [];

            var batch = new List<Block>();
            while (_pending.Count > 0) batch.AddRange(_pending.Dequeue());
            return batch;
        }
    }

    /// <summary>`启动(s)` 的去重：已在 Running 则返回 false（no-op）。**原子。**</summary>
    public bool TryEnterStep() => Interlocked.CompareExchange(ref _step, 1, 0) == 0;

    public void ExitStep() => Volatile.Write(ref _step, 0);

    public static SessionHandle Create(SessionStore store, string workingDirectory, ModelSelection model)
    {
        var projectKey = Projects.ProjectKey.FromPath(workingDirectory);
        var spec = new SessionSpec
        {
            SessionId = Guid.NewGuid().ToString("N")[..12],
            ProjectKey = projectKey,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            CreatedAt = DateTimeOffset.UtcNow,
            Model = model,
        };
        var state = new SessionState
        {
            SessionId = spec.SessionId,
            Status = "Idle",
            Turn = 0,
            Pending = [],
        };

        // ⚠️ 先 state 后 spec——顺序是刻意的，别调换。
        // 崩溃在两次写之间只可能留下「有 state、无 spec」的目录：
        // TryReadSpec 返回 null，ListSessions 静默跳过它 → 孤儿是**隐形**的。
        // 反过来（先 spec 后 state）留下的目录**能被 ListSessions 列出来**，
        // 而 ReadState 会抛 FileNotFoundException ——
        // Task 11 取 [0] 当"当前会话"，于是启动直接崩，还得手动删目录才能恢复。
        // 隐形孤儿 vs 致命孤儿：用已有的 null 守卫，不用新的错误处理。
        store.WriteState(projectKey, state);
        store.WriteSpec(projectKey, spec);

        var handle = new SessionHandle
        {
            ProjectKey = projectKey,
            Spec = spec,
            State = state,
            Directory = store.SessionDirectory(projectKey, spec.SessionId),

            // ⚠️ **轴的 effort 从 spec 播种，不是从表里的硬编码默认值。**
            // 提供商配的是 `effort=low` 时会造出一个 **Spec 说 low、轴说 high** 的会话——
            // **出生即分叉**，而 `Stepper` 从轴读 ⇒ 用户从没设过轴，effort 却变了。
            // 其余四条轴没有「会话配置里的镜像」，所以仍取默认值。
            // ⚠️ 类型必须写全 `Lilara.Core.Session.Axes`：本类有个同名属性 `Axes`，
            // 裸写 `Axes.Defaults()` 会被解析成那个实例属性（CS0120），**不是**这张表。
            Axes = WithEffortFrom(Lilara.Core.Session.Axes.Defaults(), spec.Model.Effort),
        };

        // ⚠️ 默认值也**要落盘**：不落的话第一次重启时 `state.Axes` 是 null，
        // `Load` 会补一份新的默认值——结果一样，但磁盘上「没设过」与「设成了默认值」
        // 就永远分不出来，而将来改默认值会让老会话**静默改变行为**。
        handle.SaveAxes(store);
        return handle;
    }

    /// <summary>把 spec 里冻结的 effort 播种进轴——见 <see cref="Create"/> 的注释。</summary>
    private static IReadOnlyDictionary<string, string> WithEffortFrom(
        IReadOnlyDictionary<string, string> defaults, string effort)
    {
        var d = new Dictionary<string, string>(defaults, StringComparer.Ordinal);

        // ⚠️ 这个 `if` 不是多余的：老的 `spec.json` 里 effort 可能是**表里没有的值**
        // （`ClaudeRequestBuilder.ToEffort` 抛异常的那类拼写错误）。种子进轴会让头的下拉
        // **没有任何一个选项被选中**——那是比「悄悄用默认值」更坏的显示。表里没有就退回默认值。
        if (Lilara.Core.Session.Axes.ValuesOf(Lilara.Core.Session.Axes.Effort)!
                .Contains(effort, StringComparer.OrdinalIgnoreCase))
            d[Lilara.Core.Session.Axes.Effort] = effort;
        return d;
    }

    public static SessionHandle Load(SessionStore store, string projectKey, string sessionId)
    {
        var state = store.ReadState(projectKey, sessionId);
        return new SessionHandle
        {
            ProjectKey = projectKey,
            Spec = store.ReadSpec(projectKey, sessionId),
            State = state,
            Directory = store.SessionDirectory(projectKey, sessionId),

            // ⚠️ **老 state.json 没有 `axes` 键** ⇒ 读出来是 null ⇒ 在这里补默认值。
            // 不补的话，头会渲染五个空白下拉，用户分不清「没设过」与「头读不到」。
            // ⚠️ 按 `Lilara.Core.Session.Axes.Defaults()` 补齐而不是按硬编码名单——
            // 加一条新轴时，老会话也会自动拿到它的默认值，不需要第二处修改。
            Axes = state.Axes is { Count: > 0 }
                ? state.Axes
                : Lilara.Core.Session.Axes.Defaults(),
        };
    }

    public void SaveState(SessionStore store) => store.WriteState(ProjectKey, State);

    /// <summary>
    /// 把当前轴写进 `state.json`。
    /// ⚠️ **整文件重写**（`state.json` 本来就是每步原子重写，`01` §8）——
    /// 但要**基于 <see cref="State"/> 现读的副本**，不能另构造一个，否则会把 `Turn` / `Status` 抹掉。
    /// </summary>
    public void SaveAxes(SessionStore store)
    {
        State = State with { Axes = Axes };
        store.WriteState(ProjectKey, State);
    }
}

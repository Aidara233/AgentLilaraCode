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

        return new SessionHandle
        {
            ProjectKey = projectKey,
            Spec = spec,
            State = state,
            Directory = store.SessionDirectory(projectKey, spec.SessionId),
        };
    }

    public static SessionHandle Load(SessionStore store, string projectKey, string sessionId) => new()
    {
        ProjectKey = projectKey,
        Spec = store.ReadSpec(projectKey, sessionId),
        State = store.ReadState(projectKey, sessionId),
        Directory = store.SessionDirectory(projectKey, sessionId),
    };

    public void SaveState(SessionStore store) => store.WriteState(ProjectKey, State);
}

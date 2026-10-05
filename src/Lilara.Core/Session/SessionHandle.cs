using Lilara.Core.Kernel;
using Lilara.Core.Storage;

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
        var projectKey = Kernel.ProjectKey.FromPath(workingDirectory);
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

            // ⚠️ **轴的 effort 必须与 spec 一致，否则 `Stepper` 从轴读会与建会话时的意图分叉**
            // （`CompleteAxes` 的注释里有这条规则的完整形态）。
            // 今天的唯一建会话调用点写死 `effort="high"`（`CommandDispatcher.cs:114`；
            // `ProviderDefinition` **没有** effort 字段），所以这条路径暂时恒定——
            // **但规则必须在这里立住**，因为将来接上「按提供商配 effort」时，
            // 这里是唯一能防住分叉的地方。
            //
            // ⚠️ 播种进 `state` 而不是「先构造 handle、再补一次轴」：轴与 `Turn` / `Status`
            // 住**同一份** <see cref="SessionState"/>，多留一份副本就是多一个能分叉的地方。
            // 顺带的好处：下面那次 `WriteState` 把默认值一并落了盘——磁盘上「没设过」
            // 与「设成了默认值」从此可分辨，将来改默认值不会让老会话**静默改变行为**。
            Axes = CompleteAxes(stored: null, spec.Model.Effort),
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

    /// <summary>
    /// 一份**完整**的轴字典：先铺 <see cref="Axes.Defaults"/> 打底，再用 spec 里冻结的 effort
    /// 覆盖，最后让**磁盘上存过的那份**覆盖回来。
    ///
    /// <para>⚠️ **`Create` 与 `Load` 共用这一处，这是刻意的。** 两条来路各写一份规则，
    /// 就会出现「新建的会话按 spec 播种了 effort、重启载入的却退回默认值」这种分叉——
    /// 而它只有在重启之后才显形。**同一个值出现在多处时，它们是同一个契约的副本。**</para>
    ///
    /// <para>⚠️ **打底，而不是「整份为空才补」**：判据是**逐键**齐全，不是整份非空。
    /// 一份「有键但不全」的 `axes`（手改的、或将来某个只写一条轴的写入方）在旧判据下会漏补，
    /// 而 `Stepper` 读 `Axes.Effort` 用的是**索引器** ⇒ `KeyNotFoundException`。
    /// 加第 6 条轴时，**所有** 5 键的老会话都属于这一类。</para>
    ///
    /// <para>⚠️ **覆盖顺序（默认 → spec → 磁盘）是有意的**：磁盘上那份排最后，
    /// 所以用户 `setAxis` 改过的值不会被 spec 或默认值盖掉。</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> CompleteAxes(
        IReadOnlyDictionary<string, string>? stored, string specEffort)
    {
        var d = new Dictionary<string, string>(Axes.Defaults(), StringComparer.Ordinal);

        // ⚠️ **用 `Axes.TryParse`，不要用 `ValuesOf(...).Contains(..., OrdinalIgnoreCase)`**：
        // 后者放行 `"HIGH"` 却**原样存进去**，于是 ① `ClaudeRequestBuilder.ToEffort` 的 switch
        // **区分大小写** ⇒ 请求时抛异常；② 头的下拉按表里的小写拼法匹配 ⇒ `"HIGH"` 一个都选不中。
        // `TryParse` 顺手把大小写规范化掉——它存在就是为了这件事。
        //
        // ⚠️ 解析失败**不是**多余的守卫：老的 `spec.json` 里 effort 可能是表里没有的值
        // （`ToEffort` 抛异常的那类拼写错误）。种子进轴会让头的下拉**没有任何一个选项被选中**
        // ——那是比「悄悄用默认值」更坏的显示。表里没有就保留默认值。
        if (Axes.TryParse(Axes.Effort, specEffort, out var canonical))
            d[Axes.Effort] = canonical;

        if (stored is not null)
            foreach (var (k, v) in stored) d[k] = v;

        return d;
    }

    public static SessionHandle Load(SessionStore store, string projectKey, string sessionId)
    {
        var state = store.ReadState(projectKey, sessionId);

        // ⚠️ **spec 只读一次**，handle 与 `CompleteAxes` 共用它。
        // 读两次不只是多一次 IO：两次之间磁盘可能变（这里没有事务），
        // 于是「用来播种的 effort」与「handle 上那份 spec」可以来自**不同的两代文件**。
        var spec = store.ReadSpec(projectKey, sessionId);

        // ⚠️ **老 state.json 没有 `axes` 键** ⇒ 读出来是 null ⇒ 在这里补成完整的一份，
        // 而且**补进 `State` 本身**：轴只有这一份存储，`SessionHandle` 上不该有第二份
        // （两份就会分叉，而分叉的表现是「改了轴但别处读到的还是老值」）。
        // 不补的话，头会渲染五个空白下拉，用户分不清「没设过」与「头读不到」。
        // ⚠️ 补齐按 `Axes.Defaults()` 而不是按硬编码名单——加一条新轴时，
        // 老会话也会自动拿到它的默认值，不需要第二处修改。
        // ⚠️ 走 `CompleteAxes` 而不是「null 才补」：一条**有键但不全**的 axes 也要补全，
        // 否则 `Stepper` 的索引器会抛 `KeyNotFoundException`。
        // ⚠️ **只补内存、不回写磁盘**：`Load` 是读路径，写盘会让「打开一个老会话」
        // 产生一次它没要求的写。下一次落盘（`Stepper` **成功的那一步**会写 `state.json`；
        // 抛异常与取消的路径不写）自然带上它。
        state = state with { Axes = CompleteAxes(state.Axes, spec.Model.Effort) };

        return new SessionHandle
        {
            ProjectKey = projectKey,
            Spec = spec,
            State = state,
            Directory = store.SessionDirectory(projectKey, sessionId),
        };
    }

    /// <summary>
    /// ⚠️ **对 <see cref="State"/> 的「读 → 造副本 → 写回」必须走这一把锁。**
    ///
    /// <para>为什么：`State` 是**可变字段**（宿主的激活表里存的是这个 handle 的**引用**，
    /// 所以外形不能换成 `handle = handle with { … }`，见 `CommandDispatcher.HandleSessionSetAxis`
    /// 的注释）。而 `session.setAxis` 与 `Stepper` 各做一次读-改-写、**互不共享锁**——
    /// `setAxis` 刻意不拿会话闸（`01` §3.5 要求轴即时生效），而 `Stepper` 拿的那把闸
    /// **挡不住 `setAxis`**。两条 `handle.State = handle.State with { … }` 交错时，
    /// **后写的会把先写的整份盖掉**（`SessionState` 是 record，`with` 是从**读到的**旧值造副本）：
    /// 丢 `Turn`，或把状态**永久卡在 `Running`**（终审 I2）。</para>
    ///
    /// <para>把这三步收进一把私有锁里，交错在结构上就不存在了。</para>
    /// </summary>
    private readonly object _stateGate = new();

    /// <summary>
    /// **原子换 `State`**：读、改、写全在 <see cref="_stateGate"/> 里。
    /// 返回换过之后的那份（就是字段里那份——别在调用点再读一遍，那可能已经是**下一次**改动后的值）。
    ///
    /// <para>⚠️ **任何打开 `State` 的读-改-写都必须走这里或 <see cref="UpdateStateAndPersist"/>**，
    /// 不能写成 `handle.State = handle.State with { … }`——那一句的读与写之间没有临界区。</para>
    /// </summary>
    public SessionState UpdateState(Func<SessionState, SessionState> change)
    {
        lock (_stateGate) { State = change(State); return State; }
    }

    /// <summary>
    /// 原子换 `State` **并落盘**——两步在**同一个临界区**里。
    /// ⚠️ 少了这一步，「内存对了、盘上还是旧的」这条分叉就会落在两次写之间（终审 I2 的约束 1 / 2）。
    /// </summary>
    /// <param name="persistedStatus">
    /// 落盘时把 `Status` 钉回的值（见下面 <see cref="SaveAxes"/> 的注释：`setAxis` 不拿闸，
    /// 可能在**整个模型调用**的窗口里发生，那时内存里是 `Running`，而磁盘**从不存 Running**）。
    /// 传 `null` = 原样落盘（`Stepper` 收尾时内存里本来就是 `Idle`）。
    /// </param>
    public SessionState UpdateStateAndPersist(
        SessionStore store, Func<SessionState, SessionState> change, string? persistedStatus = null)
    {
        lock (_stateGate)
        {
            State = change(State);
            store.WriteState(ProjectKey, persistedStatus is null ? State : State with { Status = persistedStatus });
            return State;
        }
    }

    /// <summary>
    /// 把**当前** `State` 落盘。
    /// ⚠️ 它只做「写」，不做读-改-写 ⇒ **要改 `State` 请走 <see cref="UpdateStateAndPersist"/>**，
    /// 别写成 `handle.State = …; handle.SaveState(store);`（那两句之间没有临界区）。
    /// </summary>
    public void SaveState(SessionStore store)
    {
        lock (_stateGate) store.WriteState(ProjectKey, State);
    }

    /// <summary>
    /// 把轴写进 `state.json`（`session.setAxis` 的写回点，Task 3）。
    ///
    /// <para>⚠️ **轴与 `Turn` / `Status` 住同一份 `State`**（<see cref="SessionState.Axes"/>），
    /// 所以这里写的是**整份** `State`——「改了轴却把 `Turn` 抹掉」这种分叉在结构上不可能发生。
    /// 保留这个名字只是让调用点自己说清楚「我改的是轴」。</para>
    ///
    /// <para>⚠️ **但正因为写的是整份 `State`，`Status` 得先钉回磁盘语义。**
    /// `session.setAxis` **不拿会话闸**，所以它能在**整个模型调用**的窗口里发生——
    /// 而那时内存里的 `Status` 已经被 `Stepper` 置成了 `Running`（`Stepper.cs:51`，
    /// 刻意**只写内存、不落盘**：这是被 `WireEvent` / `CommandDispatcher` / `CoreHost` /
    /// `SessionQueries` 四处断言的「磁盘从不存 Running」）。不钉的话，一次 `setAxis`
    /// 就把 `Running` 带上了磁盘 ⇒ 进程在步中被 kill 后 `Load` 读到它 ⇒ 快照如实回
    /// `Running` ⇒ **会话永远显示在跑，而没有任何步在跑**，也永远等不到 `step.end`。</para>
    ///
    /// <para>⚠️ **钉成 `Idle` 的依据**：运行时给 `Status` 赋过的值只有两个——
    /// `Idle`（`Create` 的播种、`Stepper` 的收尾，都上盘）与 `Running`（只上内存）。
    /// ⇒ 今天 `Running` 是**唯一**的内存专有取值，钉成 `Idle` 是对的。
    /// ⚠️ 将来若别的内存专有状态真的接上（`WaitingForInput` 等），这里要跟着改——
    /// 判据是「磁盘上合法的是哪些取值」，不是「钉成 Idle」这个动作本身。</para>
    ///
    /// <para>⚠️ **改轴请走 <see cref="UpdateStateAndPersist"/>**——它把「改内存 + 落盘」放在
    /// **同一个临界区**里，并顺带做这里的「钉回 `Idle`」。写成
    /// `handle.State = handle.State with { Axes = … }` 再调这里，那两句之间没有临界区，
    /// 正是终审 I2 点名的交错窗口（`Stepper` 的收尾读-改-写会在这里被整份盖掉）。</para>
    /// </summary>
    public void SaveAxes(SessionStore store)
    {
        lock (_stateGate) store.WriteState(ProjectKey, State with { Status = "Idle" });
    }
}

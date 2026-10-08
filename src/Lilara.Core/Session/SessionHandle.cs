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

    /// <summary>
    /// 会话的模型选择（绑哪个供应商、哪个模型、协议快照、Token 预算）。
    ///
    /// <para>⚠️ **可变**——`session.configure` 的落点。但**只能走 <see cref="UpdateSpecAndPersist"/>**，
    /// 与 <see cref="State"/> 是同一条纪律，理由也是同一个（见 <c>_gate</c>）。</para>
    ///
    /// <para>⚠️ 它**曾经是 `init`-only**，旁边那句注释写着「创建时写一次，此后不可变」。
    /// 那个形状逼出了一个**坏状态**：`provider.delete` 是**刻意不阻止**的
    /// （理由见 `CommandDispatcher.HandleProviderDelete`：删完让后果可见），
    /// 于是「会话绑的供应商没了」是一条**设计好的落点**——而那时**没有任何办法改绑**，
    /// 只能手改 `spec.json`。改绑就是那条落点缺的另一半。</para>
    /// </summary>
    public required SessionSpec Spec { get; set; }

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
    /// ⚠️ 改它的类型是一次**磁盘格式迁移**——而这件事**现在有路可走了**：格式带版本号
    /// （`Kernel.Json.FormatVersion` + `Storage.FileFormat`），旧文件读得出来、缺的字段补默认值。
    /// **所以不做它的理由不再是「格式冻结」，而是「还没到做待办持久化的时候」**
    /// （`Pending` 恒为空列表、不是「有而没写」；今天丢了只会丢未开跑的待办）。
    /// ⚠️ 那句话曾经写的是「磁盘格式已冻结」——本批之后它成了假话，别照抄。
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

    /// <summary>
    /// **本条消息链上已经跑了几步**——`01` §6.2 的「最大连续步数熔断」要它。
    ///
    /// <para>⚠️ <b>它是内存账，不落盘</b>：熔断防的是**这一次**失控，而重启之后
    /// 「链」已经断了（没有任何东西还在续跑）。落盘反而会带来一个新问题——
    /// 崩在链中间留下的计数会让重启后的第一次 `send` **继承**一个陈旧的高度。</para>
    ///
    /// <para>⚠️ <b>必须在用户消息到达时归零</b>（<see cref="ResetToolSteps"/>）：
    /// 不归零的话，一个会话用久了之后，「用户按了回车」与「模型自己续跑」就再也分不开，
    /// 熔断会在用户**正常提问**时触发。⇒ 计数的语义是「**自上次用户输入以来**」。</para>
    /// </summary>
    private int _toolSteps;

    public int ConsecutiveToolSteps => Volatile.Read(ref _toolSteps);

    public void BumpToolStep() => Interlocked.Increment(ref _toolSteps);

    public void ResetToolSteps() => Volatile.Write(ref _toolSteps, 0);

    /// <summary>
    /// **挂起的那次询问**（`01` §3.8）。非 null ⟺ <see cref="SessionState.Ask"/> 非 null。
    /// ⚠️ 名字与那个**盘上**的类型（<see cref="Kernel.PausedAsk"/>）分开，是刻意的：
    /// 它们是这件事的两半，一半在盘上、一半在内存（见下面那个嵌套类型的注释）。
    ///
    /// <para>⚠️ <b>挂起状态有<b>两份</b>，而这是刻意的、也是唯一安全的形状</b>：
    /// 一组是**盘面事实**（等哪一次调用、等多久——<see cref="Kernel.PausedAsk"/> 落进
    /// `state.json`），另一组是**内存事实**（那批问题原文——只有它才知道卡片上画什么）。
    /// ⚠️ <b>问题原文<b>不</b>落盘</b>，因为提问是一次**渲染产物**（同 `01` §3.7 的框架），
    /// 而它由工具的**纯函数**重新算得出来（`00` §5 铁律 1）。
    /// ⇒ 重启之后盘面还在、问题没了：状态**诚实**，问题**不诚实**——
    /// 恢复时用一句「会话在等待时重启过，原来的问题没能保留」顶上，用户答一次即可继续。</para>
    ///
    /// <para>⚠️ <b>于是「重新问一次工具」是恢复路径的一部分</b>，而它要求工具是纯的
    /// （同一个调用 + 同一个上下文 ⇒ 同一批问题）。不纯的工具会得到一张**对不上号**的卡，
    /// 而那个失败**不静默**：答案按 id 归位，对不上就是一句「这一项没有被回答」。
    /// </para>
    /// </summary>
    public PendingAsk? AwaitingInput { get; set; }

    /// <summary>
    /// 一次挂起询问的**内存那一半**（`01` §3.8）。见 <see cref="PendingAsk"/> 的注释。
    /// </summary>
    /// <param name="Ask">那批问题（用户答完要靠它把答案归位）。</param>
    /// <param name="Calls">
    /// **被挂住的那些调用**——⚠️ 它们还没配对，答完要按 <c>CallId</c> 给每条补一个结果。
    /// 一条询问可以盖住**多个**调用（同一批 `tool_use` 里两条都要问）。
    /// ⇒ 它是**列表**，不是一个（当年按「一问一调用」写会在这里炸）。
    /// </param>
    public sealed record PendingAsk(AskRequest Ask, IReadOnlyList<Block.ToolUseBlock> Calls);

    /// <summary>
    /// **这一份会话里被回收的结果**（记录 id 的集合）。⚠️ <b>内存账，不落盘</b>
    /// （`01` §8.0.2 八：「持久化永远存原文；注入与否只取决于 GC 状态，而 GC 状态不落盘」）。
    ///
    /// <para>不落盘不是省事，是 `00` §7 判据 8（能派生的就不要持久化）已经推出来的结果：
    /// 边界 + 策略 ⇒ 结论，**没有第二个真相来源**。顺带的好处是<b>回收策略改了不破磁盘格式</b>。</para>
    ///
    /// <para>⚠️ <b>它只在被触发时才算</b>（今天的触发源是 `session.gc` 命令）。⇒ 两条后果要认：</para>
    /// <list type="number">
    ///   <item><b>新出现的工具结果不在集合里</b> ⇒ 不被回收。这是对的——它们本来就在保护带内。</item>
    ///   <item><b>`deactivate` 之后再 `activate`，这个集合就没了</b>（handle 被丢弃、重新 `Load`）
    ///   ⇒ 要重新触发一次。这正是「重启即重新决定」的字面形态，**符合设计**。</item>
    /// </list>
    /// </summary>
    private IReadOnlySet<string> _reclaimed = NoReclaimed;

    // ⚠️ 共享的空集要**保证没人改它**：写进去的集合一律由 `Reclaim.Decide` 新造，
    // 这里只做「换一个引用」，不做增删。
    private static readonly IReadOnlySet<string> NoReclaimed = new HashSet<string>();

    public IReadOnlySet<string> Reclaimed => Volatile.Read(ref _reclaimed);

    /// <summary>换掉整份回收集合。⚠️ 调用方（`session.gc`）必须先算完再进来——这里不判、不算。</summary>
    public void SetReclaimed(IReadOnlySet<string> ids) => Volatile.Write(ref _reclaimed, ids);

    /// <param name="sessionId">
    /// ⚠️ <b>可空，而且正常路径**必须**不传</b>：给它的唯一用途是**可测性**
    /// ——一个会问用户的工具需要观察「等回答」那条路，而会话 id 随机生成时
    /// 断言写不出来。生产调用点一个都不该传（那等于让一个外部值决定盘上的身份）。
    /// </param>
    public static SessionHandle Create(
        SessionStore store, string workingDirectory, ModelSelection model, string? title = null,
        string? systemPromptPreset = null, string? sessionId = null)
    {
        // ⚠️ **一次取绝对路径，三处共用同一个值**：算 key、写 spec、定目录。
        // 改之前是**两次独立的** `GetFullPath`（`ProjectKey.FromPath` 内部一次、
        // `spec.WorkingDirectory` 一次）——两次恰好同意，所以没出过事；但键的判据
        // （`TrimEnd(分隔符)` + 小写）与存下来的路径本来就**不是**同一个规范化，
        // 而 `session.create` 现在要接受**任意** cwd ⇒ 把这条隐含依赖收成显式的一行。
        var fullPath = Path.GetFullPath(workingDirectory);
        var projectKey = Kernel.ProjectKey.FromPath(fullPath);
        var spec = new SessionSpec
        {
            // ⚠️ 走 `Ids.New()` 而不是就地 `Guid.NewGuid()...`：会话 id 与记录 id 是**同一个形状**，
            // 两处各写一遍拼法就会漂（宽度 / 大小写 / 截断位置）。
            SessionId = sessionId ?? Ids.New(),
            ProjectKey = projectKey,
            WorkingDirectory = fullPath,
            CreatedAt = CoreClock.Now(),
            Model = model,
            // 没给就是 null ⇒ `WhenWritingNull` 整个键不出现。可空是**语义**（「没起名」），
            // 不再是「为了让老文件逐字节不变」——那件事现在由**格式版本**承担。
            Title = title,
            // 同上：`null` = 没指定 ⇒ 渲染时跟随全局默认（`CorePrompts.DefaultPreset`）。
            // ⚠️ **这里不校验那个预设文件存不存在**（理由见 `SessionCreate.Preset` 的注释）。
            SystemPromptPreset = systemPromptPreset,
        };
        var state = new SessionState
        {
            SessionId = spec.SessionId,
            Status = SessionStatus.Idle,
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
    /// ⚠️ **对 handle 上那两份可变数据（<see cref="State"/> 与 <see cref="Spec"/>）的
    /// 「读 → 造副本 → 写回」都必须走这一把锁。**
    ///
    /// <para>为什么：它们是**可变字段**（宿主的激活表里存的是这个 handle 的**引用**，
    /// 所以外形不能换成 `handle = handle with { … }`，见 `CommandDispatcher.HandleSessionSetAxis`
    /// 的注释）。而 `session.setAxis` 与 `Stepper` 各做一次读-改-写、**互不共享锁**——
    /// `setAxis` 刻意不拿会话闸（`01` §3.5 要求轴即时生效），而 `Stepper` 拿的那把闸
    /// **挡不住 `setAxis`**。两条 `handle.State = handle.State with { … }` 交错时，
    /// **后写的会把先写的整份盖掉**（`SessionState` 是 record，`with` 是从**读到的**旧值造副本）：
    /// 丢 `Turn`，或把状态**永久卡在 `Running`**（终审 I2）。</para>
    ///
    /// <para>把这三步收进一把私有锁里，交错在结构上就不存在了。</para>
    ///
    /// <para>⚠️ **它曾经叫 `_stateGate`**，因为那时它只管 <see cref="State"/>。
    /// `session.configure` 让 <see cref="Spec"/> 成了**第二个**可变字段，
    /// 于是同一个问题有了第二个实例——**沿用这一把锁，不另立一把**：
    /// 两条独立的锁会让「改 spec」与「改 state」又能交错，而那正是本锁存在的理由。</para>
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// **原子换 `State`**：读、改、写全在 <see cref="_gate"/> 里。
    /// 返回换过之后的那份（就是字段里那份——别在调用点再读一遍，那可能已经是**下一次**改动后的值）。
    ///
    /// <para>⚠️ **任何打开 `State` 的读-改-写都必须走这里或 <see cref="UpdateStateAndPersist"/>**，
    /// 不能写成 `handle.State = handle.State with { … }`——那一句的读与写之间没有临界区。</para>
    /// </summary>
    public SessionState UpdateState(Func<SessionState, SessionState> change)
    {
        lock (_gate) { State = change(State); return State; }
    }

    /// <summary>
    /// 原子换 `State` **并落盘**——两步在**同一个临界区**里。
    /// ⚠️ 少了这一步，「内存对了、盘上还是旧的」这条分叉就会落在两次写之间（终审 I2 的约束 1 / 2）。
    ///
    /// <para>⚠️ **它是「改内存 + 落盘」的唯一入口。** 曾经还有 <c>SaveState(SessionStore)</c>
    /// 与 <c>SaveAxes(SessionStore)</c> 两个「只写」的方法——**两个在 <c>src/</c> 里零调用点**
    /// （只有测试在调），生产路径早就全走这一条了。它们已经删掉，理由是它们**各自重抄了一遍
    /// 「钉回磁盘语义」这条规则**：两份能分叉的判据，而其中一份根本不在跑的路径上。</para>
    /// </summary>
    /// <param name="diskStatus">
    /// **这一次该往磁盘上写哪个 <c>Status</c>。必填——没有默认值。**
    ///
    /// <para>⚠️ **它必须与 <paramref name="change"/> 分开看**，因为两者**刻意可以不同**：
    /// 内存里留 `Running`（这一步还在跑），磁盘上钉 `Idle`（磁盘从不存 `Running`）。
    /// 参数名叫 `diskStatus` 而不是 `persistedStatus`，就是为了让调用点这句话读得出来。</para>
    ///
    /// <para>⚠️ **为什么是必填、为什么没有「原样落盘」那个默认**：
    /// 它曾经是 `string? persistedStatus = null`，而那个 `null` **同时表达了两件事**——
    /// 「调用方没给」与「本来就是 `Idle`」，两者在这个签名下**无法区分**。
    /// 那正是本仓反复吃亏的形状（一个值承担两种含义 ⇒ 判据必漂）。
    /// 现在调用方必须**写出来**：`diskStatus: SessionStatus.Idle` 或
    /// `diskStatus: handle.State.Status`——后者就是「原样落盘」，但它是一句**有人签过字**的话。</para>
    ///
    /// <para>⚠️ **判据是「磁盘上合法的是哪些取值」，不是「钉成 Idle」这个动作本身。**
    /// 今天磁盘上合法的只有 <see cref="SessionStatus.Idle"/>；将来别的内存专有状态接上时，
    /// **改的是调用点传进来的那个值**——而调用点只有一个
    /// （`CommandDispatcher.HandleSessionSetAxis`），这是删掉 `SaveAxes` / `SaveState`
    /// 那两个死方法换来的东西。</para>
    /// </param>
    public SessionState UpdateStateAndPersist(
        SessionStore store, Func<SessionState, SessionState> change, string diskStatus)
    {
        lock (_gate)
        {
            State = change(State);
            store.WriteState(ProjectKey, State with { Status = diskStatus });
            return State;
        }
    }

    /// <summary>
    /// 原子换 <see cref="Spec"/> **并落盘**——「`session.configure` 的写回点」。
    /// 与 <see cref="UpdateStateAndPersist"/> **同形、同锁**，这不是巧合：
    /// `Spec` 与 `State` 是 handle 上仅有的两份可变数据，同一个交错问题、同一把锁。
    ///
    /// <para>⚠️ **`change` 是值级的 `Func&lt;SessionSpec, SessionSpec&gt;`，不是「算好整份再交进来」。**
    /// 两者曾经都在用，而区别不是风格：**只要 `change` 里要读旧的 <see cref="Spec"/> 的任何一个字段**，
    /// 引用级那条就得在**调用方**先读一次 `handle.Spec` —— 那一次**在锁外**，
    /// 于是「读到的旧值」与「被换掉的那份」可以来自不同的两代（正是 <see cref="_gate"/> 要关掉的窗口）。
    /// `session.configure` 一开始不需要读旧值（它把 `before` 整个捕获了），所以那个洞没露出来；
    /// 加 `Title` 之后它要在锁内读旧标题 ⇒ 必须走这一条。</para>
    ///
    /// <para>⚠️ **落盘走 `SessionStore.WriteSpec`，它是原子的**（`AtomicFile`：写 `.tmp` 再 `Move`），
    /// 所以崩溃不会留下半截 `spec.json`。这条与 <see cref="Create"/> 里「先 state 后 spec」
    /// 的顺序理由无关——那个顺序防的是**两条写之间**崩溃；这里只写一条。</para>
    ///
    /// <para>⚠️ **没有 `persistedStatus` 那种参数**：`Spec` 里没有 `Status` 这种
    /// 「内存专有取值」（`Running` 只写内存、从不落盘）。所以不需要钉回。</para>
    ///
    /// <para>⚠️ 返回换过之后的那份（就是字段里那份）——**别在调用点再读一遍 `handle.Spec`**，
    /// 那可能已经是**下一次**改动后的值了。与 <see cref="UpdateState"/> 的约定一致。</para>
    /// </summary>
    public SessionSpec UpdateSpecAndPersist(SessionStore store, Func<SessionSpec, SessionSpec> change)
    {
        lock (_gate)
        {
            Spec = change(Spec);
            store.WriteSpec(ProjectKey, Spec);
            return Spec;
        }
    }
}

using Lilara.Core.Protocol;
using Lilara.Core.Storage;
using Lilara.Core.Kernel;
using Lilara.Core.Network;
using Lilara.Core.Shell;
using Lilara.Core.Session.Tools;

namespace Lilara.Core.Session;

/// <summary>
/// 推进器：跑一步 = 渲染 → 调用 → **执行** → 落盘（02 §2）。
///
/// <para>⚠️ <b>一步 = <b>一次</b>模型调用 + 其工具执行</b>（`01` §4 的单数形式）。
/// ⇒ 「模型又调了工具」不是在这里绕圈，而是**再一次入队 + 启动**（`01` §6.1 的队列机制）；
/// 循环因此住在驱动层（`CommandDispatcher.RunStepAsync`），不在这里。
/// 这条单射性质还是 <b>步原子性</b>的安全来源：至多一次模型调用 ⇒ 至多一组 `tool_use`。</para>
///
/// <para>⚠️ 步是持久化的原子单位（`01` §4）。这里的次序是刻意的：
///   1. 用户消息**先落盘**——它是事实，模型调用失败也抹不掉
///   2. 模型消息与它那批工具结果**一次落盘**（`01` §3.6 ④）
///   3. state.json 最后写——崩在 2 和 3 之间只会让 turn 计数偏小，历史不损坏</para>
/// </summary>
public sealed class Stepper(
    SessionStore store, IModelAdapter adapter, IReadOnlyList<ITool>? tools = null,
    Lilara.Core.Plugins.PluginHost? plugins = null,
    UsageStore? usage = null,
    Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
    INetworkTransport? networkTransport = null,
    string? pendingCompactText = null)
{
    private readonly SessionStore _store = store;
    private readonly IModelAdapter _adapter = adapter;

    /// <summary>
    /// **用户下发过压缩指令时，尾块里那句「现在该做什么」**（2026-10-22）。
    /// ⚠️ 与 `GoalDirective` 同一条分工：**文本由解析器渲染好、推进器只转交**——
    /// 推进器不碰预设文件，也就能在「答复路径」与「步」两条路上给出同一句话。
    /// </summary>
    private readonly string? _pendingCompactText = pendingCompactText;

    /// <summary>
    /// 两次重试之间怎么等（`RetryPolicy` 的 <c>delay</c> 参数）。
    /// ⚠️ **只为可测性存在**：生产路径不传（= 真的等）；测试传一个立即返回的委托，
    /// 否则一条「十次退避」的用例要把测试套件拖慢四十多秒——而那种慢测试最后一定会被删掉或跳过。
    /// </summary>
    private readonly Func<TimeSpan, CancellationToken, Task>? _retryDelay = retryDelay;

    /// <summary>
    /// **这次调用失败了、马上要再试一次**（`RetryPolicy` 的 `onRetry`）：失败的序号、那句人话、等多久。
    ///
    /// <para>⚠️ <b>它只为「让客户端看得见」而存在</b>（用户裁定，2026-10-21：头那边不能只看到一直等待）。
    /// 与 <c>usage</c> 同一个理由**不由这里发事件**：`Stepper` 不碰事件总线——交回给调用方
    /// （`CommandDispatcher` 在会话流上发 `retry.attempt`）。
    /// ⚠️ 判据不在调用方：「要不要重试」只有 `RetryPolicy` 一处，这里只是原样转交。</para>
    ///
    /// <para>⚠️ <b>可赋值</b>（生产路径上由 `CommandDispatcher` 在解析出推进器之后赋值）：
    /// 那样「每个推进器都接上了」是一个**结构**，而不是每个构造点各自记得接的约定。</para>
    /// </summary>
    public Action<RetryNotice>? OnRetry { get; set; }

    /// <summary>
    /// 工具表。⚠️ **默认空** = 纯 chat（`04` §1.3 推论 1：「没有任何插件」是完全合法的形态）。
    /// ⚠️ 可空是给既有测试的：它们构造的是「没有工具」的推进器，而那条路上的行为
    /// **逐字节不变**（工具相位整段跳过）。
    /// </summary>
    private readonly IReadOnlyList<ITool> _tools = tools ?? [];
    private readonly Lilara.Core.Plugins.PluginHost _plugins = plugins ?? new();
    private readonly UsageStore? _usage = usage;

    /// <summary>
    /// 联网传输。⚠️ **可注入**（与 `retryDelay` 同一个理由）：SSRF 与超时的判据必须有测试，
    /// 而测试不能真的连公网。生产路径不传 = 真的走 HTTP。
    /// </summary>
    private readonly INetworkTransport _networkTransport = networkTransport ?? new HttpNetworkTransport();

    /// <summary>
    /// 构造时定下的适配器。⚠️ 这个只读访问器存在的唯一理由是**可测性**：
    /// `StepperResolver` 的契约就是「解析出来的是不是占位适配器」，
    /// 而适配器是私有字段时那条契约没法断言。它不是给运行时用的。
    /// ⚠️ 表达式别名，不是第二个字段——同一个事实只能有一份存储。
    /// </summary>
    public IModelAdapter Adapter => _adapter;

    /// <summary>
    /// 一步 = 渲染 → 调用 → 落盘（02 §2）。
    ///
    /// <para>⚠️ **它不再追加用户消息**（Task 4）。按 `01` §6.1 的次序，
    /// 「入队(x) → 若 x 是事件，**立即追加进记录**」——那次追加归 `CommandDispatcher`，
    /// 因为**入队与追加是同一个动作的两半**：一旦分开，
    /// 「已经落盘但还没入队」或「已入队但没落盘」这两种中间态就会各自丢消息。
    /// 这里的 <paramref name="blocks"/> 是**取空的整个队列**（可能来自多条消息）。</para>
    ///
    /// <para>⚠️ 次序仍是刻意的（`01` §4）：模型输出在内存合并完只落一次盘；
    /// `state.json` 最后写——崩在中间只让 turn 偏小，历史不损坏。</para>
    /// </summary>
    public async Task<StepOutcome> StepAsync(
        SessionHandle handle, IReadOnlyList<Kernel.Block> blocks, CancellationToken ct,
        bool explicitStart = false)
    {
        // 「now 冻结在步开始」（00 §2.5）——同一步内重复渲染必须逐字节相同
        var at = CoreClock.Now();

        // ⚠️ **收尾排在最前**（`01` §12.8）——它必须在**装配请求之前**，而装配请求的地方
        // 只有这一处。两件事都在里面：① 上一次那一步可能崩在两次写之间（`PendingIntent` 说
        // 「我可能欠一条 `tool_result`」）② 挂着的询问可能已经过期（`deadline` 过了，
        // 而**没有任何定时器在等它**）。
        // ⚠️ 排在这里的另一个理由：**用户答复会被下面那把闸挡在外面**
        // （`RunStepAsync` 拿的是会话闸）⇒ 从这一刻到 `TakeResume` 之间，
        // 没有第二条路径能碰 `Ask`，所以在这里合成答复是安全的。
        //
        // ⚠️ <b>`explicitStart` 也算「有人在使用它」</b>（`session.start`）：那条命令
        // **本身就是**「有人要它动」，与「用户发了话」在 `01` §5 那条判据下是同一件事
        // ⇒ 它**必须**能收掉一次撕裂的追加，否则那条命令在**唯一需要它的时候**是空的。
        Recovery.BeforeStep(_store, handle, at, hasNewInput: blocks.Count > 0 || explicitStart);
        var stepState = handle.State;
        var pluginAxes = _plugins.Freeze(PluginSessionView.Values(stepState), at, handle.Spec.Plugins ?? []);
        var pluginPrompt = _plugins.Collect(pluginAxes, handle.Spec.Plugins ?? []);

        // ⚠️ **三块槽位这一刻冻住**（2026-10-17 起抬到这里，早先内联在下面构造请求处）。
        // 理由不是整洁：`floating_context` 工具在执行相位要读**同一份**尾块
        // （`SessionToolContext.FloatingContext`）——若执行时另渲染一遍，
        // 「核心合成注入的那份」与「模型自己要来的那份」会在同一步里分叉。
        // ⚠️ 冻结的时点与原来逐字相同（步开始、`at` 已取）；跨步实时、步内逐字节稳定。
        // ⚠️ 冻结的时点与原来逐字相同（步开始、`at` 已取）；跨步实时、步内逐字节稳定。
        // ⚠️ **派生关系也在这一刻冻一次**（2026-10-24）：它进尾块（子会话那一行），
        // 而尾块必须步内逐字节稳定。⚠️ 它与下面执行相位交给工具的那份**是同一份**
        // ——两处各算一次会分叉（尾块说 A 在等回答，而工具眼里 A 已经空闲）。
        var subagents = SubagentScopeFor(handle);

        var slots = SystemBlock.Render(
            new PresetStore(_store.DataRoot, PresetStore.SystemDir),
            new PresetStore(_store.DataRoot, PresetStore.PremierDir),
            handle.Spec.SystemPromptPreset, handle.Spec.PremierPrompt,
            pluginAxes.Values, pluginPrompt,
            // ⚠️ 目标线进**尾块**（浮动上下文），不落盘：目标是状态，不是事件。
            GoalMode.IsActive(stepState) ? CorePrompts.GoalDirective : null,
            // 压缩指令同源：它也是状态（`state.json` 的 `pendingCompact`），不是事件。
            // ⚠️ 文本由解析器交进来（`StepperResolver`），推进器不碰预设——两条路给出同一句话。
            stepState.PendingCompact ? _pendingCompactText : null,
            // ⚠️ **这一行只在真有派生关系时出现**（`SubagentView.Line` 对空关系给 null）。
            SubagentView.Line(subagents));

        // ⚠️ **引擎租约**：所有 `StepAsync` 调用都在 **_stepper 的会话内串行** 之内
        // （`CommandDispatcher` 的会话闸），所以这里不需要再同步。
        // ⚠️ 先记下落盘的那份，失败了原样放回去。
        // 调用方 catch 之后继续用**同一个把手**：不回滚的话，
        // 用户刚看着一步失败，`status` 却报 Running——而磁盘上从来没写过 Running。
        // 「内存是落盘的镜像」这条不变量在这里也必须成立。
        var persisted = handle.State;
        // ⚠️ 走 `UpdateState`（原子读-改-写）：它与 `setAxis` 的读-改-写共用**同一把锁**，
        // 两条交错时不会互相整份盖掉（终审 I2）。下同。
        handle.UpdateState(s => s with { Status = SessionStatus.Running });

        var callId = Ids.New();
        var attempts = 0;
        var startedAt = CoreClock.Now();
        ModelReply reply;
        try
        {
            // 渲染：读回完整时间线 → canonical（核心永远发全量，00 §6）
            var request = new ModelRequest
            {
                ModelId = handle.Spec.Model.ModelId,
                MaxTokens = handle.Spec.Model.MaxTokens,
                // ⚠️ 用**适配器实际会说的**协议，不是 spec 里那份快照。
                // 适配器是按**当前** provider 定义选的（`StepperResolver`），而 spec 的协议是
                // 建会话时的快照——`provider set --protocol` 一改，两者就分叉。
                // 分叉的后果不是报错，是 `ReasoningPolicy` 按快照判断「能不能回放」，
                // 于是**在唯一能承载推理的那条链路上把推理静默丢掉**（复审实测的路径：
                // 会话建于 openai-chat，之后把 provider 改成 claude-messages）。
                // ⇒ 单一真相来源放在适配器上（`IModelAdapter.Protocol`）。
                Protocol = _adapter.Protocol,
                // ⚠️ **从轴读，不从 Spec 读。** Spec 是**建会话时冻结的快照**，
                // 而轴是**活**的（`session.setAxis` 改的就是它）。
                // 按 Spec 读的话，用户在头上把 effort 拨到 `max`、头显示 `max`，
                // 而**请求里发的还是建会话时那个值**——「设了没用」，且无从发现。
                // ⚠️ 五条轴里只有 effort 与 temperature 进请求（`01` §3.5：纯机械轴）。
                // ⚠️ **加一条有机械层的轴 = 加这里的一行 + 三个构造器各一行**——
                // 这条规则可 grep（搜 `NumberOrNull`）。别指望有别的机制替你接上。
                // ⚠️ `!` 是 **`Load` / `Create` 的契约**：它们保证 `State.Axes` 非空
                // （见 `SessionState.Axes` 的注释）；能拿到 null 的只有手工 `new SessionHandle`，
                // 那不是运行时路径。
                Effort = pluginAxes.Values[Axes.Effort],
                Temperature = Axes.NumberOrNull(Axes.Temperature, pluginAxes.Values[Axes.Temperature]),

                // ⚠️ **三块提示词在步开始读一次、冻进这一趟**（与上面那行 `now` 同一个道理）。
                // 两块文件是**每步都可能变的外部输入**（预设是用户随时能编辑的文件），
                // 而「渲染是纯函数」要求同一步内重复渲染**逐字节相同** ⇒ 随手现读会变成
                // 「半新半旧 ⇒ 每步破前缀」。⚠️ 所以这里**不是**每步缓存的偷懒，
                // 而是**刻意的每步一冻**：跨步实时（下一步就生效）、步内冻结。
                // 完整理由见 `SystemBlock.Render` 的注释。
                //
                // ⚠️ **它整块交给下面**（`SystemBlocks` / `PremierBlocks` / `TailBlocks` 各取一块），
                // 而不是在这里分三次调——「哪一块从哪来」的判据只有 `SystemBlock` 一处。
                // ⚠️ 轴值从 **handle 的 State** 读（不是从 spec）：轴是活的，
                // 而 `!` 是 `Load` / `Create` 的契约（见上面 `Effort` 那行）。
                //
                // ⚠️ **尾块落在哪由「工具表里有没有 `floating_context`」决定**（2026-10-17）：
                // 有 ⇒ 末尾合成一对调用 + 结果；没有 ⇒ **退回尾槽位**（不丢内容）。
                // ⚠️ **纯 chat / 无工具的会话也会走到**（`04` §1.3 推论 1：裸核心是合法的）——
                // 第一版把那种会话的尾块**整块弄丢**了（只发合成对、又没有工具可声明），
                // 三条插件集成测试当场变红。这是「下放」与「不丢」之间必须二选一时的正确取舍：
                // 拿不到声明位置就还放回原位，而不是让内容消失。
                Slots = HasFloatingContextTool ? slots with { Tail = [] } : slots,

                // ⚠️ 工具表**从工具表投影**，不是从 spec 读的——spec 里没有它，
                // 而「这一步有哪些工具」是**派生**的（04 §1.3：工具表不是独立实体）。
                // ⚠️ 它的顺序必须**确定**（前缀指纹按它算）；`_tools` 是构造时定下的一个列表，
                // 不经过任何容器枚举。
                Tools = [.. _tools.Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchemaJson))],

                // ⚠️ **去引用在这里做，不在适配器里**：记录里的快照可能是个
                // `StoreBlock`（大对象进了库，`01` §8.4）⇒ 必须换成真内容再交给适配器。
                // 三个适配器因此**都不需要认识库**——库是控制面概念（`01` §8.4：
                // 「模型压根不该意识到库存在」），让适配器去查库等于把它泄漏到协议层。
                //
                // ⚠️ **这条曾经漏掉，后果是一次单向门**：写入方（`ToolExecutor` 过阈值就进库）
                // 先上线了，而这里没解引用 ⇒ 适配器遇到 `StoreBlock` 直接抛
                // ⇒ **那个会话从此永久发不出消息**（记录只追加，改不掉）。
                // ⇒ 「只写不读」是这类改动最该先问的一句：**写下去的东西，谁读得回来？**
                //
                // ⚠️ 同一条线上现在还有两件事（都在 `TimelineRender` 里）：按记录 id **回收**（换渲染）、
                // 以及把 `fetch` 的 `Ref` 解开。三件事都得在这里做完——**适配器只看见普通块**。
                // ⚠️ 那份渲染**与头 / 控制台读的是同一个函数**（`SessionQueries.HistoryOf` 也走它）：
                // 各写一份就会出现「模型看到的」与「用户看到的」悄悄分叉。
                // ⚠️ 那份渲染**与头 / 控制台读的是同一个函数**（`SessionQueries.HistoryOf` 也走它）：
                // 各写一份就会出现「模型看到的」与「用户看到的」悄悄分叉。
                // ⚠️ **但喂模型的那一份多一条投递截断**（2026-10-17）：超过
                // `ToolLimits.MaxResultBytes` 的结果只喂开头，尾部的注记给出稳定 id 与下一个偏移
                // ⇒ 模型用 `fetch(id, offset)` 接着看，不必重跑那个工具。
                Timeline = TimelineRender.RenderForModel(_store, handle, StepFloatingPair(handle, slots, at)),
            };

            // 调用（抛异常时直接传播——这一步没完成，Turn 不动）。
            // ⚠️ **重试在这里，不在步上**（`RetryPolicy` 的类注释）：一次重试只重发这一个请求，
            // 不碰记录、不碰 `Turn`、不碰工具副作用。⚠️ **每一次失败各记一行账**——被限流后重试成功
            // 的那一步，上游实际被打了好几次，账本只记最后一次会让统计看起来比实际便宜。
            // （成功那一行由下面补记：只有这里知道「这个回复能不能用」。）
            var outcome = await RetryPolicy.CallAsync(_adapter, request, ct,
                delay: _retryDelay,
                onFailedAttempt: failed =>
                {
                    attempts = failed.Index;
                    RecordUsage(handle, callId, failed.StartedAt, reply: null, "failed",
                        attempt: failed.Index, endedAt: failed.EndedAt, error: failed.Failure.Message);
                },
                onRetry: NotifyRetry);
            reply = outcome.Reply;
            attempts = outcome.Attempts;
            RecordUsage(handle, callId, outcome.StartedAt, reply, "completed", attempt: attempts);
        }
        catch
        {
            // ⚠️ **只有「一次请求都没发出去」才在这里补一行**（装配请求那一半抛了）。
            // 模型调用本身的失败已经被上面的记账口逐次记过——在这里再记一次会把最后那次
            // 请求记两遍，而账本的总次数正是「上游被打了几次」这个问题的答案。
            if (attempts == 0) RecordUsage(handle, callId, startedAt, reply: null, "failed");
            // 回到落盘的那份。⚠️ 用捕获的引用回放，不去读磁盘：
            // 读盘本身可能失败，那会把原始的失败原因盖掉。
            //
            // ⚠️ **回滚保留轴**：轴是**控制面状态**，不是这一步的产物——
            // 回滚要撤销的是「这一步推进了什么」（`Turn` / `Status`），轴不在其中。
            // `session.setAxis` **不拿会话闸**（`HandleSessionSetAxis` 里没有 `GateFor`），
            // 所以它能在**整个模型调用**的窗口里发生——那正是这里会走到的失败 / 取消窗口。
            // （不拿闸是刻意的：`01` §3.5 要求权限这类轴**即时生效**——「调整权限 =
            // 改一个浮动字段，零前缀代价、即时生效」；排在闸后面等这一步跑完就不是即时了。）
            // ⇒ 不保留的话，用户改的轴会被一次失败 / 中断**静默吞掉**：头收到过
            // `axis.changed`、显示新值，而内存回到旧值、下一次请求带旧值发出去；
            // 再下一次成功的步把旧值写回磁盘 ⇒ 用户看到的那个新值**就此消失，且没有报错**。
            // ⚠️ `s.Axes` 是**锁内**读到的那份——用它，不重新读 `handle.State`：
            // 两条交错时「重新读」会拿到别人的写入，把回滚的基准弄歪（终审 I2）。
            handle.UpdateState(s => persisted with { Axes = s.Axes, PluginAxes = s.PluginAxes });
            throw;
        }

        // ---- 执行相位（`01` §4：一步 = 一次模型调用 **+ 其工具执行**）----
        //
        // ⚠️ 它排在**落盘之前**，是为了把「模型消息 + 它那批结果」凑成**一次** append
        // （`01` §3.6 ④ 的「步末按调用序一次落盘」）。分两次落就等于把步这个原子拆成两个崩溃窗口。
        var toolCalls = reply.Blocks.OfType<Kernel.Block.ToolUseBlock>().ToList();
        var events = new List<TimelineEvent>();

        // ⚠️ **步内摘掉的声明**（`release` 的解锁那一半，2026-10-17）：事件由调用方在落盘之后发，
        // 因为 `Stepper` 不碰事件总线（同 `StepOutcome` 的注释）。⚠️ 它必须与 `events` 同一处收集——
        // 否则「线落了、锁摘了、头不知道」，而头会继续显示模型已经说用完了的锁。
        var releasedClaims = new List<Claim>();
        // 同理：落下的线（头要据它的 id 画分隔线，而线本身被 `IsRenderable` 挡在模型之外）。
        var releasedLines = new List<TimelineEvent.ReleaseEvent>();

        // ⚠️ **本步执行出来的派生 / 注入请求**（2026-10-24）：与 `releasedClaims` 同一条分工
        // ——工具只声明，`CommandDispatcher` 落地（它才有 PluginHost / 事件总线 / Start）。
        IReadOnlyList<SpawnRequest> spawns = [];
        IReadOnlyList<TellRequest> tells = [];

        // ⚠️ **记录那一份**（未经回收、未经解引用）——`fetch` 取地址必须从它算，
        // 理由见 `SessionToolContext` 的构造参数注释。
        var recorded = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);

        // ⚠️ **真正执行过的调用**（`Batch.ExecutedCallIds`）要活过下面那个 `if`：
        // 压缩的判据与 `release` 的画线同源——名字在批里 ≠ 它跑过（闸门拒掉的也在这批里）。
        IReadOnlyList<string> executedCallIds = [];

        // 拒答不写空模型消息（Review Focus #2）
        if (!reply.Refused && reply.Blocks.Count > 0)
        {
            events.Add(new TimelineEvent.MessageEvent
            {
                Id = Ids.New(),
                At = at,
                From = Speaker.Model,
                Blocks = reply.Blocks,
            });
        }

        // ⚠️ **工具一次都还没跑，所以这一趟的落盘完全不同**（`01` §3.8）。
        // 有问 ⇒ 事件列表**空**（模型消息也不落——见下），状态转 `WaitingForInput`，
        // 那次暂停冻进 handle，等用户答完由 `CommandDispatcher` 续跑。
        AskRequest? pending = null;
        // 这一批工具结果——**给头的活事件**（`StepOutcome.ToolResults`）。有问时恒空。
        IReadOnlyList<TimelineEvent.ToolResultEvent> toolResults = [];

        // `finish_goal` 真的执行过 ⇒ 把主动性拨回 standard（见本方法末尾的状态写入）。
        var goalEnded = false;

        // ⚠️ 工具调用是**模型消息的一部分** ⇒ 有调用就一定有那条模型消息（上面那个 if 通过）。
        // `events.Count > 0` 不是多余的守卫，它挡的是「凭空落一堆悬空结果」——而悬空的
        // `tool_result` 与悬空的 `tool_use` 一样会让下一步的请求非法（`01` §3.6）。
        if (toolCalls.Count > 0 && events.Count > 0)
        {
            var blobs = new BlobStore(_store.DataRoot);
            // ⚠️ **权限档位在这里读一次，冻进这一趟**（与上面那个 `now` 同一个道理）：
            // 它是**浮动字段**（`01` §9.2：改它零前缀代价），而一步之内的判定必须一致——
            // 否则同一步里两条调用会按两个档位判，而记录里看不出为什么。
            var executor = new ToolExecutor(
                _tools, blobs, handle.State.Axes![Axes.Permission],
                new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId));
            var context = new SessionToolContext(handle.Spec.WorkingDirectory, blobs, recorded,
                // ⚠️ **与合成注入同一份字节**（`slots` 就是构造请求时用的那份尾块）：
                // `floating_context` 若在这里现渲染，同一步里「核心注入的」与「模型自己要来的」
                // 会分叉，而两边都看起来是对的（本仓栽过的形状）。
                FloatingTextOf(slots),
                // ⚠️ 联网配置与轴值**在执行这一刻**读（同权限档位）：它们是浮动字段，
                // 用户可以在模型调用期间改；按步开始的值判会与头显示的档位分叉。
                NetworkFor(handle),
                GoalMode.IsActive(handle.State),
                // ⚠️ 压缩指令是**持久状态**（`state.json`），执行这一刻现读：用户完全可能在
                // 模型思考期间刚下发它。⚠️ 但落检查点时**再用一次** `Parse`（它会重读上下文），
                // 所以「谁算数」只有 `CompactTool.Parse` 一个答案。
                handle.State.PendingCompact,
                // ⚠️ shell 配置与联网同形：执行这一刻现读（浮动字段），步内冻结。
                ShellFor(),
                // ⚠️ **用步开始冻住的那一份**（2026-10-24）：与尾块同一份字节，
                // 两处各算一次会让「模型从尾块看到的」与「工具判定的」分叉。
                subagents);

            var batch = await executor.ExecuteAsync(toolCalls, context, ct);
            pending = batch.Pending;
            events.AddRange(batch.Results);
            toolResults = batch.Results;
            executedCallIds = batch.ExecutedCallIds;
            spawns = batch.Spawns;
            tells = batch.Tells;
            goalEnded = GoalMode.IsActive(handle.State) && EndedGoal(batch, toolCalls);

            // ⚠️ **`release` 的线在这里落盘**（`01` §12.4，2026-10-11）：
            // 它和别的事件**不一样**——工具只跑出一次「成功」，而**记录上要留的那条不是结果**。
            // ⇒ 它的输出去处与时间点都在这一句上。
            //
            // ⚠️ **顺序无关紧要，因为它是判据的输入而不是判据**：真正读它的是
            // `Reclaim.Decide`，而那个判定只读**已经落盘**的线（`RecordRelease` 先落、
            // 下一步开始才渲染）。⇒ 把 `release` 调在最后一条也照样收得掉它前面的东西。
            // ⚠️ **配对不受影响**：它照样返回一条（空的）结果、照样配那条 `tool_use`——
            // 「一对不能拆」那条不变量一个字没动，所以这里不新增任何特例。
            //
            // ⚠️ **只给真正执行过的那些画线**（2026-10-17）：闸门硬拒（`off` 档）的调用
            // 也在这一批里、名字也叫 `release`，但它**一次都没跑**——为它画线等于让
            // 一次被拒绝的操作改变上下文。判据由执行器交回（`Batch.ExecutedCallIds`），
            // 因为「跑没跑」只有它知道。
            foreach (var executedCallId in batch.ExecutedCallIds)
                if (toolCalls.FirstOrDefault(c => string.Equals(c.CallId, executedCallId, StringComparison.Ordinal)) is { Name: ReleaseTool.NameOf } release
                    && RecordRelease(release, at) is TimelineEvent.ReleaseEvent line)
                {
                    events.Add(line);
                    // ⚠️ **模型侧 `release` 也摘掉自己的声明**（2026-10-17，§12.4 的「解锁」那一半）：
                    // 工具够不着 `ClaimStore`（`IToolContext` 只读），而这里够得着——
                    // 摘锁与画线是**同一件事的两半**（「这批资源我用完了」）。
                    // ⚠️ **只摘自己的**：替别人摘锁是 `claim.release` 的逃生口。
                    // ⚠️ 失败给空列表 ⇒ 最坏是「锁还在」，**线与回收照常**。
                    releasedClaims.AddRange(UnlockReleased(handle, line.Scope));
                    releasedLines.Add(line);
                }
        }

        // ⚠️ **`claim.released` 由调用方发**（`Stepper` 刻意不碰事件总线，同 `StepOutcome` 的注释）：
        // 头靠它刷新「谁在动哪些文件」；不发的话，头会继续显示模型已经说用完了的锁。
        // ⇒ 与 `StepOutcome` 一起交回，由 `CommandDispatcher` 在落盘之后发。

        // ⚠️ **有问 ⇒ 中止这一步的「执行」，但那条模型消息照样落盘**（`01` §3.8）。
        //
        // ⚠️ **这一格我第一版写反了**，记在这里：初稿写的是「这一步整个没发生」，
        // 理由是「落模型消息而不落结果会留下悬空的 `tool_use`」。
        // **恰好相反**——不落模型消息，才是**悬空**：
        // 用户答完之后我们补一条 `tool_result`，而它配对的 `tool_use`
        // **从来没有落过盘** ⇒ 那条结果**悬空** ⇒ 下一步的请求非法。**两个方向都试过了，只有这一个成立。**
        //
        // ⇒ 落到盘上的是：`[……, 用户消息, 模型消息(含 tool_use)]`——**配对缺一半，而那一半正在路上**。
        // ⚠️ 这个「缺一半」的窗口**不会漏给模型**：请求只在 `StepAsync` 里构造，
        // 而它只在**解除暂停之后**才会再跑一次（`CommandDispatcher` 的续跑）。
        // ⚠️ 而它**正是**重启后要靠 `state.json` 里那份 `PausedAsk` 去补的那一对
        // （见 `PausedAsk` 的注释）。
        if (pending is not null)
        {
            handle.AwaitingInput = new SessionHandle.PendingAsk(pending, toolCalls);
            WritePair(handle, events,
                new PendingIntent(handle.State.Turn + 1, toolCalls[0].CallId), _store, s => s with
            {
                Status = SessionStatus.WaitingForInput,
                Ask = new PausedAsk(
                    pending.Id, pending.Kind,
                    // ⚠️ 一条询问今天只可能属于**一个** Kind，而 `CallId` 那一格只装得下一个。
                    // 取第一条；真正的配对靠 `PendingAsk.Calls`（它是列表）。
                    toolCalls[0].CallId,
                    toolCalls[0].Name,
                    ApprovalStore.HashArgs(toolCalls[0].Arguments),
                    DeadlineOf(handle.Spec.AskTimeout)),
                Turn = s.Turn + 1,
            }, diskStatus: SessionStatus.WaitingForInput);

            return new StepOutcome(reply, pending, handle.State.Ask?.DeadlineAt,
                events.OfType<TimelineEvent.MessageEvent>().LastOrDefault()?.Id, releasedClaims, releasedLines);
        }

        // ---- 压缩（2026-10-22，`compact` 是一次普通工具调用）----
        //
        // ⚠️ **次序在这里是契约，不能随手调**（`docs/superpowers/plans/2026-10-22-compaction-as-tool.md`）：
        //   ① 记录（下面那句 `AppendEvents`）→ ② 检查点 → ③ 清指令 → ④ 回收窗口。
        //   检查点若先于记录写，崩在中间会得到一个「覆盖到 X、而 X 之后什么都没有」的窗口：
        //   新检查点的摘要只在**那条调用**的参数里，而那条调用在 X 之后 ⇒ 模型既看不到摘要、
        //   也看不到历史。反过来（记录先）最坏只是「模型以为自己压了、其实没压」——可见、可重试。
        //
        // ⚠️ **`CoveredThroughEventId` 是本步之前最后一条记录**，不是那条 compact 调用本身：
        //   后者会让「调用之前的那几步工具结果」被切掉，而用户要的「最新的一批消息」正是它们。
        //
        // ⚠️ **判据只有 `CompactTool.Parse` 一个**（它与工具回答那句话用的是同一个函数）：
        //   `context.CompactRequested` 是那一刻的快照，而写检查点是这一刻——两者不同时，
        //   以这一刻的上下文为准。
        var compactCall = toolCalls
            .LastOrDefault(c => string.Equals(c.Name, Tools.CompactTool.NameOf, StringComparison.Ordinal)
                                && executedCallIds.Contains(c.CallId));
        string? compactSummary = null;
        if (compactCall is not null && events.Count > 0)
        {
            var blobsForCompact = new BlobStore(_store.DataRoot);
            var contextForCompact = new SessionToolContext(
                handle.Spec.WorkingDirectory, blobsForCompact, recorded,
                FloatingTextOf(slots), NetworkFor(handle), GoalMode.IsActive(handle.State),
                handle.State.PendingCompact, ShellFor());
            compactSummary = Tools.CompactTool.Parse(
                new ToolCall(compactCall.CallId, compactCall.Name, compactCall.Arguments),
                contextForCompact, out _);
        }

        // 一次落盘 = 一个原子。⚠️ 空批次**不写**（拒答且没有工具的那一步就是空的），
        // 而 `AppendEvents` 对空批次是 no-op、不抛——见它的注释。
        // ⚠️ 浮动对**跟着这一步一起落盘**（2026-10-23）：排在模型消息与工具结果之后，
        // 配对完整（`tool_use` 紧跟 `tool_result`）。没变化时它是空的 ⇒ 记录里不新增，
        // 也就是「每步一份」改成「变了一份」。
        // ⚠️ **2026-10-23：浮动对落进 `floating.jsonl`**（不是 `history.jsonl`）——
        // 它是**环境状态**，混进对话记录会让所有「记录里有几条事件」的断言与统计重算，
        // 也会让 `fetch`/压缩把它当成可取回的对话内容（而旧读数的档位不能复活）。
        _store.AppendFloating(handle.ProjectKey, handle.SessionId, StepFloatingPair(handle, slots, at));
        _store.AppendEvents(handle.ProjectKey, handle.SessionId, events);

        // ② + ③ + ④：检查点 → 清指令 → 回收窗口（见上面那段次序）。
        CompactionCheckpoint? compaction = compactSummary is null
            ? null
            : CommitCompaction(handle, compactCall!.CallId, recorded);

        // ⚠️ Turn 递增**在拒答分支之外**：拒答是成功返回的、完成的一步，
        // 只是没有内容可写。让它和「抛异常」一样不计数，
        // 会让 state.json 里「拒答过一次」与「从没跑过」无法区分。
        // ⚠️ **Turn 自增 + 落盘走 `UpdateStateAndPersist`（同一个临界区）**，不能拆成
        // 「先换内存、再单独写盘」两句：`setAxis` 不拿会话闸，
        // 它的读-改-写能与这里交错，分开写会丢 `Turn` 或把状态卡在 `Running`（终审 I2）。
        // ⚠️ `diskStatus` 必须是**字面量 `Idle`**，不能写 `handle.State.Status`——
        // 实参是**调用前**求值的，而那一刻内存里还是上面刚置的 `Running` ⇒
        // 那会把 `Running` 写进磁盘，正是整套机制要防的那件事（`SessionStatus` 的类型注释里有完整后果）。
        // 一句话：**「原样落盘」不能用「读当前值」来表达**，因为调用点往往正好处在「内存已经领先」的时刻。
        // ⚠️ **`PendingIntent` 要在这里清掉**：这一步的两个产物都落成了（记录 + 它自己的结果）
        // ⇒ 那个「我可能欠一条」的痕迹**已经被兑现**。不清的后果是**下一步开始时会再补一条**
        // ——那会给模型一条重复的结果（多了一条假信息）。
        // ⚠️ **`Turn` 原样 `+1`**（那是既有的语义：这一步跑完了，所以 +1）。
        // ⚠️ **一次「只见消息、不见结果」的错位要在别处修**：崩在模型调用期间时，
        // 记录里会多出**一整步**（用户 + 模型两条消息），而没有任何 `tool_result` 供 `Recovery`
        // 拿它当锚 ⇒ `steps` 与 `Turn` 会**永远差一格**，而每发一条消息都会再触发一次收尾。
        // 判据在 `Recovery.BeforeStep` 里：那一格只在**末尾真的悬着一条工具调用**时才算数。
        handle.UpdateStateAndPersist(_store, s => s with
        {
            Status = SessionStatus.Idle,
            Turn = s.Turn + 1,
            PendingIntent = null,
            // ⚠️ **指令的清理在 `CommitCompaction` 里、排在检查点之后**（次序见那边）——
            // 这里不再碰它：先清指令后写检查点，崩在中间就是「指令丢了、历史没压」，
            // 而用户看到的「点过压缩」与盘面完全对不上。⚠️ 没有压缩时它原样保留。
            // ⚠️ **`finish_goal` 的效果就是这一行**：把主动性拨回 standard。
            // 拨回之后普通工具循环还会再走一步（它是一次工具调用）⇒ 模型有时间收尾总结。
            // 只在**真的执行过**且当时确实处于 goal 时才改（见 `goalEnded` 的算法）。
            Axes = goalEnded ? WithStandardInitiative(s.Axes) : s.Axes,
        }, diskStatus: SessionStatus.Idle);

        return new StepOutcome(reply, HistoryId: events.OfType<TimelineEvent.MessageEvent>().LastOrDefault()?.Id,
            ReleasedClaims: releasedClaims, ReleasedLines: releasedLines, ToolResults: toolResults,
            GoalEnded: goalEnded, Compacted: compaction, Spawns: spawns, Tells: tells);
    }

    /// <summary>
    /// **压缩的落地**：检查点 → 清指令 → 回收窗口（2026-10-22）。
    ///
    /// <para>⚠️ <b>调用它的前提</b>：本步的记录已经落盘（调用点保证），并且那条调用已经过
    /// <see cref="Tools.CompactTool.Parse"/>——所以这里不再判「算不算数」，只负责写。</para>
    ///
    /// <para>⚠️ <b>次序，以及为什么</b>：
    /// ① 检查点先写：它的 `CoveredThroughEventId` 是本步之前最后一条记录，而摘要只在
    ///    <paramref name="callId"/> 那条调用的参数里——那条调用已经落盘了（调用点先写记录）。
    ///    崩在这之后：历史已压、指令还在 ⇒ 下一步会**再压一次**（多一份摘要，可见、可恢复）。
    /// ② 清指令：它必须<b>晚于</b>检查点。反过来（先清）崩在中间就是「指令没了、历史没压」，
    ///    而那是**静默的**——用户点过压缩，盘上两处都看不出它发生过。
    /// ③ 回收窗口最后：它只影响回收判定，前两步才是语义。</para>
    ///
    /// <para>⚠️ <b>清指令走的是「读-改-写 + 落盘」同一个临界区</b>（`UpdateStateAndPersist`），
    /// 与 `Turn + 1` 那一句同锁——两处各写一次整份 state 会互相盖掉。</para>
    /// </summary>
    private CompactionCheckpoint? CommitCompaction(
        SessionHandle handle, string callId, IReadOnlyList<TimelineEvent> recorded)
    {
        // ⚠️ **没有可覆盖的记录 ⇒ 不落检查点**：边界得有一个锚（`CoveredThroughEventId`），
        // 而空记录里没有。命令那一侧已经拒掉空会话；这一条挡的是「不走命令、直接推一步」的调用
        // （测试、以及将来的嵌入方）——那里 `recorded[^1]` 会抛，而抛出来的是一条
        // 与本意无关的 `IndexOutOfRangeException`。
        if (recorded.Count == 0) return null;

        var checkpoint = new CompactionCheckpoint
        {
            CheckpointId = Ids.New(),
            // ⚠️ 本步之前最后一条（`recorded` 是本步开始时读到的那份）；
            // 步内没有记录时它就是**上一次压缩的 `compact` 调用**——那正是「保留最新的一批」的边界。
            CoveredThroughEventId = recorded[^1].Id,
            HistoryDigest = CompactionStore.ComputeDigest(recorded),
            CreatedAt = CoreClock.Now(),
            SourceCallId = callId,
        };

        new CompactionStore(_store).Append(handle.ProjectKey, handle.SessionId, checkpoint);

        handle.UpdateStateAndPersist(_store, s => s with { PendingCompact = false },
            diskStatus: SessionStatus.Idle);

        // ⚠️ **压缩就是一个回收窗口**（2026-10-17，`01` §11.7）：压缩**立即生效**
        // ⇒ 下一步的前缀反正要重建；既然要重建，就把距离与点杀一并解析一次、落成窗口记录。
        // ⚠️ 只解析**这次压缩之后仍然可见**的记录：被压掉的那段本来就由摘要代表，
        // 收它不再省任何字节（而 `Reclaim.Decide` 也只看到投影后的那段）。
        // ⚠️ 那份投影读的是**刚落盘的**历史（含本步），所以窗口覆盖到 `compact` 调用及其结果之后的一切。
        var visible = ContextCompaction.Project(_store, handle).Suffix;
        var blobs = new BlobStore(_store.DataRoot);
        var ids = Reclaim.Resolve(visible, blobs,
            new Tools.SessionToolContext(handle.Spec.WorkingDirectory, blobs, recorded));
        _store.AppendEvents(handle.ProjectKey, handle.SessionId,
            [Reclaim.Window("compaction", ids, null)]);

        return checkpoint;
    }

    /// <summary>
    /// 记一行账。⚠️ <b>一次真实请求一行</b>（`RetryPolicy` 的记账分工）：重试的每一步各调一次，
    /// 同一个 <paramref name="callId"/> 下按 <paramref name="attempt"/> 递增。
    ///
    /// <para>⚠️ <b>`try` 里的注释仍然成立</b>：账本不可写时不能把一次已经完成的模型调用重跑一遍。</para>
    /// </summary>
    private void RecordUsage(
        SessionHandle handle, string callId, DateTimeOffset startedAt,
        ModelReply? reply, string status, string purpose = "session",
        int attempt = 1, DateTimeOffset? endedAt = null, string? error = null)
    {
        if (_usage is null) return;

        // 统计属于观察面：账本不可写时不能把一次已经完成的模型调用重跑一遍。
        try
        {
            _usage.Append(new UsageCall
            {
                CallId = callId,
                ProjectKey = handle.ProjectKey,
                SessionId = handle.SessionId,
                Step = handle.State.Turn + 1,
                Purpose = purpose,
                ProviderId = handle.Spec.Model.ProviderId,
                ModelId = handle.Spec.Model.ModelId,
                Protocol = _adapter.Protocol,
                StartedAt = startedAt,
                EndedAt = endedAt ?? CoreClock.Now(),
                Status = status,
                Usage = reply?.Usage,
                ReportedModel = reply?.ReportedModel,
                ResponseId = reply?.ResponseId,
                // ⚠️ 只在「不止一次」时才写（`WhenWritingNull` 会让它整键消失）：
                // 一次就成的行**逐字节**与加重试之前相同。
                Attempt = attempt > 1 ? attempt : null,
                Error = error,
            });
        }
        catch
        {
            // 统计故障不改变会话语义；控制面可在后续版本暴露丢账告警。
        }
    }

    /// <summary>
    /// **先记录、后 state，而那条「我可能欠一条」的痕迹跟着走**（`01` §12.8）。
    ///
    /// <para>⚠️ <b>顺序不能反</b>：痕迹是「<b>那一步的 `Turn`</b> + <b>哪一条调用</b>」两件事，
    /// 而它们恰好是 `UpdateStateAndPersist` 里 <c>change</c> 的两个输入
    /// （<c>s.Turn + 1</c> 与那条调用）⇒ 在**写入点**算一遍，而不是在读取点猜。
    /// 反过来的后果是静默的：崩在两次写之间会留下一条没人认得出来的悬空调用
    /// （记录里缺配对、而 `state.json` 不知道有它）⇒ 那一步永远修不好。</para>
    ///
    /// <para>⚠️ <b>崩在「记录已落、state 未写」时痕迹<b>不在</b>盘上</b>——那一刻它还在内存里。
    /// ⇒ 重启之后那个会话照样修得回来，但靠的是<b>另</b>一条路：<c>Ask</c> 在 `state.json` 里
    /// （它上一次挂起时写下的那份）⇒ 心跳那一趟会发现「记录末回合悬着、而 state 说在等」，
    /// 于是按超时收掉。两条路各有各的适用面，<b>不是重复</b>。</para>
    /// </summary>
    private static void WritePair(
        SessionHandle handle, IReadOnlyList<TimelineEvent> events, PendingIntent intent,
        SessionStore store, Func<SessionState, SessionState> change, string diskStatus)
    {
        store.AppendEvents(handle.ProjectKey, handle.SessionId, events);
        handle.UpdateStateAndPersist(store, s => change(s) with { PendingIntent = intent }, diskStatus);
    }

    /// <summary>
    /// **把用户改写的参数换进要执行的那些调用**（`01` §3.8）。
    ///
    /// <para>⚠️ <b>为什么这件事必须在这里发生</b>：用户改的那份原文跟着 <c>tool_result</c>
    /// 走，而一条 `tool_result` 只对一条 `tool_use`（`01` §3.6 ①）⇒ 一批里第 2..N 条
    /// **拿不到属于它们的那份改写**。而重发那条路根本不传参数（只发「你重新发起」那句话）。
    /// ⇒ 把改写**换进调用本身**，执行那一层就不需要知道「用户改没改」。</para>
    ///
    /// <para>⚠️ <b>只换被盖住的那些</b>：没答到的调用保持原样，它们要重新挂起。
    /// ⚠️ <b>换的是 <see cref="Block.ToolUseBlock.Arguments"/> 那一格</b>——
    /// 它正是 `ToolExecutor` 交给工具的那份：逐字、不改写（`01` §9）。</para>
    /// </summary>
    /// <param name="calls">等待中的调用（原样那份）。</param>
    /// <param name="paused">盘上那份（要拿它的 `CallId` 与哈希来判「改了没有」）。</param>
    /// <param name="amended">用户改过的参数原文；没有就是 `null`。</param>
    public static IReadOnlyList<Block.ToolUseBlock> Amended(
        IReadOnlyList<Block.ToolUseBlock> calls, PausedAsk paused, string? amended)
    {
        if (amended is null) return calls;

        // ⚠️ **规范化之后比**（与 `ApprovalStore.HashArgs` 同一条判据）：
        // 用户只把键序倒了一下不算「改了参数」——JSON 对象无序。
        if (string.Equals(ApprovalStore.HashArgs(amended), paused.ArgsHash, StringComparison.Ordinal))
            return calls;

        return [.. calls.Select(c => string.Equals(c.CallId, paused.CallId, StringComparison.Ordinal)
            ? c with { Arguments = amended }
            : c)];
    }

    /// <summary>
    /// **从盘上把被挂起的那一批调用重新解出来**（`01` §3.8）。
    ///
    /// <para>⚠️ <b>为什么必须有它</b>：`SessionHandle.AwaitingInput` 是**内存那半**，
    /// 而**任何一次「从盘重新载入」都会把它丢掉**（不只是重启）。那时若退回
    /// 「凭空造一条调用」，参数就没了——而工具会拿到一个空参数对象。</para>
    ///
    /// <para>⚠️ <b>这个洞是 E2E 抓出来的</b>（2026-10-11，真模型 + 重启）：
    /// 那条 `update` **真的执行了**（直接执行那条路是通的），却报「缺少 `path` 参数」
    /// ——因为它收到的是我编的 <c>{}</c>。⚠️ 而**它与 `AskTool.ItemsOf` 是同一个形状**
    /// （「问题从盘上解不出」）：内存那半是**缓存**，盘上那条 `tool_use` 才是真相来源。
    /// **同一个形状在同一天栽了两次。**</para>
    ///
    /// <para>⚠️ 返回的是**那一条模型消息里的全部 `tool_use`**（不是只有 `callId` 那条）
    /// ——一张卡可能盖着好几条，而配对要按整批来。</para>
    /// </summary>
    public static IReadOnlyList<Block.ToolUseBlock> RecordedCalls(
        SessionStore store, string projectKey, string sessionId, string callId)
    {
        foreach (var e in store.ReadTimeline(projectKey, sessionId))
        {
            if (e is not TimelineEvent.MessageEvent m) continue;

            var uses = m.Blocks.OfType<Block.ToolUseBlock>().ToList();
            if (uses.Any(u => string.Equals(u.CallId, callId, StringComparison.Ordinal)))
                return uses;
        }

        return [];
    }

    /// <summary>
    /// 把一次 `release` 调用记成**线**（`01` §12.4）。
    ///
    /// <para>⚠️ <b>位置由调用方定，不由这里的 `events` 定</b>——所以它拿的是
    /// <paramref name="at"/>（步开始那一刻），与那条模型消息同一个时刻。
    /// 顺序对判定**无关紧要**（见调用点的注释），所以不为了把它塞进某个位置而排序。</para>
    ///
    /// <para>⚠️ <b>参数解析失败 ⇒ 不落这条线</b>（返回 <c>null</c>），而不是落一条空 scope 的：
    /// 一条 scope 为空的记录在判定那边**什么也收不到**（`Reclaim.LineCovers` 会直接返回），
    /// 所以它不是一个「更弱的线」，是一个**没有意义的记录**——而它会让
    /// 「记录里有多少条线」这个问题答出一个假数。
    /// ⚠️ <b>而且判据必须与工具那一侧是同一个</b>：`ReleaseTool` 对同一份坏参数回一句
    /// 「读不出来」并**什么也不做**——两处各判一次、判出两个结论，正是本仓那个老形状
    /// （两层判据必须共用一个谓词）。⇒ 这里也走 `ToolArgs.StringArray`，**同一个谓词**。</para>
    /// </summary>
    private static TimelineEvent? RecordRelease(Block.ToolUseBlock call, DateTimeOffset at)
    {
        var scope = Tools.ToolArgs.StringArray(call.Arguments, "scope");

        // 解析不出来 / 空数组 ⇒ 不画线。⚠️ **失败方向是「什么也不做」**——
        // 猜一个 scope 会让一次笔误变成一次**静默的大范围回收**。
        if (scope is null || scope.Count == 0) return null;

        return new TimelineEvent.ReleaseEvent
        {
            Id = Ids.New(),
            At = at,
            Scope = scope,
            Summary = Tools.ToolArgs.String(call.Arguments, "summary"),
        };
    }

    /// <summary>
    /// **模型侧 `release` 的解锁那一半**（`01` §12.4，2026-10-17）：摘掉**本会话自己**
    /// 落在释放作用域里的声明。
    ///
    /// <para>⚠️ <b>为什么住在这里而不是工具里</b>：`IToolContext` 是只读的
    /// （`04` §5.1），工具够不着 `ClaimStore`；而推进器本来就拿着 `SessionStore`
    /// 与工作目录。⚠️ 但**摘锁的实现不在这里**——它走 `ClaimStore.Release`，
    /// 与用户侧 `claim.release` **同一个函数**（两层判据共用一个谓词）。
    /// 这里只做**作用域筛选**：`ClaimStore` 用的是规范化后的绝对路径与逐字 `==`，
    /// 而 `release.Scope` 是模型给的**工作目录相对路径**（可以带反斜杠、尾斜杠）——
    /// 两边形状不同，必须在这一层用 `Claims.Normalize` 换算，否则一条也摘不掉。</para>
    ///
    /// <para>⚠️ <b>只摘自己的</b>：`ownerSessionId` 传本会话；替别人摘锁是
    /// `claim.release` 的逃生口，不是模型那句话的意思。</para>
    ///
    /// <para>⚠️ <b>失败方向</b>：`ClaimStore.Release` 对坏 JSON / IO 失败给空列表
    /// （同 `All` 那条），⇒ 最坏结果是「锁还在」，而**线与回收照常**。
    /// 这里不抛：它跑在步的落盘路径上，抛会把一步已经算好的结果整个丢掉。
    /// ⚠️ 事件由**调用方**发（`Stepper` 刻意不碰事件总线，见 `StepOutcome` 的注释），
    /// 所以它只交回被摘掉的那些声明。</para>
    /// </summary>
    /// <returns>真正被摘掉的声明（调用方为它们发 `claim.released`）。</returns>
    private static IReadOnlyList<Claim> UnlockReleased(SessionHandle handle, IReadOnlyList<string> scope)
    {
        if (scope.Count == 0) return [];

        var paths = scope
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Claims.Normalize(p, handle.Spec.WorkingDirectory))
            .ToList();
        if (paths.Count == 0) return [];

        try
        {
            return new ClaimStore(handle.Spec.WorkingDirectory).Release(paths, handle.SessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 摘锁是可见性机制的辅助那半；线是事实。失败不改变后者，也不该让这一步失败。
            return [];
        }
    }

    /// <summary>
    /// **答复之后直接执行**（`01` §3.8）——这一批之前走的是「让模型重新发起」。
    ///
    /// <para>⚠️ <b>为什么不再让模型重发</b>：那条路上批准只是落一句
    /// 「请重新发起它，这次会放行」，而重发要模型**再走一次往返**。代价三条：
    /// ① 多烧一次模型调用（延迟 + token）；② 多一个失败面（模型可能不重发、或重发时改了点什么）；
    /// ③ **用户改过的参数要绕一圈经过模型才被执行**——而那是**用户**的决定，
    /// 模型可能重新排版、甚至丢掉改动（那段原文本来是 `ArgumentsAmended` 要保的，
    /// 而它在 `src/` 里**零读取点**：写进去了、没人读——设计意图与实现对不上）。</para>
    ///
    /// <para>⚠️ <b>它不新增执行点</b>：跑的还是 <see cref="ToolExecutor"/>，
    /// 只是从「下一步」挪到「这一步」。判据是 <c>ArgsBypass==</c>那种「谁执行工具」
    /// 仍然只有一个答案。</para>
    ///
    /// <para>⚠️ <b>它<b>不</b>重新渲染</b>（2026-10-11 明确下来）：<see cref="StepAsync"/>
    /// 里那份渲染是**为「调模型」冻的**，而这一步**不调模型**
    /// （答复是用户给的，结果由核心自己拼）。⇒ 这里不读时间线、也不跑点杀。
    /// ⚠️ 这不是省事：若这里也跑一次点杀，冻结的那一刻就**不是**「步开始」而是
    /// 「用户按下答应的那一刻」——同一步里就会有两个判定，而那正是要躲的每步破前缀。</para>
    ///
    /// <para>⚠️ <b>改写过的参数在这里生效</b>：<paramref name="calls"/> 是调用方
    /// 按用户改的那份**换过**的调用（见 <see cref="Amended"/>），所以「用户改的就是执行的」。
    /// ⚠️ 未被回复盖住的等待中调用会**重新挂起**——一张卡可能只答了一部分，
    /// 而那几条的 `tool_use` 一条都还不能配对。</para>
    /// </summary>
    /// <param name="calls">等待中的那些调用（已按用户改写换过参数）。</param>
    /// <param name="reply">用户对那次询问的答复（盖住哪几条、答了什么）。</param>
    public async Task<StepOutcome> ResumeAsync(
        SessionHandle handle, IReadOnlyList<Block.ToolUseBlock> calls,
        ToolExecutor.Reply reply, CancellationToken ct, PausedAsk paused)
    {
        var events = new List<TimelineEvent>();
        var releasedClaims = new List<Claim>();
        var releasedLines = new List<TimelineEvent.ReleaseEvent>();
        // 答复之后真正跑掉的那批结果——**给头的活事件**（与 `StepAsync` 同一个出口）。
        IReadOnlyList<TimelineEvent.ToolResultEvent> toolResults = [];
        var goalEnded = false;
        // ⚠️ 派生 / 注入请求（2026-10-24）：与 `StepAsync` 同一条分工，走同一个出口。
        IReadOnlyList<SpawnRequest> spawns = [];
        IReadOnlyList<TellRequest> tells = [];

        if (calls.Count > 0)
        {
            var blobs = new BlobStore(_store.DataRoot);
            // ⚠️ **派生关系在这一刻算一次**（2026-10-24）：尾块与工具拿到的是同一份，
            // 两处各算一次会分叉（同 `StepAsync` 那条）。
            var subagents = SubagentScopeFor(handle);
            var executor = new ToolExecutor(
                _tools, blobs, handle.State.Axes![Axes.Permission],
                new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId));
            var context = new SessionToolContext(
                handle.Spec.WorkingDirectory, blobs,
                _store.ReadTimeline(handle.ProjectKey, handle.SessionId),
                // ⚠️ **答复路径没有步开始的槽位快照**：`floating_context` 要说的话是
                // 「答复这一刻」的那份（与头的 `/state` 同一个时间轴），所以在这里现装配一次。
                // ⚠️ 它与上面那次模型调用无关——那条路**不调模型**。
                FloatingTextAt(handle, CoreClock.Now(), subagents),
                NetworkFor(handle),
                GoalMode.IsActive(handle.State),
                handle.State.PendingCompact,
                ShellFor(),
                subagents);

            var batch = await executor.ExecuteAsync(calls, context, ct, reply);
            events.AddRange(batch.Results);
            toolResults = batch.Results;
            spawns = batch.Spawns;
            tells = batch.Tells;
            goalEnded = GoalMode.IsActive(handle.State) && EndedGoal(batch, calls);

            // ⚠️ **`release` 的线在这里也要落**（2026-10-17）——原先只在 `StepAsync` 有一半：
            // 一批里只要有一条要问，整批就挂起；用户批准之后全部执行（`ToolExecutor` 的
            // `Reply.Covers`），`release` 照样返回「已释放。」，而这里若不画线，
            // **记录里一条线都没有**，上下文也不会回收——两边都看着正常。
            // ⇒ 与 `StepAsync` 走**同一个 `RecordRelease`、同一份执行名单**，
            // 不另写一份判据。
            //
            // ⚠️ **时刻用 `CoreClock.Now()`，不用「步开始」**：这一步没有模型调用，
            // 它是**用户答复**那一刻发生的执行——与 `Answer` / `Blocked` 里的 `At` 同源。
            foreach (var executedCallId in batch.ExecutedCallIds)
                if (calls.FirstOrDefault(c => string.Equals(c.CallId, executedCallId, StringComparison.Ordinal)) is { Name: ReleaseTool.NameOf } release
                    && RecordRelease(release, CoreClock.Now()) is TimelineEvent.ReleaseEvent line)
                {
                    events.Add(line);
                    releasedClaims.AddRange(UnlockReleased(handle, line.Scope));
                    releasedLines.Add(line);
                }

            // ⚠️ **还有没答到的**（一张卡只答了一部分）⇒ 重新挂起，那些 `tool_use` 继续悬着
            // ——而这次悬着是**对的**：它们确实还没被回答。
            if (batch.Pending is { } again)
            {
                _store.AppendEvents(handle.ProjectKey, handle.SessionId, events);

                handle.AwaitingInput = new SessionHandle.PendingAsk(again, calls);
                handle.UpdateStateAndPersist(_store, s => s with
                {
                    Status = SessionStatus.WaitingForInput,
                    Ask = new PausedAsk(
                        again.Id, again.Kind, calls[0].CallId, calls[0].Name,
                        ApprovalStore.HashArgs(calls[0].Arguments),
                        DeadlineOf(handle.Spec.AskTimeout)),
                    Turn = s.Turn + 1,
                    Axes = goalEnded ? WithStandardInitiative(s.Axes) : s.Axes,
                }, diskStatus: SessionStatus.WaitingForInput);

                return new StepOutcome(NoReply, again, handle.State.Ask?.DeadlineAt,
                    ReleasedClaims: releasedClaims, ReleasedLines: releasedLines,
                    GoalEnded: goalEnded, Spawns: spawns, Tells: tells);
            }
        }

        _store.AppendEvents(handle.ProjectKey, handle.SessionId, events);

        handle.UpdateStateAndPersist(_store, s => s with
        {
            Status = SessionStatus.Idle,
            Turn = s.Turn + 1,
            Axes = goalEnded ? WithStandardInitiative(s.Axes) : s.Axes,
        }, diskStatus: SessionStatus.Idle);

        return new StepOutcome(NoReply, ReleasedClaims: releasedClaims, ReleasedLines: releasedLines,
            ToolResults: toolResults, GoalEnded: goalEnded, Spawns: spawns, Tells: tells);
    }

    /// <summary>
    /// **尾块 → 末尾的一对合成调用 + 结果**（`01` §3.2，2026-10-17 用户裁定）。
    ///
    /// <para>⚠️ <b>为什么不再拼进末尾那条 user 消息</b>：Claude 链路原先把它拼进
    /// 「用户的那条话」里 ⇒ ① 用户输入与核心注入在**同一个数组**里，模型靠包边文字才分得开；
    /// ② 下一条请求里同一条用户消息是按记录重渲染的、**不带尾块** ⇒ 前缀在第一条用户消息处
    /// 就分叉。合成一对工具调用把移动的部分挪进 tool 位置，用户消息逐字节稳定。</para>
    ///
    /// <para>⚠️ <b>为什么是「临时合成、不落盘」</b>（`01` §3.2「浮动 = 状态，不是事件」）：
    /// 落盘会让它①每步**累积**（浮动槽的整个前提是替换而非追加）②在盘上留下一条
    /// **模型从未做过的 assistant 消息**（记录是事实）。⇒ 每步现造、只进这一次请求。
    /// <see cref="TimelineRender"/> 的两条入口因此**都不认识它**：头 / 控制台的
    /// `Render` 里没有这块，`/history` 也永远不出现——浮动是「此刻是什么」，不是历史。</para>
    ///
    /// <para>⚠️ <b>它是纯的</b>：`Id` 与 `At` 都来自步开始冻住的 <paramref name="at"/> /
    /// <paramref name="turn"/>，同一份输入渲染两次逐字节相同（`00` §5 铁律 1）。
    /// 两个 id 都带 `floating-` 前缀：**一条只在这一步的请求里存在的合成调用能被认出来**
    /// （它们从不出现在盘上，也不该被任何「按 id 取回」的路径匹配上）。</para>
    ///
    /// <para>⚠️ <b>工具表里没有 `floating_context` 时整对不合成</b>（含纯 chat 会话）：
    /// 客户端（测试替身、未来的纯 chat 预设）刻意构造的无工具对话**行为逐字节不变**，
    /// 而「名字没声明就发一条调用」会在部分上游变成 400。</para>
    /// </summary>
    /// <param name="timeline">渲染后的时间线——合成对追加在它**之后**（旧内容一个字节不动）。</param>
    /// <param name="tail">尾槽位的块（<see cref="SystemBlock.Render"/> 的产物）。空 ⇒ 不合成。</param>
    /// <param name="at">步开始冻结的时刻（`now`）。</param>
    /// <param name="turn">这一步的 `Turn`——做成 id 的一部分，让不同步的合成对互不相等。</param>
    /// <summary>工具表里有没有 <see cref="FloatingContextTool.NameOf"/>（`_tools` 构造后不变，逐次查表够用）。</summary>
    private bool HasFloatingContextTool =>
        _tools.Any(t => string.Equals(t.Name, FloatingContextTool.NameOf, StringComparison.Ordinal));

    private IReadOnlyList<TimelineEvent> FloatingContextPair(
        IReadOnlyList<TimelineEvent> timeline, IReadOnlyList<Block> tail, DateTimeOffset at, int turn)
    {
        if (tail.Count == 0) return timeline;
        if (!HasFloatingContextTool) return timeline;

        var text = string.Concat(tail.OfType<Block.TextBlock>().Select(b => b.Text));
        if (text.Length == 0) return timeline;

        // ⚠️ **署名放在形状上，不放在正文里**（2026-10-23）：这一对与模型自己调出来的
        // 工具结果**同形**——首块是 `[floating_context]` 框架头。于是「这是谁给的」由形状回答
        // （`00` §7 判据 5：模型认位置与形状，不认语义），而正文里不再有任何一句
        // 「不是你发起的」。⚠️ 旧文案在正文里否认归属，而这条消息在协议上的角色就是
        // `assistant`——让读者否认自己的记录，只会把它推去**再调一次看看**（实测十二次）。
        //
        // ⚠️ **这一行是现渲染的，不是 `Framing` 字段**：合成对追加在 `RenderForModel` **之后**，
        // 走不到 `WithFraming`。形状与字段由 `FloatingContextTool.Framing` 一处给，
        // 两边都只借那一个常量 + `ToolFraming.Render`——两处各拼一遍就会漂。
        //
        // ⚠️ **末尾的 `\n` 不是排版**（2026-10-23 实测）：两个文本块在「文本化」链路上是
        // `string.Concat` 拼的，不留分隔符就会得到 `[floating_context][lilara 上下文]`——
        // 两个方括号贴在一起，**形状当场失效**（`01` §3.3：起止两行必须独立，
        // 一个块套另一个块时才分得开）。`TimelineRender.WithFraming` 那边同步加了同一个换行。
        var framing = FloatingContextTool.Framing.Render() + "\n";

        return
        [
            .. timeline,
            new TimelineEvent.MessageEvent
            {
                Id = $"floating-{turn}-call",
                At = at,
                From = Speaker.Model,
                Blocks = [new Block.ToolUseBlock($"floating-{turn}-result", FloatingContextTool.NameOf, "{}")],
            },
            new TimelineEvent.ToolResultEvent
            {
                Id = $"floating-{turn}-result",
                At = at,
                CallId = $"floating-{turn}-result",
                Content = [new Block.TextBlock(framing), new Block.TextBlock(text)],
            },
        ];
    }

    /// <summary>
    /// 尾槽位的块 → **正文**（不含框架头）。⚠️ 落在记录里的那一份，正文永远是**末块**：
    /// 合成对的内容是 `[框架头, 正文]`（`FloatingContextPair`），所以「取末块」就是判据的形状。
    /// <para>⚠️ 它同时是「变没变」的判据：尾块是轴值与插件字段的**纯函数**，
    /// 正文逐字相同 ⇔ 这一份读数仍然准确。⇒ 判据只有一处，别另比轴字典。</para>
    /// </summary>
    private static string? FloatingBodyOf(IReadOnlyList<Block> tail)
    {
        var text = string.Concat(tail.OfType<Block.TextBlock>().Select(b => b.Text));
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// **记录里最近一次落盘的浮动读数正文**（没有就是 <c>null</c>）。
    ///
    /// <para>⚠️ 只读记录、不读内存：判据的对象是「模型上一次真的看到过什么」，
    /// 而那是记录里那一份 —— 内存里留一份影子迟早会和盘面分叉（同 `Reclaim` 那条
    /// 「不缓存判定」）。</para>
    /// </summary>
    private string? LastFloatingBody(SessionHandle handle) =>
        _store.ReadFloating(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .LastOrDefault(r => r.Id.StartsWith("floating-", StringComparison.Ordinal))
            ?.Content.OfType<Block.TextBlock>().LastOrDefault()?.Text;

    /// <summary>
    /// **这一步的浮动读数**：没有变化就返回空（既不进请求，也不落盘）。
    ///
    /// <para>⚠️ <b>它是纯的</b>：同样的 <paramref name="handle"/> / <paramref name="slots"/> /
    /// <paramref name="at"/> ⇒ 逐字节相同的输出（`00` §5 铁律 1）。⇒ 可以在「装配请求」与
    /// 「落盘」两处各调一次，而不会得到两份不同的东西——这也是为什么它不写成局部变量：
    /// 那两处在两个作用域里，变量会被迫跨作用域，而跨作用域的东西迟早会被人改成可变状态。</para>
    ///
    /// <para>⚠️ <b>变化才注入的判据</b>：尾块正文（= 轴值 + 插件字段的纯函数）与记录里
    /// 最近一份逐字相同 ⇒ 那一份仍然准确 ⇒ 不产生新的一份。判据只有这一处，
    /// 不要在别处另比轴字典。</para>
    /// </summary>
    private IReadOnlyList<TimelineEvent> StepFloatingPair(
        SessionHandle handle, PromptSlots slots, DateTimeOffset at)
    {
        var pair = FloatingContextPair([], slots.Tail, at, handle.State.Turn + 1);
        if (pair.Count == 0) return pair;

        // ⚠️ **变化才注入**：与 `floating.jsonl` 里最近一份的正文逐字相同 ⇒ 不产生新的一份。
        // 判据只有这一处；不要另比轴字典（那是第二份判据，迟早漂）。
        if (FloatingBodyOf(slots.Tail) is { } body
            && string.Equals(LastFloatingBody(handle), body, StringComparison.Ordinal))
            return [];

        return pair;
    }

    /// <summary>
    /// **这一刻的尾块文本**——答复路径与控制面的探测点用（`01` §3.2）。
    ///
    /// <para>⚠️ 它读的是**这一刻**的预设文件与轴值（与头的 `/state` 同一个时间轴），
    /// 而**不是**某一步冻结的那份——那条时间线只存在于那次请求的字节里。
    /// 系统 / 首要两块的正文这里用空串：这个入口只要尾块，
    /// 而为了一句尾块去读两份预设既无必要，也会把「文件在不在」的副作用引进来。
    /// ⚠️ 装配判据仍然只有 <see cref="SystemBlock"/> 一处。</para>
    /// </summary>
    private string? FloatingTextAt(SessionHandle handle, DateTimeOffset now, SubagentScope subagents)
    {
        var state = handle.State;
        var axes = _plugins.Freeze(PluginSessionView.Values(state), now, handle.Spec.Plugins ?? []);
        var prompt = _plugins.Collect(axes, handle.Spec.Plugins ?? []);
        var slots = SystemBlock.Build(systemText: null, premierText: null, axes.Values, prompt,
            // 与 `StepAsync` 同一来源：答复路径 / 控制面探测看到的尾块也要带目标线。
            GoalMode.IsActive(state) ? CorePrompts.GoalDirective : null,
            // 压缩指令同理：答复路径上也必须看得到它，否则用户下发指令后去批准一次询问，
            // 那条尾块会「忽然没有压缩请求」——而指令明明还在。
            state.PendingCompact ? _pendingCompactText : null,
            // 子会话那一行同理：答复路径不给它的话，用户批准一次询问之后
            // 模型会以为自己没有孩子——而它其实有一堆。
            SubagentView.Line(subagents));
        return FloatingTextOf(slots);
    }

    /// <summary>尾槽位的块 → 一段文本。<c>null</c> = 没有尾块（`SystemBlock.Wrap` 的「连包边一起不产出」）。</summary>
    private static string? FloatingTextOf(PromptSlots slots)
    {
        var text = string.Concat(slots.Tail.OfType<Block.TextBlock>().Select(b => b.Text));
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// 这一刻的联网能力快照（配置 + 轴值 + 传输）。⚠️ <b>每步读一次配置</b>：
    /// 用户在头上改了端点 / key，下一步就生效；而步内是冻结的（同权限档位）。
    /// </summary>
    private NetworkAccess NetworkFor(SessionHandle handle) =>
        new(new NetworkStore(_store.DataRoot).Load(),
            handle.State.Axes?.GetValueOrDefault(Axes.Network) ?? "off",
            _networkTransport);

    /// <summary>
    /// 这一刻的 shell 能力快照（`shell.json`）。⚠️ 与 <see cref="NetworkFor"/> 同一条：
    /// **每步读一次配置**，步内冻结。「能不能跑」不在这里——那是权限轴的事。
    /// </summary>
    private ShellAccess ShellFor() => new(new ShellStore(_store.DataRoot).Load());

    /// <summary>
    /// 这一刻的**派生关系快照**（`01` §10，2026-10-24）。⚠️ 与 <see cref="ShellFor"/> 同一条：
    /// **执行那一刻现算一次**，步内冻结。
    ///
    /// <para>⚠️ <b>孩子的状态取盘上那份</b>（`Store.ReadState`），不取内存：`Running` 只写内存，
    /// 而这一层够不着 `CoreHost`。⇒ 一个正在跑的子在工具看来是 `Idle`——**如实如此**，
    /// 不假装（假装要知道内存状态，那需要给句柄开一个口子，而那是另一件事）。</para>
    ///
    /// <para>⚠️ **读不出来就跳过那个孩子**，不抛：一个坏 `state.json` 不该让父这一步整个失败
    /// （同 `/sessions` 那条「坏文件不让它打死核心」）。代价是那个 id 会从快照里消失 ⇒
    /// 模型调 `tell_child` 会得到「不是你的子会话」——**可见**，不是静默。</para>
    /// </summary>
    private SubagentScope SubagentScopeFor(SessionHandle handle)
    {
        var children = new List<SubagentChild>();

        foreach (var (projectKey, spec) in _store.ChildrenOf(handle.SessionId))
        {
            string status;
            try { status = _store.ReadState(projectKey, spec.SessionId).Status; }
            catch (Exception ex) when (ex is IOException or FileNotFoundException
                                          or UnauthorizedAccessException or FileFormatException)
            {
                continue;
            }

            children.Add(new SubagentChild(spec.SessionId, status));
        }

        return new SubagentScope(
            handle.SessionId,
            handle.Spec.ParentSessionId,
            handle.Spec.Depth ?? 0,
            handle.Spec.MaxDepth ?? Subagent.DefaultMaxDepth,
            children);
    }

    /// <summary>
    /// 这一批里有没有**真的执行过** `finish_goal`。⚠️ 走 `ExecutedCallIds`（`release` 的先例）：
    /// 被权限闸拒绝的调用也在 `toolCalls` 里，但**一次都没跑**——为它改轴等于让一次被拒绝的
    /// 操作改变控制面状态。
    /// </summary>
    private static bool EndedGoal(
        ToolExecutor.Batch batch, IReadOnlyList<Block.ToolUseBlock> calls) =>
        batch.ExecutedCallIds.Any(executed =>
            calls.Any(call => string.Equals(call.CallId, executed, StringComparison.Ordinal)
                              && string.Equals(call.Name, FinishGoalTool.NameOf, StringComparison.Ordinal)));

    /// <summary>
    /// 把主动性拨回 `standard`——**复制一份新字典**，不原地改。
    /// ⚠️ `Axes` 是只读视图，原地改会写到进程级静态表（同 `ProviderStore` 那条纪律）。
    /// </summary>
    private static IReadOnlyDictionary<string, string>? WithStandardInitiative(
        IReadOnlyDictionary<string, string>? axes)
    {
        if (axes is null) return null;

        var copy = new Dictionary<string, string>(axes, StringComparer.Ordinal)
        {
            [Axes.Initiative] = "standard",
        };
        return copy;
    }

    /// <summary>
    /// **这一步没有模型回复**——答复路径先把用户的选择变成 `tool_result`，再由
    /// `CommandDispatcher` 安排下一步模型调用。
    ///
    /// <para>它不是一个「空回复」的占位：<see cref="StepOutcome.Reply"/> 只用于本步的
    /// 事件收尾；恢复后的续跑由调用方按显式的恢复标志触发，而不是靠伪造 `tool_use`。</para>
    /// </summary>
    private static readonly ModelReply NoReply = new()
    {
        Blocks = [],
        StopReason = "end_turn",
        Refused = false,
    };

    /// <summary>
    /// **这一次模型调用失败了，马上要再试一次**（`RetryPolicy` 的 `onRetry` 原样转交）。
    /// ⚠️ 调用方（`CommandDispatcher`）据此在会话流上发 `retry.attempt`——**它不判「要不要重试」**，
    /// 收到就是「一定会再试」。
    /// </summary>
    /// <param name="Attempt">**已经失败的请求**的序号（1 起，所以「重试 N/10」正好是它：
    /// 第 1 次请求失败 ⇒ 这是第 1 次重试）。</param>
    /// <param name="Reason">那句人话（`ModelCallFailedException.Message`，已含状态码解释）。</param>
    /// <param name="Wait">即将等待多久再发下一次。</param>
    public readonly record struct RetryNotice(int Attempt, string Reason, TimeSpan Wait);

    private void NotifyRetry(RetryPolicy.FailedAttempt failed, TimeSpan wait) =>
        OnRetry?.Invoke(new RetryNotice(failed.Index, failed.Failure.Message, wait));

    /// <para>⚠️ <b><see cref="Refused"/> 是新增的</b>：`Stepper` 刻意**不碰事件总线**
    /// （模型消息与 `step.end` 都由 `CommandDispatcher` 发，这样控制台敲的那句话
    /// 在头上也看得见）。挂起那条路**本来没打算发事件**，但 `WireFormatTests` 有一条
    /// 「每个事件类型都要有发射点」的守卫——而那正是**该有的**：
    /// 一条头永远收不到的问题，等于把会话静默挂起。⇒ 与那条分工和解的做法是
    /// <b>把它作为<b>返回值的一部分</b>交出去</b>，由 `CommandDispatcher` 发。</para>
    /// </summary>
    /// <para><paramref name="ReleasedClaims"/> 同理：`release` 摘掉的声明由推进器算好
    /// （它拿着会话与工作目录），而 `claim.released` 事件由 `CommandDispatcher` 在落盘之后发。</para>
    /// </summary>
    /// <param name="Reply">原样返回给调用方（控制台要它）。</param>
    /// <param name="Refused">
    /// 这次挂起的**那件事**（要问什么、等多久）。<c>null</c> = 没挂起。
    /// ⚠️ 答案要从 <c>SessionHandle.AwaitingInput</c> 里取那批问题——它在这儿**已经算过一遍了**，
    /// 不重算的理由是「工具是纯的」这条契约不该在核心内部被当成可有可无
    /// （重算一遍会得到同一个答案，但那是**假设**，而不是这里能证明的事）。
    /// </param>
    /// <param name="ReleasedClaims">
    /// 这一步的 `release` **真正摘掉**的声明（`01` §12.4 的解锁那一半，2026-10-17）。
    /// ⚠️ 空列表是绝大多数步的答案；只有 `release` 执行且确实持有那些锁时才有内容。
    /// </param>
    /// <param name="ReleasedLines">
    /// 这一步落下的线（`ReleaseEvent`，2026-10-17）。⚠️ 它给**头**一个出口：
    /// 线本身被 `TimelineRender.IsRenderable` 挡在模型之外，而人要看得出「模型在这里收过上下文」。
    /// </param>
    public sealed record StepOutcome(
        ModelReply Reply,
        AskRequest? Refused = null,
        DateTimeOffset? DeadlineAt = null,
        string? HistoryId = null,
        IReadOnlyList<Claim>? ReleasedClaims = null,
        IReadOnlyList<TimelineEvent.ReleaseEvent>? ReleasedLines = null,
        /// <summary>
        /// 这一批工具结果——**给头的活事件**用的，不是落盘的另一份。
        ///
        /// <para>⚠️ 它们已经随 `events` 落盘了；这里交回去只是为了 `CommandDispatcher`
        /// 能发 `tool.result.appended`（2026-10-17：此前没有这个出口，头的「工具结果」
        /// 只在重拉 `/history` 时出现，正常对话流程里看不见）。</para>
        ///
        /// <para>⚠️ 交的是**记录那一份**（未回收、未解引用、未加框架）——与 `TimelineEvent`
        /// 落盘的内容逐字相同。回收/截断是**渲染期**的事，头收到活事件时那条记录刚写，
        /// 还没轮到任何判定；重拉时 `/history` 会给同一批结果套上最新形态。</para>
        /// </summary>
        IReadOnlyList<TimelineEvent.ToolResultEvent>? ToolResults = null,
        /// <summary>
        /// 本步**真的执行了** `finish_goal`，主动性已经拨回 `standard`。
        /// ⚠️ 调用方据此发 `axis.changed`（头的行 / 调音台靠它把目标标记撤掉）；
        /// <b>不要</b>据此掐掉下一次推进——用户要的是「再多跑一步让模型收尾」。
        /// </summary>
        bool GoalEnded = false,
        /// <summary>
        /// 本步**落了检查点**（`compact` 真的执行了且指令还在）。<c>null</c> = 没压（绝大多数步）。
        ///
        /// <para>⚠️ 交回去是为了让调用方发 `session.compaction.completed`——与
        /// <see cref="ReleasedLines"/> 同一条分工（`Stepper` 不碰事件总线）。
        /// 而**发不发得出去不影响已经落盘的检查点**：那是事实。</para>
        /// </summary>
        CompactionCheckpoint? Compacted = null,
        /// <summary>
        /// 本步执行出来的**派生请求**（`spawn`，2026-10-24）。⚠️ 交回去是为了让
        /// `CommandDispatcher` 真正建会话：它才有 `PluginHost` / 事件总线 / `Start`，
        /// 而 `Stepper` 刻意不碰那些（同 <see cref="ReleasedLines"/> 的分工）。
        /// ⚠️ **条目与结果一一对应、同序**（都是按调用序产出的）。
        /// </summary>
        IReadOnlyList<SpawnRequest>? Spawns = null,
        /// <summary>
        /// 本步执行出来的**注入请求**（`tell_child` / `tell_parent`，2026-10-24）。
        /// ⚠️ 与 <see cref="Spawns"/> 同一条分工：追加记录 + 入队 + 启动都在调用方。
        /// </summary>
        IReadOnlyList<TellRequest>? Tells = null);

    /// <summary>
    /// 超时的**绝对时刻**（`01` §3.8）。⚠️ <b><c>null</c> = 不超时</b>。
    ///
    /// <para>⚠️ <b>它在暂停那一刻算一次、冻进盘</b>，而不是每读一次算一次：
    /// 「等多久」是用户设的**时长**，而「什么时候到点」必须在**盘上**（重启之后
    /// 那段计时还得接着走）。现算的话，重启会把时限**重新起算** ⇒ 一个设了 5 分钟的
    /// 询问，重启几次就能无限等下去。</para>
    /// </summary>
    private static DateTimeOffset? DeadlineOf(TimeSpan? timeout) =>
        timeout is { } t ? CoreClock.Now() + t : null;

    /// <summary>
    /// **把一次询问的答复落成一条结果**（`01` §3.8）——用户答完 / 超时之后走这条路。
    ///
    /// <para>⚠️ <b>它只落<b>一条</b> <c>ToolResultEvent</c>，哪怕那次询问盖住了好几个调用</b>。
    /// 记法是「一条 <c>tool_use</c> ↔ 一条 <c>tool_result</c>」，而多问**不改记法**
    /// （见 `AskItem` 的注释：多问是一个信封）。⚠️ 那么另外那些 `tool_use` 呢？
    /// ⇒ **它们会一直悬着**，而悬空的 `tool_use` 会让下一步的请求非法（`01` §3.6）。
    /// 今天的答案：**这条询问的答复落到 <see cref="SessionState.PausedAsk.CallId"/>
    /// 指的那一条上**，其余的交由 <see cref="Blocked"/> **各补一条结果**
    /// （拒绝 / 超时 / 作废那三条路），或由 `Answer` 里那句「同一批里还有 N 条调用
    /// 被这次询问挡下了，请重新发起」交代掉（**批准**那条路）。
    ///
    /// <para>⚠️ <b>它曾经是「认下的代价」而现在已经补上一半</b>（2026-10-11）：
    /// 原先三条收尾**全都**只靠那句话让模型重发 ⇒ 那些 `tool_use` **一直悬着**，
    /// 而悬空的 `tool_use` 会让下一条请求 400。⇒ 现在「这批不跑了」的三种收尾
    /// 由 <see cref="Blocked"/> 补齐配对。
    /// ⚠️ **批准那条仍然只配一条**（它让第一条重发，而其余几条「还没轮到你」
    /// 没有一个说得清的说法）——那一条**仍是认下的代价**，见 `01` §14 #31。
    /// **别把这里读成已解决。**</para>
    ///
    /// <para>⚠️ <b>落的是<b>那一刻</b>算好的最终正文</b>——它此后冻结（`DenyReason` 那条
    /// 「理由不能渲染时重算」的判据在这里同样成立）。</para>
    /// </summary>
    /// <param name="paused">盘上那份（等哪一次调用、等多久）。</param>
    /// <param name="text">最终正文。⚠️ 由调用方算好后交进来，本方法**不判断**它是批准还是拒绝。</param>
    /// <param name="amended">
    /// 用户改写过的参数（没有就是 <c>null</c>）。⚠️ 它**不改这里落的任何一格**——
    /// 它进结果是为了让**下一步**拿去执行（见 `TimelineEvent.ToolResultEvent.ArgumentsAmended`）。
    /// </param>
    public static TimelineEvent.ToolResultEvent Answer(
        PausedAsk paused, string text, string? amended = null) => new()
    {
        // ⚠️ 稳定 id 现取：这条结果**不是**那一步的产物（那一步整个被撤销了），
        // 它是**用户答复**的产物。用同一个 `CallId` 配对就够了。
        Id = Ids.New(),
        At = CoreClock.Now(),
        CallId = paused.CallId,
        Content = [new Block.TextBlock(text)],
        ArgumentsAmended = amended,
    };

    /// <summary>
    /// **同一批里被这次询问一起挡下的其余调用，各补一条结果**（`01` §3.8）。
    ///
    /// <para>⚠️ <b>为什么必须有它</b>：`PausedAsk` 只装得下一个 `CallId`，而
    /// `ToolExecutor.Merge` 会把**一批**里多条被挡下的调用合成**一个**信封。
    /// ⇒ 「一条 `tool_use` 对多条 `tool_result`」在记法上做不出来，
    /// 于是其余几条的 `tool_use` 一直悬着——而悬空的 `tool_use` 会让
    /// **下一条请求当场 400**（上游原话：「`tool_use` ids were found without
    /// `tool_result` blocks immediately after」，`Supersede` 那条注释里记着实测）。</para>
    ///
    /// <para>⚠️ <b>`first` 那一格是「跳过几条」</b>：调用方已经用
    /// <see cref="Answer"/> 给第一条配过了（那是真正的答复），这里从第
    /// <c>first</c> 条之后开始补。⇒ 两条路**合起来**才凑齐这一批。</para>
    ///
    /// <para>⚠️ <b>只用在「这批不跑了」的三种收尾上</b>（拒绝 / 超时 / 作废）——
    /// 那时「这些调用被一起作废」是**真话**。
    /// ⚠️ <b>批准那条路不走这里</b>，而理由在 2026-10-11 变了：它原先走的是
    /// 「只让第一条重发」（其余几条的语义说不清），**现在是直接执行全部**
    /// （`ToolExecutor` 按 `Reply.Covers` 逐条跑、逐条配对）⇒ 它**不需要** `Blocked`，
    /// 因为那些调用**全都真的执行了**。</para>
    /// </summary>
    /// <param name="calls">被挂住的那些调用（`SessionHandle.PendingAsk.Calls`）。</param>
    /// <param name="first">从第几条之后开始补（调用方已配过的那条不算）。</param>
    /// <param name="said">写进每一条结果的话（三条收尾各有各的措辞）。</param>
    public static IReadOnlyList<TimelineEvent.ToolResultEvent> Blocked(
        IReadOnlyList<Block.ToolUseBlock> calls, int first, string said) =>
        [.. calls.Skip(first).Select(call => new TimelineEvent.ToolResultEvent
        {
            // ⚠️ 一条结果一个稳定 id（同 `Answer` 那条：它不是某一步的产物）。
            Id = Ids.New(),
            At = CoreClock.Now(),
            // ⚠️ **配对键是这条调用自己的 `CallId`**——不是 `paused.CallId`。
            // 用后者会配到同一条上两次，而真正的悬空那条还是悬着。
            CallId = call.CallId,
            Content = [new Block.TextBlock(said)],
        })];

    /// <summary>
    /// **用户没有回答，而是说了别的**（`01` §3.8 的 `Superseded`）。
    ///
    /// <para>⚠️ <b>它存在的理由与「批准 / 超时」一样：那个 `tool_use` 必须有配对</b>——
    /// 而这一条**是实测逼出来的**（2026-10-08）：用户对着一个询问直接打字说别的，
    /// 那条悬空的 `tool_use` 让**下一条请求当场 400**，上游原话
    /// 「`tool_use` ids were found without `tool_result` blocks immediately after」。</para>
    ///
    /// <para>⚠️ <b>措辞不能假装他答了</b>：「用户没有回答这个问题，而是说了别的：…」
    /// ——那既是事实，也告诉模型**下一步该干什么**（把那句新话当成输入）。
    /// 编一个答案比这坏得多：模型会以为它问的都得到了答复。</para>
    /// </summary>
    /// <param name="said">用户实际说的那句（截断到合理长度——它会进记录）。</param>
    public static TimelineEvent.ToolResultEvent Supersede(PausedAsk paused, string said) =>
        Answer(paused, "用户没有回答这个问题，而是说了别的："
            + (said.Length > 200 ? said[..200] + "…" : said));

    /// <summary>
    /// 一步：**追加 + 入队 + 推进**——`01` §6.1 那条复合动作的完整形态。
    ///
    /// <para>⚠️ 它保留下来是为了**让既有测试的语义不被稀释**：
    /// 那些测试断言的是「用户消息会落盘」，而 Task 4 把「追加」挪去了
    /// <see cref="Hosting.CommandDispatcher"/>。删掉这个入口就等于删掉那些断言；
    /// 而它们守的是一条**真的不变量**（用户消息是事实，模型调用失败也抹不掉）。
    /// ⇒ 生产者路径（dispatcher）走「追加 + 入队 + 取空队列」，
    /// 这个壳子给「我就想跑一步」的调用方用。</para>
    /// </summary>
    public Task<StepOutcome> StepAsync(SessionHandle handle, string userText, CancellationToken ct)
    {
        var blocks = new List<Kernel.Block> { new Kernel.Block.TextBlock(userText) };

        // ⚠️ 与 dispatcher 同一条次序：**先追加再入队**，然后取空。
        _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            Id = Ids.New(),
            At = CoreClock.Now(),
            From = Speaker.User,
            Blocks = blocks,
        });
        handle.Enqueue(blocks);

        return StepAsync(handle, handle.DrainQueue(), ct);
    }
}

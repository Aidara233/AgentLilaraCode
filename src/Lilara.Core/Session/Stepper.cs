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
    string? pendingCompactText = null,
    IBackgroundJobs? backgroundJobs = null,
    Func<string, string?>? sessionStatus = null)
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
    /// **模型正在吐的正文增量**（2026-11 流式批）。与 <see cref="OnRetry"/> 同一个形状：
    /// 解析口赋值一次，所有入口自动带上。
    ///
    /// <para>⚠️ <b>它只供显示</b>：不落盘、不进最终回复。哪怕一个增量都没送到，
    /// 这一步的结果也**完全一样**——权威事实是 <see cref="StepOutcome.Reply"/>。
    /// 这是「流式失败不影响会话正确性」的全部依据。</para>
    /// </summary>
    public Action<ModelDelta>? OnDelta { get; set; }

    /// <summary>
    /// **一条工具调用真的开始跑了**（2026-11-05）。与 <see cref="OnRetry"/> / <see cref="OnDelta"/>
    /// 同一个形状：解析口赋值一次，所有入口（`StepAsync` 与 `ResumeAsync`）自动带上。
    ///
    /// <para>⚠️ <b>为什么它不是 `StepOutcome` 的一格</b>：交回去的东西**只能等这一步结束**，
    /// 而这条事实的全部价值就是**在等待之前**到达（一次跑五分钟的 `shell`）。
    /// ⚠️ 也正因为它早于落盘，它**不能**冒充 `message.appended`——那会是一条盘上不存在的模型消息
    /// （见 `WireEvent.ToolCallStarted` 的注释）。</para>
    /// </summary>
    public Action<Block.ToolUseBlock>? OnToolCallStarted { get; set; }

    /// <summary>
    /// **一条工具结果成形了**（2026-11-05）。⚠️ 与 <see cref="OnToolCallStarted"/> 同族，
    /// 只是它晚一条调用的距离：一批五条时，第一条跑完就该有一条事件。
    ///
    /// <para>⚠️ 它**取代**了原先「步末一次性交回整批」的 <c>StepOutcome.ToolResults</c>：
    /// 同一件事不要两个出处，否则「逐条发」与「整批发」谁算数会各说各话
    /// （而那表现为头把同一条结果画两遍）。</para>
    /// </summary>
    public Action<TimelineEvent.ToolResultEvent>? OnToolResult { get; set; }

    /// <summary>
    /// **子会话的活状态**（2026-10-26）：id ⇒ <c>SessionStatus</c> 的一个值，或 <c>null</c>
    /// （= 那一刻没激活 ⇒ 调用方回落到盘面那份）。
    ///
    /// <para>⚠️ <b>为什么不在这里自己读内存</b>：`Stepper` 只有 `SessionStore`，激活表在
    /// `CoreHost` 上；为一句状态把它引进来会多一条依赖，单测还得造一个 host。
    /// ⇒ 与 <see cref="OnRetry"/> 同一个形状：**解析口赋值一次**，所有入口自动带上。</para>
    ///
    /// <para>⚠️ <b>它不是「权威状态」，只是更及时的那一份</b>：盘面仍然是重启后的唯一真相
    /// （`WaitingForInput` 落盘、`Running` 故意不落盘）。正因为如此——
    /// 只读盘面的话，一个正在跑的子会话在父的尾块里**永远是「空闲」**。</para>
    /// </summary>
    public Func<string, string?>? LiveStatus { get; set; }

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
    /// **后台任务的宿主**（2026-10-24）。⚠️ <c>null</c> = 这个推进器没有后台能力
    /// ——后台工具会回一句人话，前台路径照常。
    ///
    /// <para>⚠️ <b>它是<b>注入</b>的，不是这里造的</b>：宿主是**进程级**的（一个数据根一份），
    /// 而 `Stepper` 是**会话级**的（`CoreHost.StepperFor` 每会话一个）。
    /// 在这里 `new` 一个会让每个会话各有一张进程表，而「核心退出时杀干净」那句话当场失效。</para>
    ///
    /// <para>⚠️ 与 <see cref="INetworkTransport"/> 同一个形状与同一个理由（可测性）：
    /// 后台任务的判据（不随步死 / 杀得掉 / 上限）必须能测，而真起进程的测试既慢又不稳。</para>
    /// </summary>
    private readonly IBackgroundJobs? _backgroundJobs = backgroundJobs;

    /// <summary>
    /// **子会话状态的实况探针**（2026-10-27，`wait` 用）。⚠️ 注入而不是自查：
    /// 现读状态要知道「这一刻有没有东西在推它」，而那件事只在 `CoreHost` 手上
    /// （`LiveSessions` + 激活表）。⚠️ `null` = 没有通道（纯单测），`wait` 会如实说看不见。
    /// </summary>
    public Func<string, string?>? TaskStatus { get; set; } = sessionStatus;

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
        bool explicitStart = false, Action<string>? onPhase = null)
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

        // ⚠️ **一次性提醒的消费点就在这里**（2026-10-26）：装进尾块之后立刻清掉，所以它只出现一次。
        // ⚠️ 清在**装配之后**而不是步末：`slots` 已经把那句话冻住了，往后清与现在清没有区别；
        // 而这一步若中途失败，提醒算「说过了」——代价只是模型少看一句提示
        // （T0 与空计划它本来就看得见），不会留下错误状态。
        var iterateReplan = handle.PendingIterateReplan;
        if (iterateReplan) handle.PendingIterateReplan = false;

        // ⚠️ **三处装配点共用同一个入口**（2026-10-27）：`TailContext.For` 从 handle 派生
        // 目标线、压缩线、子会话行、计划树、一次性提醒、上下文用量——这里不再逐项传参。
        // 从前那六样是六个可空位置参数，于是「头里没有计划树」「答复路径没有计划树」
        // 这类漏传在编译期完全合法（三次）。现在少传一样东西在**结构上**不可能。
        // ⚠️ `iterateReplan:` 显式交「清之前读到的值」：那面旗子上面刚被清掉（只许说一次）。
        var slots = SystemBlock.Render(
            new PresetStore(_store.DataRoot, PresetStore.SystemDir),
            new PresetStore(_store.DataRoot, PresetStore.PremierDir),
            handle.Spec.SystemPromptPreset, handle.Spec.PremierPrompt,
            TailContext.For(handle, pluginAxes.Values, pluginPrompt, subagents, _usage,
                compactDirective: _pendingCompactText, iterateReplan: iterateReplan));

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
        // ⚠️ **声明在 `try` 外面**：`catch` 要靠它判断「有没有半截可留」（见那条 warning）。
        var partial = new PartialReply();
        ModelReply reply;
        try
        {
            // ⚠️ **`cache=stable` 的读数在这里落盘**（2026-10-27）：装配请求**之前**，
            // 这样这一次请求里就已经有它（第一步的模型也得知道轴与计划），而它只会追加到
            // 历史末尾——位置 0 保持为空，于是「没有变化」的两步请求逐字节相同。
            // ⚠️ 它是一次**独立的**原子追加（不塞进本步那批事件）：读数说的是「装配这一刻」的状态，
            // 而本步的模型回答与工具结果还没发生。崩在中间最坏是「多一条读数」，可见、无副作用。
            AppendReadout(handle, slots, at);

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
                // ⚠️ **`cache=stable` 走另一条路**（2026-10-27）：读数既不进尾槽位，也不进末尾的合成对，
                // 而是**作为真实事件落到历史末尾、只在变化时写一次**（`AppendReadout` 就在下面，
                // 而且它发生在装配这条时间线**之前** ⇒ 本次请求里就已经看得到）。
                // ⇒ 尾槽位一律清空：留着它会与历史里那一份重复。
                Slots = HasFloatingContextTool ? slots with { Tail = [] } : slots,

                // ⚠️ 工具表**从工具表投影**，不是从 spec 读的——spec 里没有它，
                // 而「这一步有哪些工具」是**派生**的（04 §1.3：工具表不是独立实体）。
                // ⚠️ 它的顺序必须**确定**（前缀指纹按它算）；`_tools` 是构造时定下的一个列表，
                // 不经过任何容器枚举。
                // ⚠️ **`cache=stable` 时 `floating_context` 换成另一份实例**（2026-10-27）：
                // 它的描述与返回值都随形态变，而描述是前缀的一部分 ⇒ 必须按会话形态投影。
                Tools = [.. ToolsFor(handle).Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchemaJson))],

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
                // ⚠️ **`cache=stable`：读数在装配这条时间线之前就落盘**（2026-10-27，见上面
                // `AppendReadout` 那次调用）。只有那个次序能同时满足两件事：① 本次请求里就看得到
                // 当前读数（否则第一步的模型在不知道轴与计划的情况下干活）；② 读数的变化**不会**
                // 出现在位置 0——它只追加到历史末尾，于是没变化的两步请求逐字节相同。
                Timeline = TimelineRender.RenderForModel(_store, handle, StepFloatingPair(handle, slots, at)),
            };

            // 调用（抛异常时直接传播——这一步没完成，Turn 不动）。
            // ⚠️ **重试在这里，不在步上**（`RetryPolicy` 的类注释）：一次重试只重发这一个请求，
            // 不碰记录、不碰 `Turn`、不碰工具副作用。⚠️ **每一次失败各记一行账**——被限流后重试成功
            // 的那一步，上游实际被打了好几次，账本只记最后一次会让统计看起来比实际便宜。
            // （成功那一行由下面补记：只有这里知道「这个回复能不能用」。）
            // 相位：请求发出去、回复还没回来（含重试等待）。判据只在核心里，头不许自己推。
            onPhase?.Invoke(SessionPhase.Generating);

            // ⚠️ **增量一边发一边攒**（2026-11-07，用户裁定「流式模式下打断要保留半截生成结果」）：
            // `OnDelta` 把每一片**只供显示**地发出去，然后就没人记得它们了 ⇒ 中断时
            // 那半页字只存在于头上那个临时气泡里（而 `StateChanged(Idle)` 一来它就被清掉）。
            // ⇒ 这里再攒一份，**只为在中断那一刻把已经说出来的话留住**。
            // ⚠️ 攒的是**同一份 `ModelDelta`**（不是解析后的回复）：`RetryPolicy` 那条
            // 「吐过就不重试」的判据、以及适配器的切片粒度都与它无关，别在中间解码两次。
            var onDelta = OnDelta is { } sink
                ? new Action<ModelDelta>(delta => { partial.Add(delta); sink(delta); })
                : null;

            var outcome = await RetryPolicy.CallAsync(_adapter, request, ct,
                delay: _retryDelay,
                onFailedAttempt: failed =>
                {
                    attempts = failed.Index;
                    RecordUsage(handle, callId, failed.StartedAt, reply: null, "failed",
                        attempt: failed.Index, endedAt: failed.EndedAt, error: failed.Failure.Message);
                },
                onRetry: NotifyRetry,
                // ⚠️ 判据全在 `RetryPolicy` 里（含「吐过就不重试」）——这里只转交。
                onDelta: onDelta);
            reply = outcome.Reply;
            attempts = outcome.Attempts;
            RecordUsage(handle, callId, outcome.StartedAt, reply, "completed", attempt: attempts,
                fingerprint: RequestFingerprint(request));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && partial.HasText)
        {
            // ⚠️ **生成到一半被打断，而已经吐出来的字要留着**（2026-11-07，用户裁定）。
            //
            // <para>⚠️ 这一步**不回滚**：回滚意味着「这一趟什么都没发生」，而事实是
            // 模型说了半页、用户也看见了那半页（`term.delta` 已经发到头上）。
            // 扔掉的代价不是磁盘上的几行——是下一步的模型**不知道自己说过那些话**
            // （于是它可能重复一遍，或者把用户看见的东西当成从没发生过）。</para>
            //
            // <para>⚠️ <b>`failure: null` 是这一条与下一条的全部分界</b>：用户按的正是「停」，
            // 所以调用方**不许**自动接续。见 <see cref="StepOutcome.Failure"/>。</para>
            if (attempts == 0) RecordUsage(handle, callId, startedAt, reply: null, "failed");
            return KeepPartialGeneration(handle, partial, at, failure: null);
        }
        catch (Exception ex) when (partial.HasText)
        {
            // ⚠️ **失败也留半截**（2026-11-13，用户裁定「就算发生报错也保留模型生成结果」）。
            //
            // <para>⚠️ <b>它为什么与上一条分开，而不是把两条并成 `catch (Exception)`</b>：
            // 两条落的东西**一样**，而交回去的那一格**不一样**——`Failure` 非 null ⇒ 调用方
            // 会**自动接续**（再推一步让模型把自己那句话说完）；取消那条**不能**接，
            // 因为用户按的正是「停」。把这一格藏在异常类型里去判，会让「接不接」由
            // 一个 `when` 过滤器决定，而那段代码在另一个文件里。</para>
            //
            // <para>⚠️ <b>走到这里的典型形状</b>：流式生成到一半，上游断/超时/5xx。
            // `RetryPolicy` 在「这一次已经吐过增量」时**不重试**（重发会把同一段话在
            // 客户端屏幕上再打一遍），于是异常冒到这里。而从前的落点是「回滚 + 抛出去」
            // ⇒ 模型说过的那半页、用户屏幕上看着的那半页，一起没了；下一步的模型
            // 不知道自己说过那些话，于是要么重复一遍、要么把用户看见的东西当成从没发生过。</para>
            //
            // <para>⚠️ **不回滚**（同上面那条）：`Turn` 要 +1，因为这一趟真的产生了记录。
            // 记账：模型调用本身的失败已被逐次记过，只有「一次请求都没发出去」才补一行。</para>
            if (attempts == 0) RecordUsage(handle, callId, startedAt, reply: null, "failed", error: ex.Message);
            return KeepPartialGeneration(handle, partial, at, failure: ex.Message);
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
        // 2026-10-26：同一条分工的第三族——`archive_child` 只说一句话，写 spec 在分派器。
        IReadOnlyList<ArchiveRequest> archives = [];

        // ⚠️ **记录那一份**（未经回收、未经解引用）——`fetch` 取地址必须从它算，
        // 理由见 `SessionToolContext` 的构造参数注释。
        var recorded = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);

        // 本批实际执行过的调用；核心效果（plan、finish_goal、mode）只依据此名单落地。
        IReadOnlyList<string> executedCallIds = [];
        // 只有 Permission 获批续跑时才非空；初次模型调用一律为空。
        IReadOnlyList<string> callIdsToExecute = [];

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

        var goalEnded = false;
        SessionPlan? planAfter = null;
        var planChanged = false;
        string? modeAfter = null;
        var modeChanged = false;
        // ⚠️ **工具相位被打断**（2026-11-07）：这一批里有半截 / 没跑的结果 ⇒ 这一步到此为止，
        // 但那批结果照常落盘（见下面那条分支）。
        var batchInterrupted = false;

        // ⚠️ 工具调用是**模型消息的一部分** ⇒ 有调用就一定有那条模型消息（上面那个 if 通过）。
        // `events.Count > 0` 不是多余的守卫，它挡的是「凭空落一堆悬空结果」——而悬空的
        // `tool_result` 与悬空的 `tool_use` 一样会让下一步的请求非法（`01` §3.6）。
        if (toolCalls.Count > 0 && events.Count > 0)
        {
            var blobs = new BlobStore(_store.DataRoot);
            // ⚠️ **权限档位在这里读一次，冻进这一趟**（与上面那个 `now` 同一个道理）：
            // 它是**浮动字段**（`01` §9.2：改它零前缀代价），而一步之内的判定必须一致——
            // 否则同一步里两条调用会按两个档位判，而记录里看不出为什么。
            // ⚠️ **模式同一条纪律**：它喂给 `ToolExecutor` 的**模式硬闸**（规划模式下拦改文件的工具）。
            var executor = new ToolExecutor(
                ToolsFor(handle), blobs, handle.State.Axes![Axes.Permission],
                new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId),
                handle.State.Axes![Axes.Mode],
                // ⚠️ **两条实时出口**（2026-11-05）：交出去的是事实，发不发、发到哪条流是调用方的事
                // （`CommandDispatcher` 在解析口接一次，同 `OnRetry` / `OnDelta`）。
                // ⇒ 于是「一次跑五分钟的 `shell`」在头上**跑起来那一刻**就看得见。
                onCallStarted: OnToolCallStarted, onResult: OnToolResult,
                // ⚠️ **通用兜底**（2026-11-07）：每步读一次 `tools.json`、步内冻结
                // （同 `ShellFor` / `NetworkFor` 那条）。它是「工具自己忘了管时间」的最后一层，
                // 不是每个工具的正常时限——工具自己的都更短，先到点的那个算数。
                callTimeout: TimeSpan.FromSeconds(ToolCallTimeoutSeconds()));
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
                ShellFor(handle),
                // ⚠️ **用步开始冻住的那一份**（2026-10-24）：与尾块同一份字节，
                // 两处各算一次会让「模型从尾块看到的」与「工具判定的」分叉。
                subagents,
                handle.State.Plan,
                handle.State.Axes![Axes.Mode],
                callIdsToExecute,
                handle.State.Axes![Axes.Initiative],
                // ⚠️ **实况探针**（2026-10-27）：`wait` 等子会话时靠它现读状态
                // ——`subagents` 那份是步开始的快照，用来「等」会永远等到超时。
                TaskStatus,
                // ⚠️ **越过 900k 之后任何模式都压得成**（2026-10-27）：判据与尾块那句命令式
                // 警告是**同一个谓词**（`ContextUsage.OverLimit`）——两处各判会分叉，
                // 而分叉的表现是「尾块喊着现在就压、工具却回一句没有指令」。
                ContextUsage.OverLimit(_usage, handle.SessionId));

            // 相位：回复里有 tool_use，接下来是工具执行。
            onPhase?.Invoke(SessionPhase.Tools);

            var batch = await executor.ExecuteAsync(toolCalls, context, ct);
            pending = batch.Pending;
            batchInterrupted = batch.Interrupted;
            events.AddRange(batch.Results);
            executedCallIds = batch.ExecutedCallIds;
            spawns = batch.Spawns;
            tells = batch.Tells;
            archives = batch.Archives;
        // T0 本身是固定目标；普通计划全部完成且不在 iterate 时，finish_goal 可以收尾当前 goal。
        // ⚠️ iterate 下即使普通计划暂时为空也不能收尾；它由下面那条自动切回 plan 续规划。
        var canFinishGoal = handle.State.Axes![Axes.Initiative] == "goal"
            && PlanTree.Current(handle.State.Plan) is null;
        goalEnded = GoalMode.IsActive(handle.State) && canFinishGoal && EndedGoal(batch, toolCalls);

            // ---- 计划的落地（2026-10-24，与 `compact` / `release` 同一条分工）----
            // 工具是**纯的**：`plan` / `advance_plan` 只判参数、回一句话，真正改树是这里。
            // ⚠️ **按调用序逐条应用**：同一批里两条 `plan` 调用，后一条看到的是前一条的结果；
            // 一次性从头算会让后一条盖掉前一条（静默丢改动，正是这条要防的）。
            (planAfter, planChanged) = ApplyPlanCalls(batch.ExecutedCallIds, toolCalls, handle.State.Plan, at);

            // ---- 工作模式的落地（2026-10-24）----
            // ⚠️ 退出规划模式那一条**只会在批准之后**出现在 `ExecutedCallIds` 里（见 `ApplyModeCalls`）。
            (modeAfter, modeChanged) = ApplyModeCalls(batch.ExecutedCallIds, toolCalls,
                handle.State.Axes![Axes.Mode], planAfter ?? handle.State.Plan, handle.State.Axes![Axes.Initiative]);
            // iterate 下普通计划刚用完 ⇒ **只置一条一次性提醒**，模式由模型自己切（2026-10-26 改）。
            // ⚠️ 核心不再替它切成 plan：自动切会把「模型本来还想接着说 / 接着做」硬掰回规划，
            // 而它并没有请求过。提醒在下一次装配时进尾块，讲清「该按 T0 规划下一段了」。
            // ⚠️ 一次性：置位之后由 `ConsumeIterateReplan` 在真正进过尾块的那一步末清掉，
            // 不在每步重复——重复的提醒会变成噪音，而模型本来就看得到 T0 与空计划。
            if (handle.State.Axes![Axes.Initiative] == Axes.Iterate
                && planChanged && PlanTree.Current(planAfter) is null
                && (planAfter?.T0 ?? handle.State.Plan?.T0) is not null)
                handle.PendingIterateReplan = true;

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

        // ⚠️ **工具相位被打断 ⇒ 这一步仍然要落盘，而且仍然是一个「说清楚了」的原子**
        // （2026-11-07，用户裁定「一旦生成完成进入工具调用阶段就保留结果」）。
        //
        // <para>⚠️ <b>为什么不能像从前那样让它冒泡</b>：模型**已经说过的话**（那条含
        // `tool_use` 的消息）与**真的跑过的副作用**（可能已经写了文件）一起消失，
        // 而记录是事实（`01` §4）——下一步的模型会以为那一轮从未发生，于是重做一遍
        // （重做一次 `write` / `rm` 不是没有代价的）。</para>
        //
        // <para>⚠️ **落的是完整的一批**：`ToolExecutor` 已经把每条调用都补成了结果
        // （跑完的 / 半截的 / 还没开始的）⇒ 记录里不会有悬空的 `tool_use`，
        // 下一次请求合法（`01` §3.6 ①）。这就是 `Batch.Interrupted` 与「异常」的分界。</para>
        //
        // <para>⚠️ **计划 / 模式 / goal 照常落地**：它们只在 `ExecutedCallIds` 上生效，
        // 而这个名单只说**真的跑过的**（被闸门拒的、还没开始的那几条不在里面）
        // ⇒ 「部分执行」不会被读成「全部执行」。</para>
        if (batchInterrupted)
        {
            var interruptedEvents = new List<TimelineEvent>(events);
            interruptedEvents.AddRange(SealFloatingCall(handle, interruptedEvents, handle.State.Turn + 1, at));

            // ⚠️ 用 `WritePair`（而不是「先 append 再写 state」）：它把**「我可能欠一条」的痕迹**
            // 与记录写进同一个临界区。万一崩在这么靠后的地方，下一步的 `Recovery.BeforeStep`
            // 仍然认得出「那一步没写完」，而不是留下一个没人认得的悬空调用。
            WritePair(handle, interruptedEvents,
                new PendingIntent(handle.State.Turn + 1, toolCalls[0].CallId), _store, s => s with
                {
                    Status = SessionStatus.Idle,
                    Turn = s.Turn + 1,
                    Plan = planChanged ? planAfter : s.Plan,
                    Axes = WithMode(goalEnded ? WithStandardInitiative(s.Axes) : s.Axes,
                        modeChanged ? modeAfter : null),
                }, diskStatus: SessionStatus.Idle);

            // ⚠️ 痕迹在两个产物都落成之后才清（同正常路径那条）。
            handle.UpdateStateAndPersist(_store, s => s with { PendingIntent = null },
                diskStatus: SessionStatus.Idle);

            return new StepOutcome(reply,
                // ⚠️ 从 `events`（封口**之前**那份）里取，不是从 `interruptedEvents`：
                // `SealFloatingCall` 会往后追加一对占位，而那一对的第一条也是 `MessageEvent`
                // ⇒ 取「最后一条消息」会拿到 `floating-…-reclaim-call`，头拿它当身份去重会把
                // 真正的模型消息顶掉。
                HistoryId: events.OfType<TimelineEvent.MessageEvent>()
                    .LastOrDefault(m => m.From == Speaker.Model)?.Id,
                ReleasedClaims: releasedClaims, ReleasedLines: releasedLines,
                GoalEnded: goalEnded, Spawns: spawns, Tells: tells, Archives: archives,
                PlanChanged: planChanged, Plan: planAfter, ModeChanged: modeChanged, Mode: modeAfter,
                ExecutedCallIds: executedCallIds,
                Interrupted: true);
        }


        // ⚠️ **`claim.released` 由调用方发**（`Stepper` 刻意不碰事件总线，同 `StepOutcome` 的注释）：
        // 头靠它刷新「谁在动哪些文件」；不发的话，头会继续显示模型已经说用完了的锁。
        // ⇒ 与 `StepOutcome` 一起交回，由 `CommandDispatcher` 在落盘之后发。

        // ⚠️ iterate 的提醒**只有一处判据**（上面那条 `if`），这里不再判第二次：
        // 两处都判会让「什么时候该提醒」有两份真相，而读代码的人不知道该信哪一份。

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
                Ask = PausedOf(pending, toolCalls, handle.Spec.AskTimeout),
                Turn = s.Turn + 1,
                // ⚠️ **挂起这条也要落计划**（2026-10-24）：同一批里「改计划 + 要问」是合法的，
                // 而这条分支在压缩之前就返回了 ⇒ 不在这里写，那次编辑会**静默丢掉**
                //（工具已经回答过「本步结束时生效」）。
                Plan = planChanged ? planAfter : s.Plan,
                Axes = WithMode(goalEnded ? WithStandardInitiative(s.Axes) : s.Axes, modeChanged ? modeAfter : null),
            }, diskStatus: SessionStatus.WaitingForInput);

            return new StepOutcome(reply, pending, handle.State.Ask?.DeadlineAt,
                events.OfType<TimelineEvent.MessageEvent>().LastOrDefault()?.Id, releasedClaims, releasedLines,
                PlanChanged: planChanged, Plan: planAfter, ModeChanged: modeChanged, Mode: modeAfter,
                ExecutedCallIds: executedCallIds);
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
                handle.State.PendingCompact, ShellFor(handle),
                initiative: handle.State.Axes![Axes.Initiative],
                // ⚠️ 落检查点时**重判一次**超限（同 `Parse` 那条注释：以这一刻的上下文为准），
                // 用的是与步开始那条工具上下文**同一个谓词**。
                contextFull: ContextUsage.OverLimit(_usage, handle.SessionId));
            compactSummary = Tools.CompactTool.Parse(
                new ToolCall(compactCall.CallId, compactCall.Name, compactCall.Arguments),
                contextForCompact, out _);
        }

        // 一次落盘 = 一个原子。⚠️ 空批次**不写**（拒答且没有工具的那一步就是空的），
        // 而 `AppendEvents` 对空批次是 no-op、不抛——见它的注释。
        // ⚠️ **浮动每一轮都在记录里留一对**（2026-10-26 用户裁定）：一次调用 + 一句已回收占位，
        // 无论模型这一轮真的调了没有。两个理由：
        //   ① 模型下一步看到的历史形状始终一致——不会出现「上一轮我没调」这种缺口，
        //      而缺口正是它去补调一次的动因；
        //   ② 真实读数不落库 ⇒ 记录里永远只有占位，旧读数的档位不可能被当成此刻的。
        // ⚠️ **不许出现第二对**：模型真调了就把那一条结果的内容换写成占位，而不是另补一对；
        // 否则同一轮里会有两条 `floating_context` 调用，配对与「一步几条事件」都跟着歪。
        // ⚠️ 它与模型消息、那批结果**同一次** `AppendEvents` 落盘 ⇒ 「一步 = 一个原子」没被拆开。
        events.AddRange(SealFloatingCall(handle, events, handle.State.Turn + 1, at));
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
            // ⚠️ **计划只在真的改过时才写**：`planChanged` 是「本步有没有落地过计划编辑」，
            // 不是「树现在是什么」。没有它，一次空手步会把 `Plan = null` 写回去（把计划抹掉），
            // 而清空计划与没动计划在类型上是同一个 null。
            Plan = planChanged ? planAfter : s.Plan,
            // ⚠️ **指令的清理在 `CommitCompaction` 里、排在检查点之后**（次序见那边）——
            // 这里不再碰它：先清指令后写检查点，崩在中间就是「指令丢了、历史没压」，
            // 而用户看到的「点过压缩」与盘面完全对不上。⚠️ 没有压缩时它原样保留。
            // ⚠️ **`finish_goal` 的效果就是这一行**：把主动性拨回 standard。
            // 拨回之后普通工具循环还会再走一步（它是一次工具调用）⇒ 模型有时间收尾总结。
            // 只在**真的执行过**且当时确实处于 goal 时才改（见 `goalEnded` 的算法）。
            Axes = WithMode(goalEnded ? WithStandardInitiative(s.Axes) : s.Axes, modeChanged ? modeAfter : null),
        }, diskStatus: SessionStatus.Idle);

        return new StepOutcome(reply, HistoryId: events.OfType<TimelineEvent.MessageEvent>().LastOrDefault()?.Id,
            ReleasedClaims: releasedClaims, ReleasedLines: releasedLines,
            GoalEnded: goalEnded, Compacted: compaction, Spawns: spawns, Tells: tells,
            Archives: archives,
            PlanChanged: planChanged, Plan: planAfter, ModeChanged: modeChanged, Mode: modeAfter,
            ExecutedCallIds: executedCallIds);
    }

    /// <summary>
    /// **把半截生成留住**（2026-11-07，用户裁定：流式模式下打断要保留半截生成结果；
    /// 2026-11-13 扩到「失败」——用户裁定「就算发生报错也保留模型生成结果」）。
    ///
    /// <para>⚠️ <b>它落的是什么</b>：一条 <c>From=Model</c> 的普通消息 + `Interrupted = true`。
    /// 于是「模型说过这半页」与「它没说完」两件事都在记录里（渲染期那句注记由
    /// `TimelineRender` 加，头靠事件上的 `interrupted` 画标记）。</para>
    ///
    /// <para>⚠️ <b>Turn 要 +1</b>：这一步真的产生了记录（`Turn` 与「记录里非结果事件数」的
    /// 对应关系是 `Recovery.BeforeStep` 的判据）⇒ 不 +1 会让下一次收尾把这条半截消息
    /// 当成「多出来的一步」去补配对。</para>
    ///
    /// <para>⚠️ <b>不写 `PendingIntent`</b>：它说的是「我可能欠一条 `tool_result`」，
    /// 而半截正文里**没有任何 `tool_use`**（增量只有正文，见 `PartialReply`）⇒ 不欠。
    /// 写上去反而会让下一步的收尾去补一条没人要的结果。</para>
    ///
    /// <para>⚠️ <b>记录里分不出「用户按停」与「失败」</b>（<c>Interrupted = true</c> 一个值），
    /// 而这是**有意的**：两件事在记录这一层说的是同一件事——<i>这条正文没说完</i>。
    /// 「要不要替他接着跑」是**运行时**的判断（<see cref="StepOutcome.Failure"/>），
    /// 不是盘上的事实；把它落盘等于给记录加一条「上一次为什么断」，而
    /// `TimelineEvent` 的格式是一扇单向门（见 `01` §12.8.2 的代价）。</para>
    /// </summary>
    /// <param name="failure">
    /// **失败原话**；<c>null</c> = 用户主动打断（不要自动接续）。
    /// ⚠️ 它只随 <see cref="StepOutcome"/> 回到调用方，**不进记录**。
    /// </param>
    private StepOutcome KeepPartialGeneration(
        SessionHandle handle, PartialReply partial, DateTimeOffset at, string? failure)
    {
        var blocks = partial.Build();
        var message = new TimelineEvent.MessageEvent
        {
            Id = Ids.New(),
            At = at,
            From = Speaker.Model,
            Blocks = blocks,
            Interrupted = true,
        };

        // ⚠️ **浮动那一对照常封口**：记录里「每一步留一对占位」的形状不能因为打断就断一节
        // （`SealFloatingCall` 的注释：形状一致才不会被模型读成「上一轮我没调用」）。
        var events = new List<TimelineEvent> { message };
        events.AddRange(SealFloatingCall(handle, events, handle.State.Turn + 1, at));
        _store.AppendEvents(handle.ProjectKey, handle.SessionId, events);

        handle.UpdateStateAndPersist(_store, s => s with
        {
            Status = SessionStatus.Idle,
            Turn = s.Turn + 1,
            PendingIntent = null,
        }, diskStatus: SessionStatus.Idle);

        return new StepOutcome(
            new ModelReply { Blocks = blocks, StopReason = "interrupted", Refused = false },
            HistoryId: message.Id,
            Interrupted: true,
            Failure: failure);
    }

    /// <summary>
    /// **一路攒下来的增量**（2026-11-07）——只为「被打断时把已经说出来的话留住」而存在。
    ///
    /// <para>⚠️ <b>只留正文（<c>text</c>），坚决不碰思考（<c>reasoning</c>）</b>：
    /// 思考块在上游是**带签名**的，而记录就是下一次请求的输入 ⇒ 半截思考回放过去
    /// 会被拒（一条 400，而报的是「块不合法」这种与真因隔了一层的话）。
    /// 正文没有这个问题：它就是模型说过的话。</para>
    ///
    /// <para>⚠️ <b>按块序号分开攒</b>：一次回复可以有多个正文块（头部一句、工具调用之间一句），
    /// 拼成一块会把它们粘成一段——而「模型说过什么」是按块记的事实。</para>
    ///
    /// <para>⚠️ <b>它不是 `DeltaPump` 的替代品</b>：那个是**显示**用的（攒 50ms 就发走、
    /// 发完就扔，`retained:false`），而这一份是**记录**用的（一步一份、只在中途被打断时读一次）。
    /// 两件事恰好都吃同一串增量，所以共用一次投递、各攒各的。</para>
    /// </summary>
    private sealed class PartialReply
    {
        private readonly Lock _gate = new();
        private readonly SortedDictionary<int, System.Text.StringBuilder> _text = [];

        public void Add(ModelDelta delta)
        {
            if (!string.Equals(delta.Kind, "text", StringComparison.Ordinal)) return;

            lock (_gate)
            {
                if (!_text.TryGetValue(delta.BlockIndex, out var text))
                    _text[delta.BlockIndex] = text = new System.Text.StringBuilder();
                text.Append(delta.Text);
            }
        }

        /// <summary>⚠️ 「吐过字」才算有半截：一片空增量不该让中断变成一次「保留」。</summary>
        public bool HasText
        {
            get { lock (_gate) return _text.Values.Any(t => t.Length > 0); }
        }

        public IReadOnlyList<Kernel.Block> Build()
        {
            lock (_gate)
            {
                return
                [
                    .. _text.Where(pair => pair.Value.Length > 0)
                        .Select(pair => (Kernel.Block)new Kernel.Block.TextBlock(pair.Value.ToString())),
                ];
            }
        }
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
        int attempt = 1, DateTimeOffset? endedAt = null, string? error = null,
        string? fingerprint = null)
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
                // ⚠️ **请求指纹**（2026-10-27 补上——这个字段一直在，却从没被写过）：
                // 它是「这一步发出去的请求，和上一步是不是同一份」这个问题的唯一机械答案。
                // 缓存命中率一掉，第一件事就是看这个：相邻两步**相同** ⇒ 我们的字节没问题，
                // 该查上游（缓存池/TTL/换模型）；**不同** ⇒ 是我们自己每步在变，那就有了下一步的方向。
                RequestFingerprint = fingerprint,
            });
        }
        catch
        {
            // 统计故障不改变会话语义；控制面可在后续版本暴露丢账告警。
        }
    }

    /// <summary>
    /// **这一步请求「头部」的稳定指纹**——三块提示词 + 工具表。
    ///
    /// <para>⚠️ <b>为什么只算头部，不算历史</b>：历史**每步都在追加**，把它算进去指纹就永远在变，
    /// 这个字段也就什么都不回答了。而缓存诊断真正要分清的是两件事：
    /// ① <b>头部变了没有</b>（轴、预设、插件字段、工具表、缓存形态——都是外力，变了就是我们这边）；
    /// ② <b>历史是不是纯追加</b>（那是另一条性质，由 `CacheStableTests` 的逐字节用例钉住）。
    /// ⇒ 这个字段答的是①：相邻两步**相同** ⇒ 头部没动，缓存该命中头部；
    /// **不同** ⇒ 有东西每步在改头部，方向立刻有了。</para>
    ///
    /// <para>⚠️ <b>不取时间戳</b>：它每步都不同，取进去等于把字段废掉。</para>
    /// </summary>
    private static string RequestFingerprint(ModelRequest request)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var block in request.Slots.System.Concat(request.Slots.Premier).Concat(request.Slots.Tail))
            builder.Append(BlockText(block)).Append('\u0002');
        foreach (var tool in request.Tools)
            builder.Append(tool.Name).Append('\u0001').Append(tool.Description)
                .Append('\u0001').Append(tool.ParametersSchemaJson).Append('\u0002');

        return BlobStore.Sha256Of(System.Text.Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string BlockText(Block block) => block switch
    {
        Block.TextBlock t => "t:" + t.Text,
        Block.ToolUseBlock u => $"u:{u.CallId}:{u.Name}:{u.Arguments}",
        Block.StoreBlock s => "s:" + s.Sha256,
        _ => block.GetType().Name,
    };

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
        ToolExecutor.Reply reply, CancellationToken ct, PausedAsk paused, Action<string>? onPhase = null)
    {
        // ⚠️ **已经配上对的那几条不再进这一趟**（2026-10-29）。
        //
        // <para>一批里有两种询问时（授权 + 提问），它们**一轮一轮**地被回答：第一张卡答完
        // ⇒ 它的结果落了盘、剩下的几条重新挂起。⚠️ 那时手里的 `calls` 仍是**整批**
        // （它来自 `AwaitingInput` 或盘上那条模型消息），于是**已经答完的那条会被重新问一次**
        // ——用户看到的是「同一句话被反复要求回答」，而它永远走进不了收敛。</para>
        //
        // <para>⚠️ 判据只能是**记录**：`tool_use` 已经有配对的结果 ⇒ 它这一生已经结束了
        // （`01` §3.6 ① 的一条对一条）。内存里的 `calls` 是缓存，重启之后更不可信
        // （`RecordedCalls` 给的是整批）。⇒ 在**进来的第一件事**上剔掉它们，
        // 于是「还剩谁没答」这件事只有一个答案。</para>
        calls = Unpaired(handle, calls);

        var events = new List<TimelineEvent>();
        var releasedClaims = new List<Claim>();
        var releasedLines = new List<TimelineEvent.ReleaseEvent>();
        var goalEnded = false;
        // 计划落地结果（见 `ApplyPlanCalls`）——与 `StepAsync` 同一对变量、同一条判据。
        SessionPlan? planAfter = null;
        var planChanged = false;
        // 模式落地结果（见 `ApplyModeCalls`）。
        string? modeAfter = null;
        var modeChanged = false;
        // ⚠️ 派生 / 注入请求（2026-10-24）：与 `StepAsync` 同一条分工，走同一个出口。
        IReadOnlyList<SpawnRequest> spawns = [];
        IReadOnlyList<TellRequest> tells = [];
        IReadOnlyList<ArchiveRequest> archives = [];
        IReadOnlyList<string> executedCallIds = [];

        if (calls.Count > 0)
        {
            var blobs = new BlobStore(_store.DataRoot);
            // ⚠️ **派生关系在这一刻算一次**（2026-10-24）：尾块与工具拿到的是同一份，
            // 两处各算一次会分叉（同 `StepAsync` 那条）。
            var subagents = SubagentScopeFor(handle);
            var executor = new ToolExecutor(
                ToolsFor(handle), blobs, handle.State.Axes![Axes.Permission],
                new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId),
                // 同 `StepAsync`：模式硬闸按**答复这一刻**的模式值判。
                handle.State.Axes![Axes.Mode],
                // ⚠️ 答复之后真正跑起来的那批也要实时报（2026-11-05）：用户刚点完「可以跑」，
                // 那一批往往正是最慢的那几条（批准过的危险操作用来都不快）。
                onCallStarted: OnToolCallStarted, onResult: OnToolResult,
                // ⚠️ 通用兜底同 `StepAsync`（且**同一份读数**，见 `ToolCallTimeoutSeconds`）。
                callTimeout: TimeSpan.FromSeconds(ToolCallTimeoutSeconds()));
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
                ShellFor(handle),
                subagents,
                handle.State.Plan,
                handle.State.Axes![Axes.Mode],
                reply.Kind is AskKind.Permission ? reply.CallIds : null,
                handle.State.Axes![Axes.Initiative],
                TaskStatus,
                // 同上（2026-10-27）：答复路径也要知道「是不是已经越过那条硬线」，
                // 否则用户批准的那次 compact 会被拒，而尾块刚刚才命令它压。
                ContextUsage.OverLimit(_usage, handle.SessionId));

            // 相位：答复之后真正跑掉的那批工具。
            onPhase?.Invoke(SessionPhase.Tools);

            var batch = await executor.ExecuteAsync(calls, context, ct, reply);
            events.AddRange(batch.Results);
            spawns = batch.Spawns;
            tells = batch.Tells;
            archives = batch.Archives;
            var canFinishGoal = handle.State.Axes![Axes.Initiative] == "goal"
                && PlanTree.Current(handle.State.Plan) is null;
            goalEnded = GoalMode.IsActive(handle.State) && canFinishGoal && EndedGoal(batch, calls);

            // ⚠️ **计划在答复这条路上也要落地**（2026-10-24）：一批里只要有一条要问，整批就挂起，
            // 用户批准之后**全部执行**——`plan` / `advance_plan` 照样返回「本步结束时生效」。
            // 这里不应用的话，那句话就是假的（同 `release` 那条线的理由）。
            (planAfter, planChanged) = ApplyPlanCalls(batch.ExecutedCallIds, calls, handle.State.Plan, CoreClock.Now());

            // 模式同理：答复这一批里也可能有 `switch_plan_mode`（退出的那一条**正是**从这里来的）。
            (modeAfter, modeChanged) = ApplyModeCalls(batch.ExecutedCallIds, calls, handle.State.Axes![Axes.Mode],
                planAfter ?? handle.State.Plan, handle.State.Axes![Axes.Initiative]);
            // 与 `StepAsync` 同一条判据、同一个效果（答复路径上也可能刚把普通计划推空）：
            // 只置一次性提醒，不替模型切模式。
            if (handle.State.Axes![Axes.Initiative] == Axes.Iterate
                && planChanged && PlanTree.Current(planAfter) is null
                && (planAfter?.T0 ?? handle.State.Plan?.T0) is not null)
                handle.PendingIterateReplan = true;

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

            // ⚠️ **答复这一批也被打断了 ⇒ 同样要落盘**（2026-11-07，与 `StepAsync` 那条同一条裁定）：
            // 这几条调用**真的跑过**（用户刚点过「可以跑」的那些往往正是危险操作），
            // 而它们的结果在前一条被打断时已经成形了。扔掉的话记录会少一整批，
            // 而那几条 `tool_use` 会永远悬着（下一次请求非法）。
            if (batch.Interrupted)
            {
                _store.AppendEvents(handle.ProjectKey, handle.SessionId, events);

                handle.UpdateStateAndPersist(_store, s => s with
                {
                    Status = SessionStatus.Idle,
                    Turn = s.Turn + 1,
                    Axes = WithMode(goalEnded ? WithStandardInitiative(s.Axes) : s.Axes,
                        modeChanged ? modeAfter : null),
                    Plan = planChanged ? planAfter : s.Plan,
                }, diskStatus: SessionStatus.Idle);

                return new StepOutcome(NoReply, ReleasedClaims: releasedClaims, ReleasedLines: releasedLines,
                    GoalEnded: goalEnded, Spawns: spawns, Tells: tells, Archives: archives,
                    PlanChanged: planChanged, Plan: planAfter, ModeChanged: modeChanged, Mode: modeAfter,
                    ExecutedCallIds: executedCallIds, Interrupted: true);
            }

            // ⚠️ **还有没答到的**（一张卡只答了一部分）⇒ 重新挂起，那些 `tool_use` 继续悬着
            // ——而这次悬着是**对的**：它们确实还没被回答。
            // ⚠️ 已经答到的那几条**照常落结果**（`events` 里就有）：这一步不再整批作废
            // ——那是「反复要求询问」的另一半（见 `ToolExecutor` 第三趟那条注释）。
            if (batch.Pending is { } again)
            {
                _store.AppendEvents(handle.ProjectKey, handle.SessionId, events);

                // ⚠️ **只把还没配对的那几条交给下一次**（这一轮刚答完的那几条已经落盘了）：
                // `AwaitingInput.Calls` 的语义是「这些 `tool_use` 还在等回答」，把答过的留在里面
                // 会让「还剩谁没答」有两个互相矛盾的答案（另一个由 `ResumeAsync` 入口那个
                // `Unpaired` 从记录里算）。⇒ 这里落盘之后再算，两处得出同一个集合。
                handle.AwaitingInput = new SessionHandle.PendingAsk(again, Unpaired(handle, calls));
                handle.UpdateStateAndPersist(_store, s => s with
                {
                    Status = SessionStatus.WaitingForInput,
                    Ask = PausedOf(again, calls, handle.Spec.AskTimeout),
                    Turn = s.Turn + 1,
                    Axes = WithMode(goalEnded ? WithStandardInitiative(s.Axes) : s.Axes, modeChanged ? modeAfter : null),
                    Plan = planChanged ? planAfter : s.Plan,
                }, diskStatus: SessionStatus.WaitingForInput);

                // ⚠️ **结果此刻已经实时发过了**（2026-11-05：`OnToolResult` 在每条结果成形时就发），
                // 所以这条返回路径上不再带结果——带上就是同一个出处两份，头会把它们画两遍。
                return new StepOutcome(NoReply, again, handle.State.Ask?.DeadlineAt,
                    ReleasedClaims: releasedClaims, ReleasedLines: releasedLines,
                    GoalEnded: goalEnded, Spawns: spawns, Tells: tells, Archives: archives,
                    PlanChanged: planChanged, Plan: planAfter, ModeChanged: modeChanged, Mode: modeAfter,
                    ExecutedCallIds: executedCallIds);
            }
        }

        _store.AppendEvents(handle.ProjectKey, handle.SessionId, events);

        handle.UpdateStateAndPersist(_store, s => s with
        {
            Status = SessionStatus.Idle,
            Turn = s.Turn + 1,
            Axes = WithMode(goalEnded ? WithStandardInitiative(s.Axes) : s.Axes, modeChanged ? modeAfter : null),
            Plan = planChanged ? planAfter : s.Plan,
        }, diskStatus: SessionStatus.Idle);

        return new StepOutcome(NoReply, ReleasedClaims: releasedClaims, ReleasedLines: releasedLines,
            GoalEnded: goalEnded, Spawns: spawns, Tells: tells,
            Archives: archives,
            PlanChanged: planChanged, Plan: planAfter, ModeChanged: modeChanged, Mode: modeAfter,
            ExecutedCallIds: executedCallIds);
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

    /// <summary>
    /// **这一步的工具表**（2026-10-27）：`cache=stable` 时把 `floating_context` 换成「变化时」那一份实例。
    ///
    /// <para>⚠️ 为什么必须换实例：那一份的**描述**（发给模型的工具表 = 前缀的一部分）与**返回值**
    /// 都随形态变，而描述是 `ITool.Description`（没有上下文的属性）⇒ 只能由实例携带。
    /// ⚠️ 名字不变（推进器按名字找它合成那对调用，改名字会静默断掉合成）。</para>
    /// </summary>
    private IReadOnlyList<ITool> ToolsFor(SessionHandle handle) =>
        !SystemBlock.CacheStable(handle) || !HasFloatingContextTool
            ? _tools
            : [.. _tools.Select(t => t is FloatingContextTool ? new FloatingContextTool(stable: true) : t)];

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
    /// **把这一轮的浮动调用钉进记录**：恰好一对，内容恒为已回收占位（`01` §3.2，2026-10-26）。
    ///
    /// <para>⚠️ <b>「模型调没调」都要有这一对</b>：模型下一步看到的历史必须形状一致。
    /// 某一轮缺了它，下一步就会看到「上一轮我没调用」——而描述要求每轮都调用，
    /// 那个缺口唯一可行的补救动作就是补调一次，而补的那一次仍然只拿到占位
    /// （<see cref="FloatingContextTool.ReclaimedBody"/>）。</para>
    ///
    /// <para>⚠️ <b>真调了就改写那一条，不再补一对</b>：一条结果就地换成占位，
    /// 而不是追加第二条同名调用——「一组调用里出现两个块」正是这次改动要防的形态。</para>
    /// </summary>
    /// <returns>要追加的事件：没调时是一对占位；真调了就重写了那一份，返回空。</returns>
    private IReadOnlyList<TimelineEvent> SealFloatingCall(
        SessionHandle handle, List<TimelineEvent> events, int turn, DateTimeOffset at)
    {
        if (!HasFloatingContextTool) return [];

        // ⚠️ **`cache=stable` 不补占位，也不改写模型调出来的那条**（2026-10-27）：
        // 那个形态里的读数**本来就以真实内容落进过记录**（`AppendReadout`），而「每步留一对占位」
        // 的存在理由是「形状一致、不让模型看到缺口」——它换来的代价是每步多两条事件、
        // 并且把「变化才写」这半个承诺变成假的。模型若这一轮自己调了，那条结果就照实留着
        // （它给的是当前读数或一句「没有变化」，都有内容，改写成占位反而抹掉信息）。
        if (SystemBlock.CacheStable(handle)) return [];

        var callId = events
            .OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .FirstOrDefault(u => string.Equals(u.Name, FloatingContextTool.NameOf, StringComparison.Ordinal))
            ?.CallId;

        if (callId is not null)
        {
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i] is TimelineEvent.ToolResultEvent r
                    && string.Equals(r.CallId, callId, StringComparison.Ordinal))
                {
                    events[i] = r with
                    {
                        Content = [new Block.TextBlock(FloatingContextTool.ReclaimedBody)],
                    };
                }
            }

            return [];
        }

        // ⚠️ **id 与顶上那个真实块不同号**：它们说的是两件事（这条是「调用已记账」的占位，
        // 那条是「此刻的读数」）。同号会让下一步的去重判据把真实块整条挡掉。
        return
        [
            new TimelineEvent.MessageEvent
            {
                Id = $"floating-{turn}-reclaim-call",
                At = at,
                From = Speaker.Model,
                Blocks =
                [
                    new Block.ToolUseBlock(
                        $"floating-{turn}-reclaim-result", FloatingContextTool.NameOf, "{}"),
                ],
            },
            new TimelineEvent.ToolResultEvent
            {
                Id = $"floating-{turn}-reclaim-result",
                At = at,
                CallId = $"floating-{turn}-reclaim-result",
                Content =
                [
                    new Block.TextBlock(FloatingContextTool.ReclaimedFraming.Render() + "\n"),
                    new Block.TextBlock(FloatingContextTool.ReclaimedBody),
                ],
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
        // ⚠️ **`stable` 形态：末尾什么都不插**（2026-10-27 用户裁定）。
        // 理由只有一个，但足够：这一对的 id 是 `floating-{turn}…`，而 turn 每步都变
        // ⇒ 请求的**第 1 个 token 起**就与上一步不同 ⇒ 任何严格前缀缓存 0 命中，
        // 每步都是全量重算（"非 DeepSeek 那种分段缓存"的提供商上这是致命的）。
        // `stable` 换形态：读数改成**变化才追加**的真实事件（`ReadoutEvent`），位置在历史末尾。
        if (SystemBlock.CacheStable(handle)) return [];

        // ⚠️ **`dynamic` 形态：每步都插，而且只活在这一次请求里**（2026-10-26 用户裁定）：
        // 不落库、也不比较「变了没有」——那套判据只在读数要落盘时才有意义。
        // ⇒ 这里没有第二份判据可漂，模型每步看到的都是「此刻这一份」。
        return FloatingContextPair([], slots.Tail, at, handle.State.Turn + 1);
    }

    /// <summary>
    /// **`stable` 形态下的读数事件**：读数作为<b>真实内容</b>追加到历史末尾，**只在变化时**。
    ///
    /// <para>⚠️ 三个判据都在这里，一个都别挪走：</para>
    /// <list type="number">
    ///   <item><b>形态</b>：不是 <c>stable</c> ⇒ 空（那条路走位置 0 的合成对）。
    ///   ⚠️ 必须与 <see cref="StepFloatingPair"/> 用**同一个**判据，否则两步都会插一份。</item>
    ///   <item><b>有没有正文</b>：尾块为空 ⇒ 空（没有内容可播报）。</item>
    ///   <item><b>变了没有</b>：与记录里最近一份读数正文逐字相同 ⇒ 空。
    ///   ⚠️ 这是「前缀完全稳定」的全部秘密：没变就<b>一个字节都不写</b>，
    ///   于是两步之间（外力没变）请求逐字节相同，缓存整段命中。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>它写的是真实内容，不是占位</b>（用户裁定）：这一条会像普通工具结果一样
    /// 参与回收（`Reclaim` 的占位判据只认那句「读数不在这里」）。所以压缩/GC 之后它可能变成
    /// 注记——那时记录里的原文本还在，故**不会**因此重复注入；而真到下次变化，
    /// 会追加一份新的完整读数，模型于是重新拿到当前状态。</para>
    /// </summary>
    private void AppendReadout(SessionHandle handle, PromptSlots slots, DateTimeOffset at)
    {
        if (!SystemBlock.CacheStable(handle)) return;

        if (FloatingBodyOf(slots.Tail) is not { } body) return;
        if (string.Equals(body, RecordedReadoutBody(handle), StringComparison.Ordinal)) return;

        _store.AppendEvents(handle.ProjectKey, handle.SessionId, ReadoutEvents(handle, body, at));
    }

    private static IReadOnlyList<TimelineEvent> ReadoutEvents(
        SessionHandle handle, string body, DateTimeOffset at)
    {
        // ⚠️ **id 必须每次追加都唯一，不能只用 `turn`**（实测事故两次：`floating-8539-…`、
        // `floating-8974-…` 各出现两遍 ⇒ `CompactionStore.LatestValid` 的 `ToDictionary`
        // 抛「同一键已存在」⇒ 整个会话的 `/history` 500）。
        // `turn` 不是持久唯一序号：凡是「先写历史、后写 state」之间出岔子的那一格
        // （失败重试、崩溃后补写、同一步里写两次），下一次都会拿到同一个 `turn`。
        // ⇒ 唯一性由生成器保证，不由调用点推理保证；后缀不变（`RecordedReadoutBody` 靠它认）。
        var id = $"floating-{Ids.New()}-readout-result";

        return
        [
            new TimelineEvent.MessageEvent
            {
                Id = id + "-call",
                At = at,
                From = Speaker.Model,
                Blocks = [new Block.ToolUseBlock(id, FloatingContextTool.NameOf, "{}")],
            },
            new TimelineEvent.ToolResultEvent
            {
                Id = id,
                At = at,
                CallId = id,
                // ⚠️ 与请求末尾那条合成对**同一形状**（框架头 + 正文）：读者靠形状认来源，
                // 而这一份是**真的落进记录**了的（`节律` 说的也是「变化时」，不是「每步」）。
                Content =
                [
                    new Block.TextBlock(FloatingContextTool.StableFraming.Render() + "\n"),
                    new Block.TextBlock(body),
                ],
            },
        ];
    }

    /// <summary>
    /// **记录里最近一份读数正文**（`stable` 形态的那条真实事件；没有 ⇒ <c>null</c>）。
    ///
    /// <para>⚠️ <b>只读记录、不读内存</b>：判据的对象是「模型上一次真的看到过什么」，
    /// 而那是记录里那一份——内存里留一份影子迟早会和盘面分叉（同 `Reclaim` 那条「不缓存判定」）。</para>
    /// <para>⚠️ <b>认的是 id，不是内容</b>：`-readout-result` 是只有这条路会写的号；
    /// `dynamic` 的占位（`-reclaim-result`）与它同前缀但不同后缀，两者不会混淆。</para>
    /// </summary>
    private string? RecordedReadoutBody(SessionHandle handle)
    {
        var last = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .LastOrDefault(r => r.Id.EndsWith("-readout-result", StringComparison.Ordinal));

        // ⚠️ 首块是框架头（`[floating_context …]`），正文在**第二个**文本块——与请求末尾那条合成对
        // 同形状，所以这里 `Skip(1)`。写成「取末块」也一样，但「跳过框架头」把意图说清了。
        return last is null
            ? null
            : string.Concat(last.Content.OfType<Block.TextBlock>().Skip(1).Select(b => b.Text));
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
        // ⚠️ **与 `StepAsync` 共用 `TailContext.For`**（2026-10-27）：这条路从前逐项传参，
        // 于是目标线、压缩线、子会话行、上下文用量都在、**计划树不在**——而「头里看不到计划」
        // 与「批准一次询问之后计划消失」是同一个缺陷的两个面。今天它只交 handle + 三样外部输入。
        var slots = SystemBlock.Build(systemText: null, premierText: null,
            TailContext.For(handle, axes.Values, prompt, subagents, _usage,
                compactDirective: _pendingCompactText));
        return FloatingTextOf(slots);
    }

    /// <summary>
    /// 本会话上次请求的输入用量那一行。
    /// ⚠️ <b>实现只有一份</b>（<see cref="ContextUsage.Line"/>）：控制面的
    /// `GET /sessions/{id}/context` 读的是同一份测量，两处不该各自算一遍
    /// ——从前它们连「取哪一条调用」的判据都不同（一处回退到更早的已知值，一处不回退）。
    /// </summary>
    internal string PreviousContextUsage(SessionHandle handle) =>
        ContextUsage.Line(_usage, handle.SessionId);

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
    ///
    /// <para>⚠️ <b><paramref name="handle"/> 是 2026-10-24 才加的参数</b>：后台任务要按
    /// **会话归属**（`IShellJobHost` 只认本会话的任务），而会话 id 只能从 handle 来。
    /// 另给一个 <c>string sessionId</c> 参数也能做到，但那会让两个调用点各挑一个——
    /// 交整个 handle 是「归属就是那个会话」这句话的直接形状。</para>
    /// </summary>
    private ShellAccess ShellFor(SessionHandle handle) =>
        new(new ShellStore(_store.DataRoot).Load(), _backgroundJobs, handle.SessionId);

    /// <summary>
    /// 这一刻的**工具调用通用上限**（`tools.json`，2026-11-07）。⚠️ 与 <see cref="ShellFor"/>
    /// 同一条：**每步读一次配置**，步内冻结——用户在头上改了，下一步生效。
    ///
    /// <para>⚠️ 抽成一个属性而不是在构造点写 `new ToolStore(...).Load()`：`ResumeAsync`
    /// 那一处也要造执行器，两处各读一遍的后果是「答复那一步用的是另一个上限」，
    /// 而在两个数不同的那一瞬**没有任何东西会报错**。</para>
    /// </summary>
    private int ToolCallTimeoutSeconds() =>
        new Lilara.Core.Tools.ToolStore(_store.DataRoot).Load().CallTimeoutSeconds;

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
    ///
    /// <para>⚠️ <b>归档的孩子同样不在这里</b>（2026-10-26）：过滤发生在
    /// <see cref="SessionStore.ChildrenOf"/> 一处，所以这一层**不用**再判一次
    /// ——`archive_child` 的效果（尾块那一行少一格、`tell_child` 不再认它）正是从这里来的。
    /// 别在这加一个 `spec.ArchivedAt` 判断：判据抄第二遍就会与那一处漂。</para>
    /// </summary>
    internal SubagentScope SubagentScopeFor(SessionHandle handle)
    {
        var children = new List<SubagentChild>();

        foreach (var (projectKey, spec) in _store.ChildrenOf(handle.SessionId))
        {
            // ⚠️ **内存优先、盘面回退**（2026-10-26）：`Running` 只写内存，只读盘面的话
            // 一个正在跑的子会话永远显示「空闲」——而模型据此以为它已经做完了。
            string status;
            try
            {
                status = LiveStatus?.Invoke(spec.SessionId)
                         ?? _store.ReadState(projectKey, spec.SessionId).Status;
            }
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
    /// **本步 `plan` / `advance_plan` 的落地**（2026-10-24）。
    ///
    /// <para>⚠️ <b>为什么在推进器里、不写在工具里</b>：工具是**纯的**（`00` §5 铁律 1）——
    /// 它没有改会话状态的手，只有 <see cref="Tools.SessionToolContext"/> 那支只读的笔。
    /// 与 `compact`（`CommitCompaction`）、`release`（画线）同一条分工：
    /// 工具判参数、回一句话，**落地在推进器这一步的收尾处**。</para>
    ///
    /// <para>⚠️ <b>按 <paramref name="executedCallIds"/> 的次序逐条应用</b>：
    /// 同一批里两条 `plan` 调用必须**依序**生效（后一条看到前一条的结果）。
    /// 每条都从初始树重算会让后一条盖掉前一条——那是静默丢改动，正是这一批要修的东西。</para>
    ///
    /// <para>⚠️ <b>应用失败就跳过那一条</b>（树保持上一次成功的样子）：工具那一侧已经用同一个
    /// <see cref="PlanTree.Apply"/> 判过一次，走到这里还能失败只剩两种——同批里前面的调用改动了
    /// 树（`advance_plan` 的 `currentId` 因此过期是**正常**的），或参数在工具那侧就不合法。
    /// 两种都不该让整步失败：**这一步已经发生了**，能改的只有计划。</para>
    /// </summary>
    private static (SessionPlan? Plan, bool Changed) ApplyPlanCalls(
        IReadOnlyList<string> executedCallIds, IReadOnlyList<Block.ToolUseBlock> calls,
        SessionPlan? current, DateTimeOffset at)
    {
        var plan = current;
        var changed = false;
        foreach (var callId in executedCallIds)
        {
            if (calls.FirstOrDefault(c => string.Equals(c.CallId, callId, StringComparison.Ordinal)) is not { } call)
                continue;

            if (string.Equals(call.Name, Tools.PlanTool.NameOf, StringComparison.Ordinal))
            {
                var (edit, parseError) = Tools.PlanJson.ParseEdit(call.Arguments);
                if (edit is null || parseError is not null) continue;
                var (next, error) = PlanTree.Apply(plan, edit, at);
                if (error is not null) continue;
                plan = next;
                changed = true;
            }
            else if (string.Equals(call.Name, Tools.AdvancePlanTool.NameOf, StringComparison.Ordinal))
            {
                if (ToolArgs.String(call.Arguments, "currentId") is not { Length: > 0 } expectedId) continue;
                var (next, error) = PlanTree.Advance(plan, expectedId, at);
                if (error is not null) continue;
                plan = next;
                changed = true;
            }
        }
        return (plan, changed);
    }

    /// <summary>
    /// **本步 `switch_plan_mode` 的落地**（2026-10-24）。与 <see cref="ApplyPlanCalls"/> 同形：
    /// 工具只判（它没有写轴的手），真正改 `axes.mode` 的是这里。
    ///
    /// <para>⚠️ <b>退出那一条能走到这里，说明它<b>已经被批准过</b></b>：第一次调用时工具返回的是
    /// `Ask`（`Permission`），那一批<b>一条都没执行</b>（`ExecutedCallIds` 为空）⇒ 这里根本不会跑；
    /// 用户批准之后核心续跑那一条，它才进 `ExecutedCallIds`。⇒ 「退出必须问人」这条
    /// **不靠这里再判一次**，而是靠「没批准就没有执行」这个结构。</para>
    ///
    /// <para>⚠️ <b>同值不落地</b>：工具那边已经回过「已经在 X 模式」了，这里再判一次是防
    /// 「同一批里两条调用」互相抵消——`mode` 被中间的调用改过时，后一条的目标可能等于当前。</para>
    /// </summary>
    private static (string? Mode, bool Changed) ApplyModeCalls(
        IReadOnlyList<string> executedCallIds, IReadOnlyList<Block.ToolUseBlock> calls,
        string? current, SessionPlan? plan, string? initiative)
    {
        var mode = current ?? Tools.PlanModeTool.Act;
        var changed = false;
        foreach (var callId in executedCallIds)
        {
            if (calls.FirstOrDefault(c => string.Equals(c.CallId, callId, StringComparison.Ordinal)) is not { } call)
                continue;
            if (!string.Equals(call.Name, Tools.PlanModeTool.NameOf, StringComparison.Ordinal)) continue;
            var target = ToolArgs.String(call.Arguments, "mode");
            if (target is not (Tools.PlanModeTool.Plan or Tools.PlanModeTool.Act)) continue;
            if (string.IsNullOrWhiteSpace(ToolArgs.String(call.Arguments, "reason"))) continue;
            if (target == Tools.PlanModeTool.Act && initiative == "goal" && PlanTree.Current(plan) is not null) continue;
            if (target == mode) continue;
            mode = target;
            changed = true;
        }
        return (mode, changed);
    }

    /// <summary>
    /// 把 `mode` 写进轴字典——**复制一份新字典**，不原地改（同 <see cref="WithStandardInitiative"/>：
    /// `Axes` 是只读视图，原地改会写到进程级静态表）。
    /// <paramref name="mode"/> 为 <c>null</c> ⇒ 原样返回（= 本步没改模式）。
    /// </summary>
    private static IReadOnlyDictionary<string, string>? WithMode(
        IReadOnlyDictionary<string, string>? axes, string? mode)
    {
        if (axes is null || mode is null) return axes;
        return new Dictionary<string, string>(axes, StringComparer.Ordinal) { [Axes.Mode] = mode };
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
        IReadOnlyList<TellRequest>? Tells = null,
        /// <summary>
        /// 本步执行出来的**归档请求**（`archive_child`，2026-10-26）。
        /// ⚠️ 与 <see cref="Spawns"/> / <see cref="Tells"/> 同一条分工：写
        /// <see cref="SessionSpec.ArchivedAt"/> 与发 `session.archived` 都在调用方
        /// （`Stepper` 不碰 store 的 spec，也不碰事件总线）。
        /// </summary>
        IReadOnlyList<ArchiveRequest>? Archives = null,
        /// <summary>
        /// 本步**真的改过计划**（`plan` / `advance_plan` 执行过、且落地成功）。
        /// ⚠️ 与 <see cref="GoalEnded"/> 同一条分工：交回去是为了让调用方发 `plan.updated`。
        /// ⚠️ <b>它和 <see cref="Plan"/> 必须成对看</b>：计划可以被清空，那时 <see cref="Plan"/>
        /// 是 <c>null</c>，而 <c>null</c> 也正是「没改」的默认值 ⇒ 单看计划分不出两者。
        /// </summary>
        bool PlanChanged = false,
        SessionPlan? Plan = null,
        bool ModeChanged = false,
        string? Mode = null,
        IReadOnlyList<string>? ExecutedCallIds = null,
        /// <summary>
        /// **这一步是「被打断」结束的，而不是跑完的**（2026-11-07）。
        ///
        /// <para>⚠️ <b>它非 <c>true</c> 时，这一趟的记录已经落盘了</b>（可能是一条半截的模型消息、
        /// 也可能是一整批「跑完的 / 半截的 / 还没开始」的结果）——这正是它与「抛
        /// `OperationCanceledException` 冒出去」的分界：那条路什么都没写。</para>
        ///
        /// <para>⚠️ <b>调用方要做的三件事</b>：① 把这一趟的模型消息发出去（`HistoryId` 在，
        /// 且带 `interrupted`）；② 发 `state.changed(Idle)`（头据此收掉临时气泡）；
        /// ③ <b>不要续跑</b>（打断就是没有下一步）——⚠️ **除非** <see cref="Failure"/> 非空
        /// （那是「失败留了半截」，那条路要自动接续，见那一格的注释）。</para>
        /// </summary>
        bool Interrupted = false,
        /// <summary>
        /// **这一趟是「失败但有东西可留」结束的**（2026-11-13，用户裁定「就算发生报错也
        /// 保留模型生成结果」）。非 <c>null</c> = 失败的那句人话；<c>null</c> = 正常结束
        /// 或用户主动打断。
        ///
        /// <para>⚠️ <b>它与 <see cref="Interrupted"/> 是两格，不是一格</b>，因为调用方要做的
        /// 事**相反**：两条路都落了记录、都算一步（`Turn` 都 +1），但
        /// 「用户按停」⇒ 不许再推；「上游断了」⇒ **要自动接续**——模型那半句话只是
        /// 被打断了，用户要的事还没做完。把两者合成一个 bool，判据就只能靠
        /// 「这个布尔是在哪个 catch 里置的」去猜，而那在另一个文件里。</para>
        ///
        /// <para>⚠️ <b>它<b>不</b>进记录</b>：盘上只有 `Interrupted = true`（「这条正文没说完」）。
        /// 「为什么没说完」是运行时的事，落盘要给 `TimelineEvent` 加一格 = 一次磁盘格式迁移
        /// （`Json.FormatVersion` + 全部金样本），而它今天只影响「下一步要不要自动跑」。</para>
        ///
        /// <para>⚠️ <b>非 <c>null</c> 时 <see cref="Interrupted"/> 一定为 <c>true</c></b>
        /// （失败那一路也是「留了东西、正常返回」，不抛异常）。</para>
        /// </summary>
        string? Failure = null);

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
    /// **这批调用里还有哪几条没有配对的结果**（`01` §3.6 ①）。
    ///
    /// <para>⚠️ <b>判据只能是记录，不能是内存</b>：`AwaitingInput.Calls` 与
    /// `RecordedCalls` 给的都可能是**整批**（含已经答完、结果已经落盘的那几条）
    /// ⇒ 拿它当「还剩谁没答」会让答过的那条被重新问一次（死循环的一半，见 `ResumeAsync`）。</para>
    ///
    /// <para>⚠️ <b>顺序保持入参那序</b>：批内次序是事实（`01` §3.6 ③），这里只做过滤。</para>
    /// </summary>
    private IReadOnlyList<Block.ToolUseBlock> Unpaired(
        SessionHandle handle, IReadOnlyList<Block.ToolUseBlock> calls)
    {
        if (calls.Count == 0) return calls;

        var paired = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Select(r => r.CallId)
            .ToHashSet(StringComparer.Ordinal);

        return [.. calls.Where(c => !paired.Contains(c.CallId))];
    }

    /// <summary>
    /// **盘面那份「在等谁」**（`PausedAsk`）——两条挂起路径（新的一步 / 答复之后又挂起）
    /// **共用这一个**，因为各写一份就会漂，而漂的代价见下面那条。
    ///
    /// <para>⚠️ <b>判据是「这一次询问是替哪条调用问的」（<see cref="AskRequest.CallIds"/> 的第一条），
    /// 而不是「这批里的第一条调用」</b>。后者看着差不多，但它把答案配到**别的**调用上：
    /// 实测（2026-10-29）一批里同时有 `plan` 与 `ask` 时，`ask` 不是第一条 ⇒ 答复落到 `plan` 上 ⇒
    /// 真正被问的那条**永远等不到答复** ⇒ 下一轮又问同一句，**永不收敛**（用户只能作废）。</para>
    ///
    /// <para>⚠️ <paramref name="calls"/> 里找不到那条（从盘上载入的批、或询问来自别处）⇒
    /// 退回批里第一条：宁可「说不准是谁」，也不要造一条不在记录里的调用。</para>
    /// </summary>
    private static PausedAsk PausedOf(
        AskRequest ask, IReadOnlyList<Block.ToolUseBlock> calls, TimeSpan? timeout)
    {
        var callId = ask.CallIds.Count > 0 ? ask.CallIds[0] : calls.Count > 0 ? calls[0].CallId : "";
        var call = calls.FirstOrDefault(c => string.Equals(c.CallId, callId, StringComparison.Ordinal))
                   ?? calls.FirstOrDefault();

        return new PausedAsk(
            ask.Id, ask.Kind,
            // ⚠️ `CallId` 那一格只装得下一个；真正的配对靠 `PendingAsk.Calls` 与
            // `AskRequest.CallIds`（两者都是列表）。
            call?.CallId ?? callId,
            call?.Name ?? "",
            ApprovalStore.HashArgs(call?.Arguments ?? "{}"),
            DeadlineOf(timeout));
    }

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
    /// <para>⚠️ <b><paramref name="answered"/> 那一格是「哪一条已经配过了」</b>：调用方已经用
    /// <see cref="Answer"/> 给它配过（那是真正的答复），这里跳过**那一条**、其余全补。
    /// ⚠️ 从前这一格是「跳过几条」（整数），而那个判据假设了「被答的总是第一条」——
    /// 2026-10-29 起不再成立（`PausedAsk.CallId` 指的是**被问的那条**，它未必排第一）。
    /// 按 id 跳过才对：按序号跳会**漏掉一条、重复另一条**。</para>
    ///
    /// <para>⚠️ <b>只用在「这批不跑了」的三种收尾上</b>（拒绝 / 超时 / 作废）——
    /// 那时「这些调用被一起作废」是**真话**。
    /// ⚠️ <b>批准那条路不走这里</b>，而理由在 2026-10-11 变了：它原先走的是
    /// 「只让第一条重发」（其余几条的语义说不清），**现在是直接执行全部**
    /// （`ToolExecutor` 按 `Reply.Covers` 逐条跑、逐条配对）⇒ 它**不需要** `Blocked`，
    /// 因为那些调用**全都真的执行了**。</para>
    /// </summary>
    /// <param name="calls">被挂住的那些调用（`SessionHandle.PendingAsk.Calls`）。</param>
    /// <param name="answered">已经被 <see cref="Answer"/> 配过的那条（`PausedAsk.CallId`）。</param>
    /// <param name="said">写进每一条结果的话（三条收尾各有各的措辞）。</param>
    public static IReadOnlyList<TimelineEvent.ToolResultEvent> Blocked(
        IReadOnlyList<Block.ToolUseBlock> calls, string? answered, string said) =>
        [.. calls
            .Where(call => !string.Equals(call.CallId, answered, StringComparison.Ordinal))
            .Select(call => new TimelineEvent.ToolResultEvent
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
    public Task<StepOutcome> StepAsync(SessionHandle handle, string userText, CancellationToken ct, Action<string>? onPhase = null)
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

        return StepAsync(handle, handle.DrainQueue(), ct, onPhase: onPhase);
    }
}

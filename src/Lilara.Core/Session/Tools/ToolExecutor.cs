using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **一步之内**执行模型的全部工具调用。
///
/// <para>⚠️ <b>四件事在这里定死，而它们的次序不是随意的</b>（`01` §3.6 ③④ / §3.8）：</para>
/// <list type="number">
///   <item><b>顺序 = 调用序</b>（模型发出的次序），**不是完成序**——
///   否则重放时记录会漂，而记录是事实（`01` §4）。</item>
///   <item><b>快照进库由这里做</b>，不是工具自己做。判据是 `BlobStore.InlineThresholdBytes`
///   一处判，而工具因此可以真的是只读的（`04` §5.1）。</item>
///   <item><b>失败是内容</b>，不是异常——见 <see cref="ToolOutcome"/>。</item>
///   <item>⚠️ <b>先判全，再执行任何一个</b>——见 <see cref="ExecuteAsync"/>。</item>
/// </list>
/// </summary>
/// <param name="tools">工具表。⚠️ 它是**派生**的（`04` §1.3），不是注册表；
/// 名字查不到时走「这个工具不存在」，那条**也是内容**（与「工具在、但被拒」要分开显示）。</param>
/// <param name="blobs">库。⚠️ **今天只读**：写入方是下面的 `Materialize` 那条路。</param>
/// <param name="permissionLevel">
/// 当前权限档位（`01` §3.5 的 `permission` 轴值）。
/// ⚠️ <b>它没有默认值，而这是刻意的</b>：给一个默认（哪怕是 `null`）就等于让
/// 「用哪个档位」变成**沉默的选择**——而那正是 `04` §5 警告过的那种默认
/// （未声明的东西会被静静地用掉）。⇒ 每个调用点**必须写出来**。
/// ⚠️ <b>`null` 是合法的，且表示"未知档位"</b>，而 <see cref="Gate.Decide"/> 把未知
/// 当作**最严的那一档**（宁可漏放不可错放）。
/// </param>
/// <param name="approvals">
/// 批准的记忆（`01` §3.8）。⚠️ <b>可空 = 没有记忆</b>（测试、以及还没有会话的路径）。
/// ⚠️ <b>它<b>只</b>决定「还要不要问」</b>——命中记忆 ⇒ 那条 `Ask` 变 `Allow`；
/// **它从不放行一条 `Deny`**（档位不够是硬线，用户批过某一个参数不该让整类工具通过）。
/// </param>
/// <param name="onCallStarted">
/// **一条调用真的开始执行了**（2026-11-05）。⚠️ 回调只交事实，发不发、发到哪条流是调用方的事
/// （与 `StepOutcome` 同一条分工：执行器不认识事件总线）。
///
/// <para>⚠️ <b>它只在 <c>willRun</c> 那一支被调</b>——被闸门拒的、名字不存在的、
/// 挂在询问上等答复的调用**一条都不报**。那些调用的 `tool_use` 照样会随模型消息到达，
/// 头显示的是「被拒 / 等你答」；把它们也报成「执行中」是**假信息**。</para>
/// <para>⚠️ 调用点在 `RunAsync` **之前**、`executed` 记名之后：那一刻「它要跑了」是事实，
/// 而报告本身必须发生在等待之前——否则长工具跑完才有事件，这正是要修的那个毛病。</para>
/// </param>
/// <param name="onResult">
/// **一条结果成形了**（2026-11-05）。⚠️ 与 <paramref name="onCallStarted"/> 同一条分工。
/// 它在结果被 `Materialize` 之后、返回之前逐条触发 ⇒ 一批里第一条跑完就有一条事件，
/// 而不是整批跑完才一起发。
/// <para>⚠️ 交的是<b>记录那一份</b>（未回收、未截断、未加框架）——与落盘的内容逐字相同；
/// 回收 / 截断是**渲染期**的事，那时这条记录还没写。</para>
/// </param>
public sealed class ToolExecutor(
    IReadOnlyList<ITool> tools,
    Storage.BlobStore blobs,
    string? permissionLevel,
    ApprovalStore? approvals = null,
    string? mode = null,
    Action<Block.ToolUseBlock>? onCallStarted = null,
    Action<TimelineEvent.ToolResultEvent>? onResult = null,
    TimeSpan? callTimeout = null)
{
    /// <summary>
    /// **规划模式下被硬拦的工具名**（2026-10-24，用户裁定：「计划模式强制拦截几个内置的常见修改文件指令
    /// （shell 之类的不拦截，硬要改也拦不住）」）。
    ///
    /// <para>⚠️ <b>这是<b>模式</b>的闸，不是权限的闸</b>：它按 `mode=plan` 拒，与档位无关
    /// （`trusted` 在规划模式下同样写不了文件）——因为「模式」答的是「此刻按什么规矩做」，
    /// 而权限答的是「你有多少能力」。两个轴各问各的。</para>
    ///
    /// <para>⚠️ <b>为什么不拦 `shell` / `dotnet_*` / `git_read`</b>：那些不是「改文件」这一个动作
    /// （`shell` 能改一切，也什么都拦不住），把它们塞进来只会让规划模式变成
    /// 「连读都读不了」——而读是规划的原料。用户原话：硬要改也拦不住。</para>
    ///
    /// <para>⚠️ <b>名单是<b>名字</b>，不是权限类别</b>：按类别拦会把 `Writing` 那一整档
    /// （也包括将来某个「写日志」这类无害工具）一起拦掉，而用户要的是「几个常见的修改文件指令」。
    /// ⇒ 加一个名字就在这里加一行，并想清楚它是不是「改本机文件」。</para>
    /// </summary>
    private static readonly HashSet<string> PlanModeBlocked = new(StringComparer.Ordinal)
    {
        "write", "update", "delete", "mkdir", "git_write",
    };

    private bool InPlanMode => string.Equals(mode, PlanModeTool.Plan, StringComparison.Ordinal);

    /// <summary>
    /// **一次工具调用的通用上限**（2026-11-07，用户裁定「加个通用兜底」）。
    ///
    /// <para>⚠️ <b>它是兜底，不是每个工具的正常时限</b>：工具自己的时限（`shell` 120 秒、
    /// `dotnet_build` 180 秒、`git` 8/30 秒、联网 20 秒…）都更短 ⇒ 它们先到点。
    /// 这一格只防一种情形：**一个没有时限、又不听取消的工具**把整步挂到天荒地老
    /// ——从前唯一的救法是用户手动 `interrupt`。</para>
    ///
    /// <para>⚠️ <b>它不负责「转后台」</b>：那件事只有**拥有进程的工具**做得到（它手上有活着的
    /// 子进程，能交给 <see cref="IBackgroundJobs.Adopt"/>）。这一层只做两件事：
    /// 到点发取消，等不到就交一条「超时被终止」的结果。⇒ 分工是
    /// 「能转后台的自己在时限里转（它们的时限更短），转不了/没转的由这里兜底终止」。</para>
    ///
    /// <para>⚠️ <b>数从 `tools.json` 来</b>（`Stepper` 每步读一次、步内冻结，同
    /// `ShellAccess` / `NetworkAccess`）：用户在头上改，下一步生效。</para>
    /// </summary>
    private readonly TimeSpan _callTimeout =
        callTimeout ?? TimeSpan.FromSeconds(ToolCallLimits.DefaultTimeoutSeconds);

    /// <summary>
    /// **一条调用抛了异常时落在结果里的那句话**（2026-11-13）。⚠️ 文案在
    /// <see cref="Interruption.FailedText"/> 一处（与 `CutText` / `UnrunText` 同族）——
    /// 这里只负责把「哪条调用、什么异常」装进去，不另写一套措辞。
    /// </summary>
    private static string ToolFailureText(Block.ToolUseBlock call, Exception ex) =>
        Interruption.FailedText(call.Name, ex.Message);

    private static string PlanModeDenial(string tool) =>
        $"现在在规划模式：'{tool}' 会改本机文件，已被拦下。"
        + $"要动手请先调用 {PlanModeTool.NameOf}（mode=act）请求退出规划模式——那会让用户确认一次。"
        + "读、搜、查历史、`shell` 都不受这条限制。";

    /// <summary>
    /// **用户批过的那几条：跳闸门，直接执行**（`01` §3.8）。
    ///
    /// <para>⚠️ <b>`null` = 走正常那条路</b>（所有调用都过闸门）。非空 ⇒ 列在里面那些
    /// `callId` **跳过判定、直接跑**。</para>
    ///
    /// <para>⚠️ <b>它为什么必须存在，而不是「写条记忆让闸门自己放行」</b>：
    /// 批准那一步是 `CommandDispatcher` 里落的结果，而**它没法把参数原文送回来**——
    /// 用户改过的参数只走 `tool_result` 的 <c>ArgumentsAmended</c> 那一格，
    /// 而形状上「一条 `tool_result` 对一条 `tool_use`」（`01` §3.6 ①）。
    /// ⇒ 一批里第 2..N 条**拿不到属于它们的那份改写**。由调用方把**执行用的参数**
    /// 直接交进来，这一层就不需要知道「用户改没改」。</para>
    ///
    /// <para>⚠️ <b>传进来的不只是原样重发</b>：调用方（`Stepper.ResumeAsync`）会把
    /// 那几条调用换成用户改写后的参数，并把「用户对那次询问的答复」也传进来
    /// —— 于是「那几项没有被回答」这句话由**核心**写，不再绕经模型。</para>
    /// </summary>
    public sealed record Reply(
        AskKind Kind,
        IReadOnlyList<string> CallIds,
        IReadOnlyList<AskItem> Items,
        IReadOnlyDictionary<string, string> Answers)
    {
        /// <summary>这次回复盖住了这条调用吗。（空列表 = 全都不盖。）</summary>
        public bool Covers(string callId) =>
            CallIds.Any(id => string.Equals(id, callId, StringComparison.Ordinal));

        /// <summary>
        /// 按 id 拼回**一条**结果（`01` §3.8：一次询问一个信封）。
        /// ⚠️ 缺项如实写「未回答」，**不编一个空值**——那是「多了一条假信息」。
        ///
        /// <para>⚠️ <b>附言另起一行、排在最后</b>（用户 2026-11-13）：它是「随这次答复
        /// 一起说的一句话」，不是某个问题的答案 ⇒ 不按 id 归位，而是整段答复的收尾。
        /// 空（或全空白）时**不出现**——那与「没写」是同一件事。</para>
        /// </summary>
        public string Compose()
        {
            var lines = new List<string>();
            foreach (var item in Items)
            {
                lines.Add(Answers.TryGetValue(item.Id, out var v)
                    ? $"{item.Id}: {v}"
                    : $"{item.Id}: （未回答）");
            }

            if (AskAnswers.RemarkOf(Answers) is { } remark) lines.Add(AskAnswers.RemarkLine(remark));

            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// 全部执行完，返回与入参**一一对应、同序**的结果。
    ///
    /// <para>⚠️ <b>三趟，次序是刻意的</b>：</para>
    /// <list type="number">
    ///   <item><b>判</b>：每条调用过闸门（不存在的工具 · `Allow` / `Deny` / `Ask`）。
    ///   <b>一条都不执行。</b></item>
    ///   <item><b>收问</b>：`Ask` 且**没有记忆**的那些攒起来；声明了
    ///   <see cref="ITool.AsksBeforeRunning"/> 的工具在这里跑一遍、只取它的问题。</item>
    ///   <item><b>有问 ⇒ 一条都不跑。</b>否则**全部执行**、按调用序成结果。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>「先判全」不是保守，是必须的</b>：否则「call 1 已经写了文件、call 2 要问」
    /// ⇒ 中止 ⇒ <b>那次写入没有记录</b>，而记录是事实。代价是「一批里有一个要问，整批都不跑」
    /// ——认下，因为反过来是**静默的不一致**。</para>
    ///
    /// <para>⚠️ <b>于是「问只能发生在执行之前」是一条契约</b>：工具想在**执行中**发现
    /// 「要不要继续」（「这文件 20 MB，读全吗」）⇒ **不许问**——把那个发现作为普通结果返回，
    /// 让模型下一步用 `ask` 工具问。这既保住步原子性，也让「我想问一句」变成
    /// 模型看得见的一个正常动作。</para>
    ///
    /// <para>⚠️ <b>今天循环执行（串行），而这是刻意的偏离</b>：`01` §4 说「步内默认并发」。
    /// 偏离的理由是<b>本批的工具都是纯读的</b>——并发零收益，而取消钩子打进一个执行
    /// 比打进 N 个并发简单得多。⚠️ <b>改成并发不需要动别的</b>：下面的「按调用序」
    /// 是<b>结构</b>保证的（结果按 `callId` 归位），与执行策略无关。</para>
    /// </summary>
    /// <param name="calls">这一批工具调用。⚠️ 空批次是合法的（纯文本的一步）。</param>
    /// <returns>
    /// <see cref="Batch.Results"/> 与入参一一对应、同序；<see cref="Batch.Pending"/>
    /// 非 null ⇒ 这次**真的产生了询问**，而 `Results` 是**空列表**
    /// （一条结果都不许落盘——那些 `tool_use` 悬着，等用户答完再配对）。
    ///
    /// <para>⚠️ <b>为什么返回一个小记录、而不是 <c>out</c> 参数</b>：<b>C# 的 `async`
    /// 方法不许有 <c>ref</c> / <c>in</c> / <c>out</c> 参数</b>（编译器语言规则，
    /// 不是风格选择）。⇒ 「一次调用、两个产出」在这里只能靠返回值——
    /// <b>而这是查过语言规则才定的，不是试出来的</b>。</para>
    /// </returns>
    public async Task<Batch> ExecuteAsync(
        IReadOnlyList<Block.ToolUseBlock> calls, IToolContext context, CancellationToken ct,
        Reply? reply = null)
    {
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // ---- 第一趟：判。一条都不执行。 ----
        var plans = new List<Plan>(calls.Count);
        foreach (var call in calls)
        {
            if (!byName.TryGetValue(call.Name, out var tool))
            {
                plans.Add(new Plan(call, null, Verdict.Deny, $"没有名为 '{call.Name}' 的工具。"));
                continue;
            }

            // ⚠️ **规划模式的硬闸**（2026-10-24）：在**一切放行之前**判——
            // 包括「用户刚批过」那条捷径。理由：模式是**上限**，不是一次性的许可；
            // 若批准能穿透它，那 `plan` 模式就成了「多问一句话就能绕过」的装饰。
            if (InPlanMode && PlanModeBlocked.Contains(tool.Name))
            {
                plans.Add(new Plan(call, tool, Verdict.Deny, PlanModeDenial(tool.Name)));
                continue;
            }

            // ⚠️ **用户刚批过的那几条：跳闸门**（`01` §3.8）。判据是「用户已经就这一次调用
            // 说过话」——而不是「写没写过记忆」。这两件事在**这一批**上看着等效，
            // 但只对**回复盖住的那些**成立：没被盖住的那几条**照样要问**
            //（一张卡可能只答了三条，而模型发了五条）。
            // ⚠️ 它**跳的是判定，不是执行**——工具照常由下面那一趟跑，执行点仍然只有这一个。
            if (reply is not null && reply.Covers(call.CallId))
            {
                plans.Add(new Plan(call, tool, Verdict.Allow, ""));
                continue;
            }

            // ⚠️ 用**工具**那个重载，不是按类别那个：`compact` 声明了不归闸门管
            // （`ITool.GatedByPermission`），而那个判断必须先于 `档位 × 类别` 那张表。
            var verdict = Gate.Decide(tool, permissionLevel);

            // ⚠️ **记忆只把 `Ask` 变成 `Allow`**（`01` §3.8）：它**绝不**把 `Deny` 变成 `Allow`
            // ——档位不够是**硬线**，而批准记的是「这一次调用」，不是「这一类工具」。
            // ⚠️ 判据是「批过就**不问**」，不是「批过就放行」。
            if (verdict is Verdict.Ask && approvals is not null
                && approvals.Covers(call.Name, ApprovalStore.HashArgs(call.Arguments)))
            {
                verdict = Verdict.Allow;
            }

            plans.Add(new Plan(call, tool, verdict, Gate.DenyReason(tool.PermissionClass, permissionLevel)));
        }

        var asks = new List<AskRequest>();

        // ⚠️ **这一轮在问的那几条调用**（协议 id）。它们**不跑、也不落结果**——那一条结果要等回答。
        // 它同时是「哪几条**不用**跑」的判据（见第三趟），所以**产出 `Ask` 的那一刻**就得记下，
        // 而不是事后从 `AskRequest.CallIds` 猜（那个字段是给**答复**那条路用的）。
        var asked = new HashSet<string>(StringComparer.Ordinal);

        // ---- 第二趟：收问。 ----
        //
        // ⚠️ **按序号走，不是 `foreach`**（2026-11-13）：下面那个预跑可能**抛异常**，
        // 而处理它的方式是**把这一条换成一个 `Deny`**（一句人话跟着结果回给模型）。
        // 用 `foreach` 就没有那个位置可写——而让它冒出去会**炸掉整步**：
        // 模型那条含 `tool_use` 的消息与同批其它调用的结果一起消失（同第三趟那条注释）。
        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            // 不存在的工具与硬拒：它们**不问**，理由在 `Says` 里。
            if (plan.Tool is null || plan.Verdict is Verdict.Deny) continue;

            // ⚠️ 已答复过的调用不再收问（它与上面那条跳闸门是同一条判据的两半）。
            if (reply is not null && reply.Covers(plan.Call.CallId)) continue;

            if (plan.Verdict is Verdict.Ask)
            {
                asks.Add(Gate.AskFor(plan.Tool, plan.Call, permissionLevel));
                asked.Add(plan.Call.CallId);
                continue;
            }

            // ⚠️ **声明了「执行前要问」的工具，在这里跑一遍、只取它的问题**。
            // 跑它**没有副作用**——那正是它敢声明这个的前提（见 `ITool.AsksBeforeRunning`）。
            if (plan.Tool.AsksBeforeRunning)
            {
                ToolOutcome outcome;
                try
                {
                    outcome = await plan.Tool.RunAsync(
                        new ToolCall(plan.Call.CallId, plan.Call.Name, plan.Call.Arguments), context, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // ⚠️ **用户打断照样原样冒出去**（整条链上认「打断」的判据就是这个类型）：
                    // 这一趟发生在任何副作用之前，`Stepper` 那边还**没有**开始执行相位
                    // ⇒ 记录里没有半批要救，抛出去是最诚实的结局（`RunStepAsync` 认它是中断）。
                    throw;
                }
                catch (Exception ex)
                {
                    // ⚠️ **预跑抛了 ⇒ 这条调用降级成「没有执行」**（`Deny` + 一句人话）：
                    // 典型形状是参数解析失败（`ask` / `plan` 的 `RunAsync` 就是解析参数）。
                    // 于是模型下一步拿到的是一条**说得清的结果**，而不是「整步没了」。
                    plans[i] = new Plan(plan.Call, null, Verdict.Deny, ToolFailureText(plan.Call, ex));
                    continue;
                }

                if (outcome.Ask is { } ask)
                {
                    asks.Add(ask);
                    asked.Add(plan.Call.CallId);
                }
            }
        }

        // ---- 第三趟：有问 ⇒ 一条都不跑；否则全部执行。 ----
        //
        // ⚠️ **那个「一条都不跑」只对<b>新的一步</b>成立**（`reply is null`）：那时一条都没执行过，
        // 「先判全」保住的正是「记录不会出现半批」。⚠️ <b>答复那一步（`reply != null`）必须分开</b>
        // ——用户已经就**已经答到的那几条**说过话了：
        //   · 让它们照常跑完、落结果，答案才留得下来；
        //   · 只把**这一轮才要问**的那几条挂起。
        // ⚠️ 不分开的话，实测（2026-10-29）就是那个死循环：一批里同时有 `plan` 与 `ask` 时，
        // 答复之后**又**冒出一条要问的 ⇒ 整批不跑 ⇒ 上一次的答案**一个字都没落盘** ⇒
        // 下一轮又问同一句，永不收敛（用户看到的「反复要求询问，只能作废」）。
        if (asks.Count > 0 && reply is null) return new Batch([], Merge(asks));

        var results = new List<TimelineEvent.ToolResultEvent>(calls.Count);

        // ⚠️ **授权卡「批准」时，用户写的那句附言落在哪**（用户 2026-11-13 裁定）。
        //
        // <para>提问卡（`Question`）那条不用它：答案本身就是内容，附言由
        // <see cref="Reply.Compose"/> 拼在那段答复的最后一行。</para>
        //
        // <para>而授权卡批准时**没有「答复正文」这种东西**——那几条调用直接执行、
        // 各落各的工具结果 ⇒ 附言只能**附在结果后面**（`WithRemark`），
        // 而且**只附一次**（第一条第被批准的调用）：它是「用户对这次批准说的一句话」，
        // 不是每条工具输出各说一遍。⚠️ 附的是**内容**、不是新的一条结果——
        // 「一条 `tool_use` 对一条 `tool_result`」是结构性的（`01` §3.6 ①）。</para>
        var remark = reply is { Kind: AskKind.Permission } ? AskAnswers.RemarkOf(reply.Answers) : null;

        // ⚠️ **工具声明、核心落地**的两族（2026-10-24）：`spawn` 派生、`tell_*` 注入。
        // 与 `Ask` 同一条判据——工具没有 store 的手，而这两件事都要碰会话。
        var spawns = new List<SpawnRequest>();
        var tells = new List<TellRequest>();
        // 2026-10-26：第三族——`archive_child` 收起。同上：工具只说一句话，写 spec 在分派器。
        var archives = new List<ArchiveRequest>();
        // 2026-11-19：`approve_child` 的放行请求（与上面三族同一条纪律：只有真跑过的才产出）。
        // ⚠️ **不能叫 `approvals`**：那个名字是构造参数（人的批准记忆 `ApprovalStore`）的，
        // 同名的局部变量会让它在本方法里整段被遮住（CS0841）。
        var childApprovals = new List<ApproveChildRequest>();

        // ⚠️ **只有走到下面那个 `RunAsync` 的调用才进来**——「名字在批里」不足以说明
        // 「它执行过」（见 `Batch.ExecutedCallIds`）。`release` 的线按这份名单画。
        var executed = new List<string>(calls.Count);
        // ⚠️ **这一批是不是被用户打断了**（2026-11-07）：判据只有 `ct` 的取消——
        // 通用时限那条路在自己的 `RunWithDeadlineAsync` 里就把超时翻成了一条结果（不抛）。
        var interrupted = false;
        // ⚠️ 被打断那条调用的**半截结果**（工具在取消时交出来的，可能没有）。
        ToolInterruptedException? cut = null;
        foreach (var plan in plans)
        {
            // ⚠️ **这一轮在问的那几条：不跑、也不配对**（见上面那个 `asked`）。
            // 它们的结果要等用户回答，所以这一批里一条都不能有——而其余那几条照常跑，
            // 于是「答复之后又冒出一条要问」不再让整批作废（2026-10-29）。
            if (asked.Contains(plan.Call.CallId)) continue;

            // ⚠️ **问意见（`Question`）那条：用户答复过的不跑**（`01` §3.8）。
            //
            // <para>`ask` 问的不是「准不准跑」——它问的是「你怎么想」，而**答案本身就是内容**
            // （`AskKind.Question`）⇒ 那条调用落的是用户给的内容，工具一个字都不碰。</para>
            //
            // <para>⚠️ <b>授权（`Permission`）那条恰好相反</b>：用户说「可以」的意思就是
            // **让它跑**——那条调用在挂起时**一次都没执行过**，所以这里**必须**往下走去执行。
            // ⇒ 两处判据只在 `Question` 上分叉，而这一格写错的表现是
            // 「批准了、结果也落了，而工具一次都没跑」——**看着完全正常**。</para>
            if (reply is { Kind: AskKind.Question } && reply.Covers(plan.Call.CallId))
            {
                results.Add(new TimelineEvent.ToolResultEvent
                {
                    Id = Ids.New(),
                    At = CoreClock.Now(),
                    CallId = plan.Call.CallId,
                    // ⚠️ 内容由**核心**拼（`Reply.Compose`），**不经过模型**——
                    // 这是这一批的重点：让模型「把答案转述一遍」既多一次往返，
                    // 也会让答案在转述里走样。
                    Content = [new Block.TextBlock(reply.Compose())],
                });
                // ⚠️ 它也是**一条结果**，所以也要报（2026-11-05）：这条路上工具一个字都没跑，
                // 而「答完就没下文」正是 `tool.result.appended` 那一批要防的形状。
                onResult?.Invoke(results[^1]);
                continue;
            }

            // ⚠️ **被拒的调用绝不 `RunAsync`**——否则就是「拒了，但还是删了库」。
            // ⚠️ 这一格也是 `ExecutedCallIds` 的唯一写入点：名单在**执行前**记下，
            // 与 `RunAsync` 同一支，不会出现「结果落了、名单没有」或反过来。
            var willRun = plan.Tool is not null && plan.Verdict is Verdict.Allow;
            if (willRun)
            {
                executed.Add(plan.Call.CallId);
                // ⚠️ **报在 `RunAsync` 之前**（2026-11-05）：这条事件的全部价值就是「别等它跑完」。
                // ⚠️ 报的只是**要跑**：被拒 / 不存在的那几支不经过这里（见构造参数那条注释）。
                onCallStarted?.Invoke(plan.Call);
            }

            ToolOutcome outcome;
            try
            {
                outcome = willRun
                    ? await RunWithDeadlineAsync(plan.Tool!, plan.Call, context, ct)
                    : new ToolOutcome { Content = [new Block.TextBlock(plan.Says!)] };
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                // ⚠️ **用户在这一条上打断了**（2026-11-07，用户裁定「工具执行了一半也保留
                // 半截输出」）。从前这里让它原样冒出去，`Stepper` 也就不落盘
                // ⇒ 模型**已经说过的话**与**真的跑过的副作用**一起从记录里消失，
                // 而记录是事实（`01` §4）。现在：这一条收下它交出来的半截（如果有），
                // 其余各条在循环外各补一条「还没开始」，整批照常交给调用方落盘。
                //
                // ⚠️ **判据是 `ct` 的取消，不是「任何 OCE」**：工具自己抛的取消（与用户无关）
                // 仍然是一次失败，不能读成打断。
                interrupted = true;
                cut = ex as ToolInterruptedException;
                break;
            }
            catch (Exception ex)
            {
                // ⚠️ **工具自己抛了异常 ⇒ 这条也是「内容」，不是「整步炸掉」**（2026-11-13，
                // 用户裁定「就算发生报错也保留模型生成结果」）。
                //
                // <para>⚠️ <b>从前它从这里冒出去，后果比「少一条输出」大得多</b>：`Stepper` 的
                // 工具相位**不在**任何 `try` 里 ⇒ 已经跑完的那几条结果、模型那条含 `tool_use`
                // 的消息、以及**真的发生过的副作用**（可能已经写了文件）一起从记录里消失。
                // 下一步的模型以为那一轮从未发生，于是重做一遍（重做一次 `write` / `rm`
                // 不是没有代价的）。而 `01` §3.6 ① 那条配对要求也不谈判：缺一条结果
                // 就等于下一条请求非法。</para>
                //
                // <para>⚠️ **与上面那条 OCE 分界是这个 `when`**：`ct` 被取消 = 用户在按停（那是
                // 打断，整批收尾）；其余异常 = 这次调用失败了 ⇒ 一条结果、接着往下跑。
                // ⚠️ **工具自己抛的 OCE**（与用户无关，比如它自己的超时）走这里 ⇒ 也是内容。
                // 这正是 `ToolOutcome` 那条「失败也是结果，不是异常」在兜底层的实现。</para>
                outcome = new ToolOutcome
                {
                    Content = [new Block.TextBlock(ToolFailureText(plan.Call, ex))],
                    // ⚠️ 无指纹：它**没跑完**，说「世界没动过」是假信息（同超时那条）。
                    Fingerprint = null,
                    DependsOn = null,
                    Framing = new ToolFraming(plan.Call.Name,
                        [new ToolFramingField("error", ex.GetType().Name)]),
                };
            }
            // ⚠️ **两个「工具够不着、但它必须说出来」的出口**（2026-10-24）：与 `Ask`
            // 同一条判据——工具没有 store 的手，落地在核心。⚠️ **只收真正执行过的那些**
            // （`willRun` 那一支）：被闸门拒的调用不产出 `Spawn` / `Tell`，那是它
            // **一次都没跑**的自然结果，不需要额外守卫。
            if (outcome.Spawn is { } spawn) spawns.Add(spawn);
            if (outcome.Tell is { } tell) tells.Add(tell);
            if (outcome.Archive is { } archive) archives.Add(archive);
            // 2026-11-19：第四族——`approve_child` 替孩子放行。同上：工具只说一句话，
            // 清挂起 + 续跑那一步在分派器（那里才有队列与事件总线）。
            if (outcome.Approve is { } approve) childApprovals.Add(approve);

            // ⚠️ **批准时那句附言**：附在这一条的结果后面，**只附一次**（见循环外那段）。
            // ⚠️ 在 `Materialize` **之前**拼：库对象与落盘内容必须与事件里那条逐字一致
            // （`onResult` 交的是记录那一份），事后改内容会让「头看到的」与「盘上的」分叉。
            var content = outcome.Content;
            if (remark is { } note && reply is not null && reply.Covers(plan.Call.CallId))
            {
                content = WithRemark(content, note);
                remark = null;
            }

            results.Add(new TimelineEvent.ToolResultEvent
            {
                // ⚠️ 本记录的稳定 id（与协议 id `CallId` **不是一回事**）。见 `01` §8.0.2 第七节。
                Id = Ids.New(),
                At = CoreClock.Now(),
                CallId = plan.Call.CallId,
                Content = Reclaim.Materialize(content, blobs),
                // ⚠️ 框架**不进 `Materialize`**：它是**事实**（薄薄几个字段），
                // 不是"内容"——把它塞进库会为几十个字节开一个对象，而
                // `Materialize` 的判据（`InlineThresholdBytes`）管的是**字节量**。
                Framing = outcome.Framing,
                DependsOn = outcome.DependsOn,
                Fingerprint = outcome.Fingerprint,
            });
            // ⚠️ **成形一条报一条**（2026-11-05）：一批五条时，第一条的结果不该等第五条文完
            // 才到头上。⚠️ 报的是**记录那一份**（`Materialize` 之后、`AppendEvents` 之前）。
            onResult?.Invoke(results[^1]);
        }

        // ⚠️ **被打断 ⇒ 把这一批补成「每条都有结果」**（2026-11-07）。
        // 不补的话记录里会留下一批悬空的 `tool_use`，而下一次请求直接非法
        // （`01` §3.6 ①）——那比「少了一条结果」坏得多。
            if (interrupted)
        {
            results = CompleteInterrupted(calls, asked, executed, results, cut, onResult);
        }

        return new Batch(results, asks.Count > 0 ? Merge(asks) : null)
        {
            ExecutedCallIds = executed,
            Spawns = spawns,
            Tells = tells,
            Archives = archives,
            Approvals = childApprovals,
            Interrupted = interrupted,
        };
    }

    /// <summary>
    /// **跑一条工具调用，带通用上限**（2026-11-07）。三种出口，**判据只有两条**：
    ///
    /// <list type="number">
    ///   <item>工具自己在时限内回来了（含它自己判的超时、它自己抛的异常）⇒ 原样交回/抛出。
    ///   ⚠️ **用户打断也算这一支**：`ct` 被取消时工具 unwind 出来的 `OperationCanceledException`
    ///   照旧抛上去（`RunStepAsync` 靠它区分「中断」与「失败」）。</item>
    ///   <item>到了通用上限 ⇒ 先把**取消令牌**发下去（工具该照它收拾现场），给
    ///   <see cref="ToolCallLimits.GraceSeconds"/> 收尾。它在这段时间里交回的答案**照样算数**
    ///   ——一个已经写了「被终止、输出不完整」的工具不该被这句兜底盖掉。</item>
    ///   <item>宽限也过完 ⇒ 交一条「超时被终止」的结果，**并丢下那个 task**。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>为什么是「丢下」而不是等它</b>：托管代码里的一个 task 杀不掉。等到它自己结束
    /// 就等于没有上限。⇒ 丢掉是唯一诚实的做法，而那条被丢下的 task 靠我们发下去的取消令牌
    /// 自己退（多数工具会）。⚠️ 它的异常必须**有人观察**，否则会变成一条没人看的
    /// 未观测异常——下面那个 `ContinueWith` 就是干这个的。</para>
    ///
    /// <para>⚠️ <b>它<b>不</b>负责「转后台」</b>：那是拥有进程的工具自己的事
    /// （`shell` / `dotnet_*` 在自己的时限里把活进程交给 `IBackgroundJobs.Adopt`）。
    /// 这一层够不着进程句柄，也**不该**够着——见 `ITool` 那条「工具是纯的」。</para>
    /// </summary>
    private async Task<ToolOutcome> RunWithDeadlineAsync(
        ITool tool, Block.ToolUseBlock call, IToolContext context, CancellationToken ct)
    {
        var arguments = new ToolCall(call.CallId, call.Name, call.Arguments);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_callTimeout);

        var running = tool.RunAsync(arguments, context, deadline.Token);

        // ⚠️ `CancellationToken.None`：这个计时器**不许**被那两把令牌影响，它就是判据本身。
        var expired = Task.Delay(_callTimeout, CancellationToken.None);

        if (await Task.WhenAny(running, expired).ConfigureAwait(false) == running)
            return await running.ConfigureAwait(false);

        // ⚠️ **先分清两种取消**：用户 `interrupt` 也会让 `ct` 触发，而那**不是超时**
        // （`RunStepAsync` 靠 `OperationCanceledException` 把它区分成「中断」而不是「失败」）。
        ct.ThrowIfCancellationRequested();

        // 到点了：先让工具自己收尾。
        await deadline.CancelAsync().ConfigureAwait(false);

        var grace = Task.Delay(TimeSpan.FromSeconds(ToolCallLimits.GraceSeconds), CancellationToken.None);
        if (await Task.WhenAny(running, grace).ConfigureAwait(false) == running)
        {
            try
            {
                return await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 工具是**因为我们的取消**才抛的 ⇒ 这是超时，不是失败。
            }
        }
        else
        {
            // 丢下它，但别让它的异常没人观察（同 `EventBus` 那条「抛出去会变成没人观测的
            // 后台任务异常」）。
            _ = running.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var seconds = (int)_callTimeout.TotalSeconds;
        return new ToolOutcome
        {
            Content =
            [
                new Block.TextBlock(
                    $"'{call.Name}' 超过 {seconds} 秒仍未结束，已被核心终止；这一条没有结果。\n"
                    + "可以缩小范围后重试；需要跑很久的活请让它自己去后台"
                    + "（`shell` / `dotnet_build` / `dotnet_test` 都有 `background` 参数）。"),
            ],
            // ⚠️ 无指纹：它**没跑完**，说「世界没动过」是假信息。
            Fingerprint = null,
            DependsOn = null,
            Framing = new ToolFraming(call.Name, [new ToolFramingField("timeout", seconds.ToString())]),
        };
    }

    /// <summary>
    /// **被打断之后把这一批补全**（2026-11-07，用户裁定「哪怕被打断的时候工具执行了一半，
    /// 也保留半截工具输出」）。
    ///
    /// <para>⚠️ <b>三档结果必须分开写，合成一句就是假信息</b>（`01` §3.6 ①之二）：</para>
    /// <list type="number">
    ///   <item><b>已经跑完的</b>：原样（`results` 里就有）。</item>
    ///   <item><b>跑到一半被打断的</b>：工具交出来的那半截 + `interrupted=partial`。</item>
    ///   <item><b>还没轮到的</b>：一句「还没开始」+ `interrupted=not-started`。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>顺序按调用序重建</b>：`results` 是「跑到哪儿」的顺序，而记录里的次序是
    /// **调用序**（`01` §3.6 ③）。补全这一步是唯一知道「谁缺」的地方，所以次序也在这里定。</para>
    ///
    /// <para>⚠️ **在问的那几条照旧不补**：它们的 `tool_use` 本就该悬着等用户回答
    /// （那是既有语义，见 `asked` 的注释）——给它们补一条「被打断」会让那张卡
    /// **再也等不到答复**（配对已经存在）。</para>
    /// </summary>
    /// <param name="executed">
    /// **真的开始跑过的**调用 id（`Batch.ExecutedCallIds` 那份，执行前就记下了）。
    /// ⚠️ 它是「跑到一半」与「还没开始」的**唯一判据**：名单里的那条被打断时**已经在跑了**
    /// （只是没交出任何东西），名单外的那些**一次都没轮到**。两种情形各说各的话——
    /// 把前者说成「还没开始」会抹掉「它可能已经动过世界」这件事。
    /// </param>
    private List<TimelineEvent.ToolResultEvent> CompleteInterrupted(
        IReadOnlyList<Block.ToolUseBlock> calls,
        HashSet<string> asked,
        IReadOnlyList<string> executed,
        List<TimelineEvent.ToolResultEvent> results,
        ToolInterruptedException? cut,
        Action<TimelineEvent.ToolResultEvent>? onResult)
    {
        var byCall = new Dictionary<string, TimelineEvent.ToolResultEvent>(StringComparer.Ordinal);
        foreach (var result in results) byCall[result.CallId] = result;

        if (cut is not null)
        {
            IReadOnlyList<Block> content = cut.Partial is { Content.Count: > 0 } partial
                ? Reclaim.Materialize(partial.Content, blobs)
                // ⚠️ 跑了但一个字都还没吐出来 —— 与「还没开始」分开说（后者见下面那支）。
                : [new Block.TextBlock(Interruption.CutText(""))];

            byCall[cut.CallId] = new TimelineEvent.ToolResultEvent
            {
                Id = Ids.New(),
                At = CoreClock.Now(),
                CallId = cut.CallId,
                Content = content,
                // ⚠️ 无指纹：它没跑完，说「世界没动过」是假信息（同超时那条）。
                Fingerprint = null,
                DependsOn = null,
                Framing = new ToolFraming(
                    calls.FirstOrDefault(c => string.Equals(c.CallId, cut.CallId, StringComparison.Ordinal))?.Name ?? "",
                    [new ToolFramingField("interrupted", "partial")]),
            };
            onResult?.Invoke(byCall[cut.CallId]);
        }

        var completed = new List<TimelineEvent.ToolResultEvent>(calls.Count);
        foreach (var call in calls)
        {
            if (asked.Contains(call.CallId)) continue;

            if (byCall.TryGetValue(call.CallId, out var already))
            {
                completed.Add(already);
                continue;
            }

            var filler = new TimelineEvent.ToolResultEvent
            {
                Id = Ids.New(),
                At = CoreClock.Now(),
                CallId = call.CallId,
                Content =
                [
                    new Block.TextBlock(
                        // ⚠️ **「跑到一半」与「还没开始」分开说**（见 `executed` 那条注释）。
                        executed.Contains(call.CallId)
                            ? Interruption.CutText("")
                            : Interruption.UnrunText()),
                ],
                Framing = new ToolFraming(call.Name,
                [
                    new ToolFramingField("interrupted",
                        executed.Contains(call.CallId) ? "partial" : "not-started"),
                ]),
            };
            // ⚠️ 合成的一条也要报：不报的话头上那条调用会永远停在「执行中」
            // ——而那正是这一批要修的形状（`tool.call.started` 已经发出去了）。
            onResult?.Invoke(filler);
            completed.Add(filler);
        }

        return completed;
    }

    /// <summary>
    /// **一趟执行的两个产出（`01` §3.8）。
    ///
    /// <para>⚠️ <b>它们**不是**互斥的</b>（2026-10-29 改，而这一格是那条死循环的一半）：
    /// <see cref="Results"/> 非空、<see cref="Pending"/> 也非 null ⇒ 这是<b>答复那一步</b>
    /// ——已经答到的那几条跑完了（结果在这里），而**这一轮才要问**的那几条还没配对
    /// （等用户回答）。⚠️ <b>新的一步里仍然互斥</b>（`reply == null` ⇒ 有问则一条都不跑），
    /// 因为那时一条都还没执行过，而「记录里出现半批」正是要防的东西。</para>
    /// </summary>
    /// <param name="Results">
    /// 按调用序的结果。⚠️ 新的一步里有问时恒空；答复那一步里可以非空（见类型注释）。
    /// ⚠️ <b>被问的那几条在这里<b>没有</b>结果</b>——它们的 `tool_use` 继续悬着，等回答。
    /// </param>
    /// <param name="Pending">要问用户的那件事。⚠️ <c>null</c> = 没问。</param>
    public sealed record Batch(
        IReadOnlyList<TimelineEvent.ToolResultEvent> Results, AskRequest? Pending)
    {
        /// <summary>
        /// 本批**真的执行过**的调用 id（`01` §12.4 的线要它）。
        ///
        /// <para>⚠️ <b>它存在，是因为「名字相同」与「真的执行了」不是一回事</b>：
        /// 一条 `release` 可能被闸门硬拒、可能落在挂起的那批里、也可能只配了
        /// 一条「这批不跑了」的补丁结果——那三种情况下**都没有画过线**。
        /// 若推进器只看「本批里有没有叫 release 的调用」就画，那么一次被拒绝的
        /// `off` 档调用、一次从未执行的恢复批次，会各留下一条线
        /// （而线是**单调的判据**：它会把那些结果永久收掉）。</para>
        ///
        /// <para>⚠️ <b>它是执行器唯一知道、而调用方无法重建的事实</b>：拒绝理由与补丁正文
        /// 都是内容，但「这条到底跑没跑」只有这一步知道。⇒ 由执行器交回，而不是让
        /// 推进器拿结果去猜。⚠️ 只有真正执行的分支会填它；拒绝 / 不存在 / 被这一轮问住的 /
        /// 提问答复（答案由核心拼，工具没跑）**一律为空**。
        /// ⚠️ 答复那一步可以<b>同时</b>有内容与一个 <see cref="Pending"/>（已答到的那几条跑了，
        /// 这一轮才要问的那几条没跑）：名单只说**跑过的**，所以推进器照它落地不会漏也不会多。</para>
        /// </summary>
        public IReadOnlyList<string> ExecutedCallIds { get; init; } = [];

        /// <summary>
        /// 本批执行出来的**派生请求**（`spawn`，2026-10-24）。
        ///
        /// <para>⚠️ 与 <see cref="ExecutedCallIds"/> 同一条纪律：只有**真的跑过**的工具
        /// 才产出它（被闸门拒的那条不打这条通道）。⇒ 推进器不需要再判一次「它跑没跑」。</para>
        /// </summary>
        public IReadOnlyList<SpawnRequest> Spawns { get; init; } = [];

        /// <summary>
        /// 本批执行出来的**注入请求**（`tell_child` / `tell_parent`，2026-10-24）。
        ///
        /// <para>⚠️ 按**产出序**（= 调用序）排：父对同一个孩子连说两句时，那两句的次序
        /// 就是记录里的次序——而记录里的次序是事实（`01` §4）。</para>
        /// </summary>
        public IReadOnlyList<TellRequest> Tells { get; init; } = [];

        /// <summary>
        /// 本批执行出来的**归档请求**（`archive_child`，2026-10-26）。
        ///
        /// <para>⚠️ 与 <see cref="Spawns"/> / <see cref="Tells"/> 同一条纪律：只有**真的跑过**
        /// 的工具才产出它——被闸门拒的那条调用不打这条通道，所以分派器不需要再判一次
        /// 「它跑没跑」。⚠️ 按产出序（= 调用序）排：同一步里先后收起两个孩子的次序是事实。</para>
        /// </summary>
        public IReadOnlyList<ArchiveRequest> Archives { get; init; } = [];

        /// <summary>
        /// 本批执行出来的**放行请求**（`approve_child`，2026-11-19）。
        ///
        /// <para>⚠️ 与 <see cref="Spawns"/> / <see cref="Tells"/> / <see cref="Archives"/> 同一条纪律：
        /// 只有**真的跑过**的工具才产出它（被闸门拒的那条不打这条通道）。
        /// ⚠️ 与 <see cref="Tells"/> 分开是刻意的：那个只往孩子的记录里加一句话，
        /// 这个会让被挡下的那条调用**真的执行**——落地次序也因此不同（见 `DeliveryOrder`）。</para>
        /// </summary>
        public IReadOnlyList<ApproveChildRequest> Approvals { get; init; } = [];

        /// <summary>
        /// **这一批被用户打断了**（2026-11-07）。
        ///
        /// <para>⚠️ <b>它不是异常，而是一个正常返回的批次</b>——这是这一批改动的核心：
        /// 「被打断」也是一种**有结果**的结局（跑完的那几条 + 半截的那条 + 还没开始的那几条），
        /// 而异常会把它们全丢掉。调用方（`Stepper`）据此落盘并**不再续跑**。</para>
        ///
        /// <para>⚠️ <b>别把它读成「这些结果可以当正常结果用」</b>：里面至少有一条是
        /// 半截或没跑。区分靠每条结果自己的 <c>Framing</c>（`interrupted=partial` / `not-started`）。</para>
        /// </summary>
        public bool Interrupted { get; init; }
    }

    /// <summary>
    /// 把同一批里**多条**询问并成**一次**（`01` §3.8：「一次询问要能够扔出来好几个问题」）。
    ///
    /// <para>⚠️ <b>Kind 相同才并</b>：一个 `Permission` 与一个 `Question` 混在一张卡上，
    /// 用户分不清「我答的是不是要它去做」——而那个区别是**整套设计的分界线**。
    /// 今天两条路不会同时出现（`Permission` 只来自闸门、`Question` 只来自 `ask` 工具），
    /// 但**判据必须写下来**，否则将来放宽时没人知道它曾经是个约束。
    /// ⚠️ 真出现混合时按**先到的那个 Kind** 走，剩下的留到**下一张卡**
    /// （它们的 `tool_use` 还没配对，下一趟会再问一次）。⚠️ 而「留到下一张卡」只在
    /// **答复那一步不再整批作废**时才成立——2026-10-29 之前它不成立，于是混合批次
    /// 变成「反复问同一句」（见 <see cref="ExecuteAsync"/> 第三趟那条注释）。</para>
    /// </summary>
    private static AskRequest Merge(IReadOnlyList<AskRequest> asks)
    {
        var kind = asks[0].Kind;
        var same = asks.Where(a => a.Kind == kind).ToList();

        return new AskRequest
        {
            Id = Ids.New(),
            Kind = kind,
            Items = [.. same.SelectMany(a => a.Items)],
            // ⚠️ 记忆档位取**并集**：同一批里两个调用的可记忆性不同，而卡片只画一次。
            Remember = [.. same.SelectMany(a => a.Remember).Distinct()],
            // ⚠️ **盖住的调用也取并集**（2026-10-29）：一张卡同时替这几条调用问的，
            // 答完要盖住的正是它们——而「这一批里的第一条」不是答案（见 `AskRequest.CallIds`）。
            CallIds = [.. same.SelectMany(a => a.CallIds).Distinct(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// 把用户那句**附言**拼进一条结果的内容末尾（授权卡批准时用，`01` §3.8）。
    ///
    /// <para>⚠️ <b>拼的是内容，不是新的一条结果</b>：「一条 `tool_use` 对一条 `tool_result`」
    /// 是结构性的（`01` §3.6 ①）——多造一条结果就等于给同一个调用配了两次对。</para>
    ///
    /// <para>⚠️ <b>末尾是文本块就接在它后面，不是就另起一块</b>：另起一块会让本来能存成
    /// 纯文本（`text/plain`）的大结果退化成块数组 JSON（见 `Reclaim.Materialize` 的分流），
    /// 而那纯属排版事故。⚠️ 前面空两行：工具输出与「用户说的」之间必须看得出分界，
    /// 否则那句话会被读成工具自己说的。</para>
    /// </summary>
    private static IReadOnlyList<Block> WithRemark(IReadOnlyList<Block> content, string remark)
    {
        var line = "\n\n" + AskAnswers.RemarkLine(remark);

        var blocks = content.ToList();
        if (blocks.Count > 0 && blocks[^1] is Block.TextBlock last)
            blocks[^1] = last with { Text = last.Text + line };
        else
            blocks.Add(new Block.TextBlock(line));

        return blocks;
    }

    /// <summary>第一趟的产物：一条调用 + 它该走哪条路。</summary>
    /// <param name="Says">不该执行时那条要说的话（工具不存在 / 被拒）。</param>
    private sealed record Plan(Block.ToolUseBlock Call, ITool? Tool, Verdict Verdict, string Says);
}

/// <summary>
/// **一次工具调用跑到一半被用户打断了，而它已经有半截输出**（2026-11-07）。
///
/// <para>⚠️ <b>为什么用异常带内容，而不是让工具返回一条「被打断」的结果</b>：返回一条正常结果
/// 意味着「这条结束了，接着跑下一条」——而打断意味着**后面一条都不该跑**。
/// 异常是唯一能同时表达「停下」与「这是我已经产出的东西」的东西。</para>
///
/// <para>⚠️ <b>它必须是 <see cref="OperationCanceledException"/> 的子类</b>：整条链上
/// （`ToolExecutor` → `Stepper` → `CommandDispatcher`）认「用户打断」的判据就是那个类型，
/// 换一个正交的类型会让打断在某一层变成「失败」（`error` 事件、头上一片红）。</para>
///
/// <para>⚠️ <b>只有<有进程的工具>会抛它</b>（`shell` / `dotnet_*` —— 它们手上有活着的子进程，
/// 能说清「到这一刻为止输出了什么」）。纯读的工具被取消时没有半截可言，
/// 由 <see cref="ToolExecutor"/> 自己补一条「跑到一半、没有输出」的结果。</para>
/// </summary>
/// <param name="callId">哪一条调用（协议 id）。⚠️ 执行器靠它把半截放回**正确的那一格**。</param>
/// <param name="partial">到这一刻为止的产出。⚠️ 内容由工具给（只有它知道自己的输出长什么样）。</param>
public sealed class ToolInterruptedException(string callId, ToolOutcome? partial, CancellationToken token)
    : OperationCanceledException($"工具调用 {callId} 被中断了。", token)
{
    public string CallId { get; } = callId;

    public ToolOutcome? Partial { get; } = partial;
}

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
public sealed class ToolExecutor(
    IReadOnlyList<ITool> tools,
    Storage.BlobStore blobs,
    string? permissionLevel,
    ApprovalStore? approvals = null,
    string? mode = null)
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

        // ---- 第二趟：收问。 ----
        foreach (var plan in plans)
        {
            // 不存在的工具与硬拒：它们**不问**，理由在 `Says` 里。
            if (plan.Tool is null || plan.Verdict is Verdict.Deny) continue;

            // ⚠️ 已答复过的调用不再收问（它与上面那条跳闸门是同一条判据的两半）。
            if (reply is not null && reply.Covers(plan.Call.CallId)) continue;

            if (plan.Verdict is Verdict.Ask)
            {
                asks.Add(Gate.AskFor(plan.Tool, plan.Call, permissionLevel));
                continue;
            }

            // ⚠️ **声明了「执行前要问」的工具，在这里跑一遍、只取它的问题**。
            // 跑它**没有副作用**——那正是它敢声明这个的前提（见 `ITool.AsksBeforeRunning`）。
            if (plan.Tool.AsksBeforeRunning)
            {
                var outcome = await plan.Tool.RunAsync(
                    new ToolCall(plan.Call.CallId, plan.Call.Name, plan.Call.Arguments), context, ct);

                if (outcome.Ask is { } ask) asks.Add(ask);
            }
        }

        // ---- 第三趟：有问 ⇒ 一条都不跑；否则全部执行。 ----
        if (asks.Count > 0) return new Batch([], Merge(asks));

        var results = new List<TimelineEvent.ToolResultEvent>(calls.Count);

        // ⚠️ **工具声明、核心落地**的两族（2026-10-24）：`spawn` 派生、`tell_*` 注入。
        // 与 `Ask` 同一条判据——工具没有 store 的手，而这两件事都要碰会话。
        var spawns = new List<SpawnRequest>();
        var tells = new List<TellRequest>();
        // 2026-10-26：第三族——`archive_child` 收起。同上：工具只说一句话，写 spec 在分派器。
        var archives = new List<ArchiveRequest>();

        // ⚠️ **只有走到下面那个 `RunAsync` 的调用才进来**——「名字在批里」不足以说明
        // 「它执行过」（见 `Batch.ExecutedCallIds`）。`release` 的线按这份名单画。
        var executed = new List<string>(calls.Count);
        foreach (var plan in plans)
        {
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
                continue;
            }

            // ⚠️ **被拒的调用绝不 `RunAsync`**——否则就是「拒了，但还是删了库」。
            // ⚠️ 这一格也是 `ExecutedCallIds` 的唯一写入点：名单在**执行前**记下，
            // 与 `RunAsync` 同一支，不会出现「结果落了、名单没有」或反过来。
            var willRun = plan.Tool is not null && plan.Verdict is Verdict.Allow;
            if (willRun) executed.Add(plan.Call.CallId);

            var outcome = willRun
                ? await plan.Tool!.RunAsync(
                    new ToolCall(plan.Call.CallId, plan.Call.Name, plan.Call.Arguments), context, ct)
                : new ToolOutcome { Content = [new Block.TextBlock(plan.Says!)] };

            // ⚠️ **两个「工具够不着、但它必须说出来」的出口**（2026-10-24）：与 `Ask`
            // 同一条判据——工具没有 store 的手，落地在核心。⚠️ **只收真正执行过的那些**
            // （`willRun` 那一支）：被闸门拒的调用不产出 `Spawn` / `Tell`，那是它
            // **一次都没跑**的自然结果，不需要额外守卫。
            if (outcome.Spawn is { } spawn) spawns.Add(spawn);
            if (outcome.Tell is { } tell) tells.Add(tell);
            if (outcome.Archive is { } archive) archives.Add(archive);

            results.Add(new TimelineEvent.ToolResultEvent
            {
                // ⚠️ 本记录的稳定 id（与协议 id `CallId` **不是一回事**）。见 `01` §8.0.2 第七节。
                Id = Ids.New(),
                At = CoreClock.Now(),
                CallId = plan.Call.CallId,
                Content = Reclaim.Materialize(outcome.Content, blobs),
                // ⚠️ 框架**不进 `Materialize`**：它是**事实**（薄薄几个字段），
                // 不是"内容"——把它塞进库会为几十个字节开一个对象，而
                // `Materialize` 的判据（`InlineThresholdBytes`）管的是**字节量**。
                Framing = outcome.Framing,
                DependsOn = outcome.DependsOn,
                Fingerprint = outcome.Fingerprint,
            });
        }

        return new Batch(results, null)
        {
            ExecutedCallIds = executed,
            Spawns = spawns,
            Tells = tells,
            Archives = archives,
        };
    }

    /// <summary>
    /// 一趟执行的两个产出（`01` §3.8）。⚠️ <b>两者互斥</b>：
    /// <see cref="Pending"/> 非 null ⇒ <see cref="Results"/> 必然是空的（一条都没跑）。
    /// <para>⚠️ 挂起时 <see cref="Batch.ExecutedCallIds"/> 也是空的：那一趟**一条都没跑**。</para>
    /// </summary>
    /// <param name="Results">按调用序的结果。⚠️ 有问时恒空——见类型注释。</param>
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
        /// 推进器拿结果去猜。⚠️ 只有真正执行的分支会填它；拒绝 / 不存在 / 挂起 / 提问答复
        /// （答案由核心拼，工具没跑）**一律为空**。</para>
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
    }

    /// <summary>
    /// 把同一批里**多条**询问并成**一次**（`01` §3.8：「一次询问要能够扔出来好几个问题」）。
    ///
    /// <para>⚠️ <b>Kind 相同才并</b>：一个 `Permission` 与一个 `Question` 混在一张卡上，
    /// 用户分不清「我答的是不是要它去做」——而那个区别是**整套设计的分界线**。
    /// 今天两条路不会同时出现（`Permission` 只来自闸门、`Question` 只来自 `ask` 工具），
    /// 但**判据必须写下来**，否则将来放宽时没人知道它曾经是个约束。
    /// ⚠️ 真出现混合时按**先到的那个 Kind** 走，剩下的自然留到下一张卡
    /// （它们的 `tool_use` 也还没配对，下一趟会再问一次）。</para>
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
        };
    }

    /// <summary>第一趟的产物：一条调用 + 它该走哪条路。</summary>
    /// <param name="Says">不该执行时那条要说的话（工具不存在 / 被拒）。</param>
    private sealed record Plan(Block.ToolUseBlock Call, ITool? Tool, Verdict Verdict, string Says);
}

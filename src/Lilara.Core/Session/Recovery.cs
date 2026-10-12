using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// **收尾**——`01` §12.8 的崩溃语义落到代码上的那一个动作。
///
/// <para>⚠️ <b>它回答的问题是「上一次那一步算不算跑过」</b>，而答案只能从**记录**里读。
/// 三条来路（超时 / 强杀 / 断电）各有各的触发条件，而**收尾动作只有一件**：
/// 补一条说得清的结果（工具调用那条 = 一条 <c>tool_result</c>；询问那条 = 一条合成答复）。</para>
///
/// <para>⚠️ <b>判据必须比<b>记录</b>，不能比渲染</b>：`TimelineRender` 会把已回收的结果换成
/// <b>注记</b>、并给每条结果<b>加一块框架</b> ⇒ 拿渲染后的那份去判配对，看见的是<b>假的</b>。
/// 同仓那条「探针 ≠ 被测物」在这里的形态。</para>
///
/// <para>⚠️ <b>它<b>不</b>做「重启之后接着跑」</b>：补配对是为了让**下一次请求合法**，
/// 而那次模型调用**不重放**。理由见 `01` §12.8 的记账——两条实现各有硬代价。</para>
/// </summary>
public static class Recovery
{
    /// <summary>
    /// **这一步开始之前，先把上一次留下的东西收掉。**
    ///
    /// <para>⚠️ <b>它住这里，是因为装配请求的地方只有一处</b>（`Stepper.StepAsync`）
    /// ——三个适配器各补一次等于把同一条规则抄三遍，而抄漏一个的后果是
    /// **那一条协议上，那个会话从此发不出消息**（上游 400，且报的是「配对」这种
    /// 与真因隔了一层的话）。</para>
    ///
    /// <para>⚠️ <b>两种漏法都要收</b>，而它们<b>不是</b>同一类问题（这就是「同一个函数、
    /// 两条判据」的形状）：
    /// <list type="number">
    ///   <item><b>工具调用缺配对</b> —— 终局脏状态。判据是
    ///   <see cref="Interruption.WasInterrupted"/>（或 `PendingIntent` 说「我可能欠一条」）。</item>
    ///   <item><b>询问到期</b> —— 一个**临时**状态（用户随时可能答）。⚠️ 展开这一步已经
    ///   决定了「现在没有别的路径能挽救它」（§13 会话内严格串行：用户答复会被闸挡在外面），
    ///   所以此刻合成答复是安全的、且**必须**在此刻做：晚一格，`Ask` 与内存那份就可能错位。</item>
    /// </list></para>
    ///
    /// <para>⚠️ <b>崩在两次写之间的那个窗口</b>：记录与 `state.json` 是两次独立写，
    /// 而 `PendingIntent` <b>记下来了</b>（它是**盘上**的，所以重启之后还在）⇒
    /// 那一步算不算跑过、欠哪一条，重启之后照样答得出来。见 `SessionState.PendingIntent`。</para>
    /// </summary>
    /// <param name="hasNewInput">
    /// 这一步**带着新的用户输入**吗（队列里真的取到了块）。
    ///
    /// <para>⚠️ <b>它是 ① 那支的闸，而这条闸是有判据的、不是权宜</b>：`01` §5 说
    /// 「超时/断裂由**任何试图恢复这个会话的动作**发现」，而那个动作的判据是
    /// **有人真的在使用它**——用户发了话。⚠️ 少了这条闸，**一次空推进**
    /// （工具循环的续跑、或某个后台步的尾巴）也会去补配对：
    /// 那种步会把「上一次挂起时留下的那条悬空」替用户答掉，而用户根本没说话。
    /// ⚠️ 实测的形状（本批）：`AskTests` 里一条 `send` 后起的后台步可能在
    /// 「挂起 → 答复」之后才跑到，于是它补了第二条结果
    /// （`R:c2` 出现两次，其中一条 id 带 `interrupt-`）。</para>
    ///
    /// <para>⚠️ <b>② 那支不吃这条闸</b>：询问到期要**任何**一步都发现得了它，
    /// 否则一个只挂着的会话永远等不到下一次 `send`（而那正是它唯一的唤醒来源之一）。</para>
    /// </param>
    /// <returns>补了几条记录行（调用方拿它决定值不值得说一句话）。</returns>
    public static int BeforeStep(
        SessionStore store, SessionHandle handle, DateTimeOffset now, bool hasNewInput)
    {
        var lines = new List<TimelineEvent>();
        var behind = false;

        // ---- ① 工具调用缺配对 ----
        //
        // ⚠️ <b>判据<b>不是</b>「扫一遍记录看有没有悬空」</b>：末尾那一条**永远**是悬空的
        // （`Interruption` 的规则），而**用户刚发完话、这一步还没跑**正是那个形状
        // ⇒ 那样每发一条消息都会凭空多补一条结果。
        //
        // ⚠️ <b>判据是「记录比 `state.Turn` 多走了一格」——而那个形状有两种来历，
        // 它们是同一件事的两半，别只判一半</b>：
        //   · <b>崩在步中</b>（记录已落、`state` 未写）⇒ 多出来的正是那一步；
        //   · <b>挂起中崩</b>（`Ask` 写了、而挂起那条路的两次写只完成了一次）⇒ 同一种不一致。
        // ⚠️ **两种都指向同一个结论：欠一条结果。** 而**欠哪一条**从**记录末尾**解
        // （不读 `PendingIntent` 里那个 `CallId`）：挂起中崩那种情形下痕迹可能根本不在盘上，
        // 而末尾那条悬空的调用**就是**我们欠的那一条。
        // ⇒ 那个字段只用来**回答「有没有欠」**（它写下来的那一刻两处都还没写全），
        // 而 `Turn` 与条数的比对是**独立**的第二重判据——两者都真才动手。
        var timeline = store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var steps = timeline.Count(e => e is not TimelineEvent.ToolResultEvent);
        var lastCall = timeline
            .OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .LastOrDefault();

        // ⚠️ **只认「正好落后一格」**：差两格以上说明那是别的东西（手改过的记录、
        // 或更老的格式），不替它编一个解释、也不动 `Turn`。
        //
        // ⚠️ **判据是「记录里比 `Turn` 多走的**那一格**里真的悬着一条调用**」
        // （`Interruption` 那条规则，用的是**同一个谓词**）——`lastCall` 只是候选。
        // ⚠️ 而**`Turn` 要补到「记录里的步数」**（不是 `+1`）：收尾之后两者的差应当是 0，
        // 否则**下一步又会看见同一个错位**，于是每步补一条（实测：每步一条，累积）。
        var dangling = lastCall is not null
            && Interruption.WasInterrupted(timeline, lastCall.CallId, Interruption.NoteIdPrefix)
            ? lastCall.CallId : null;

        if (hasNewInput && handle.State.PendingIntent is not null
            && dangling is not null && steps > handle.State.Turn)
        {
            behind = true;
            lines.Add(new TimelineEvent.ToolResultEvent
            {
                // ⚠️ **id 带前缀**：它是「这条是我补的」的唯一判据（见 `Interruption.NoteIdPrefix`）。
                // 用一个普通 id 会让这一步看起来**真的跑完了**——那是 §3.6 ①之二
                // 警告的「多了一条假信息」。
                Id = Interruption.NoteIdPrefix + Ids.New(),
                At = now,
                // ⚠️ **配对键取那条**真的悬空**的调用**（由 `WasInterrupted` 判出来），
                // 不是「最后一条 `tool_use`」——两者在「一条消息里两条调用」时**不是同一条**
                // （`StepperToolTests` 断的就是那个形状）。
                CallId = dangling,
                Content = [new Block.TextBlock(Interruption.AbortText())],
            });
        }
        else
        {
            behind = false;
        }

        // ---- ② 询问到期（`01` §5 / §3.8）----
        //
        // ⚠️ **它排在①之后，而两者可能同时成立**（挂起时崩过、而 `deadline` 也刚过）。
        // 那种情形下**两件都要做**：①把那条断裂补上、②把这次询问合成答复——
        // 少了任何一件，那条悬空的 `tool_use` 都还在，下一条请求照样 400。
        if (handle.State.Status == SessionStatus.WaitingForInput
            && handle.State.Ask is { } ask
            && ask.DeadlineAt is { } deadline && now >= deadline)
        {
            var calls = Stepper.RecordedCalls(store, handle.ProjectKey, handle.SessionId, ask.CallId);

            // ⚠️ **走到这里 ⇒ 那一条一定还没配对**（会话挂着的整段时间里没有东西在推进它）。
            // 直接落，不调 `WasInterrupted`（那个在挂起态下会答错，见它的注释）。
            store.AppendEvent(handle.ProjectKey, handle.SessionId,
                Stepper.Answer(ask, TimeoutText(ask.Kind, handle.Spec.AskTimeout)));

            // ⚠️ **其余那些调用也要各补一条**（`Stepper.Blocked`，与 `approval.respond`
            // 那三条收尾**同一个函数**）：一条询问是一个信封，而那张卡可能盖着好几条调用
            // ⇒ 只配一条会让其余的悬着，而悬空的 `tool_use` 会让下一条请求 400。
            if (calls.Count > 1)
                store.AppendEvents(handle.ProjectKey, handle.SessionId, Stepper.Blocked(
                    calls, first: 1,
                    "（这一批里的这次调用和它一起被作废了：等待超时，没有人回答。）"));

            handle.AwaitingInput = null;
            handle.UpdateStateAndPersist(store, s => s with
            {
                Status = SessionStatus.Idle,
                Ask = null,
                PendingIntent = null,
                Turn = behind ? steps : s.Turn,
            }, diskStatus: SessionStatus.Idle);

            return lines.Count + (calls.Count > 1 ? calls.Count : 1);
        }

        if (lines.Count == 0) return 0;

        store.AppendEvents(handle.ProjectKey, handle.SessionId, lines);

        // ⚠️ **记录先落、痕迹后清**（顺序不能反）：崩在中间会让下一次收尾**看不见**这条欠账
        // （痕迹没了）——而那正是它存在的理由。⚠️ 而**那条记录在盘上** ⇒ 下一次
        // 走的是「记录比 `Turn` 多一格」那一半，照样收得掉。
        handle.UpdateStateAndPersist(store, s => s with
        {
            Turn = behind ? steps : s.Turn,
            PendingIntent = null,
        }, diskStatus: handle.State.Status);

        return lines.Count;
    }

    /// <summary>
    /// 超时时那条结果（`01` §3.8）。⚠️ <b>两种 Kind 的后果不同</b>（用户裁定）：
    /// 授权默认**拒绝**；问意见返回一句「没有人回答」。
    ///
    /// <para>⚠️ <b>它与 `CommandDispatcher.TimeoutText` 是同一句话的两个副本</b>
    /// ——那是**刻意**的暂时形态：那一处的调用点（`approval.respond`）走的是
    /// 「用户答复来晚了」那条路，措辞是「等你回答了 X，没有人回答」；
    /// 这一处是**真的没人答**。两句说的是不同的事，**别合并**。</para>
    /// </summary>
    private static string TimeoutText(AskKind kind, TimeSpan? timeout)
    {
        var waited = timeout is { } t ? t.TotalMinutes.ToString("0.#") + " 分钟" : "一段时间";

        return kind == AskKind.Permission
            ? $"这次调用被拒绝了：等了 {waited}，没有人回答。"
            : $"没有人回答（等了 {waited}）。";
    }
}

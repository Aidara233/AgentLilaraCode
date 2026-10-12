namespace Lilara.Core.Kernel;

/// <summary>
/// **打断的识别**——「这个会话在某一步的中间停过」这件事，怎么从**记录**里读出来。
///
/// <para>⚠️ <b>它存在的理由是把三件看着无关的事收成一条规则</b>（`01` §5 / §6.1 / §8.0.2）：
/// <b>询问超时</b> · <b>进程被强杀 / 断电</b> · <b>会话被踢出内存</b>。
/// 三者的触发条件各不相同，而**盘上留下的痕迹是同一种**：
/// <i>一条 <c>tool_use</c> 没有配对的结果</i>。</para>
///
/// <para>⚠️ <b>判据是「记录里有没有配对」，不是「时间过了多久」。</b>
/// 时间那半在这里**根本算不出来**：超时是**盘面自证**（`01` §3.8：没有定时器在等），
/// 而「这一刻没有任何东西在替这个会话往下走」才是超时、强杀、离线三者共有的那个事实
/// ——所以它们不是「恰好」落进同一个恢复动作，是**同一条规则**。</para>
///
/// <para>⚠️ <b>两个死法在协议上同形，如实记下</b>：超时那条与崩那条都只声明
/// 「这条调用没有执行」，**核心侧没有各自的标记**——Claude 的 <c>is_error</c> 今天也
/// 不发（本批只落既有的 <c>tool_result</c> 形状，不自造区分）。</para>
/// </summary>
public static class Interruption
{
    /// <summary>
    /// **收尾补出来的那条结果，`Id` 带这个前缀。**
    ///
    /// <para>⚠️ <b>它必须存在，而且只能由这里定</b>（`00` §7 判据 5「被消费的 id 由选择那一刻决定」）：
    /// 没有它，<i>「这条结果是不是我上次补的」</i>就无从判别，于是
    /// <see cref="WasInterrupted"/> 会把**自己上一次的收尾**当成一次正常配对，
    /// 而**真正悬着的那条照样悬着** ⇒ 规则自己把自己关掉。</para>
    ///
    /// <para>⚠️ <b>反过来也成立，而那是最坏的失败</b>：若收尾补出来的结果**不带前缀**，
    /// 它看起来就是一条正常的工具结果 ⇒ 那一步会被读成「跑完了」，
    /// **而工具一次都没跑**——正是 `01` §3.6 ①之二警告的「最坏的失败不是少了信息，
    /// 是<b>多了一条假信息</b>」。</para>
    /// </summary>
    public const string NoteIdPrefix = "interrupt-";

    /// <summary>
    /// **那条结果：说清「这条调用没有执行」**（`01` §3.8 的合成结果，与
    /// <c>Stepper.Supersede</c> / <c>TimeoutText</c> 同族）。
    ///
    /// <para>⚠️ <b>三个死法共用这一句话，是刻意的</b>：超时 / 强杀 / 断电在**这件事上**是同一件
    /// ——那次调用没有结果。而**分别措辞**会让模型以为三者有行为上的差别（它读得到的只有这句话），
    /// 于是它会去猜「超时是不是意味着我可以重试」。</para>
    ///
    /// <para>⚠️ <b>它<b>不</b>说「你可以重发」</b>：对 <c>write</c> / <c>update</c> 这类调用，
    /// 重发可能是**第二次副作用**。模型的下一步归模型判断，核心只负责让「缺失」变得可见。</para>
    /// </summary>
    public static string AbortText() =>
        "[lilara 框架] 上一次这一步在这里被打断了——这条调用没有执行，也没有返回任何内容。"
        + "（这一步之前的内容都还在，可以接着往下做。）";

    /// <summary>
    /// **这条调用是不是在一次断裂里悬着**——`01` §3.6 的配对是结构性的，
    /// 所以「缺了一半」这件事从记录里读得出来。
    ///
    /// <para><b>规则一句话</b>：一条 <c>tool_use</c> 没有配对的结果，
    /// 且<b>把这条调用记下来之后</b>遇到的下一条消息不是它的结果，就是一次断裂。
    /// <b>末尾那一条永远是断裂</b>——不管多旧（那正是「重启之后接着跑」要用的钩子）。</para>
    ///
    /// <para>⚠️ <b>算法是正向扫描，不假设结果先落</b>：结果与调用在同一个 <c>AppendEvents</c> 批次里，
    /// 而<b>批次的落盘顺序没有保证</b>（本批实测确认了这条既有注释：两个 `Parallel` 用例断言
    /// <c>.Result</c> 时输出为空、**变异存活**——要用
    /// `TaskContinuationOptions.ExecuteSynchronously` 才拿得到确定序）。
    /// ⇒ 一次正向遍历同时记下「谁被消费了」与「这条调用在第几条消息」。</para>
    ///
    /// <para>⚠️ <b>「前一次断裂的解析不再算第二次」是这条规则的机制，不是一条纪律</b>：
    /// 扫描<b>到那条调用就返回</b>，后面一律不看 ⇒ 收尾补上去的结果（它排在后面）
    /// 自然不参与判定。「前一次断裂的补丁不再算数但调用仍是断裂的」这条**靠两件事成立**：
    /// ① 那次 `Reconcile` 与结果落盘在**同一个临界区**里（`SessionHandle.UpdateStateAndPersist`），
    /// ② 它**清掉 `PendingIntent`** ⇒ 不会有人拿旧观测量再判一次。
    /// 删掉那个 <c>return</c> 的后果是<b>二次恢复叠加</b>：每次收尾都把上一次的结果当新配对，于是再补一条。</para>
    /// </summary>
    /// <param name="timeline">**记录**（`SessionStore.ReadTimeline` 那份）。⚠️ 不是渲染后的那份
    /// ——渲染<b>会把结果换成注记</b>，于是判据看见的配对是假的。</param>
    /// <param name="callId">要判的那条调用的协议 id（`ToolUseBlock.CallId`）。</param>
    /// <param name="resultIdPrefix">收尾补出来的那种结果的 id 前缀（生产调用点传
    /// <see cref="NoteIdPrefix"/>）。⚠️ <b>当参数的唯一理由是可测</b>——同
    /// <c>SessionHandle.Create</c> 那个 <c>sessionId</c>；生产调用点<b>永远</b>传常量。</param>
    public static bool WasInterrupted(
        IReadOnlyList<TimelineEvent> timeline, string callId, string resultIdPrefix)
    {
        // 正向扫到这条调用为止。⚠️ 扫描过程中顺手记下「谁被消费了」——
        // 结果可能排在调用**前面**（批次顺序无保证），所以不能边走边判。
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var evt in timeline)
        {
            switch (evt)
            {
                case TimelineEvent.ToolResultEvent r:
                    // ⚠️ **只认我们自己补的那种**：`Id` 带前缀 ⇒ 这次收尾的结果不算配对。
                    // 不带前缀的正常结果当然算——它就是那半配对。
                    if (!r.Id.StartsWith(resultIdPrefix, StringComparison.Ordinal))
                        consumed.Add(r.CallId);
                    break;

                case TimelineEvent.MessageEvent m when m.Blocks.OfType<Block.ToolUseBlock>()
                    .Any(u => string.Equals(u.CallId, callId, StringComparison.Ordinal)):
                    // 找到了装着这条调用的那条消息 ⇒ 判配对；之后的回合一律不看。
                    return !consumed.Contains(callId);

                case TimelineEvent.MessageEvent m when m.Blocks.OfType<Block.ToolUseBlock>().Any():
                    // ⚠️ **另一条装着 tool_use 的消息**：它要么是**同批兄弟**（配对排在后面
                    // ⇒ 还没被消费 ⇒ 断裂），要么是**下一个回合**（⇒ 上一个回合缺配对）。
                    // 两种情况**都判真**，而两个 `return true` 的落点互不重叠、各自可测。
                    // ⇒ 这就是「按**位置**判、不按块数判」那条要求的落点。
                    return true;

                case TimelineEvent.MessageEvent:
                    // 用户消息（或一句纯文本的模型发言）。⚠️ 正常的用户消息里不会有 `tool_use`，
                    // 但**记录是盘上的字节** ⇒ 一条手改过的、或格式更旧的行可能有。
                    // 「记录里根本没有这条调用」必须落到末尾那个 `return false`，
                    // 不能在这里被判成「断裂」——那会凭空补一条结果。
                    return true;
            }
        }

        // 走到这里 = 记录里根本没有这条调用（还没落盘，或 id 写错了）。
        // ⚠️ 「找不到」与「悬着」是两件事：这里返回假，**不替它编一条结果**。
        return false;
    }
}

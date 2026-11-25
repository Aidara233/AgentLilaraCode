using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `approve_child`——**父替孩子放行一条授权**（2026-11-19，用户裁定）。
///
/// <para>⚠️ <b>它解决的是那个具体的死等</b>：子会话碰到权限闸门就会挂起，等人点头。
/// 从前父**什么都做不了**（`EscalateAsk` 对授权一条都不发），于是父在 `wait` 上耗着、
/// 而人能看见的那张卡可能正被别的东西压着。现在父可以自己把这件事了结——
/// **前提是它自己本来就做得了**。</para>
///
/// <para>⚠️⚠️ <b>判据是「授权范围 ⊆ 父」（`01` §10.3），而它在这一层的形状只有一句：
/// `Gate.Decide(那类, 父档位) == Allow`</b>。父的孩子想做写入，而父的档位本来就放行写入
/// ⇒ 父批它，**委托的边界一格没动**（那件事从头到尾都在父已有的授权之内）；
/// 父的档位也不放行 ⇒ 这条**只能等人**，工具如实拒绝——**不是"试一下"**：
/// 结果一落盘就改不了，而一句"已批准"配上一次没发生的事是一条假话。</para>
///
/// <para>⚠️ <b>它与 `tell_child` 是两个动词，不能合成一个</b>：这个会让被挡下的那条调用
/// **真的执行**（走的是人的 `approval.respond` 同一条恢复路径），那个只是往孩子的记录里
/// 加一句话。合成一格之后，「父到底放没放行」在孩子的记录里就分不出来了。</para>
///
/// <para>⚠️ <b>工具不写任何东西</b>：它只产出 <see cref="ToolOutcome.Approve"/>，
/// 清挂起 + 续跑那一步都在 `CommandDispatcher`（`Stepper` 没有队列，也不碰事件总线）。
/// 而**校验必须在工具这一侧**（同 `tell_child` / `archive_child` 的理由）。</para>
///
/// <para>⚠️ <b>它不写批准记忆</b>（与人的那张卡不同，那一条带「记住」档位）：记忆是
/// 「人替这一类点过一次头」；父的放行是**这一次**，而下一次父的档位可能已经变了。
/// 落一条记忆就等于让一次旧档位下的点头在新档位下继续生效——那是静默的提权。</para>
/// </summary>
public sealed class ApproveChildTool : ITool
{
    /// <summary>⚠️ 常量：与 <see cref="TellChildTool.NameOf"/> 的理由相同——名字只写一遍。</summary>
    public const string NameOf = "approve_child";

    public string Name => NameOf;

    /// <summary>
    /// **消耗型**：它会让一个子会话真的执行一条被挡下的调用，并再跑一步（花钱、花时间）。
    /// ⚠️ 比 `tell_child` **更重**——那个只是补一句话，这个会让副作用发生。
    /// </summary>
    public PermissionClass PermissionClass => PermissionClass.Consuming;

    /// <summary>
    /// ⚠️ **描述只写模型看不出来的四件事**（`00` §2.2）：① 它替谁批、② 凭什么能批、
    /// ③ 批不了时该怎么办、④ 什么时候不该用它。**不写**「放心，这就是合法的」那类安抚。
    /// </summary>
    public string Description =>
        "替一个正挂着授权的子会话放行。它能过的前提是**那条调用在你自己的权限档位之内**"
        + "（你已经做得了这件事），否则会被如实拒绝——那类请求只有人能批。"
        + "放行之后它会接着执行那条调用，你不用等。"
        + "⚠️ 它只对**授权**有效：孩子挂着提问时用 tell_child 回答；"
        + "⚠️ 想拦下它、或者换个方向，也用 tell_child（那会把那条调用顶掉，不会执行）。"
        + "`child` 必须是**你自己派生出来的**会话 id。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "child": {
              "type": "string",
              "description": "子会话 id（spawn 的结果里给过）。"
            },
            "ask_id": {
              "type": "string",
              "description": "要批的是哪一条询问（升级报告里带着）。省略 = 它此刻挂着的那一条，回执里会说出究竟批了哪一条。"
            }
          },
          "required": ["child"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var childId = ToolArgs.String(call.ArgumentsJson, "child");
        var askId = ToolArgs.String(call.ArgumentsJson, "ask_id");
        if (string.IsNullOrWhiteSpace(askId)) askId = null;

        if (string.IsNullOrWhiteSpace(childId))
            return Task.FromResult(ToolText.Of("`child` 得给：批哪一个子会话？"));

        // ⚠️ **没有派生上下文 ⇒ 如实说**，不猜（同 `spawn` / `tell_child` / `archive_child`）。
        if (context.Subagents is not { } scope)
            return Task.FromResult(ToolText.Of("这个会话没有派生上下文，没有可批的子会话。"));

        var known = scope.Children.FirstOrDefault(
            c => string.Equals(c.SessionId, childId, StringComparison.Ordinal));
        if (known is null)
        {
            var list = scope.Children.Count == 0
                ? "你现在没有（未归档的）子会话。"
                : "你的子会话：" + string.Join(" / ", scope.Children.Select(c => c.SessionId));
            return Task.FromResult(ToolText.Of($"`{childId}` 不是你的子会话，没有批准任何东西。{list}"));
        }

        // ⚠️ **没在等 ⇒ 一句人话，什么都不做**：对着一条已经结束的询问点「批准」，
        // 回一句「已批准」会让人以为那次批准生效了（同 `approval.respond` 的既有判据）。
        //
        // <para>⚠️ **还要分清「真的没在等」与「我这儿的快照旧了」**：`SubagentScope` 是
        // **这一步开始时**冻住的，而孩子在父这一步的模型调用期间完全可能刚挂上一条询问
        // （那份快照里就什么都没有）。⇒ 用实况探针分辨（`SubagentStatus` 是现读的）；
        // 现读说「在等」而快照没有细节时，**如实说看不见细节**，而不是断言它没在等
        // ——后者会让父以为自己看错了（`00` §7 判据 4：把它答得清，别让它去猜）。</para>
        if (known.Status != SessionStatus.WaitingForInput || known.Waiting is not { } waiting)
        {
            var live = (context as SessionToolContext)?.SubagentStatus?.Invoke(childId);
            if (string.Equals(live, SessionStatus.WaitingForInput, StringComparison.Ordinal))
            {
                return Task.FromResult(ToolText.Of(
                    $"子会话 {childId} 此刻确实挂在一条询问上，但我手上这份快照是这一步开始时取的，"
                    + "没带上它的细节（ask id / 种类 / 工具名）——所以没有批准任何东西。"
                    + "它的回执会以 subagent_report 到你手里，或者用 wait 等它；下一步再看一次就能批。"));
            }

            return Task.FromResult(ToolText.Of(
                $"子会话 {childId} 现在没有在等回答，没有批准任何东西。"
                + "它落定之后会以 subagent_report 把结果给你。"));
        }

        // ⚠️ **提问那条不是授权**：让 `ask` 的「答案」变成一个空的批准，等于把一次询问吞掉。
        if (waiting.Kind == AskKind.Question)
        {
            return Task.FromResult(ToolText.Of(
                $"子会话 {childId} 挂着的是**提问**（ask {waiting.AskId}），不是授权——没有「批准」这回事。"
                + "要回答它，用 tell_child：那句话会落进那条提问的结果里。"));
        }

        // ⚠️ **ask_id 对不上 = 时代的眼泪**（模型给的是上一条询问的 id）⇒ 拒绝，
        // 而不是「当作当前那条」——后者会把一次陈旧的批准**安到另一条调用上**（同
        // `HandleApprovalRespond` 的既有一句）。
        if (askId is not null && !string.Equals(askId, waiting.AskId, StringComparison.Ordinal))
        {
            return Task.FromResult(ToolText.Of(
                $"子会话 {childId} 此刻挂着的是 ask {waiting.AskId}，不是你给的 {askId}——没有批准。"
                + "要么按那一条重发，要么省掉 `ask_id` 批当前这一条。"));
        }

        // ---- 授权范围 ⊆ 父：**父自己得做得了这件事**（`01` §10.3）----
        //
        // ⚠️ 查不到类别 ⇒ 按**最严**那一档（同 `Gate.ClassOf` 的保守方向：编一个更轻的类别
        // 会让一次越权看起来像是被允许的）。档位取本会话自己的（`context.PermissionLevel`，
        // 与闸门同一个值）：读不到（`null`）时 `Gate.Decide` 把它当未知 ⇒ 最严。
        var cls = context.ToolClass(waiting.ToolName) ?? PermissionClass.Destructive;
        if (Gate.Decide(cls, context.PermissionLevel) is not Verdict.Allow)
        {
            var name = cls switch
            {
                PermissionClass.ReadOnly => "只读",
                PermissionClass.Consuming => "消耗型（会联网或花钱）",
                PermissionClass.Writing => "写入",
                _ => "高危（不可逆）",
            };
            var level = context.PermissionLevel is { } l ? Axes.LabelOf(Axes.Permission) + "＝" + l : "未知";

            return Task.FromResult(ToolText.Of(
                $"没批：`{waiting.ToolName}` 是**{name}**类，而你的档位（{level}）也不放行这一类。"
                + "这一条只能等人批——把它说给用户，或者用 tell_child 让孩子改个道。"));
        }

        return Task.FromResult(new ToolOutcome
        {
            Content =
            [
                new Block.TextBlock(
                    $"已批准子会话 {childId} 的 `{waiting.ToolName}`（ask {waiting.AskId}）。"
                    + "它在你的档位之内，所以由你放行；它会接着执行那条调用，不用等它的回报。"),
            ],
            Approve = new ApproveChildRequest(childId, waiting.AskId),
        });
    }
}

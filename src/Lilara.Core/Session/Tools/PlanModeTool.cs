using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `switch_plan_mode`——**模型自己切工作模式**（2026-10-24）。
///
/// <para>⚠️ <b>它是<b>一个</b>工具、两个方向，而两个方向<b>不对称</b></b>：
/// 进入规划模式是<b>自我限制</b>（从「能动手」退到「只规划」），随时可以、不问人；
/// 退出是<b>恢复动手能力</b>，<b>任何权限档位都要用户点头</b>（用户原话：
/// 「退出在任何权限都得询问」）。⇒ 这个不对称是刻意的，也是这个工具存在的全部理由：
/// 一次自我限制不需要许可，而一次解除限制必须有人签字。</para>
///
/// <para>⚠️ <b>为什么带 <c>reason</c> 参数</b>：退出那张卡上要写清「它想退出，理由是 X」——
/// 一句「模型请求退出规划模式」不给用户任何判断依据，而用户是这个决定唯一的负责人。
/// 进入也要求理由：它是写进结果、给模型下一步自己看的一句话（「我刚才为什么决定先规划」）。</para>
///
/// <para>⚠️ <b>`GatedByPermission => false</b>：这一条直接来自「退出在任何权限都得询问」。
/// 走权限闸门的话，`off` 档会在工具跑起来之前就把它拒掉 ⇒ 模型连<b>请求</b>都发不出，
/// 而「问人」这件事本来不改变世界（同 `compact` 的理由）。</para>
///
/// <para>⚠️ <b>`AsksBeforeRunning => true</b>：它没有执行相位——第一次跑只产出那张询问卡，
/// 用户答完之后核心再跑一次（`AskKind.Permission` 的「批准」意思就是「让它跑」），
/// 那一次它靠 <see cref="SessionToolContext.IsApproved"/> 认出「已经批过了」并给出落地文本。
/// 真正的模式切换<b>不在这里</b>：与 `plan` / `finish_goal` 同一条分工——工具只判、核心在步末落地。</para>
/// </summary>
public sealed class PlanModeTool : ITool
{
    public const string NameOf = "switch_plan_mode";

    /// <summary>两种模式的名字。⚠️ 它们是 <see cref="Axes.Mode"/> 的合法值，别另起一套拼法。</summary>
    public const string Plan = "plan";
    public const string Act = "act";

    public string Name => NameOf;

    /// <summary>
    /// ⚠️ 只读：切模式**不碰工作目录**。它改的是「意图」，而意图的核心事实
    /// （`axes.mode`）由推进器在步末写——工具自己连那个手都没有。
    /// </summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    /// <summary>见类型注释：退出要「任何权限都问」，所以它不能先被闸门挡掉。</summary>
    public bool GatedByPermission => false;

    /// <summary>见类型注释：第一次跑只产出询问卡，没有执行相位。</summary>
    public bool AsksBeforeRunning => true;

    public string Description =>
        "切换工作模式：mode=plan 只规划不动手，mode=act 按计划动手。"
        + "**任务需要先摸清结构、或动手前要先跟用户对齐方案时，进 plan 模式**——进入不需要许可，随时可以。"
        + "退出（mode=act）会弹用户确认卡，卡上显示你填的理由；用户不批就继续留在规划模式。"
        + "⚠️ 必须填 reason：一句话说明为什么要切，退出时它就是给用户的判断依据。"
        + "⚠️ 只在确实需要改变工作方式时调用；不要为了汇报进度、也不要为了打个招呼而切换。"
        + "规划模式下 write / update / delete / mkdir / git_write 会被硬拒（shell、dotnet、path 不受限），"
        + "要动文件必须先退出。iterate 主动性下普通计划用完时核心会自动切回 plan，本工具不能退出该循环；"
        + "goal 下普通计划没清空时也不能退出。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "mode": {
              "type": "string",
              "enum": ["plan", "act"],
              "description": "目标模式。plan = 只规划不动手；act = 恢复动手。"
            },
            "reason": {
              "type": "string",
              "description": "为什么要切换，一句话。退出规划模式时它会显示在用户的确认卡上。"
            }
          },
          "required": ["mode", "reason"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var target = ToolArgs.String(call.ArgumentsJson, "mode");
        var reason = ToolArgs.String(call.ArgumentsJson, "reason")?.Trim();

        if (target is not (Plan or Act))
            return Task.FromResult(ToolText.Of($"mode 必须是 \"{Plan}\" 或 \"{Act}\"。"));

        // ⚠️ 理由**必填**，而且不给自己编一个默认值：一张写着「（未说明理由）」的确认卡
        // 会把决定变成一次盲签——那比不让它退出更坏。
        if (string.IsNullOrWhiteSpace(reason))
            return Task.FromResult(ToolText.Of("必须填 reason：一句话说明为什么要切换模式（退出时用户会看到它）。"));

        var current = (context as SessionToolContext)?.Mode ?? Act;
        var initiative = (context as SessionToolContext)?.Initiative;
        var plan = (context as SessionToolContext)?.Plan;

        if (target == Act && initiative == "goal" && PlanTree.Current(plan) is not null)
            return Task.FromResult(ToolText.Of(
                "普通计划尚未完成；goal 模式完成所有普通节点后才能请求退出规划模式。"));
        if (target == Act && initiative == Axes.Iterate)
            return Task.FromResult(ToolText.Of(
                "iterate 模式不能主动退出规划循环。普通计划完成后核心会回到 plan，并提示你按 T0 规划下一段。"));

        if ((context as SessionToolContext)?.Initiative == Axes.Iterate
            && current == Plan && target == Act)
            return Task.FromResult(ToolText.Of(
                "当前是 iterate 模式；计划用完时核心会自动切回 plan，由你规划下一段。"
                + "现在先完成 T0 指令和当前计划，不要请求退出。"));

        if ((context as SessionToolContext)?.Initiative == Axes.Iterate
            && target == Act
            && PlanTree.Current((context as SessionToolContext)?.Plan) is null)
            return Task.FromResult(ToolText.Of(
                "iterate 模式下普通计划为空；核心会切回 plan。先参考 T0 规划下一段并用 plan 写入，之后才能请求切到 act。"));

        // 幂等：目标等于当前 ⇒ 什么也不做、不发事件（同 `session.setAxis` 的规矩）。
        if (string.Equals(target, current, StringComparison.Ordinal))
            return Task.FromResult(ToolText.Of(
                $"已经在{(target == Plan ? "规划" : "行动")}模式，无需切换。"));

        if (target == Plan)
            return Task.FromResult(ToolText.Of(
                $"已切到规划模式（理由：{reason}）。本步结束时生效——在此之前仍然照旧判定。"
                + "退出规划模式需要用户批准。"));

        // ---- 退出：**任何权限档位都问**（用户裁定）----
        // ⚠️ 判据是「这次调用有没有被批准过」，不是「记不记得住」：
        // 退出**不给「记住」**（`Remember = []`）——记住「永远可以退出规划模式」
        // 等于把闸门永久关掉，而这张卡存在的唯一理由就是不让人这么做。
        if ((context as SessionToolContext)?.IsApproved(call.CallId) != true)
        {
            return Task.FromResult(new ToolOutcome
            {
                // ⚠️ 内容必须给个非空值（`required`）；这条路径上它**不会被落盘**
                // ——`ToolExecutor` 见到 `Ask` 就整批中止（同 `AskTool` 那条）。
                Content = [],
                Ask = new AskRequest
                {
                    Id = Ids.New(),
                    Kind = AskKind.Permission,
                    Items =
                    [
                        new AskItem
                        {
                            Id = "decision",
                            Shape = AskShape.Choice,
                            Question = $"模型请求退出规划模式、恢复动手能力。理由：{reason}",
                            Options = [new AskOption("allow", "允许"), new AskOption("deny", "继续规划")],
                        },
                    ],
                    // ⚠️ 空 = 不给「记住」（见上）。
                    Remember = [],
                },
            });
        }

        return Task.FromResult(ToolText.Of(
            $"用户已批准，退出规划模式（理由：{reason}）。本步结束时生效。"
            + "此后可以正常改文件；需要再规划时重新调用本工具进入。"));
    }
}

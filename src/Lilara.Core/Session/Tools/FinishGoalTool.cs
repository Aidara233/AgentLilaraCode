using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `finish_goal`——goal 模式的收尾信号。iterate 模式不调用它；T0 固定长期任务仍然保留。
/// </summary>
public sealed class FinishGoalTool : ITool
{
    public const string NameOf = "finish_goal";
    public string Name => NameOf;
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    public string Description =>
        "goal 模式下，只有普通计划已空且目标真正完成时才调用。它不会结束会话，只把主动性拨回 standard，" +
        "之后还会有一回合用于总结。iterate 模式绝不能调用；T0 固定长期任务不会被此工具移除。";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{},"required":[]}
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        if (context is not SessionToolContext session)
            return Task.FromResult(ToolText.Of("缺少会话模式上下文，未结束目标。"));

        if (!session.GoalActive)
            return Task.FromResult(ToolText.Of("当前不在 goal 模式，没有做任何改动。"));

        if (session.Initiative == "goal" && PlanTree.Current(session.Plan) is not null)
            return Task.FromResult(ToolText.Of(
                "普通计划尚有未完成节点，不能结束 goal；请先完成并推进所有普通节点。"));

        return Task.FromResult(ToolText.Of(
            "目标收尾已记录。普通计划为空；主动性将在本步结束时回到 standard，之后请总结。"));
    }
}

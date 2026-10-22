using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `finish_goal`——goal 模式的**收尾信号**。
///
/// <para>⚠️ <b>它本身不改任何东西</b>（不写文件、不改锁、不改状态）：真正的那一下是
/// <see cref="Stepper"/>——只有「真的执行过」（`Batch.ExecutedCallIds`）且当时主动性确实是
/// `goal`，才把轴拨回 `standard`。工具在这里只负责回答得当：不在 goal 模式下调用它，
/// 就是一句「什么都没改」，不能假装收尾。</para>
///
/// <para>⚠️ <b>拨回轴之后不是立刻结束</b>：因为它是一次工具调用，普通工具循环还会再走一步，
/// 模型因此有机会把工作总结出来。这个「多跑一步」是用户明确要的，别在这里擅自掐掉。</para>
/// </summary>
public sealed class FinishGoalTool : ITool
{
    public const string NameOf = "finish_goal";

    public string Name => NameOf;

    /// <summary>只读：它不做任何有副作用的事（拨轴的是推进器，不是它）。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    public string Description =>
        "只有当你确认当前目标已经真正完成时才调用。"
        + "它不会结束会话，只会把主动性轴从 goal 拨回 standard；拨回之后你还会有一回合，"
        + "请用那回合总结已完成的工作。不在 goal 模式下调用它不会改变任何东西。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
        Task.FromResult(ToolText.Of(context.GoalActive
            ? "目标收尾已记录。主动性将回到 standard——下一回合请用一段话总结已完成的工作。"
            : "当前不在 goal 模式（主动性轴不是 goal），没有做任何改动。"));
}

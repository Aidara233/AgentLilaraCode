using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `tell_child`——**父对子说一句**（`01` §10.1 的「纯注入」，2026-10-24）。
///
/// <para>⚠️ <b>它与 `session.send` 是同一条机制</b>：追加一条 `MessageEvent` 到收件人的
/// 记录、入队、启动。差别只在**谁在说**——`session.send` 的说话人是用户，
/// 这里是父会话的模型。⚠️ 所以它同样**不要求对方在线**、**不阻塞**、**可以反复**
/// （「信箱」是为了补「只能喊一次、必须当场收回结果」的缺陷，而追加式注入没有那个缺陷）。</para>
///
/// <para>⚠️ <b>工具不写入</b>：它只产出 <see cref="ToolOutcome.Tell"/>，真正的追加与启动
/// 在 `CommandDispatcher`（`Stepper` 刻意不碰事件总线）。而**「它是不是我的孩子」这条校验
/// 在工具这一侧做**（句柄里就有 `Subagents.Children`）——因为结果一旦落盘就改不了，
/// 而「说了已转达、其实不是你的孩子」是一条假话。</para>
/// </summary>
public sealed class TellChildTool : ITool
{
    /// <summary>⚠️ 常量：推进器按名字挑出它（同 <see cref="ReleaseTool.NameOf"/> 的理由）。</summary>
    public const string NameOf = "tell_child";

    public string Name => NameOf;

    /// <summary>
    /// **消耗型**：它会让一个子会话**再跑一步**（花钱、花时间），而它自己不碰任何文件。
    /// </summary>
    public PermissionClass PermissionClass => PermissionClass.Consuming;

    public string Description =>
        "给一个子会话补一句话。它会在自己的下一步里看到（不需要它在线，也不会等它回答）。"
        + "可以反复说——这就是你与它持续对话的方式。"
        + "`child` 必须是**你自己派生出来的**会话 id。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "child": {
              "type": "string",
              "description": "子会话 id（spawn 的结果里给过）。"
            },
            "text": {
              "type": "string",
              "description": "要说的话。"
            }
          },
          "required": ["child", "text"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var childId = ToolArgs.String(call.ArgumentsJson, "child");
        var text = ToolArgs.String(call.ArgumentsJson, "text");

        if (string.IsNullOrWhiteSpace(childId) || string.IsNullOrWhiteSpace(text))
            return Task.FromResult(ToolText.Of("`child` 与 `text` 都得给：给谁、说什么。"));

        if (context.Subagents is not { } scope)
            return Task.FromResult(ToolText.Of("这个会话没有派生上下文，没有可对话的子会话。"));

        // ⚠️ **校验在产出之前**（理由见类型注释）：结果一落盘就改不了，
        // 而「已转达」配上一个不存在的收件人是一条假话。
        // ⚠️ 判据是**扫描 `Children`**，不是「id 非空就行」——后者会让模型随手
        // 把话发进任何会话（那是越权，而且它自己看不出来）。
        var known = scope.Children.FirstOrDefault(
            c => string.Equals(c.SessionId, childId, StringComparison.Ordinal));
        if (known is null)
        {
            var list = scope.Children.Count == 0
                ? "你现在没有子会话。"
                : "你的子会话：" + string.Join(" / ", scope.Children.Select(c => c.SessionId));
            return Task.FromResult(ToolText.Of($"`{childId}` 不是你的子会话，没有转达。{list}"));
        }

        return Task.FromResult(new ToolOutcome
        {
            Content = [new Block.TextBlock($"已转达给子会话 {childId}。它会在下一步看到，不会立刻回答你。")],
            Tell = new TellRequest(childId, text),
        });
    }
}

/// <summary>
/// `tell_parent`——**子对父说一句**（2026-10-24）。
///
/// <para>⚠️ <b>它是「反复通话」的子上行那一半</b>：没有它，子只有在**落定**时才有一次发言
/// 机会（那条自动的 `subagent_report`），而「我卡住了 / 我改主意了 / 这条我先不做」这些
/// 中间的判断就只能等落定。设计文档 §10.2 说得很清楚：<b>子是决定何时值得打扰父的那个人</b>。
/// </para>
///
/// <para>⚠️ <b>两个方向共用一条注入机制</b>（<see cref="TellRequest"/>），
/// 但**方向上不对称**是有理由的：父 → 子写进子记录的是**一条用户消息**（子那边
/// 「用户」就是驱动它的人，而父正是那个人）；子 → 父**不能**写成用户消息
/// ——那会让父的记录谎报「用户说过话」。⇒ 子上行落成一对**合成的工具结果**
/// （`subagent_report`），与 `floating_context` 同一个形状。见
/// `CommandDispatcher` 里那段注入代码。</para>
/// </summary>
public sealed class TellParentTool : ITool
{
    public const string NameOf = "tell_parent";

    public string Name => NameOf;

    /// <summary>
    /// **消耗型**：它会把父会话再推一步（花钱、花时间）。⚠️ 这里**不设频次闸**——
    /// 用户 2026-10-24 明确裁定「不设上限，就当反复讨论」；刹车是**人**（头里看得见
    /// 往返轮数，随时能停），而不是一个数。
    /// </summary>
    public PermissionClass PermissionClass => PermissionClass.Consuming;

    public string Description =>
        "跟你的父会话说一句话。它在自己的下一步里会看到（不要求它在线，也不等它回答）。"
        + "适合：你卡住了、你发现方向错了、有件事得先问清楚——"
        + "而不是「我又推进了一步」这种它不需要知道的进度。"
        + "没有父会话时调用它不会做任何事。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "text": {
              "type": "string",
              "description": "要说的话。说清楚你要什么（一个决定？一条信息？还是只是通报）。"
            }
          },
          "required": ["text"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var text = ToolArgs.String(call.ArgumentsJson, "text");
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(ToolText.Of("缺少 `text`：要跟父会话说什么？"));

        if (context.Subagents is not { } scope || scope.ParentSessionId is not { } parent)
            return Task.FromResult(ToolText.Of("这个会话没有父会话（它是顶层会话），没有转达。"));

        return Task.FromResult(new ToolOutcome
        {
            Content = [new Block.TextBlock($"已转达给父会话 {parent}。它会在下一步看到。")],
            Tell = new TellRequest(parent, text),
        });
    }
}

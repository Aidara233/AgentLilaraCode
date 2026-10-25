using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `subagent_report`——**子会话的回流在这条链路上的名字**（2026-10-24）。
///
/// <para>⚠️ <b>它存在的理由与 <see cref="FloatingContextTool"/> 逐字相同：让核心合成的
/// 那对调用有一个**声明过的名字**。</b>子会话落定（或它调 `tell_parent`）时，核心往父的
/// 时间线里追加一对合成的 `tool_use(subagent_report)` + `tool_result(子说的话)`——
/// 而某些上游会检查「这个名字在工具表里吗」。它在这里，那个问题就不存在。</para>
///
/// <para>⚠️ <b>合成的那个形状不是省事，是唯一能白拿三样的选择</b>（用户 2026-10-24 的
/// 「用 fetch 偏移那套」在这里兑现）：子会话的报告可能很长，而只要它是
/// <see cref="TimelineEvent.ToolResultEvent"/>，就**自动**得到
/// ① 投递截断（`TimelineRender.Truncate` 只认这个类型）、② `fetch(id, offset)` 读后半段
/// （`SessionToolContext.RawBytesOf` 只从这些事件里找）、③ 三个适配器零改动
/// （它们本来就只认 `MessageEvent` / `ToolResultEvent`）。
/// ⚠️ 反过来，若回流做成一个**新的事件类型**，这三样一样都拿不到——那是三个适配器 +
/// 渲染层 + 取回层 + 控制面 `Render` 的 switch，而每一个都是单向门。</para>
///
/// <para>⚠️ <b>模型自己调它时是兜底</b>：正常路径上这个调用由核心合成、从不经过执行器
/// （`ExecutedCallIds` 里不会有它）。模型真调一次，得到的是一句「没有待读的上报」——
/// 而不是一条编出来的内容。</para>
/// </summary>
public sealed class SubagentReportTool : ITool
{
    /// <summary>⚠️ 常量：合成对按它拼工具名（推进器拿不到实例，见 `<see cref="FloatingContextTool.NameOf"/>`）。</summary>
    public const string NameOf = "subagent_report";

    public string Name => NameOf;

    /// <summary>只读：它什么都不改（合成的那一份连执行器都不经过）。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    /// <summary>
    /// ⚠️ **描述只写三件模型看不出来的事**（`00` §2.2：能进工具描述的绝不进系统提示词）：
    /// ① 它是什么 ② 什么时候会出现 ③ 认得出它的标记（id 前缀）——
    /// **不写「不需要你做什么」**那类关于读者记忆的断言（`CorePrompts` 那条改口的同一条判据：
    /// 能被核对的预言比任何安抚都管用）。
    /// </summary>
    public string Description =>
        "子会话的回报。它会以一次**工具调用**的形式出现在你的上下文里："
        + "工具名是 subagent_report，参数里带 `child`（哪个子会话），结果里是它说的话。"
        + "这些调用由核心在你收到回报时自动合成，id 以 subagent- 开头。"
        + "你也可能收到它主动转达的一句（它调了 tell_parent）——形状完全相同。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "child": {
              "type": "string",
              "description": "哪个子会话（核心合成时填）。省略则返回最近一条待读回报。"
            }
          },
          "required": []
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        // ⚠️ **不做任何取回**：回流已经是父时间线里的记录（模型一进来就看得到），
        // 而「再取一遍」只会多一条同义记录。⇒ 如实说清它为什么在这里、以及去哪里看。
        // ⚠️ **不编内容**：空就是空（同 `FloatingContextTool` 那条「如实说这一刻没有」）。
        var from = ToolArgs.String(call.ArgumentsJson, "child");

        return Task.FromResult(ToolText.Of(from is null
            ? "没有待读的子会话回报——回报会以新的调用直接出现在你的上下文里，不需要你主动来取。"
            : $"子会话 {from} 的回报会以新的调用直接出现在你的上下文里，不需要主动来取。"));
    }
}

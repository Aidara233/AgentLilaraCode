using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `floating_context`——**读回核心每步注入的那块浮动上下文**（`01` §3.2）。
///
/// <para>⚠️ <b>它存在的理由不是「让模型读一遍」，是「让核心的合成调用有一个声明过的名字」</b>
/// （2026-10-17 用户裁定）：浮动块不再拼进 user 消息，而是由推进器在时间线末尾
/// **合成一对** `tool_use(floating_context)` + 配对的 `tool_result`。合成的那条 `tool_use`
/// 引用一个工具名 ⇒ 某些上游会检查「这个名字在工具表里吗」。它在这里，那个问题就不存在。</para>
///
/// <para>⚠️ <b>它不被任何人执行</b>：合成的那一对只进请求、不进执行器
/// （`ToolExecutor` 永远看不到它，`ExecutedCallIds` 里也不会有它）。本类的
/// <see cref="RunAsync"/> 只在模型**自己**真的调用一次时跑——而那是一个兜底：
/// 某条链路丢了注入，模型还能自己要一份。</para>
///
/// <para>⚠️ <b>返回的是「这一刻」的浮动块，不是历史快照</b>：它经
/// <see cref="IToolContext.FloatingContext"/> 读的是推进器/答复路径在**执行时刻**冻住的那份
/// （`StepAsync` 是步开始、`ResumeAsync` 是答复那一刻）。⇒ 与头的 `/state` 快照端点
/// 同一个时间轴，而不是「某一步当时发出去的那一份」。
/// ⚠️ 它**不落盘**、也不该落盘：浮动是状态（`00` §2.4「状态进槽位、事件进时间线」），
/// 存进记录就变成了一条它本来不是的「事件」。</para>
///
/// <para>⚠️ <b>权限是 <see cref="PermissionClass.ReadOnly"/></b>：它只读会话状态，
/// 不改任何东西。挂成别的档位会让模型真调它时在 `standard` 档弹一次授权——
/// 而它读的是核心自己刚刚注入的内容，那个「问」没有任何意义。</para>
/// </summary>
public sealed class FloatingContextTool : ITool
{
    /// <summary>
    /// ⚠️ **名字是常量**：推进器合成那一对调用时按它挑工具名，而它拿不到实例
    /// （工具表是 `IReadOnlyList&lt;ITool&gt;`）。名字写成两遍就会漂，
    /// 而漂的表现是**一行合成都不发生**——静默（同 `ReleaseTool.NameOf` 那条理由）。
    /// </summary>
    public const string NameOf = "floating_context";

    public string Name => NameOf;

    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    /// <summary>
    /// ⚠️ **描述里要写三件模型看不出来的事**（`00` §2.2：能进工具描述的绝不进系统提示词）：
    /// ① 它返回的是什么 ② 核心已经每步自动注入过一次，所以**不需要调**
    /// ③ **上下文末尾那次调用不是你发起的**——模型会在历史里看到一条自己没发过的
    /// `tool_use`，而它对「我没做过的事」的典型反应是编一段解释（自我怀疑 / 自我指控）。
    /// ⇒ 这一句是给那份推理一个正确的出口，不是请求它别想。
    /// ⚠️ 再加上那条**机械后果**：「调用会进历史、之后每一步都会重放」——
    /// 它比「别当刷新按钮用」这种请求靠得住，因为它是一个会被验证的预言。
    /// </summary>
    public string Description =>
        "读回核心此刻注入的「浮动上下文」：当前的行为轴（主动性、权限、安全、验证强度等）"
        + "与插件贡献的字段。"
        + "核心每一步都会在上下文末尾自动注入一次对它的调用和结果——那块内容通常已经在你的上下文里，"
        + "**你不需要调用它**。"
        + "你在上下文末尾看到的那次 floating_context 调用（以及它上面那条 assistant 消息）"
        + "是核心自动生成的，**不是你发起的**：不需要为它找原因，也不需要补调一次。"
        + "只有当你确实没有看到那一块时，再调一次重新读取。"
        + "它返回的是**此刻**的值，不是历史快照。"
        + "调用会像任何工具结果一样进入历史、之后每一步都会被重放，所以别把它当刷新按钮用。";

    /// <summary>
    /// 无参数。⚠️ **不给它加任何参数**：加一个「要哪一段」就等于把「浮动块长什么样」
    /// 泄漏成工具语义，而那块由 <see cref="SystemBlock.Render"/> 一处装配（`01` §3.2）。
    /// </summary>
    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {}
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
        Task.FromResult(ToolText.Of(
            context.FloatingContext is { Length: > 0 } text
                ? text
                // ⚠️ **空要有一种说得清的说法**（不是空串、也不是一句编出来的默认内容）：
                // 没有进上下文的轴时尾块**本来就不存在**（`SystemBlock.Wrap` 那条判据），
                // 所以这里如实说「这一刻没有浮动内容」——而不是伪造一个只有包边的块。
                : "这一刻没有浮动上下文：没有轴进上下文，也没有插件贡献浮动字段。"));
}

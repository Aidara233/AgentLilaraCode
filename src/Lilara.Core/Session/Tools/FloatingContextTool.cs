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

    /// <summary>
    /// **这一条结果自己的框架头**——署名与节律都在这里，且**只有这一处**。
    ///
    /// <para>⚠️ <b>为什么要抽成一个常量</b>：这条框架头有<b>两个</b>渲染点——
    /// ① 模型自己调出来的那份走 <c>ToolFraming</c> 字段，由 <c>TimelineRender.WithFraming</c> 加；
    /// ② 核心每步合成的那个走 <see cref="Session.Stepper.FloatingContextPair"/>，现渲染。
    /// 两处各写一遍就会漂，而漂的表现是「核心注入的」与「模型自己要来的」**在形状上分叉**
    /// （`TheToolReturnsTheSameTextTheCoreInjected` 钉的就是不分叉）。</para>
    ///
    /// <para>⚠️ <b>字段必须是恒定的</b>：「每步」说的是节律，不是「第几步」。带上步号会让
    /// 同一个值在不同步渲染成不同字节，而这条结果虽然不落盘、不破前缀，却会让
    /// 「同一步里渲染两次逐字节相同」这条（`00` §5 铁律 1）在**答复路径**上没法保证。</para>
    ///
    /// <para>⚠️ <b>字段不许为空</b>：别的工具结果是 <c>[read path foo · 第 1–40 · 共 1200]</c>，
    /// 而一个零字段的头读起来像「这次调用没有元数据」，甚至像一次空调用——
    /// 那正是「空的不能长得像有内容」的反面（`SystemBlock.Wrap` 同一条判据）。</para>
    /// </summary>
    public static readonly ToolFraming Framing =
        new(NameOf, [new ToolFramingField("节律", "每步")]);

    public string Name => NameOf;

    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    /// <summary>
    /// ⚠️ **描述里只写三件模型看不出来的事**（`00` §2.2：能进工具描述的绝不进系统提示词）：
    /// ① 它返回的是什么 ② 核心已经每步自动注入过一次 ③ 怎么**认出来**（署名在形状上）。
    ///
    /// <para>⚠️ <b>2026-10-23 改口：删掉了「不是你发起的」「不需要为它找原因」「不需要补调一次」</b>。
    /// 那三句是对读者做工作（请它相信、请它别想），而它没有可用来核对的东西——
    /// 它的全部证据就是这份上下文，而上下文里那条消息的角色就是 <c>assistant</c>。
    /// ⇒ 现在给它两样**能自己核**的东西：<c>floating-</c> 前缀的 id、结果首块的
    /// <c>[floating_context]</c> 框架头；再给它一条**机械后果**（再调一次不会带来新信息，
    /// 因为返回的就是同一份字节）。能被验证的预言比任何安抚都管用。</para>
    /// </summary>
    public string Description =>
        "读回核心此刻注入的「浮动上下文」：当前的行为轴（主动性、权限、安全、验证强度等）"
        + "与插件贡献的字段。"
        + "核心每一步都会在时间线末尾自动注入一次对它的调用和结果——id 以 floating- 开头，"
        + "结果的首块是 [floating_context] 框架头；那块内容通常已经在你的上下文里。"
        + "它返回的是**此刻**的值（不是历史快照），与你已经看到的那一份逐字节相同："
        + "再调一次不会带来新信息，只会让这条记录进入历史、并在之后每一步被重放。"
        + "所以只在确实没有看到那一块时，才需要用一次。";

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
        Task.FromResult(new ToolOutcome
        {
            // ⚠️ **署名与合成的那一份逐字相同**：同一条 `[floating_context]` 框架头，由
            // `ToolFraming.Render` 一处给。⚠️ 两边若各写一遍，模型自己调出来的那份与核心注入
            // 的那份就会**在形状上分叉**——而 `TheToolReturnsTheSameTextTheCoreInjected`
            // 钉的正是「不分叉」。合成那边写不到 `Framing` 字段（它追加在渲染之后），
            // 所以它在正文首块里现渲染一次；两处都只借这一个 Render 方法。
            Framing = new ToolFraming(NameOf, []),
            Content =
            [
                new Block.TextBlock(
                    context.FloatingContext is { Length: > 0 } text
                        ? text
                        // ⚠️ **空要有一种说得清的说法**（不是空串、也不是一句编出来的默认内容）：
                        // 没有进上下文的轴时尾块**本来就不存在**（`SystemBlock.Wrap` 那条判据），
                        // 所以这里如实说「这一刻没有浮动内容」——而不是伪造一个只有包边的块。
                        : "这一刻没有浮动上下文：没有轴进上下文，也没有插件贡献浮动字段。"),
            ],
        });
}

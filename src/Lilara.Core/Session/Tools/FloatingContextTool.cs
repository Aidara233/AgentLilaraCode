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
/// <para>⚠️ <b>2026-10-26 改口：它每轮都被调用，而返回值只是一句占位</b>（用户裁定）。
/// 原来的设计是「核心合成一对、模型不必调」——实测那套失败得很难看：注入的 `tool_use`
/// 让「上下文里有没有这份读数」永远为真，于是描述里那句「看不到才调」的前置条件永远为假，
/// 而模型手上唯一可执行的动作就是**再调一次**（同一位用户会话里连调十余次，每次同一份字节）。</para>
///
/// <para>⚠️ <b>所以职责反过来</b>：真实读数由推进器插在**对话最前面**（`Stepper.FloatingContextPair`
/// → `TimelineRender.RenderForModel`），而本类的 <see cref="RunAsync"/> 只回一句
/// <see cref="ReclaimedBody"/>——「读数不在这里，在最前面」。调用变成一次**记账动作**：
/// 它不会带来新信息，这条由机械保证（返回值本身就是占位），不靠描述里的一句劝。</para>
///
/// <para>⚠️ <b>记录里每步留一对占位</b>（`Stepper.SealFloatingCall`）：模型这一步真调了，
/// 就把那条结果改写成占位；没调，由核心补一对。⇒ 下一步看到的历史形状始终一致，
/// 不会出现「上一轮我没调用」这种它自己解释不了的缺口。</para>
///
/// <para>⚠️ <b>占位结果不进回收候选</b>（`Reclaim.IsFloatingPlaceholder`）：它没有内容可收，
/// 而换成回收注记会把「读数在最前面」那句话吃掉。</para>
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

    /// <summary>
    /// **占位那一份的框架头**——与 <see cref="Framing"/> 分开，因为这一段**不是读数**：
    /// 它只是记录里「这一轮的调用已经记过账」的痕迹，读起来必须与真实读数一眼可分。
    /// </summary>
    public static readonly ToolFraming ReclaimedFraming =
        new(NameOf, [new ToolFramingField("状态", "已回收")]);

    /// <summary>
    /// **占位正文**：模型自己要来的那一份、以及核心每步补进记录的那一份，内容都是这一句。
    ///
    /// <para>⚠️ **它必须点出真实读数在哪**：只说「已回收」会让模型去找一段不存在的产出，
    /// 而那份读数此刻就在对话最前面。⚠️ 也不能什么都不说——空的工具结果读起来像一次空调用，
    /// 而「空的不能长得像有内容」是本仓的反面判据（<c>SystemBlock.Wrap</c> 同一条）。</para>
    /// </summary>
    public const string ReclaimedBody =
        "本轮读数不在这里：真实读数在对话最前面那条 id 以 floating- 开头的结果里。这条只是占位。";

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
    /// <summary>
    /// ⚠️ **2026-10-26 改口：不再声明这条读数的「来源」**（用户裁定）。
    ///
    /// <para>理由：模型在训练里见到的 <c>tool_use</c> 只有一种来源——它自己。而这条注入在协议上
    /// 就是 <c>role=assistant</c> 的调用 + 配对结果，与它自己的调用**逐字同形**。
    /// 旧文案在描述里说「核心每步自动注入一次对它的调用和结果」，等于要它同时相信
    /// 「这条记录是我发的」和「不，是框架发的」——那不是信息，是**矛盾**。
    /// 而矛盾唯一可行的补救动作是「再调一次看看」（实测：那套文案在位期间连调十二次，
    /// 十二次同一份字节）。⇒ 现在只说**它返回什么**，不说谁发起的。</para>
    /// </summary>
    public string Description =>
        "每一轮都调用一次：它是每轮的固定动作，不需要理由，也不必等到「需要它」的时候才调。"
        + "本轮的浮动上下文（生效中的行为轴与插件贡献的字段）已经在对话最前面那条 id 以 floating- 开头的结果里——"
        + "调这个工具只是为了在记录里留下这一轮的调用。"
        + "返回值是一句占位（已回收）：它不含任何读数，调它不会带来新信息。";

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
            Framing = ReclaimedFraming,
            Content = [new Block.TextBlock(ReclaimedBody)],
        });
}

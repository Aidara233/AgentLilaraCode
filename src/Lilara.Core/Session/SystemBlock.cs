using Lilara.Core.Kernel;
using Lilara.Core.Storage;
using Lilara.Core.Plugins;

namespace Lilara.Core.Session;

/// <summary>
/// <b>三个槽位各自的产物</b>——`01` §3.1 那两块 + §3.2 那一块。
///
/// <para>⚠️ <b>类型上把它们分开，不是为了让调用方少拼一次字符串，是判据要分开。</b>
/// 三块的失效半径、来源、以及「空」的含义**没有一条相同**：</para>
///
/// <list type="table">
///   <item><description><b>系统块</b>（<see cref="System"/>）：核心提供的恒定单文件。
///     文件不在 ⇒ 回落内置模板；非空预设附加机制说明；文件空且无插件系统贡献 ⇒ 无块。</description></item>
///   <item><description><b>首要块</b>（<see cref="Premier"/>）：用户 / 项目 / 插件。**没有兜底内容**
///     ⇒ 文件不在 = 无块，不是「用回内置模板」。</description></item>
///   <item><description><b>尾块</b>（<see cref="Tail"/>）：核心每步现算（轴表）。
///     它不是文件，所以没有「不在」这一格。</description></item>
/// </list>
///
/// <para>⚠️ <b>三块都空是合法形态</b>（`04` §1.3 推论 1：裸核心 = 纯 chat）。
/// 两个 <c>IReadOnlyList</c> 用空列表表达「没有」，**不用 <c>null</c>**——
/// 「没有块」与「不知道有没有块」是两件事，而后者在这个类型里不存在。</para>
///
/// <para>⚠️ <b>尾块为什么是<b>列表</b>而不是一块</b>：它在协议上要拼进别的地方
/// （见 `ClaudeRequestBuilder`：拼进末尾那条 user 消息），而拼进去的是**块**。
/// 今天恒为 0 或 1 块，但类型不必替将来做主。</para>
/// </summary>
public sealed record PromptSlots(
    IReadOnlyList<Block> System,
    IReadOnlyList<Block> Premier,
    IReadOnlyList<Block> Tail)
{
    /// <summary>三块都没有——`PromptSlots` 的零元，也是「裸核心」那一格。</summary>
    public static readonly PromptSlots Empty = new([], [], []);
}

/// <summary>
/// <b>三个槽位从哪来</b>——`01` §3.1 头槽位那两块 + §3.2 尾槽位那一块，一处装配。
///
/// <para>⚠️ <b>判定与产物挤在一个类里</b>（同 <see cref="Reclaim"/> 那条理由）：
/// 「哪条路」与「出来是什么」若各写一份，早晚会漂。</para>
///
/// <para>⚠️ <b>为什么它取代了原来的 <c>SystemBlock</c> 而不是在旁边加一个</b>：
/// 原来的类只有「系统块从哪来」这一件事，而本批加进来的两块**判据与它同源**
/// （文件在不在 / 空不空 ⇒ 有没有块），拆成两个类等于把同一条三值判定抄两遍——
/// 而它们迟早会漂成两个版本。**判据只有一份**，这就是它现在住在这里的理由。</para>
///
/// <para>插件宿主先校验、冻结并排序贡献；这里仅合并三个槽位并附加框架说明。</para>
/// </summary>
public static class SystemBlock
{
    /// <summary>
    /// 这一次该带的三块。⚠️ <b>必须在「步开始」调一次并冻住，不是每次渲染时调</b>。
    ///
    /// <para>两块文件是**每步都可能变的外部输入**（预设是用户随时能编辑的文件），
    /// 而 [`00` §5] 铁律 1 要求<b>渲染是纯函数</b>：同一步内重复渲染必须<b>逐字节相同</b>。
    /// 随手现读的话，用户在半步中间改了文件就会「半新半旧 ⇒ <b>每步都破前缀</b>」——
    /// 那正是 `04` §7.3 机制 1 要防的事，而它给的解法现成：
    /// <i>贡献集合每步取一次快照，与 `now` 冻结同步开始</i>（`01` §3.2）。</para>
    ///
    /// <para>⇒ <b>「实时引用」= 跨步实时</b>（下一步就生效），<b>不是</b>「渲染时现读」。
    /// 两句话都得写在这里——只写「实时」会让下一个人以为可以随手现读。</para>
    /// </summary>
    /// <param name="presets">预设的落点（系统块那个目录）。</param>
    /// <param name="premiers">首要块的落点。⚠️ 与预设是**两个目录**，见 `CorePrompts.DefaultPremier`。</param>
    /// <param name="systemPresetId"><c>null</c> = 没指定 ⇒ 跟随全局默认那个名字。</param>
    /// <param name="premierId"><c>null</c> = 没指定 ⇒ 跟随 <c>CorePrompts.DefaultPremier</c>。</param>
    /// <param name="axes">这个会话此刻的轴值（`State.Axes`）。⚠️ 由调用方交进来，**不在这里读盘**：
    /// 轴是 handle 上的活数据，而本类是纯函数。</param>
    public static PromptSlots Render(
        PresetStore presets, PresetStore premiers,
        string? systemPresetId, string? premierId,
        IReadOnlyDictionary<string, string> axes,
        PromptContributionSnapshot? pluginPrompt = null,
        string? goalDirective = null,
        string? compactDirective = null,
        string? subagentLine = null,
        SessionPlan? plan = null) =>
        Build(
            // ⚠️ 传**可空**的预设正文，不在这里折成 `SystemFallback`：那一折会抹掉
            // 「文件不在」与「文件在」的区分，而 `SystemBody` 的判据正是它
            // （文件不在时兜底里已含机制说明；文件在时补一句）。
            Load(presets, systemPresetId),
            Load(premiers, premierId),
            axes, pluginPrompt, goalDirective, compactDirective, subagentLine, plan);

    /// <summary>
    /// 三块槽位的**纯装配**——输入是已经读好的两份正文 + 轴 + 插件贡献，不碰任何存储。
    ///
    /// <para>⚠️ <b>它必须有，是因为 <see cref="Render"/> 够不到「答复路径」与控制面</b>
    /// （2026-10-17，`floating_context` 批）：<c>Stepper.ResumeAsync</c> 没有步开始的槽位快照，
    /// 而待答询问的探测点也不该为了拿一句尾块去读两份预设。⇒ 两份正文由调用点自己取
    /// （各取各的：步开始取预设、答复/探测用空串），**装配判据仍然只有这一处**。</para>
    ///
    /// <para>⚠️ <b>两格正文的语义与 <see cref="Render"/> 的「文件不在 / 空」那三种状态同一套</b>：
    /// <c>null</c> = 没有这一块，<c>""</c> = 有但为空（系统块那一格甚至会产出空串块——
    /// 那是「用户明说纯聊天」的声明，见 <see cref="Text"/>）。</para>
    /// </summary>
    internal static PromptSlots Build(
        string? systemText, string? premierText,
        IReadOnlyDictionary<string, string> axes,
        PromptContributionSnapshot? pluginPrompt = null,
        string? goalDirective = null,
        string? compactDirective = null,
        string? subagentLine = null,
        SessionPlan? plan = null) =>
        new(
            System: Text(SystemBody(systemText, pluginPrompt?.Text(PromptSlot.System))),
            Premier: Wrap(CorePrompts.PremierOpen,
                Join(premierText, pluginPrompt?.Text(PromptSlot.Primary)), CorePrompts.PremierClose),
            Tail: Wrap(CorePrompts.TailOpen,
                TailBody(axes, pluginPrompt?.Text(PromptSlot.Floating), goalDirective,
                    compactDirective, subagentLine, plan),
                CorePrompts.TailClose));

    // 非空自定义预设也需要机制说明；显式空预设且无系统贡献时仍不产生系统块。
    private static string SystemBody(string? preset, string? plugin)
    {
        var body = Join(preset ?? CorePrompts.SystemFallback, plugin);
        if (body is null) return "";
        // ⚠️ 用户自己的预设正文之后追加的是**整份**框架说明（回收 + 浮动上下文 + 工作流程规范），
        // 不是只有浮动那一段：漏掉哪一段都会让「用户写了预设」的会话静默少一条机制约定。
        return preset is null ? body : Join(body, CorePrompts.SystemAddendum)!;
    }

    private static string? Join(params string?[] values)
    {
        var nonEmpty = values.Where(v => !string.IsNullOrEmpty(v)).ToArray();
        return nonEmpty.Length == 0 ? null : string.Join("\n", nonEmpty);
    }

    /// <summary>
    /// 读一份预设。⚠️ <c>null</c> = **文件不存在**，与「空文件」不是一回事
    /// （<see cref="PresetStore.Load"/> 的契约，这里只是把「没指定」折成默认那个名字）。
    /// </summary>
    private static string? Load(PresetStore store, string? id) =>
        store.Load(id ?? CorePrompts.DefaultPreset);

    /// <summary>
    /// 一段正文 → 系统块。⚠️ <b>三种「没有内容」要分开</b>：
    /// <c>null</c> = 文件不在（没配过）⇒ 兜底；<c>""</c> = 文件在但空（纯聊天）⇒ 空块。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用 <c>IsNullOrEmpty</c> 而不是 <c>IsNullOrWhiteSpace</c>：只有空白字符的文件也是**用户写的**内容，
    /// 把它当空会静默丢掉用户的东西；真按空白算的话，它发出去就是一段空白，看得见。
    /// </remarks>
    private static IReadOnlyList<Block> Text(string content) =>
        content.Length == 0 ? [] : [new Block.TextBlock(content)];

    /// <summary>
    /// 一段**可空**的正文 → 带包边的块。⚠️ <b>没有正文就<b>不产出块</b></b>（连包边一起不产出）。
    ///
    /// <para>⚠️ <b>这条是硬判据，不是省事</b>：空的包边在渲染上与「有内容」**分不开**
    /// （同 `ControlServer` 那条「头必须显示『原问题没能保留』，而不是画一张空卡」）。
    /// 一个只有 <c>[lilara 上下文]</c> 与 <c>[/lilara 上下文]</c> 的块会让模型
    /// 去猜「中间被吃掉了什么」——而那是一句**我们编出来的**假信息。</para>
    /// </summary>
    private static IReadOnlyList<Block> Wrap(string open, string? body, string close) =>
        string.IsNullOrEmpty(body) ? [] : [new Block.TextBlock($"{open}\n{body}\n{close}")];

    /// <summary>
    /// 尾块的正文——框架自述、当前轴表与等级解释、插件浮动字段。
    ///
    /// <para>⚠️ <b>自述是恒定的，所以它不参与「有没有内容」的判定</b>：判据是
    /// <b>有没有轴表或插件浮动字段</b>。若把自述也算进去，一个没有内容的会话会得到一个只有自述的尾块
    /// ——而那正是 <see cref="Wrap"/> 那条「空的不能长得像有内容」要防的。</para>
    /// </summary>
    private static string? TailBody(
        IReadOnlyDictionary<string, string> axes, string? plugin, string? goalDirective,
        string? compactDirective = null, string? subagentLine = null, SessionPlan? plan = null)
    {
        var table = AxisTable(axes);
        var planText = PlanTree.Render(plan);
        // ⚠️ **计划那两句话按轴判**（2026-10-24）：都是提示词层的语义，不是闸门——
        // 谁能改计划由权限轴说了算（`plan` 是 `ReadOnly` 类工具，`off` 档照拒）。
        // 计划为空时两句都不出现：没有树就谈不上「以 ▶ 为准」，也不该教模型先去建一棵。
        var planDirective = string.IsNullOrEmpty(planText)
            ? null
            : axes.GetValueOrDefault(Axes.Mode) == "plan"
                ? CorePrompts.PlanModeDirective
                : axes.GetValueOrDefault(Axes.Initiative) == "step"
                    ? CorePrompts.StepPlanDirective
                    : null;
        var iterateDirective = axes.GetValueOrDefault(Axes.Initiative) == Axes.Iterate
            && PlanTree.Current(plan) is null && plan?.T0 is not null
                ? CorePrompts.IteratePlanningDirective
                : null;
        var descriptions = Axes.RenderOrder.Where(Axes.InContext)
            .Where(axes.ContainsKey)
            .Select(axis => Axes.DescriptionOf(axis, axes[axis]) is { } explanation
                ? $"{Axes.LabelOf(axis)} {axes[axis]}：{explanation}" : null);
        // ⚠️ 压缩线排在目标线**之后**：两者可以同时成立（目标模式下也要能压），而「此刻该动手做的那件事」
        // ——压缩——放最后，离模型最近。
        // ⚠️ **子会话那一段排在轴解释之后、目标线之前**（2026-10-24）：它也是「环境」，
        // 而不是「此刻该做什么」。⇒ 与轴同一侧，不跟目标/压缩抢最后那个位置。
        // ⚠️ **计划树与它的那句说明排在目标/压缩线之前**（2026-10-24）：计划是**环境**
        // （这一批还剩什么），而目标/压缩是**此刻要动手的那件事**——后者仍然离模型最近。
        var body = Join(table is null ? null : CorePrompts.TailAxesPrefix + table,
            Join(descriptions.ToArray()), subagentLine,
            string.IsNullOrEmpty(planText) ? null : planText, planDirective, iterateDirective,
            goalDirective, compactDirective, plugin);
        return body is null ? null : $"{CorePrompts.TailDeclaration}\n{body}";
    }

    /// <summary>
    /// 轴表——**哪些轴进上下文由 <see cref="Axes.InContext"/> 一处判定**（`01` §3.5）。
    ///
    /// <para>⚠️ <b>顺序取 <see cref="Axes.RenderOrder"/>（ordinal），不取 <c>Axes.Names</c>（表序）</b>
    /// ——理由在那条属性的注释里（表序是**人**排的，模板要的是逐机一致的字节）。</para>
    ///
    /// <para>⚠️ <b>表里没有的轴 ⇒ 那一行根本不出现</b>，不是印成一个空值——
    /// 同 <see cref="Wrap"/> 那条「空的不能长得像有内容」。</para>
    /// </summary>
    private static string? AxisTable(IReadOnlyDictionary<string, string> axes)
    {
        var parts = new List<string>();

        foreach (var axis in Axes.RenderOrder)
        {
            if (!Axes.InContext(axis)) continue;

            // ⚠️ 取不到就不印这一行：轴值缺失是**老文件**那一格（`SessionHandle.CompleteAxes`
            // 会补齐），而在这里编一个默认值会与 `state.json` 里那份**分叉**。
            if (!axes.TryGetValue(axis, out var value)) continue;

            parts.Add($"{Axes.LabelOf(axis)} {value}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

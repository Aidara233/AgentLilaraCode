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
/// **尾块的输入**——浮动读数里除了轴表与插件字段之外的每一样东西（2026-10-27）。
///
/// <para>⚠️ <b>它取代了六个各自可空的位置参数</b>，理由不是整洁，是一类已经发生了三次的缺陷：
/// 尾块的输入在<b>三个</b>装配点各拼一遍（`Stepper.StepAsync` 的步开始、
/// `Stepper.FloatingTextAt` 的答复 / 探测路径、`ControlServer.SlotsOf` 的头槽位），
/// 而每加一样东西（目标线、压缩指令、子会话行、计划树、一次性提醒、上下文用量）都只被
/// **记得加的那一处**传进去 ⇒ 头里看不到计划树、答复路径看不到计划树，两处都「看着对」。
/// 位置参数 + 默认值让「漏传」在编译期完全合法，那正是它能重复发生的原因。</para>
///
/// <para>⚠️ <b>所以：这个记录里的每一项都有值，而值只由 <see cref="For"/> 一处派生。</b>
/// 三个装配点不再逐项拼参数，它们交的是同一个 handle ⇒ 少传一样东西<b>在结构上不可能</b>。</para>
///
/// <para>⚠️ <b>这里的每一项仍然是「产物」而不是「判据」</b>：派生规则（目标线看主动性、
/// 压缩线看 <c>pendingCompact</c>、计划树看 <c>State.Plan</c>……）住在 <see cref="For"/>，
/// 而「怎么渲染」住在 <see cref="SystemBlock.TailBody"/>——两件事各一处，谁都不许在调用点再判一遍。</para>
/// </summary>
public sealed record TailContext(
    IReadOnlyDictionary<string, string> Axes,
    PromptContributionSnapshot? Plugin = null,
    string? GoalDirective = null,
    string? CompactDirective = null,
    string? SubagentLine = null,
    SessionPlan? Plan = null,
    bool IterateReplan = false,
    string? UsageLine = null,
    bool ContextFull = false,
    string? IterateDirective = null)
{
    /// <summary>
    /// **从会话状态派生整份尾块输入**——步、答复路径、头槽位都走这一个入口。
    ///
    /// <para>⚠️ <b>它唯一拿不到、必须由调用方交进来的三样</b>：
    /// ① 轴值（插件宿主冻结后的那一份，见 <c>PluginSessionView.Values</c>）；
    /// ② 插件贡献快照（同一个 <c>Collect</c> 的产物）；
    /// ③ 派生关系快照（`SubagentScope`，它要读孩子的盘面，本类是纯函数）。
    /// 其余全部从 <paramref name="handle"/> 读——**这才是「不可能漏」的来源**。</para>
    ///
    /// <para>⚠️ <b><paramref name="iterateReplan"/> 可覆盖</b>：`StepAsync` 在装配<b>之前</b>
    /// 就把那个一次性提醒清掉了（它只许说一次），所以那一路要交「清之前读到的值」。
    /// 其余调用点不传，读到的就是 handle 上此刻的值。</para>
    ///
    /// <para>⚠️ <b><paramref name="compactDirective"/> 的默认值是那唯一一句话</b>：
    /// 从前这句话由三个调用点各传一次（推进器从解析器注入、控制面直接写常量），
    /// 而它们本来就该逐字相同。今天它有一个默认值 ⇒ 谁都不用再传，也就没有「传了别的一句」这一格。</para>
    /// </summary>
    public static TailContext For(
        SessionHandle handle,
        IReadOnlyDictionary<string, string> axes,
        PromptContributionSnapshot? plugin,
        SubagentScope subagents,
        UsageStore? usage,
        string? compactDirective = null,
        bool? iterateReplan = null) =>
        new(
            Axes: axes,
            Plugin: plugin,
            // 目标线：主动性 = goal 才有（目标是**状态**，不是事件）。
            GoalDirective: GoalMode.IsActive(handle.State) ? CorePrompts.GoalDirective : null,
            // iterate 线（2026-11-03）：与目标线同形同位置，两档互斥（主动性只有一格）。
            // ⚠️ 它讲的是「这一档会一直被推着走、以及你唯一能停下来等人的方式」——
            // 没有它，模型会把「本回合没输出」当成停下的许可，而核心会立刻把它推起来。
            IterateDirective: GoalMode.IsIterating(handle.State) ? CorePrompts.IterateDirective : null,
            // 压缩线：用户下过指令（`state.json` 的 `pendingCompact`）才有。
            CompactDirective: handle.State.PendingCompact
                ? compactDirective ?? CorePrompts.CompactDirective
                : null,
            // 子会话那一行：只在真有派生关系时出现（`SubagentView.Line` 对空关系给 null）。
            SubagentLine: SubagentView.Line(subagents),
            // 计划树：T0 与剩余节点都在 `State.Plan` 里，渲染判据在 `PlanTree.Render`。
            Plan: handle.State.Plan,
            IterateReplan: iterateReplan ?? handle.PendingIterateReplan,
            // 上下文用量：与 `/sessions/{id}/context` 同一份测量（`ContextUsage`）。
            // ⚠️ `stable` 下取「整 100k 才播报」（`TailLine` 的 `coarse`）：精确值每步都变，
            // 而那一步的代价是**整段前缀缓存作废**（2026-10-27）。
            UsageLine: ContextUsage.TailLine(usage, handle.SessionId, coarse: SystemBlock.CacheStable(handle)),
            // ⚠️ 越过那条硬线没有（2026-10-27）：**同一个谓词**也决定 `compact` 的授权
            // （`Stepper` 把它交进 `SessionToolContext.ContextFull`）——两处各算一次就会分叉，
            // 而分叉的表现是「尾块喊着快压，工具却说没有指令」。
            ContextFull: ContextUsage.OverLimit(usage, handle.SessionId));
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
    /// 这个会话的缓存形态是不是 <c>stable</c>（2026-10-27）。⚠️ <b>判据只有这一处</b>：
    /// 三处装配点（步开始 / 答复路径 / 头的槽位）与 `Stepper` 的注入形态、工具表投影都读它
    /// ——各判一遍就会分叉，而分叉的表现是「头显示稳定形态、请求里却仍是每步一份」。
    ///
    /// <para>⚠️ <b>缺键 ⇒ <c>true</c></b>（2026-10-27 用户裁定：默认 <c>stable</c>）：
    /// 老会话的 `state.json` 里没有这条轴，而 `SessionHandle.Load` 的 `CompleteAxes` 会把默认值
    /// 补成 `stable` 进内存——这里再兜一层，是因为手工 `new SessionHandle`（测试与嵌入方）
    /// 也可能不带 `Axes`。</para>
    /// </summary>
    public static bool CacheStable(SessionHandle handle) =>
        handle.State.Axes is not { } axes
        || string.Equals(axes.GetValueOrDefault(Axes.Cache), Axes.CacheStable, StringComparison.Ordinal);

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
    /// <param name="tail">尾块的输入（<see cref="TailContext"/>）。⚠️ **要装配一个会话的三块，
    /// 就用 <see cref="TailContext.For"/>**——它是唯一一处派生规则，三个装配点共用。
    /// 轴值、插件贡献、派生关系快照这三样是它拿不到的（要调用方先取），别的全部由 handle 派生。</param>
    public static PromptSlots Render(
        PresetStore presets, PresetStore premiers,
        string? systemPresetId, string? premierId,
        TailContext tail) =>
        Build(
            // ⚠️ 传**可空**的预设正文，不在这里折成 `SystemFallback`：那一折会抹掉
            // 「文件不在」与「文件在」的区分，而 `SystemBody` 的判据正是它
            // （文件不在时兜底里已含机制说明；文件在时补一句）。
            Load(presets, systemPresetId),
            Load(premiers, premierId),
            tail);

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
    internal static PromptSlots Build(string? systemText, string? premierText, TailContext tail) =>
        new(
            System: Text(SystemBody(systemText, tail.Plugin?.Text(PromptSlot.System))),
            Premier: Wrap(CorePrompts.PremierOpen,
                Join(premierText, tail.Plugin?.Text(PromptSlot.Primary)), CorePrompts.PremierClose),
            Tail: Wrap(CorePrompts.TailOpen, TailBody(tail), CorePrompts.TailClose));

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
    /// 尾块的正文——框架自述、轴表与等级解释、子会话那两行、计划树与它的说明、
    /// 上次调用上下文长度、插件浮动字段，以及（目标 / 压缩 / iterate 提醒那几条）「此刻该做什么」。
    ///
    /// <para>⚠️ <b>它只读 <see cref="TailContext"/>，不再逐个接六个可空参数</b>（2026-10-27）：
    /// 那些参数由三处调用点各自拼，漏传在编译期完全合法——头里因此长期看不到计划树与上下文长度。
    /// 今天「有哪几项」由记录说清，「值从哪来」由 <see cref="TailContext.For"/> 一处派生。</para>
    ///
    /// <para>⚠️ <b>自述是恒定的，所以它不参与「有没有内容」的判定</b>：判据是
    /// <b>有没有轴表或插件浮动字段</b>。若把自述也算进去，一个没有内容的会话会得到一个只有自述的尾块
    /// ——而那正是 <see cref="Wrap"/> 那条「空的不能长得像有内容」要防的。</para>
    /// </summary>
    private static string? TailBody(TailContext tail)
    {
        var axes = tail.Axes;
        var table = AxisTable(axes);
        var planText = PlanTree.Render(tail.Plan);
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
        // ⚠️ **提醒由标志决定，不由「树现在是空的」决定**（2026-10-26）：树空而模型没切模式时
        // 每步都念一遍就成了噪音——用户要的是「刚用完那一步说一次」。
        // ⇒ 判据是 `SessionHandle.PendingIterateReplan`（`Stepper` 在计划刚被推空时置位，
        // 装配进尾块之后立刻清掉，所以它天然只出现一次）。
        var iterateDirective = tail.IterateReplan && axes.GetValueOrDefault(Axes.Initiative) == Axes.Iterate
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
        // ⚠️ 子会话那两行**成对**出现（2026-10-26）：状态行 + 一条机制约定（回报会自动回来，
        // 别去轮询、别催）。没有派生关系时两句都不出现——见 `SubagentNorm` 的注释。
        var subagentNorm = string.IsNullOrEmpty(tail.SubagentLine) ? null : CorePrompts.SubagentNorm;
        // ⚠️ **越过 900k 之后那句话替换掉 goal / iterate 那条，而不是与它并列**（2026-10-27）：
        // 那一格没有选择——「随时可以压」会让「现在就压」变软，而这一步正是最不该被稀释的。
        var compactionAdvice = tail.ContextFull
            ? CorePrompts.ContextOverflowDirective
            : axes.GetValueOrDefault(Axes.Initiative) is "goal" or Axes.Iterate
                ? CorePrompts.AutonomousCompactionDirective
                : null;
        var body = Join(table is null ? null : CorePrompts.TailAxesPrefix + table,
            Join(descriptions.ToArray()), tail.SubagentLine, subagentNorm,
            string.IsNullOrEmpty(planText) ? null : planText, planDirective, iterateDirective,
            tail.UsageLine, compactionAdvice, tail.GoalDirective, tail.IterateDirective,
            tail.CompactDirective, tail.Plugin?.Text(PromptSlot.Floating));
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

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
///     文件不在 ⇒ 回落内置模板；文件空 ⇒ 无块。</description></item>
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
/// <para>⚠️ <b>别在这里做「装配器」</b>：今天只有三块、且没有插件贡献者，
/// 所以这里是三段直白的逻辑。<b>插件宿主落地时</b>（贡献可以是任意多段、任意顺序、
/// 要收集 + 排序 + 拼合）才配一个 `PromptAssembler`。在那之前建它是空壳。</para>
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
        PromptContributionSnapshot? pluginPrompt = null) =>
        new(
            System: Text(Join(Load(presets, systemPresetId) ?? CorePrompts.SystemFallback,
                pluginPrompt?.Text(PromptSlot.System)) ?? ""),
            Premier: Wrap(CorePrompts.PremierOpen,
                Join(Load(premiers, premierId), pluginPrompt?.Text(PromptSlot.Primary)), CorePrompts.PremierClose),
            Tail: Wrap(CorePrompts.TailOpen,
                Join(TailBody(axes), pluginPrompt?.Text(PromptSlot.Floating)), CorePrompts.TailClose));

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
    /// 尾块的正文——**自述那一句 + 轴表那一行**。
    ///
    /// <para>⚠️ <b>自述是恒定的，所以它不参与「有没有内容」的判定</b>：判据是
    /// <b>有没有轴表</b>。若把自述也算进去，一个没有轴的会话会得到一个只有自述的尾块
    /// ——而那正是 <see cref="Wrap"/> 那条「空的不能长得像有内容」要防的。</para>
    /// </summary>
    private static string? TailBody(IReadOnlyDictionary<string, string> axes) =>
        AxisTable(axes) is { } table
            ? $"{CorePrompts.TailDeclaration}\n{CorePrompts.TailAxesPrefix}{table}"
            : null;

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

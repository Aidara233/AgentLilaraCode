namespace Lilara.Core.Kernel;

/// <summary>
/// **问用户的两种事**（`01` §3.8）。
///
/// <para>⚠️ <b>判据是「答完谁动手」，不是「谁问的」</b>：
/// <see cref="Permission"/> 答完是**核心**去触发那个被挂起的调用；
/// <see cref="Question"/> 答完**答案本身就是内容**（模型读它，自己决定下一步）。
/// ⇒ 按「谁问的」切（闸门 / 工具 / 子会话 / 定时器…）是无界的，按「答完谁动手」切只有两个。</para>
///
/// <para>⚠️ **子会话上提不是第三个 Kind**：`approvals` 是**全局流**，载荷里的 `sessionId`
/// 就是「该回答谁」⇒ 子会话的问自然落在它自己的会话上（`03` §5 把审批放在独立流上买到的）。</para>
/// </summary>
public enum AskKind
{
    /// <summary>授权：准不准那个调用。⚠️ 答「准」⇒ 核心执行它；答「不准」⇒ 落一条拒绝结果。</summary>
    Permission,

    /// <summary>问意见：答案就是内容。⚠️ 核心**不据此动手**——它只是喂回模型。</summary>
    Question,
}

/// <summary>
/// **一个问题长什么样**——它决定卡片、载荷、恢复路径三件事（`01` §3.8）。
///
/// <para>⚠️ <b>只有三种，而「确认」不在其中</b>：一个布尔问题就是
/// <see cref="Choice"/> 少一个选项。「确认」不是第四种形状——**别再加一种**
/// （本仓对「少一种形状」有硬偏好）。</para>
///
/// <para>⚠️ <b>「改写参数后批准」也不是第四种</b>：那是 <see cref="Verbatim"/>，
/// 而它只在 <see cref="AskKind.Permission"/> 里出现。</para>
/// </summary>
public enum AskShape
{
    /// <summary>从几个选项里挑。⚠️ 选项**可以只有两个**（那就是「确认」）。</summary>
    Choice,

    /// <summary>自由文本。</summary>
    Text,

    /// <summary>一问多个字段。</summary>
    Form,

    /// <summary>
    /// **模型给的那段参数原文**，给用户改。
    ///
    /// <para>⚠️ <b>它只有核心造得出来</b>：造它需要知道「参数叫什么」（`arguments`），
    /// 而那个名字是**核心与协议之间的约定**，工具不知道。
    /// ⇒ 用它下面的 <see cref="AskItem.Verbatim"/> 造，别在工具里手拼一个
    /// <see cref="Form"/> 冒充——冒充的那个字段名是**工具编的**，而执行时读的是
    /// <c>arguments</c>。</para>
    /// </summary>
    Verbatim,
}

/// <summary>
/// 一个选项（<see cref="AskShape.Choice"/> 用）。
/// </summary>
/// <param name="Value">回给核心的**值**。</param>
/// <param name="Label">
/// 给人看的**标签**。⚠️ 空 = 就用 <paramref name="Value"/>。
/// 两者分开是因为它们可以不同（值是 `b`、标签是「方案 B：先做接口」），
/// 而合并成一个会让头**只能显示机器话**。
/// </param>
public sealed record AskOption(string Value, string Label = "");

/// <summary>
/// 一个表单字段（<see cref="AskShape.Form"/> / <see cref="AskShape.Verbatim"/> 用）。
///
/// <para>⚠️ <b><see cref="Value"/> 是<b>值</b>，不是控件</b>：它同时承担「初始值」与
/// 「用户填的东西」。⚠️ 本仓**没有**「控件类型」这个概念，也不打算有——
/// 头按 <see cref="AskItem.Shape"/> 决定画什么。</para>
/// </summary>
/// <param name="Name">回给核心的键。⚠️ 由**提问者**定。</param>
/// <param name="Label">给人看的标签。</param>
/// <param name="Value">初始值（可空）。</param>
public sealed record AskField(string Name, string Label, string? Value = null);

/// <summary>
/// **一个问题**。
///
/// <para>⚠️ <b>一次询问是一个信封，不是一条记录</b>：下面那张表里的任一组合都只对应
/// **一次** <c>tool_use</c> ↔ **一条** <c>tool_result</c>。
/// 「一条 use 对多条 result」会让记法本身改掉（`01` §3.6 ① 的配对是结构性的）——
/// 而多问这件事不必付那个代价：模型自己知道哪个答案是哪个问题的（它写的 `question` 原样回来的）。</para>
///
/// <para>⚠️ <b><see cref="Id"/> 是稳定的</b>，它是授权卡关联的键、也是记忆的键；
/// <b><see cref="Question"/> 是给人看的</b>。两者不许互换。</para>
/// </summary>
public sealed record AskItem
{
    /// <summary>稳定 id。⚠️ 同一个信封里唯一。</summary>
    public required string Id { get; init; }

    public required AskShape Shape { get; init; }

    /// <summary>问什么。⚠️ 这是**给人看的那句话**。</summary>
    public required string Question { get; init; }

    /// <summary><see cref="AskShape.Choice"/> 用。⚠️ 其余形状恒空。</summary>
    public IReadOnlyList<AskOption> Options { get; init; } = [];

    /// <summary>
    /// <see cref="AskShape.Text"/> 用。初始值（可空）。
    /// ⚠️ <b>这里<b>不</b>放「参数名」</b>——Text 命名的那个问题是「工具自己的」，
    /// 而它自己知道；想让模型看到「答的是哪个字段」，把那个信息写进
    /// <see cref="Question"/>。
    /// </summary>
    public string? Value { get; init; }

    /// <summary><see cref="AskShape.Form"/> / <see cref="AskShape.Verbatim"/> 用。</summary>
    public IReadOnlyList<AskField> Fields { get; init; } = [];

    /// <summary>多选？⚠️ 只对 <see cref="AskShape.Choice"/> 有意义。</summary>
    public bool Multi { get; init; }

    // ---- 工厂：形状约束挤在这里，于是「造一个非法的问」不可达 ----
    //
    // ⚠️ **判据**：一个形状该带哪些格子，看它**实际用得到什么**——
    // Choice 只用 Options，Text 只用 Value，Form/Verbatim 用 Fields。
    // 「所有形状共用一个大 record + 随便填」会让非法组合（一个带 Options 的 Text）**长得合法**。

    public static AskItem Choice(
        string id, string question, IReadOnlyList<string> options, bool multi = false) => new()
    {
        Id = id,
        Shape = AskShape.Choice,
        Question = question,
        // ⚠️ 空标签 = 用值（见 `AskOption.Label`）。
        Options = [.. options.Select(o => new AskOption(o))],
        Multi = multi,
    };

    /// <summary>
    /// 自由文本。
    ///
    /// <para>⚠️ <b>不给「字段名」</b>：Text 的答案直接就是内容，核心不按名取用
    /// （对比 <see cref="Form"/>——那里的名是提问者要用的）。</para>
    /// </summary>
    public static AskItem Text(string id, string question, string? prefill = null) => new()
    {
        Id = id,
        Shape = AskShape.Text,
        Question = question,
        Value = prefill,
    };

    public static AskItem Form(string id, string question, IReadOnlyList<AskField> fields) => new()
    {
        Id = id,
        Shape = AskShape.Form,
        Question = question,
        Fields = fields,
    };

    /// <summary>
    /// **参数原文，给用户改**（「改写后批准」）。⚠️ <b>只有核心能造</b>——见 <see cref="AskShape.Verbatim"/>。
    ///
    /// <para>⚠️ 字段名是 <c>arguments</c>：那是核心与协议之间的约定，
    /// 而用户改完的东西会**原样**成为下一次执行用的 <c>ArgumentsJson</c>
    /// （同 `ToolCall.ArgumentsJson` 的逐字铁律）。</para>
    /// </summary>
    public static AskItem Verbatim(string id, string question, string originalJson) => new()
    {
        Id = id,
        Shape = AskShape.Verbatim,
        Question = question,
        Fields = [new AskField("arguments", "参数（可改写）", originalJson)],
    };
}

/// <summary>
/// 可选的记忆档位（`01` §3.8）。
///
/// <para>⚠️ <b>两档的键不同，粗细分明</b>：<see cref="Args"/> 是
/// <c>(工具名, 参数哈希)</c>；<see cref="Tool"/> 是 <c>(工具名)</c>——**那一档是粗的**，
/// 卡片上必须写明「本会话内不再问**任何** `X` 调用」，不许与「记住这些参数」
/// 并排成两个长得差不多的复选框。</para>
///
/// <para>⚠️ 「这一次」不在这个枚举里——它是**默认**（不选任何一档 = 不写记忆），
/// 当成一个枚举成员会让「没选」与「选了这一次」两件事长得一样。</para>
/// </summary>
public enum AskMemory
{
    /// <summary><c>(工具名, 参数哈希)</c>。⚠️ 参数<b>完全一样</b>才命中——用户批一次 `rm -rf build/`，`rm -rf src/` **照样要问**。</summary>
    Args,

    /// <summary><c>(工具名)</c>。⚠️ <b>粗的那一档</b>——放行的是这个工具在本会话里的**任何**参数。</summary>
    Tool,
}

/// <summary>
/// **一次询问**。
///
/// <para>⚠️ <b>它是一次<b>返回</b>，不是一次等待</b>：谁发起询问，谁就**中止**自己的
/// 那一趟并把它交出去。让 `ToolExecutor` 去 <c>await</c> 一个可能永不回来的任务，
/// 等于**把会话的推进权借给一个将来**——取消、中断、重启全都要重新长一套。
/// 而「返回」这条路**本来就在**（`Stepper` 的一步结束 ⇒ 交给 `CommandDispatcher`）。</para>
/// </summary>
public sealed record AskRequest
{
    /// <summary>稳定 id：授权卡关联的键 · 记忆的键 · `approval.respond` 的键。</summary>
    public required string Id { get; init; }

    public required AskKind Kind { get; init; }

    /// <summary>问题。⚠️ <b>恒非空</b>——一个没有问题的询问不是询问。</summary>
    public required IReadOnlyList<AskItem> Items { get; init; }

    /// <summary>
    /// 允许用户选「记住」的档位。⚠️ <b>空 = 这张卡不给记忆选项</b>
    /// （<see cref="AskKind.Question"/> 就是这样——**问意见没有「记住」这回事**）。
    /// </summary>
    public IReadOnlyList<AskMemory> Remember { get; init; } = [];
}

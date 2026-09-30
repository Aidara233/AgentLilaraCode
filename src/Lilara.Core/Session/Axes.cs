namespace Lilara.Core.Session;

/// <summary>
/// 行为控制轴（`01` §3.5）——**五轴的唯一真相来源**。
///
/// <para>⚠️ 它是一张**表**，不是五个独立的散落判断。轴名、合法值、默认值三样放在一起，
/// 是因为它们必须同步演进：加一个档而忘了改默认值或错误信息，正是这类表最常见的漂移。
/// 任何别处要用这些值（头的下拉、控制台的解析、错误信息）都**从这张表读**，不许手抄。</para>
///
/// <para>⚠️ **但那是要去的方向，不是现状**，别照着写：
/// `ClaudeRequestBuilder.ToEffort` 就是一份**手抄的第二副本**（已知，计划 G10 那类，见下
/// `Effort` 那条）；而那面头的下拉**今天还不存在**（`head/` 里没有任何轴字面量）。</para>
///
/// <para>⚠️ **`effort` 在表里，但它不进上下文**（`01` §3.5：纯请求参数）。
/// 它仍然是一条轴：用户要能改它，机械层（请求参数）也要跟着变。
/// 「给头看 ≠ 给模型看」——头显示它是正确的，把它塞进提示词才是错的。</para>
/// </summary>
public static class Axes
{
    public const string Initiative = "initiative";
    public const string Permission = "permission";
    public const string Safety = "safety";
    public const string Verify = "verify";
    public const string Effort = "effort";

    /// <summary>⚠️ 顺序**就是**渲染顺序，别按字母排。</summary>
    public static readonly IReadOnlyList<string> Names =
        [Initiative, Permission, Safety, Verify, Effort];

    /// <summary>
    /// ⚠️ 值类型是 <see cref="IReadOnlyList{T}"/> 而**不是** `string[]`，且每个值都被
    /// <see cref="Array.AsReadOnly{T}"/> 包过一层：`Dictionary` 只是阻止**换掉**条目，
    /// 挡不住调用方拿到数组后**就地改元素**。
    ///
    /// <para>后果不是理论上的：改的是**进程级静态表**，`TryParse` / `LegalValuesText`
    /// 以及**任何从本表读的调用方**会一起变，且**不报错**——那正是这个类声称要消灭的
    /// 漂移，只是换成了运行期版本。</para>
    ///
    /// <para>⚠️ 当时那 7 条测试**兜不住**它，但「兜不住」有边界，别写成全称句：
    /// 它们每次都重读**同一份已污染的表**，所以只要被改的元素**既不是默认值、
    /// 也不是哪条断言点名的值**，就一条都不红。改 `safety[0]` 则相反，是抓得到的——
    /// 默认值 `dev` 会变非法（`Defaults_CoverEveryAxis…` 红）、
    /// 而 `production` 由非法变合法（`UnknownValue_IsRefused…` 红）。
    /// ⇒ 结论不是「测试写得差」，而是**靠现有断言兜底不可靠，得靠类型本身挡**。</para>
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> Table = new(StringComparer.Ordinal)
    {
        // 主动性 = 循环的停止条件（§3.5）。档名就是**本表定义的**串。
        // 范围要说准：**落盘**（`state.json` 的 `Axes`）与**控制面快照**
        //（`ControlServer` 整份序列化 `SessionState`）携带的轴值都以这里为源
        //（`SessionHandle` 用 `Defaults()` / `TryParse` 播种，见 `SessionHandle.cs:155,165`）。
        // ⚠️ 但**模型请求**那条路**不是**：`ClaudeRequestBuilder.ToEffort` 是一份
        // **手抄的第二副本**（已知，即计划 G10 说的那类）——改这里**改不到它**，两头要一起改。
        [Initiative] = Array.AsReadOnly<string>(["step", "standard", "goal"]),

        // 权限 = 闸门。0..5 单调阶梯，低档是拒绝线、高档是询问线。
        [Permission] = Array.AsReadOnly<string>(
            ["off", "readonly", "investigate", "standard", "authorized", "trusted"]),

        // 安全 = 纯提示。⚠️ 没有「机械兜底」——不设「生产 ⇒ 权限封顶」（§3.5 明说不许）。
        [Safety] = Array.AsReadOnly<string>(["dev", "staging", "prod"]),

        [Verify] = Array.AsReadOnly<string>(["none", "self", "strict"]),

        // ⚠️ 与 `ClaudeRequestBuilder.ToEffort`（`ClaudeRequestBuilder.cs:164-173`，5 条臂
        // 166-170 + 一条 `_ => throw` 在 171）**必须逐值对齐**，但两个方向的后果**不同**，
        // 别压成一句因果——它们的箭头的**方向是相反的**：
        //   ① 表**少**一档（如缺 `xhigh`）⇒ 表先把它**拒掉**（`TryParse` 返回 false），
        //      用户少一档能力、请求根本发不出去 —— **不会**抛异常（`ToEffort` 没机会看到）。
        //   ② 表**多**一档（`ToEffort` 不认的值）⇒ 表放行、走到 `_ =>` 抛
        //      `ArgumentException` —— 这才是「放行之后才炸」。
        // ⇒ 两个集合应当**相等**（本表 5 档 = `ToEffort` 的 5 条臂）。
        // ⚠️ `01` §3.5 只写三档（低/中/高），这里取五档是**故意的**：**文档待对齐**；
        // 多出来的两档是 SDK 已有的能力，不是这里新造的。
        [Effort] = Array.AsReadOnly<string>(["low", "medium", "high", "xhigh", "max"]),
    };

    private static readonly Dictionary<string, string> DefaultTable = new(StringComparer.Ordinal)
    {
        [Initiative] = "step",
        [Permission] = "standard",
        // ⚠️ 默认落在**非告警**档。告警条件（`01` §3.5）：`safety==prod`
        // **且** `permission ∈ {authorized, trusted}`——两轴同时满足才响。
        // 反过来说：默认权限是 `standard`，所以**即便把这里改成 `prod` 也不会响一声**，
        // 「设了会吵」不是拒绝它的理由。真正的理由是 §3.5 那句「安全是提醒装置」：
        // 一个默认就自称生产环境的提醒装置，会在真项目上变成噪音、被用户学会无视。
        [Safety] = "dev",
        [Verify] = "self",
        // ⚠️ 与建会话时的既有默认一致：`CommandDispatcher.HandleSessionCreate`
        // 调 `ToSelection(provider, null, "high", 16000)`——改这里就得同时改那里，
        // 否则新会话的轴显示一个值、实际请求参数是另一个。
        [Effort] = "high",
    };

    /// <summary>
    /// ⚠️ **三条入口一律把 <see langword="null"/> 当「未知轴」**（返 false / null / false），
    /// 所以形参标成 `string?`——这不是防御性编程，是**可达路径**。
    ///
    /// <para>来路：`WireJson.Options`（`WireJson.cs:25-32`）**没有**开
    /// `RespectNullableAnnotations`，所以线上的 <c>{"kind":"session.setAxis","axis":null}</c>
    /// 会一路落到这里；而 <c>Dictionary</c> 的 **key 不允许为 null**。实测（修前）那条
    /// <c>SetAxis_WithANullAxis_IsRejected_NotACrash</c> 就是红的，栈是
    /// <c>IsKnown(null) ⇒ ContainsKey(null) ⇒ ArgumentNullException「Parameter 'key'」</c>。
    /// 控制面外层是兜底 <c>catch (Exception)</c>（`ControlServer.cs:323`），连接不会死，
    /// 但用户看到的是「帧形状不对」——而真正的问题是**轴名没给**。</para>
    ///
    /// <para>⚠️ **判在这里而不是各调用方**：本类是「轴名 → 合法值」这个契约的拥有者，
    /// 而 <see cref="ValuesOf"/> 早就声明了「未知 → null」——**null 就是未知的一种**。
    /// 三条入口今天**只有 <see cref="IsKnown"/> 会被喂到 null**（`session.setAxis` 那条路，
    /// 见上；另外两条的调用方只传非空轴），但契约不该按调用点拆——
    /// 把规则留在调用方，下一个调用点就得自己重新记得一遍。
    /// 顺带那条 <see cref="LegalValuesText"/> 是 <see cref="ValuesOf"/> 的包装，
    /// 不用单独判（null ⇒ 空串）。</para>
    /// </summary>
    public static bool IsKnown(string? axis) => axis is not null && Table.ContainsKey(axis);

    /// <summary>该轴的合法取值。<see langword="null"/> = 未知轴。返回的是只读视图，改不动。</summary>
    public static IReadOnlyList<string>? ValuesOf(string? axis)
    {
        // ⚠️ 不写成 `axis is not null && Table.TryGetValue(axis, out var v) ? v : null`：
        // 短路成功时 `v` 有值，短路失败时 `v` **未赋值**（不是 null）⇒ CS0165。
        if (axis is null) return null;
        return Table.TryGetValue(axis, out var v) ? v : null;
    }

    /// <summary>合法值清单（错误信息用）。⚠️ 从表里拼，不手抄。</summary>
    public static string LegalValuesText(string? axis) =>
        ValuesOf(axis) is { } v ? string.Join("|", v) : "";

    /// <summary>
    /// 校验并规范化（`PROD` → `prod`）。
    ///
    /// <para>⚠️ **两半的大小写策略是刻意不同的，别"统一"成一个：**</para>
    /// <list type="bullet">
    ///   <item><b>axis 敏感</b>（`Table` 用 <see cref="StringComparer.Ordinal"/>）——
    ///         轴名是协议标识符，`Safety` 不是 `safety`，应当**拒绝**而不是猜。</item>
    ///   <item><b>value 不敏感</b>（<see cref="StringComparison.OrdinalIgnoreCase"/>）——
    ///         取值是用户手打的，写 `Prod` 是常态。</item>
    /// </list>
    /// <para>所以 `TryParse("safety", "PROD")` 为真，而 `TryParse("Safety", "dev")` 为假。
    /// 两句都由 <c>AxisTests</c> 各钉一条，改动前先看那两条。</para>
    /// </summary>
    public static bool TryParse(string? axis, string value, out string canonical)
    {
        canonical = "";
        if (axis is null) return false;   // ⚠️ null 键会让 `Dictionary` 抛，见 IsKnown 的注释
        if (!Table.TryGetValue(axis, out var values)) return false;

        foreach (var v in values)
        {
            if (!string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) continue;
            canonical = v;
            return true;
        }
        return false;
    }

    /// <summary>默认值。**每次返回新字典**：调用方要往 handle 上挂，共享可变实例会串会话。</summary>
    public static IReadOnlyDictionary<string, string> Defaults() =>
        new Dictionary<string, string>(DefaultTable, StringComparer.Ordinal);
}

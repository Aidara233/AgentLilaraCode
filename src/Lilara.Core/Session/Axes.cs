namespace Lilara.Core.Session;

/// <summary>
/// 行为控制轴（`01` §3.5）——**五轴的唯一真相来源**。
///
/// <para>⚠️ 它是一张**表**，不是五个独立的散落判断。轴名、合法值、默认值三样放在一起，
/// 是因为它们必须同步演进：加一个档而忘了改默认值或错误信息，正是这类表最常见的漂移。
/// 任何别处（头的下拉、控制台的解析、错误信息）都**从这张表读**，不许手抄。</para>
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
    /// <para>后果不是理论上的：`((string[])Axes.ValuesOf("safety"))[2] = "production"`
    /// 改的是**进程级静态表**，`TryParse` / `LegalValuesText` / 头的下拉会**一起变**——
    /// 而当时那 7 条测试**一条都发现不了**（它们每次都重读被改的同一份表）。
    /// 那正是这个类声称要消灭的漂移，只是换成了运行期版本。</para>
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> Table = new(StringComparer.Ordinal)
    {
        // 主动性 = 循环的停止条件（§3.5）。档名就是**本表定义的**串：
        // 线上协议与落盘携带的值以这里为源，改这里即改协议（故不能随手重命名）。
        [Initiative] = Array.AsReadOnly<string>(["step", "standard", "goal"]),

        // 权限 = 闸门。0..5 单调阶梯，低档是拒绝线、高档是询问线。
        [Permission] = Array.AsReadOnly<string>(
            ["off", "readonly", "investigate", "standard", "authorized", "trusted"]),

        // 安全 = 纯提示。⚠️ 没有「机械兜底」——不设「生产 ⇒ 权限封顶」（§3.5 明说不许）。
        [Safety] = Array.AsReadOnly<string>(["dev", "staging", "prod"]),

        [Verify] = Array.AsReadOnly<string>(["none", "self", "strict"]),

        // ⚠️ 与 `ClaudeRequestBuilder.ToEffort` 的取值**必须一致**——
        // 少了对应关系，轴改了而请求参数没改，用户看到的是「设了没用」。
        // ⚠️ `01` §3.5 只写三档（低/中/高），这里取五档是**故意的**：表必须覆盖
        // `ToEffort` 认的全部值，否则用户写 `xhigh` 能被表放行、却在建请求时抛异常。
        // ⇒ **文档待对齐**（多出来的两档是 SDK 已有的能力，不是这里新造的）。
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

    public static bool IsKnown(string axis) => Table.ContainsKey(axis);

    /// <summary>该轴的合法取值。<see langword="null"/> = 未知轴。返回的是只读视图，改不动。</summary>
    public static IReadOnlyList<string>? ValuesOf(string axis) =>
        Table.TryGetValue(axis, out var v) ? v : null;

    /// <summary>合法值清单（错误信息用）。⚠️ 从表里拼，不手抄。</summary>
    public static string LegalValuesText(string axis) =>
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
    public static bool TryParse(string axis, string value, out string canonical)
    {
        canonical = "";
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

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

    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        // 主动性 = 循环的停止条件（§3.5）。档名用英文，与线上协议一致。
        [Initiative] = ["step", "standard", "goal"],

        // 权限 = 闸门。0..5 单调阶梯，低档是拒绝线、高档是询问线。
        [Permission] = ["off", "readonly", "investigate", "standard", "authorized", "trusted"],

        // 安全 = 纯提示。⚠️ 没有「机械兜底」——不设「生产 ⇒ 权限封顶」（§3.5 明说不许）。
        [Safety] = ["dev", "staging", "prod"],

        [Verify] = ["none", "self", "strict"],

        // ⚠️ 与 `ClaudeRequestBuilder.ToEffort` 的取值**必须一致**——
        // 少了对应关系，轴改了而请求参数没改，用户看到的是「设了没用」。
        [Effort] = ["low", "medium", "high", "xhigh", "max"],
    };

    private static readonly Dictionary<string, string> DefaultTable = new(StringComparer.Ordinal)
    {
        [Initiative] = "step",
        [Permission] = "standard",
        [Safety] = "dev",       // ⚠️ 绝不能默认「生产」——那会让告警每步都响
        [Verify] = "self",
        // ⚠️ 与建会话时的既有默认一致：`CommandDispatcher.HandleSessionCreate`
        // 调 `ToSelection(provider, null, "high", 16000)`——改这里就得同时改那里，
        // 否则新会话的轴显示一个值、实际请求参数是另一个。
        [Effort] = "high",
    };

    public static bool IsKnown(string axis) => Table.ContainsKey(axis);

    public static IReadOnlyList<string>? ValuesOf(string axis) =>
        Table.TryGetValue(axis, out var v) ? v : null;

    /// <summary>合法值清单（错误信息用）。⚠️ 从表里拼，不手抄。</summary>
    public static string LegalValuesText(string axis) =>
        ValuesOf(axis) is { } v ? string.Join("|", v) : "";

    /// <summary>校验并规范化。大小写不敏感（用户会写 `Prod`）。</summary>
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

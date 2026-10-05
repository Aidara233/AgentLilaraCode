namespace Lilara.Core.Kernel;

/// <summary>
/// 行为控制轴（`01` §3.5）——**五轴的唯一真相来源**。
///
/// <para>⚠️ 它是一张**表**，不是五个独立的散落判断。轴名、中文名、合法值、默认值、
/// 进不进上下文**五样放在一起**（见 <see cref="Spec"/>），是因为它们必须同步演进：
/// 加一个档而忘了改默认值或错误信息，正是这类表最常见的漂移。
/// 任何别处要用这些值（头的下拉、控制台的解析、错误信息）都**从这张表读**，不许手抄。</para>
///
/// <para>⚠️ **但那是要去的方向，不是现状**，别照着写：**手抄的第二副本已经有两份**——
/// `ClaudeRequestBuilder.ToEffort`（已知，计划 G10 那类，见 <see cref="Effort"/> 那条）
/// 与 `head/wwwroot/app.js` 里的 `"safety"` / `"permission"` 两处字面量。
/// 改这里的常量，那两处会**静默**错配（判据恒 false，相应的下拉与告警就不画了）。</para>
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

    /// <summary>安全轴里代表「生产」的档名（`01` §3.5 的跨轴告警的一端）。</summary>
    public const string AlertSafety = "prod";

    /// <summary>权限轴里「授权及以上」的**起始档**（`01` §3.5 的跨轴告警的另一端）。</summary>
    public const string AlertFrom = "authorized";

    /// <summary>
    /// 轴的**全部**静态事实，按渲染顺序排（`01` §3.5 表格的顺序）。
    /// ⚠️ **单一张表**：轴名 / 中文名 / 合法值 / 默认值 / 进不进上下文**都从这里读**。
    /// 拆成几个平行容器就会漂——`Names` 有序而 `Dictionary` 枚举序未定义（`01` §9.1：
    /// 「一次 `Remove` 后新 `Add` 会复用空槽跑到前面」）。
    ///
    /// <para>⚠️ <see cref="Values"/> 的类型是 <see cref="IReadOnlyList{T}"/> 而**不是** `string[]`，
    /// 且每个值都被 <see cref="Array.AsReadOnly{T}"/> 包过一层：交出去的是**只读视图**，
    /// 调用方拿不到可写数组——否则改一处就等于改了**进程级静态表**（`TryParse` /
    /// `LegalValuesText` 以及**任何从本表读的调用方**会一起变，且**不报错**）。</para>
    /// </summary>
    private sealed record Spec(
        string Name, string Label, IReadOnlyList<string> Values, string Default, bool InContext);

    /// <summary>
    /// 五轴的**唯一**一张表。顺序**就是**渲染顺序（`01` §3.5 表格），别按字母排。
    /// </summary>
    private static readonly IReadOnlyList<Spec> Table =
    [
        // 主动性 = 循环的停止条件（§3.5）。档名就是**本表定义的**串。
        // 范围要说准：**落盘**（`state.json` 的 `Axes`）与**控制面快照**
        //（`ControlServer` 整份序列化 `SessionState`）携带的轴值都以这里为源
        //（`SessionHandle` 用 `Defaults()` / `TryParse` 播种，见 `SessionHandle.cs:155,165`）。
        // ⚠️ 但**模型请求**那条路**不是**：`ClaudeRequestBuilder.ToEffort` 是一份
        // **手抄的第二副本**（已知，即计划 G10 说的那类）——改这里**改不到它**，两头要一起改。
        new(Initiative, "主动性",
            Array.AsReadOnly<string>(["step", "standard", "goal"]),
            "step", InContext: true),

        // 权限 = 闸门。0..5 单调阶梯，低档是拒绝线、高档是询问线。
        // ⚠️ 档位**顺序是契约**：头的跨轴告警按
        // `values.indexOf(当前) >= values.indexOf(AlertFrom)` 判「授权及以上」——
        // 重排或插入第 6 档会让告警静默报错档位（正是 `01` §3.5 要防的那种静默失效）。
        new(Permission, "权限",
            Array.AsReadOnly<string>(
                ["off", "readonly", "investigate", "standard", "authorized", "trusted"]),
            "standard", InContext: true),

        // 安全 = 纯提示。⚠️ 没有「机械兜底」——不设「生产 ⇒ 权限封顶」（§3.5 明说不许）。
        // ⚠️ 默认落在**非告警**档（见下 Default 那条注释）。
        new(Safety, "安全",
            Array.AsReadOnly<string>(["dev", "staging", "prod"]),
            "dev", InContext: true),

        new(Verify, "验证强度",
            Array.AsReadOnly<string>(["none", "self", "strict"]),
            "self", InContext: true),

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
        // ⚠️ `effort` 是**唯一** `InContext = false` 的（`01` §3.5：纯请求参数，不进上下文）。
        new(Effort, "思考强度",
            Array.AsReadOnly<string>(["low", "medium", "high", "xhigh", "max"]),
            "high", InContext: false),
    ];

    /// <summary>按轴名找那一行。<see langword="null"/> 与未知轴都返回 <see langword="null"/>。</summary>
    private static Spec? Find(string? axis) =>
        axis is null ? null : Table.FirstOrDefault(s => s.Name == axis);

    /// <summary>
    /// 轴名，**按渲染顺序**（`01` §3.5 表格的顺序）。
    /// ⚠️ **从 <see cref="Table"/> 派生**，不是另写一份名单——另写就有第二个地方要同步，
    /// 而顺序不稳等于每步重排。派生之后「标签与轴名同序」这条约定**不再存在**：
    /// 它们本来就是同一行的两个字段（原本 `Names` 有序、`Labels` 是 `Dictionary`，枚举序未定义）。
    /// </summary>
    public static readonly IReadOnlyList<string> Names = Table.Select(s => s.Name).ToList();

    /// <summary>
    /// 轴的中文名（`01` §3.5 的表格）。⚠️ **它归核心**（`05` §3：渲染归核心，头只显示）——
    /// 头的下拉要画 `主动性` / `安全`，而它拿到的只有英文档名。
    ///
    /// <para>⚠️ **未知轴原样返回**（与 <see cref="ValuesOf"/> 的「未知 → null」同一套约定）；
    /// **`null` 返回空串**——`null` 是「轴名没给」，没有半截标签可回吐，给空串与「未知轴」同向。</para>
    /// </summary>
    public static string LabelOf(string? axis) => Find(axis)?.Label ?? (axis ?? "");

    /// <summary>
    /// 这条轴的取值**进不进上下文**（`01` §3.5）。
    /// ⚠️ 只有 `effort` 是 `false`——它是**纯请求参数**，模型不必被告知自己的推理预算。
    /// 头据此把它画灰；写进提示词才是错的（那句话是全系统每步都付全价的槽位）。
    ///
    /// <para>⚠️ **未知轴与 `null` 都返回 `false`**。理由：本方法的唯一用途是判「要不要把它
    /// 写进提示词」，而一条**叫不出名字**的轴没有可写进上下文的合法取值——宁可**漏写**
    /// （不注入），不可**错写**。这与 <see cref="IsKnown"/> 的「未知即假」同向：
    /// 不知道的轴，一律当作「不进上下文」这一侧。</para>
    /// </summary>
    public static bool InContext(string? axis) => Find(axis)?.InContext ?? false;

    /// <summary>
    /// ⚠️ **所有吃轴名的入口一律把 <see langword="null"/> 当「未知轴」**
    /// （返 false / null / 空串），所以形参标成 `string?`——这不是防御性编程，是**可达性**在契约上的体现。
    ///
    /// <para>`null` 的来路：`WireJson.Options`（`WireJson.cs:25-32`）**没有**开
    /// `RespectNullableAnnotations`，所以线上的 <c>{"kind":"session.setAxis","axis":null}</c>
    /// 能一路落到这里；而 <c>Dictionary</c> 的 **key 不允许为 null**。实测（修前）那条
    /// <c>SetAxis_WithANullAxis_IsRejected_NotACrash</c> 就是红的，栈是
    /// <c>IsKnown(null) ⇒ ContainsKey(null) ⇒ ArgumentNullException「Parameter 'key'」</c>。
    /// 控制面外层是兜底 <c>catch (Exception)</c>（`ControlServer.cs:323`），连接不会死，
    /// 但用户看到的是「帧形状不对」——而真正的问题是**轴名没给**。</para>
    ///
    /// <para>⚠️ **线上那条路已经接上了**：`ControlServer.ParseCommand` 有 `session.setAxis`
    /// （`b1696b4`），所以 `{"kind":"session.setAxis","axis":null}` 真的能落到这里。
    /// ⚠️ 本段曾经写「**待接**，`ParseCommand` 尚无此命令」——那句在接上之后就成了**假的**，
    /// 而它与 `AxisTests.cs` 里同源的那一句**一起**陈旧了：**两处文字描述同一个代码事实，
    /// 代码一改，两处都没跟上**（G10 换个位置又出现一次）。
    /// ⇒ 别再在这里复述 `ParseCommand` 的细节：**那种句子会腐**。要核就去看那个 switch。</para>
    ///
    /// <para>⚠️ **判在这里而不是各调用方**：本类是「轴名 → 合法值」这个契约的拥有者，
    /// 而 <see cref="ValuesOf"/> 早就声明了「未知 → null」——**null 就是未知的一种**。
    /// 吃轴名的入口一共六条——<see cref="IsKnown"/> · <see cref="ValuesOf"/> ·
    /// <see cref="LegalValuesText"/> · <see cref="TryParse"/> · <see cref="LabelOf"/> ·
    /// <see cref="InContext"/>——**六条都判**（`LegalValuesText` 是 `ValuesOf` 的包装，
    /// 不单独判，null ⇒ 空串）。契约不该按调用点拆——把规则留在调用方，
    /// 下一个调用点就得自己重新记得一遍。</para>
    /// </summary>
    public static bool IsKnown(string? axis) => Find(axis) is not null;

    /// <summary>该轴的合法取值。<see langword="null"/> = 未知轴。返回的是**只读视图**，改不动。</summary>
    public static IReadOnlyList<string>? ValuesOf(string? axis) => Find(axis)?.Values;

    /// <summary>合法值清单（错误信息用）。⚠️ 从表里拼，不手抄。</summary>
    public static string LegalValuesText(string? axis) =>
        ValuesOf(axis) is { } v ? string.Join("|", v) : "";

    /// <summary>
    /// 校验并规范化（`PROD` → `prod`）。
    ///
    /// <para>⚠️ **两半的大小写策略是刻意不同的，别"统一"成一个：**</para>
    /// <list type="bullet">
    ///   <item><b>axis 敏感</b>（<see cref="Find"/> 用 `==`，即 `Ordinal`）——
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
        if (Find(axis) is not { } spec) return false;   // ⚠️ null 键会让 `Dictionary` 抛，见 IsKnown 的注释

        foreach (var v in spec.Values)
        {
            if (!string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) continue;
            canonical = v;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 默认值。**每次返回新字典**：调用方要往 handle 上挂，共享可变实例会串会话。
    ///
    /// <para>⚠️ 默认值就是 `01` §3.5 表里那个「最没有意外」的档，不是随手挑的。
    /// 尤其 `safety = dev`：真正的理由是 §3.5 那句「安全是提醒装置」——
    /// 一个默认就自称生产环境的提醒装置，会在真项目上变成噪音、被用户学会无视。
    /// （「设了会吵」**不是**理由：默认权限是 `standard`，告警要 `safety==prod`
    /// **且** `permission ∈ {authorized, trusted}` 两轴同时满足才响，单独改成 `prod` 一声不响。）</para>
    ///
    /// <para>⚠️ `effort` 的默认与建会话时的既有默认**必须一致**：
    /// `CommandDispatcher.HandleSessionCreate` 调 `ToSelection(provider, null, "high", 16000)`
    /// ——改这里就得同时改那里，否则新会话的轴显示一个值、实际请求参数是另一个。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Defaults() =>
        Table.ToDictionary(s => s.Name, s => s.Default, StringComparer.Ordinal);
}

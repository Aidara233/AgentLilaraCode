namespace Lilara.Core.Kernel;

using System.Globalization;

/// <summary>
/// 行为控制轴（`01` §3.5）——**轴的唯一真相来源**。
///
/// <para>⚠️ 它是一张**表**，不是几条独立的散落判断。轴名、中文名、合法值、默认值、
/// 进不进上下文及等级解释**放在一起**（见 <see cref="Spec"/>），是因为它们必须同步演进：
/// 加一个档而忘了改默认值或错误信息，正是这类表最常见的漂移。
/// 任何别处要用这些值（头的下拉、控制台的解析、错误信息）都**从这张表读**，不许手抄。</para>
///
/// <para>⚠️ **但那是要去的方向，不是现状**，别照着写：**手抄的第二副本已经有两份**——
/// `ClaudeRequestBuilder.ToEffort`（已知，计划 G10 那类，见 <see cref="Effort"/> 那条）
/// 与 `src/Lilara.Head/wwwroot/app.js` 里的 `"safety"` / `"permission"` 两处字面量。
/// 改这里的常量，那两处会**静默**错配（判据恒 false，相应的下拉与告警就不画了）。</para>
///
/// <para>⚠️ **`effort` 在表里，但它不进上下文**（`01` §3.5：纯请求参数）。
/// 它仍然是一条轴：用户要能改它，机械层（请求参数）也要跟着变。
/// 「给头看 ≠ 给模型看」——头显示它是正确的，把它塞进提示词才是错的。</para>
///
/// <para>⚠️ **有一条轴是「数值范围」而不是「枚举档」**（<see cref="Temperature"/>）。
/// 它照样有 <see cref="Values"/>——那是一张**由范围生成的网格**，不是手抄的清单。
/// 这样做的理由见 <see cref="Grid"/>：`ValuesOf` 的「未知 → null」契约因此一个字不用动，
/// 头也就不用为「这条轴的 values 可能是 null」加分支（那是会静默炸掉整个面板的一类改动）。</para>
/// </summary>
public static class Axes
{
    public const string Initiative = "initiative";
    public const string Permission = "permission";
    public const string Safety = "safety";
    public const string Verify = "verify";
    public const string Effort = "effort";

    /// <summary>
    /// 联网能力（2026-10-22）。⚠️ 它管的是「这个会话能碰哪一类联网工具」，
    /// **不是凭据**——端点与 key 属于 `<数据根>/network.json` 的全局配置。
    /// 与权限轴是两道闸：这一根管能力开没开，`permission` 管 `Consuming` 这一类允不允许。
    /// </summary>
    public const string Network = "network";

    /// <summary>
    /// 温度（采样参数）。**与 effort 同侧**：有机械层（进请求）、零上下文暴露（`InContext: false`）。
    ///
    /// <para>⚠️ **它是本表第一条「数值范围」轴**，取值不像别的轴那样是一个能穷举的枚举档列表。
    /// 范围 <c>[0, 2]</c> 是**实测**的（本机网关对 2.5 回
    /// <c>Invalid temperature value, the valid range of temperature is [0, 2]</c>），
    /// 且实测**温度与 `output_config.effort`（思考）同时发不报错**——
    /// 这条要写下来，因为「Anthropic 官方的 thinking 与 temperature 互斥」是会被人套用过来的说法，
    /// 而**本项目用的端点不是官方那个**。</para>
    /// </summary>
    public const string Temperature = "temperature";

    /// <summary>Planning versus execution intent; this does not alter tool permissions.</summary>
    public const string Mode = "mode";
    public const string Iterate = "iterate";

    /// <summary>安全轴里代表「生产」的档名（`01` §3.5 的跨轴告警的一端）。</summary>
    public const string AlertSafety = "prod";

    /// <summary>权限轴里「授权及以上」的**起始档**（`01` §3.5 的跨轴告警的另一端）。</summary>
    public const string AlertFrom = "authorized";

    /// <summary>
    /// 一条**数值范围**轴的形状。<see cref="Values"/> 由它派生（<see cref="Grid"/>），不手抄。
    /// <see cref="Step"/> 同时是**合法性的判据**：落在网格之间的值（如 0.75）会被拒，而不是悄悄吸到最近的档。
    /// </summary>
    public sealed record NumericRange(double Min, double Max, double Step);

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
    ///
    /// <para>⚠️ <see cref="Range"/> 非空 ⇒ 这是一条**数值范围轴**，此时 <see cref="Values"/>
    /// **必定**是 <see cref="Grid"/> 出来的那张只读网格（构造时由 <see cref="Numeric"/> 保证）。
    /// 反过来说：**别手工构造「Range 非空但 Values 是手抄清单」的行**。</para>
    /// </summary>
    private sealed record Spec(
        string Name, string Label, IReadOnlyList<string> Values, string Default, bool InContext,
        NumericRange? Range = null, IReadOnlyDictionary<string, string>? Descriptions = null);

    /// <summary>温度的范围与步长。⚠️ **唯一一份**——`Values` 网格由它生成，别在别处再列一遍 0.0…2.0。</summary>
    private static readonly NumericRange TempRange = new(Min: 0, Max: 2, Step: 0.1);

    /// <summary>
    /// 数值范围轴的**值网格**。⚠️ 它是 `Values` 的唯一来源——手抄 21 个值就是把「范围」这件事
    /// 复制成两份，改范围时必漏一处（正是 `ToEffort` 那个已知缺陷的同一形状）。
    ///
    /// <para>⚠️ **这条设计是为了保住 `ValuesOf` 的 null 契约**：若数值轴返回 `null`，
    /// 那么「未知轴」与「非枚举轴」两种 null 就混成一个，而
    /// `AxisTests.EveryAxis_HasAtLeastTwoValues` 的 `ValuesOf(axis)!` 会**当场 NRE**，
    /// 头的 `for (const v of a.values)` 会**静默炸掉整个轴面板**。
    /// 生成网格之后，`ValuesOf` 对所有**已知**轴都非空，null 依旧只表示「未知」。</para>
    /// </summary>
    private static IReadOnlyList<string> Grid(NumericRange r)
    {
        var count = (int)Math.Round((r.Max - r.Min) / r.Step);
        var values = new string[count + 1];
        for (var i = 0; i <= count; i++) values[i] = Format(r.Min + i * r.Step, r.Step);
        return Array.AsReadOnly(values);
    }

    /// <summary>
    /// 数值 → 规范串。**唯一**的规范化拼法（`TryParse` 的数值分支与 <see cref="Grid"/> 共用），
    /// 所以「网格里的值」与「用户敲进来的值」规范化之后**逐字相同**。
    ///
    /// <para>⚠️ 小数位数**由步长追出来**（步长 0.1 ⇒ 一位小数），不是写死的 `"0.##"`：
    /// 后者会让 `1.0` 印成 `"1"`，于是网格看起来是 `0.9, 1, 1.1`——不一致，且
    /// 「用户敲 1、表里是 1.0」这种差异会渗进 `state.json`。</para>
    ///
    /// <para>⚠️ **InvariantCulture 是硬要求**：区域设置会把小数点写成逗号，
    /// 于是 `state.json` 里出现 `"0,7"`，而它再也读不回来。</para>
    /// </summary>
    private static string Format(double v, double step)
    {
        var decimals = 0;
        for (var s = step; s < 1 - 1e-9 && decimals < 6; s *= 10) decimals++;
        return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    /// <summary>数值范围轴的一行。⚠️ `Values` 从这里派生——别绕过它手写一行。</summary>
    private static Spec Numeric(string name, string label, NumericRange range, string @default) =>
        new(name, label, Grid(range), @default, InContext: false, Range: range);

    // 可见枚举轴的合法值和等级解释由同一组声明派生，避免另维护一份档位表。
    private static Spec Context(string name, string label, string @default,
        params (string Value, string Description)[] levels) =>
        new(name, label, Array.AsReadOnly(levels.Select(l => l.Value).ToArray()), @default,
            InContext: true, Descriptions: new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                levels.ToDictionary(l => l.Value, l => l.Description, StringComparer.Ordinal)));

    /// <summary>
    /// 轴的**唯一**一张表。顺序**就是**渲染顺序（`01` §3.5 表格），别按字母排。
    /// </summary>
    private static readonly IReadOnlyList<Spec> Table =
    [
        // 主动性 = 循环的停止条件（§3.5）。档名就是**本表定义的**串。
        // 范围要说准：**落盘**（`state.json` 的 `Axes`）与**控制面快照**
        //（`ControlServer` 整份序列化 `SessionState`）携带的轴值都以这里为源
        //（`SessionHandle` 用 `Defaults()` / `TryParse` 播种，见 `SessionHandle.cs:155,165`）。
        // ⚠️ 但**模型请求**那条路**不是**：`ClaudeRequestBuilder.ToEffort` 是一份
        // **手抄的第二副本**（已知，即计划 G10 说的那类）——改这里**改不到它**，两头要一起改。
        Context(Initiative, "主动性", "step",
            ("step", "按用户明确给出的当前步骤行动；完成本步后汇报，等待下一步指示，不自行扩展目标。"),
            ("standard", "在用户任务范围内自主行动；遇到需要用户决策的事项时征询，没有后续可执行动作时汇报并停止。"),
            ("goal", "围绕用户给定目标持续推进，自行处理可判断的实现细节；完成前继续执行可行动的后续工作，遇到审批或缺少关键输入时说明阻塞。此档不授予额外权限。"),
            ("iterate", "围绕固定 T0 长期任务反复规划与执行：自行在 mode=plan/act 间切换；普通计划用完时核心回到 plan 并要求规划下一段；T0 未完成时不得调用 finish_goal。此档不授予额外权限。")),

        // 权限 = 闸门。0..5 单调阶梯，低档是拒绝线、高档是询问线。
        // ⚠️ 档位**顺序是契约**：头的跨轴告警按
        // `values.indexOf(当前) >= values.indexOf(AlertFrom)` 判「授权及以上」——
        // 重排或插入第 6 档会让告警静默报错档位（正是 `01` §3.5 要防的那种静默失效）。
        Context(Permission, "权限", "standard",
            ("off", "所有工具调用均被拒绝；只能依据已有上下文回答。"),
            ("readonly", "只读类工具可执行；消耗资源、写入和高危类工具均被拒绝。"),
            ("investigate", "只读和消耗资源类工具可执行；写入和高危类工具被拒绝。"),
            ("standard", "只读和消耗资源类工具可执行；写入类工具需要审批，高危类工具被拒绝。"),
            ("authorized", "只读、消耗资源和写入类工具可执行；高危类工具需要审批。"),
            ("trusted", "权限闸门允许所有工具类别，包括不可逆的高危操作；仍须遵守用户任务范围与明确约束。")),

        // 安全 = 纯提示。⚠️ 没有「机械兜底」——不设「生产 ⇒ 权限封顶」（§3.5 明说不许）。
        // ⚠️ 默认落在**非告警**档（见下 Default 那条注释）。
        Context(Safety, "安全", "dev",
            ("dev", "当前为开发环境，可在任务范围内试验和迭代；注意保留已有工作，避免无关改动。"),
            ("staging", "当前为迭代或预发布环境，关注兼容性、集成影响与回退方式，避免影响其他人的验证。"),
            ("prod", "当前为生产环境，优先最小改动、数据完整性与可回退性；执行前评估影响范围，对不可逆操作格外谨慎。")),

        Context(Verify, "验证强度", "self",
            ("none", "不主动运行额外验证；完成改动后说明结果，并明确未执行验证。"),
            ("self", "完成改动后自行执行相关编译、测试或检查，并如实报告结果及未验证部分。"),
            ("strict", "执行任务所需的完整验证；验证未通过时不得声称完成，受阻无法验证时明确报告阻塞和未验证部分。")),

        // 联网 = 能力开关（2026-10-22）。⚠️ 默认 off：新会话不该在没人点头时出网。
        // 档名是**本表定义的**串，`NetworkAccess.Allows` 按它判；未知值落到「全拒」。
        Context(Network, "联网", "off",
            ("off", "不联网：web_search 与 web_fetch 的调用都会被拒绝，模型只能依据已有上下文回答。"),
            ("search", "允许联网搜索（web_search）；直接抓取网页（web_fetch）被拒绝。"),
            ("fetch", "允许抓取指定网页（web_fetch）；联网搜索（web_search）被拒绝。"),
            ("all", "搜索与抓取都允许；仍受权限轴（消耗型工具）与核心联网配置的约束。")),

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
        // ⚠️ `effort` 是**唯一** `InContext = false` 的枚举轴（`01` §3.5：纯请求参数，不进上下文）。
        new(Effort, "思考强度",
            Array.AsReadOnly<string>(["low", "medium", "high", "xhigh", "max"]),
            "high", InContext: false),

        // 温度。⚠️ 这是**第二条** `InContext = false` 的轴（纯请求参数，与 effort 同侧）。
        // 默认 `1.0` = **两家 API 各自的默认采样温度** ⇒ 发它等于不发（按值等价），
        // 于是「从未设过温度的老会话」行为不变。⚠️ 这也是为什么默认值**不是**看着更讨喜的 0.7：
        // 那会把每一个既有会话的采样行为**悄悄改掉**，而用户从没要求过。
        Numeric(Temperature, "温度", TempRange, @default: "1.0"),

        Context(Mode, "工作模式", "act",
            ("plan", "规划模式：可调整普通计划树与 T0 长期任务说明；文件修改工具 write/update/delete/mkdir/git_write 会被模式硬闸拒绝，shell、dotnet、path 不在此模式闸范围内。权限轴仍独立决定其它工具。"),
            ("act", "行动模式：按当前计划执行；工具权限仍由权限轴独立决定。")),
    ];

    /// <summary>按轴名找那一行。<see langword="null"/> 与未知轴都返回 <see langword="null"/>。</summary>
    private static Spec? Find(string? axis) =>
        axis is null ? null : Table.FirstOrDefault(s => s.Name == axis);

    /// <summary>
    /// 轴名，**按表序**（`01` §3.5 表格的顺序）。
    /// ⚠️ **从 <see cref="Table"/> 派生**，不是另写一份名单——另写就有第二个地方要同步，
    /// 而顺序不稳等于每步重排。派生之后「标签与轴名同序」这条约定**不再存在**：
    /// 它们本来就是同一行的两个字段（原本 `Names` 有序、`Labels` 是 `Dictionary`，枚举序未定义）。
    ///
    /// <para>⚠️ <b>它是<b>人</b>看的序</b>（控制面 `/axes` 照它发，头照它铺面板）——表序与
    /// `01` §3.5 那张表逐行对应，而那张表是**按语义分组**排的（先三条行为轴、再两条请求参数轴）。
    /// <b>提示词模板用的不是它</b>，见 <see cref="RenderOrder"/>：那两处的读者不同，序的理由也不同。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> Names = Table.Select(s => s.Name).ToList();

    /// <summary>
    /// 轴名，**按 ordinal 升序**——<b>提示词模板</b>的序（`01` §3.2）。
    ///
    /// <para>⚠️ <b>为什么模板那一格不直接用 <see cref="Names"/></b>：表序是**人**排的
    /// （按语义分组），而模板要的是「同一份数据、任何机器、任何区域设置 ⇒ 同样的字节」。
    /// 这两个要求今天**恰好不冲突**（<see cref="Table"/> 是字面量，逐机一致），
    /// 但它们的**理由**不同——把「模板的序」挂在「人怎么排那张表」上，
    /// 等于让下一次给表分组的人**顺手改掉前缀**。</para>
    ///
    /// <para>⚠️ <b>ordinal，不是文化序</b>——理由与 `01` §9.1 给工具表的那条**逐字相同**：
    /// 默认的 <c>OrderBy(x =&gt; x)</c> / <c>string.CompareTo</c> 走 **ICU 文化序**，
    /// <c>i</c> 与 <c>I</c> 在 <c>en-US</c> 与 <c>tr-TR</c>（土耳其无点 i）下**序不同**
    /// ⇒ 同一份数据、两台机器、两个顺序，而且是**静默破前缀**。
    /// ⚠️ 本仓已有家法：<c>PresetStore</c> · <c>SessionStore</c> · <c>ProviderStore</c> ·
    /// <c>EventBus</c> · <c>ListTool</c> · <c>ApprovalStore</c> 六处都显式写 <c>StringComparer.Ordinal</c>。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> RenderOrder =
        [.. Table.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// 轴的中文名（`01` §3.5 的表格）。⚠️ **它归核心**（`05` §3：渲染归核心，头只显示）——
    /// 头的下拉要画 `主动性` / `安全`，而它拿到的只有英文档名。
    ///
    /// <para>⚠️ **未知轴原样返回**（与 <see cref="ValuesOf"/> 的「未知 → null」同一套约定）；
    /// **`null` 返回空串**——`null` 是「轴名没给」，没有半截标签可回吐，给空串与「未知轴」同向。</para>
    ///
    /// <para>⚠️ <b>那个「原样返回」的兜底今天<b>够不着</b>，而它曾经是一次真的漂</b>：
    /// 它与 <see cref="Table"/> 里那一行**是两个词汇表**（表里是「权限」，兜底吐的是
    /// <c>permission</c>），所以谁把某条轴的 <c>Label</c> 写漏，头上那一格就会印出英文档名
    /// ——而 <c>EveryAxis_HasItsOwnChineseLabel_NotTheFallbackName</c> 早就钉住了「每个轴都有自己的标签」，
    /// 于是那个兜底**只可能**在未知轴那一格生效，而那一格由 <see cref="IsKnown"/> 先挡着。
    /// ⇒ 留着它是因为类型上需要一条返回路径，**不是因为它在生产里会被走到**。</para>
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

    /// <summary>当前等级的提示解释；未知等级和纯请求参数轴没有解释。</summary>
    public static string? DescriptionOf(string? axis, string? value) =>
        Find(axis)?.Descriptions is { } descriptions && value is not null &&
        TryParse(axis, value, out var canonical) ? descriptions.GetValueOrDefault(canonical) : null;

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

    /// <summary>
    /// 该轴的合法取值。<see langword="null"/> = **未知轴**（这条契约一个字没动）。
    /// 返回的是**只读视图**，改不动。
    ///
    /// <para>⚠️ **数值范围轴也走这里**，交出的是一张由范围生成的网格（见 <see cref="Grid"/>）——
    /// 它**不是** null。别把「非枚举」塞进这个 null 里去：那样「未知」与「非枚举」就分不开了，
    /// 而下面的 <see cref="RangeOf"/> 正是靠区分它们才有意义。</para>
    /// </summary>
    public static IReadOnlyList<string>? ValuesOf(string? axis) => Find(axis)?.Values;

    /// <summary>
    /// 该轴是不是**数值范围**轴，是就给出范围，否则 <see langword="null"/>。
    ///
    /// <para>⚠️ **注意这里 null 的含义与 <see cref="ValuesOf"/> 不同**：那个 null 表示「**未知轴**」，
    /// 这个 null 表示「**已知，但不是数值轴**」。所以判「轴存不存在」要用
    /// <see cref="IsKnown"/>，**不要**用 `RangeOf(...) is not null`——那会把五条枚举轴
    /// 全判成不存在。</para>
    /// </summary>
    public static NumericRange? RangeOf(string? axis) => Find(axis)?.Range;

    /// <summary>
    /// 该轴的值**是不是数值**（头据此画数字输入而不是下拉）。
    /// ⚠️ 未知轴返回 false——与 <see cref="IsKnown"/> 的「未知即假」同向。
    /// </summary>
    public static bool IsNumeric(string? axis) => Find(axis)?.Range is not null;

    /// <summary>合法值清单（错误信息用）。⚠️ 从表里拼，不手抄。</summary>
    public static string LegalValuesText(string? axis) => Find(axis) switch
    {
        null => "",
        // 数值轴**不能**把整张网格用 `|` 拼出来——21 个数印进一句错误信息就没法读了。
        // 给范围与步长（那才是用户要知道的东西）。⚠️ 措辞与枚举轴不同是**故意的**。
        { Range: { } r } => $"{r.Min}~{r.Max} 之间的数值（步长 {r.Step}）",
        { Values: var v } => string.Join("|", v),
    };

    /// <summary>
    /// 轴上的规范串 → <see cref="double"/>。不是数值轴 / 解析不出来 ⇒ <see langword="null"/>。
    ///
    /// <para>⚠️ 这是「轴 → 请求」那条路上**唯一**的转换点（`Stepper` 用它读温度）。
    /// 别在构造器里各写一遍 `double.Parse`：那样区域设置、越界判据、失败处理会各漂各的，
    /// 而这三个恰恰是最容易悄悄出错的地方。</para>
    /// </summary>
    public static double? NumberOrNull(string? axis, string? canonical)
    {
        if (Find(axis)?.Range is not { } r || canonical is null) return null;

        return double.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d >= r.Min && d <= r.Max
            ? d
            : null;
    }

    /// <summary>
    /// 校验并规范化（`PROD` → `prod`；`0.70` → `0.7`）。
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
    ///
    /// <para>⚠️ **数值轴走的是另一条分支**（大小写那套对它不适用）：解析成 `double`、
    /// 查范围、**吸附到步长网格**，并且**拒绝落在网格之间的值**。
    /// 拒绝而不是静默吸附——用户敲 `0.75`（步长 0.1）时，悄悄存成 `0.8` 是**另一种**行为，
    /// 而他不会知道。（吸附只用来吸收浮点噪声：`0.1` 加十次不是精确的 `1.0`。）</para>
    /// </summary>
    public static bool TryParse(string? axis, string value, out string canonical)
    {
        canonical = "";
        if (Find(axis) is not { } spec) return false;   // ⚠️ null 键会让 `Dictionary` 抛，见 IsKnown 的注释

        if (spec.Range is { } range) return TryParseNumeric(range, value, out canonical);

        foreach (var v in spec.Values)
        {
            if (!string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) continue;
            canonical = v;
            return true;
        }
        return false;
    }

    /// <summary>数值分支：解析 → 查区间 → 复验「本来就在网格上」→ 规范化。</summary>
    private static bool TryParseNumeric(NumericRange r, string value, out string canonical)
    {
        canonical = "";

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return false;

        // ⚠️ **必须显式挡 `NaN` / `±Infinity`**：`double.TryParse("NaN")` 是**成功**的，
        // 而接下来每一句与 NaN 的比较都是 `false` ⇒ **区间检查与网格检查会双双放行**，
        // 于是 `"NaN"` 被当成合法值存进 `state.json`、再原样发去 API。
        // （Infinity 那半会被区间检查挡住——`inf > Max` 为真——但 `IsFinite` 一句把两条都管了。）
        // 这个洞是**测试抓出来的**，不是看出来的。
        if (!double.IsFinite(d)) return false;

        if (d < r.Min || d > r.Max) return false;

        // ⚠️ **要求「本来就在网格上」，不是「吸附过去」**：`0.75`（步长 0.1）必须**被拒**，
        // 不能悄悄存成 `0.8`——那是另一种行为，而用户不会知道。
        // 容差 `1e-9` 只用来吸收浮点噪声（`7 * 0.1` 是 `0.7000000000000001`，不是 `0.7`），
        // 比半个步长小得多，所以「正中间」那类值照样落网。
        var steps = Math.Round((d - r.Min) / r.Step);
        var snapped = r.Min + steps * r.Step;
        if (Math.Abs(snapped - d) > 1e-9) return false;

        canonical = Format(snapped, r.Step);
        return true;
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
    /// `CommandDispatcher.HandleSessionCreate` 调 `ToSelection(provider, null, "high", 64000)`
    /// ——改这里就得同时改那里，否则新会话的轴显示一个值、实际请求参数是另一个。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Defaults() =>
        Table.ToDictionary(s => s.Name, s => s.Default, StringComparer.Ordinal);
}

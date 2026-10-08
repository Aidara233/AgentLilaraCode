using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>闸门的判定。⚠️ <see cref="Ask"/> **本批没有生产者**，见 <see cref="Gate"/> 的注释。</summary>
public enum Verdict
{
    /// <summary>放行。</summary>
    Allow,

    /// <summary>拒绝。⚠️ 拒绝**也必须产生一条结果**（`01` §3.6 ②：一对不能拆）。</summary>
    Deny,

    /// <summary>
    /// 要问用户。⚠️ <b>本批<b>没有生产者</b></b>——它要转 `WaitingForInput`，
    /// 而那个状态**从来没有被写过**（用户裁定：审批单独一批）。
    /// ⇒ 留着这个值是为了让那个缺口**看得见**（一个安静的"拒"会把「本该问」永久藏起来）。
    /// </summary>
    Ask,
}

/// <summary>
/// **权限闸门**——`01` §3.5 那张 `档位 × 类别` 表的唯一落点。
///
/// <para>⚠️ <b>这条分工是本仓的硬约束</b>（`01` §9.2，用户裁定）：
/// <b>「不在于模型是否可以调用，而是调用后能不能生效，保住工具列表」</b>。
/// ⇒ <b>工具表一个字不动</b>（改它破前缀最前端，而且贵），
/// 闸门在**执行期**拦——改一个浮动字段，**零前缀代价、即时生效**。</para>
///
/// <para>⚠️ <b>今天这张表只有一个可观测的效果</b>：三个内建（`ls` / `read` / `fetch`）
/// <b>全是只读</b>，而只读在所有档位（≥1）都通 ⇒ 闸门对它们**永远是 no-op**。
/// 要覆盖那张 6×4 的表，测试必须有一个**写类的替身**（见 `GateTests`）。
/// 这不是缺陷，是「契约必须先于生产者落地」的正常形态。</para>
/// </summary>
public static class Gate
{
    /// <summary>
    /// 档位 → 它**放行**哪些类别。⚠️ <b>顺序就是 `01` §3.5 表里的行序</b>，
    /// 而且它是**单调阶梯**（每升一档，只移动一个边界）——`GateTests` 钉着这条。
    ///
    /// <para>⚠️ <b>它是一张<b>显式</b>集合的表，不是"枚举数值 ≤ 某个数"</b>：
    /// 后者依赖<b>枚举成员的相邻性</b>，往 `PermissionClass` 中间插一档（或重排）
    /// 会让整张表**静默错位**——而那正是 `Axes.cs` 里给权限轴写的那条警告的同一个形状。</para>
    ///
    /// <para>⚠️ <b>两处「问」在表里是 `Ask`，这里实现成 <c>Deny</c></b>：
    /// `standard` 的写入格、`authorized` 的高危格。理由见 <see cref="Verdict.Ask"/>。
    /// ⚠️ <b>这个偏离必须写进 `01` §3.5</b>——否则就是「文档说一套、代码做一套」，
    /// 而那正是本仓反复栽的那个形状。</para>
    /// </summary>
    private static readonly Dictionary<string, PermissionClass[]> Allowed = new(StringComparer.Ordinal)
    {
        ["off"] = [],
        ["readonly"] = [PermissionClass.ReadOnly],
        ["investigate"] = [PermissionClass.ReadOnly, PermissionClass.Consuming],
        // ⚠️ 表里写作「问」，本批降级为拒 ⇒ 只到 Consuming 为止。
        ["standard"] = [PermissionClass.ReadOnly, PermissionClass.Consuming],
        // ⚠️ 同上：表里高危是「问」⇒ 只到 Writing 为止。
        ["authorized"] = [PermissionClass.ReadOnly, PermissionClass.Consuming, PermissionClass.Writing],
        ["trusted"] =
        [
            PermissionClass.ReadOnly, PermissionClass.Consuming,
            PermissionClass.Writing, PermissionClass.Destructive,
        ],
    };

    /// <summary>
    /// 判定。⚠️ <b>未知档位一律当作最严的那一档</b>——与 `Axes.InContext` 那条
    /// 「不知道的一律当作不进上下文」同一条判据：**宁可漏放，不可错放**。
    ///
    /// <para>⚠️ 别指望"上游一定拦住了"：盘上的 `state.json` 是**用户可手改的**
    /// （`SessionStore.TryReadSpec` 那条已知边界），而闸门是**安全**那一侧。</para>
    /// </summary>
    public static Verdict Decide(PermissionClass cls, string? level) =>
        Allowed.TryGetValue(level ?? "", out var allowed) && allowed.Contains(cls) ? Verdict.Allow : Verdict.Deny;

    /// <summary>
    /// 拒绝理由。⚠️ <b>它在<b>执行那一刻</b>就落进结果、此后冻结</b>
    /// （`01` §9.2 点 3）：`ToolResultEvent` 是**只追加的记录**，
    /// ⇒ 理由**不能**在渲染时按当前档位重算——那样改一次档位，
    /// **同一条历史会渲染出不同的原因**，而「事实不可变」当场破。
    ///
    /// <para>⚠️ <b>措辞要能让模型知道下一步怎么办</b>：说清是**哪一类**被拦、
    /// **当前档位是什么** —— 它据此才知道该请求提权，而不是反复重试同一个调用。</para>
    /// </summary>
    public static string DenyReason(PermissionClass cls, string? level)
    {
        var name = cls switch
        {
            PermissionClass.ReadOnly => "只读",
            PermissionClass.Consuming => "消耗型（会联网或花钱）",
            PermissionClass.Writing => "写入",
            _ => "高危（不可逆）",
        };

        var current = level is null ? "未知" : Axes.LabelOf(Axes.Permission) + "＝" + level;

        // ⚠️ 刻意**不写**「本可以问你、但我还不会问」——那是一句内部实现的话，
        // 对模型没有帮助，而它该做的是**请求提权**（`01` §3.5：模型不能自提权，只能请求）。
        return $"这次调用被拒绝了：它是**{name}**类工具，而当前权限档位（{current}）不允许这一类。"
             + "要让它能跑，请让用户提高权限档位（session.setAxis permission）。";
    }

    /// <summary>
    /// **把一次 `Ask` 变成一张卡**（`01` §3.8）：「准不准跑这一次？」
    ///
    /// <para>⚠️ <b>它是「统一处理」那个要求落在闸门这一侧的样子</b>：闸门不自己弹窗、
    /// 不自己落盘、不碰状态机——它只**描述问题**，然后由 `ToolExecutor` 交出去。
    /// `ask` 工具走的是**同一条**恢复路径，只是 <see cref="AskKind"/> 不同。</para>
    ///
    /// <para>⚠️ <b>卡上必须能看出「本该问」还是「档位不够」</b>（`01` §3.5 记的那处
    /// 本批的已知不精确：两者今天都印"档位不允许"）。这里把措辞分开：
    /// 这一条说的是「**这需要你点头**」，而 <see cref="DenyReason"/> 说的是
    /// 「**这一档不许**」。</para>
    ///
    /// <para>⚠️ <b>它是只读的、无副作用的</b>——`ToolExecutor` 会在「先判全」那一趟
    /// **每条被问的调用调它一次**，而那时一条工具都还没跑。</para>
    /// </summary>
    /// <param name="call">被问的那个调用。⚠️ 它的**参数原文**进卡（改写后批准要用）。</param>
    public static AskRequest AskFor(ITool tool, Block.ToolUseBlock call, string? level)
    {
        var cls = tool.PermissionClass;
        var current = level is null ? "未知" : Axes.LabelOf(Axes.Permission) + "＝" + level;

        var what = cls switch
        {
            PermissionClass.Writing => "会改动东西",
            PermissionClass.Destructive => "不可逆（高危）",
            _ => "需要授权",
        };

        return new AskRequest
        {
            Id = Ids.New(),
            Kind = AskKind.Permission,
            Items =
            [
                AskItem.Verbatim(
                    "arguments",
                    $"要跑 `{tool.Name}`（{what}），当前档位（{current}）要求先问你。可以改参数再批准。",
                    call.Arguments),
            ],
            // ⚠️ 两档都给，**粗的那档由卡片负责写明**（见 `AskMemory`）。
            Remember = [AskMemory.Args, AskMemory.Tool],
        };
    }
}

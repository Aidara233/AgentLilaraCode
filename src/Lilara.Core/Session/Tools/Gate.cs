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
    /// 档位 → 它**自动放行**哪些类别（`01` §3.5 那张表）。
    ///
    /// <para>⚠️ <b>顺序就是表里的行序</b>，而且它是**单调阶梯**（每升一档，只移动一个边界）
    /// ——`GateTests` 钉着这条。</para>
    ///
    /// <para>⚠️ <b>它是一张<b>显式</b>集合的表，不是"枚举数值 ≤ 某个数"</b>：
    /// 后者依赖<b>枚举成员的相邻性</b>，往 `PermissionClass` 中间插一档（或重排）
    /// 会让整张表**静默错位**——而那正是 `Axes.cs` 里给权限轴写的那条警告的同一个形状。</para>
    ///
    /// <para>⚠️ <b>⚠️ 只写「自动放行」的那半，这一格是本文件最容易读错的地方</b>——
    /// 表里还有一列（<see cref="Asks"/>），而**「问」与「拒」是不同的档**：
    /// 低档的边界是**拒绝线**（硬），高档的边界是**询问线**（软），档位 = 那条线在哪。
    /// 本方法返回 <see cref="Verdict.Ask"/> 的那些格**不在**这张表里。</para>
    /// </summary>
    private static readonly Dictionary<string, PermissionClass[]> Allowed = new(StringComparer.Ordinal)
    {
        ["off"] = [],
        ["readonly"] = [PermissionClass.ReadOnly],
        ["investigate"] = [PermissionClass.ReadOnly, PermissionClass.Consuming],
        ["standard"] = [PermissionClass.ReadOnly, PermissionClass.Consuming],
        ["authorized"] = [PermissionClass.ReadOnly, PermissionClass.Consuming, PermissionClass.Writing],
        ["trusted"] =
        [
            PermissionClass.ReadOnly, PermissionClass.Consuming,
            PermissionClass.Writing, PermissionClass.Destructive,
        ],
    };

    /// <summary>
    /// 档位 → 它**要问用户**哪些类别（`01` §3.5 的「询问线」那一列）。
    ///
    /// <para>⚠️ <b>这条线是 2026-10-08（询问批）才接上的</b>：在那之前这两格**实现成 `Deny`**
    /// ——因为「问」要转 `WaitingForInput`，而那个状态当时**没有任何生产者**。
    /// ⇒ 那是一次**看得见的降级**（如实写在 `01` §3.5 与 `实现现状` 里），
    /// 而它的代价当时就写明了：默认档是 `standard`，降级之后**默认档写不了任何东西**。
    /// 本批把降级掀掉，那个代价随之消失。</para>
    ///
    /// <para>⚠️ <b>结构与 <see cref="Allowed"/> 完全同形</b>（显式集合、不依赖枚举相邻性），
    /// 理由也同一条。两张表**合起来**才是 `01` §3.5 那张 6×4 表的第一列：
    /// 每一格要么在 `Allowed` 里、要么在 `Asks` 里、要么两者都不在（= 拒）。</para>
    /// </summary>
    private static readonly Dictionary<string, PermissionClass[]> Asks = new(StringComparer.Ordinal)
    {
        // 3 标准：写入是**询问线**（表里那一格写着「问」）。
        ["standard"] = [PermissionClass.Writing],
        // 4 授权：高危是**询问线**。
        ["authorized"] = [PermissionClass.Destructive],
    };

    /// <summary>
    /// 判定。⚠️ <b>未知档位一律当作最严的那一档</b>——与 `Axes.InContext` 那条
    /// 「不知道的一律当作不进上下文」同一条判据：**宁可漏放，不可错放**。
    ///
    /// <para>⚠️ 别指望"上游一定拦住了"：盘上的 `state.json` 是**用户可手改的**
    /// （`SessionStore.TryReadSpec` 那条已知边界），而闸门是**安全**那一侧。</para>
    ///
    /// <para>⚠️ <b>「先放行、再问」的次序不是随机的</b>：一格同时出现在两张表里时
    /// 以放行为准——两张表都写成显式集合，那个重叠是**配置错误**，
    /// 而两个判据冲突时取更宽松的那个是**唯一说得通**的默认（另一个方向会让
    /// 一次「本来能跑」的调用变成一次没必要的打扰）。</para>
    /// </summary>
    public static Verdict Decide(PermissionClass cls, string? level)
    {
        var key = level ?? "";

        if (Allowed.TryGetValue(key, out var allowed) && allowed.Contains(cls)) return Verdict.Allow;
        if (Asks.TryGetValue(key, out var asked) && asked.Contains(cls)) return Verdict.Ask;

        return Verdict.Deny;
    }

    /// <summary>
    /// **执行期用的那个判定**——先问工具自己「要不要过闸」（<see cref="ITool.GatedByPermission"/>）。
    ///
    /// <para>⚠️ <b>它<b>不是</b> <see cref="Decide(PermissionClass, string?)"/> 的替代品</b>：那张
    /// `档位 × 类别` 的表还在，也仍然是<b>唯一</b>一张——本方法只是多了一个「这类调用压根不在表里」
    /// 的分支。<c>GatedByPermission == false</c> 的工具今天只有一个（`compact`），理由写在那个属性上。</para>
    ///
    /// <para>⚠️ <b>为什么是「按工具放行」而不是「往类别里加一档」</b>：`Allowed` / `Asks` 两张表的
    /// 每一行都写着每一档的边界，加一档等于让六个档位各移动一次边界，而
    /// `GateTests` 钉的是「单调阶梯」（只移动一格）——那会静默错位（见 <see cref="Allowed"/> 的注释）。</para>
    /// </summary>
    public static Verdict Decide(ITool tool, string? level) =>
        tool.GatedByPermission ? Decide(tool.PermissionClass, level) : Verdict.Allow;

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

        return new AskRequest
        {
            Id = Ids.New(),
            Kind = AskKind.Permission,
            Items = SecondHalfOf(call, cls, current),
            // ⚠️ 两档都给，**粗的那档由卡片负责写明**（见 `AskMemory`）。
            Remember = [AskMemory.Args, AskMemory.Tool],
        };
    }

    /// <summary>
    /// 闸门那条询问的**问题**（`01` §3.8）——「准不准跑这一次，参数可以改」。
    ///
    /// <para>⚠️ <b>它被抽出来，是因为它有两个调用点，而它们必须给出**同一张卡**</b>：
    /// ① <see cref="AskFor"/>（刚挂起时，内存里那份）；
    /// ② `AskTool.ItemsOf`（**重启之后**，从盘上那条 `tool_use` 重新解）。
    /// 两处各写一份的话，就会出现「刚挂起时能改参数、重启之后那张卡变成空的」——
    /// 而那正是本仓那条「两层判据必须共用一个谓词」的形状。</para>
    ///
    /// <para>⚠️ <b>为什么「重启之后」也要解得出来</b>：内存那半
    /// （`SessionHandle.AwaitingInput`）**只在「本进程亲手挂起过」时才在**
    /// ——任何一次从盘载入都会丢掉它。而闸门这条询问的问题**只有重建得出来的那一半**
    /// 才活得下去：`Verbatim` 里的参数原文来自<b>落盘的那条 <c>tool_use</c></b>，
    /// 而**问句本身是从类别现推的**。⇒ 参数原文逐字保真，问句由
    /// <paramref name="cls"/> / <paramref name="current"/> 重建。
    /// ⚠️ 判据是：**那张卡对用户仍然有用**（他看得到参数、能改、能批），
    /// 而不是「他看到的每个字都与当时逐字相同」——后者做不到（类别是工具当时的，
    /// 而工具表可以在两次载入之间变）。</para>
    /// </summary>
    /// <param name="call">那条 `tool_use`（参数原文从这里来）。</param>
    public static IReadOnlyList<AskItem> SecondHalfOf(
        Block.ToolUseBlock call, PermissionClass cls, string current)
    {
        var what = cls switch
        {
            PermissionClass.Writing => "会改动东西",
            PermissionClass.Destructive => "不可逆（高危）",
            _ => "需要授权",
        };

        return
        [
            AskItem.Verbatim(
                "arguments",
                $"要跑 `{call.Name}`（{what}），当前档位（{current}）要求先问你。可以改参数再批准。",
                call.Arguments),
        ];
    }

    /// <summary>
    /// 从**落盘的那条调用**重建那张卡——`AskTool.ItemsOf` 重启之后走这条。
    ///
    /// <para>⚠️ <b>类别从哪来</b>：优先用**当时那条调用所属的工具**（`tools` 里按名字找），
    /// 找不到就退回「需要授权」这句不点名的措辞——**不编一个类别**
    /// （工具表可以在两次载入之间变，而编一个会让用户看到一个假的「它是写入类」）。</para>
    ///
    /// <para>⚠️ 档位**用当前的**：`level` 由调用方现读。这与「拒绝理由在执行那一刻冻结」
    /// （<see cref="DenyReason"/>）**不冲突**——那条说的是**落进记录的**理由不能重算；
    /// 而这里是一张**问用户的卡**，它本来就该反映此刻的档位（用户要据此判断该不该提权）。</para>
    /// </summary>
    public static IReadOnlyList<AskItem> SecondHalfOf(Block.ToolUseBlock call, string? level) =>
        SecondHalfOf(call, ClassOf(call.Name),
            level is null ? "未知" : Axes.LabelOf(Axes.Permission) + "＝" + level);

    private static PermissionClass ClassOf(string toolName) =>
        BuiltInTools.All.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal))
            ?.PermissionClass ?? PermissionClass.Destructive;
}

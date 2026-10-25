namespace Lilara.Core.Kernel;

/// <summary>
/// **派生关系的词汇层**（`01` §10）——父子链、深度、以及「哪些记录是核心替模型注入的」。
///
/// <para>⚠️ <b>它放在 Kernel，因为四个层都要用而它自己不依赖任何东西</b>
/// （Kernel 对别层零依赖是 `ArchitectureTests` 钉着的第一条）：`SessionSpec` 存它、
/// `Interruption` 判它、`Stepper` 算它、`CommandDispatcher` 执行它。
/// 分开放会让「深度上限」这个数出现两遍，而漂了的后果是**一棵树能长到任意深**。</para>
///
/// <para>⚠️ <b>默认只有一层，而多层是「改一个数」而不是「另一条代码路径」</b>
/// （用户 2026-10-24 裁定）：`DefaultMaxDepth = 1` ⇒ 顶层会话（`Depth = 0`）能派生，
/// 子会话（`Depth = 1`）不能。想开多层就把它调大——判据只有
/// <see cref="CanSpawnFrom"/> 一处，**没有第二处需要跟着改**。</para>
/// </summary>
public static class Subagent
{
    /// <summary>
    /// 一棵树最多能有多深。**顶层会话的 `Depth` 是 0**，所以 `1` = 「一层子会话」。
    ///
    /// <para>⚠️ <b>它只是默认值</b>：真正的上限冻在 <see cref="SessionSpec.MaxDepth"/> 里
    /// （建树那一刻定、沿树继承）。改这个常量**不影响已经在跑的树**——那是对的，
    /// 「中途改上限」会让同一棵树里两棵子树按两个规则判。</para>
    /// </summary>
    public const int DefaultMaxDepth = 1;

    /// <summary>
    /// **核心替模型注入的那些记录的 id 前缀**（回流的合成对、以及它们的调用那一半）。
    ///
    /// <para>⚠️ <b>它必须存在，而且只能由这里定</b>（同 `Interruption.NoteIdPrefix` 的理由）：
    /// 合成对在时间线上**长得完全像一次真实的工具调用**（那正是我们要的——三个适配器
    /// 一个字都不用改），而有一处判据必须把它们与真实的调用分开：
    /// <see cref="Interruption.WasInterrupted"/> 的配对扫描。没有这个前缀，
    /// 父正挂着一次询问时收到子的回报，那条挂着的调用会被判成**断裂**，
    /// 于是核心补一条「上一次这一步被打断了」——而用户只是在想怎么回答。</para>
    ///
    /// <para>⚠️ <b>前缀写在 id 上，不写在内容里</b>：内容会进模型上下文（每步全价付），
    /// 而 id 只在判定那一侧被读一眼。同 `floating-` 前缀的判据（`00` §7 判据 5：
    /// 能进机械层的，绝不靠嘴说）。</para>
    /// </summary>
    public const string InjectedIdPrefix = "subagent-";

    /// <summary>这条记录是不是核心替模型注入的（见 <see cref="InjectedIdPrefix"/>）。</summary>
    public static bool IsInjected(TimelineEvent evt) =>
        evt.Id.StartsWith(InjectedIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 这个会话还能不能派生子会话（`01` §10.3 的「深度限量而非禁止」）。
    ///
    /// <para>⚠️ <b>判据只有这一处。</b>工具那一侧（给模型一句人话）与执行那一侧
    /// （真的建不建）**都调它**——两处各写一个 `depth &gt;= max` 就是本仓反复栽的
    /// 「两层判据必须共用一个谓词」，而漂了的表现是「工具说能派，核心不建」。</para>
    /// </summary>
    /// <param name="depth">这个会话自己的深度（顶层 = 0）。</param>
    /// <param name="maxDepth"><c>null</c> = 用 <see cref="DefaultMaxDepth"/>。</param>
    public static bool CanSpawnFrom(int depth, int? maxDepth) => depth + 1 <= (maxDepth ?? DefaultMaxDepth);

    /// <summary>
    /// **收窄**：子的权限档位 ≤ 父（`01` §10.3「委托的第一性质：授权范围 ⊆ 父」）。
    ///
    /// <para>⚠️ <b>判据只有一个：轴表里的档位序</b>（`Axes.ValuesOf` 的返回序就是阶梯序，
    /// 那条注释明写「顺序是契约」）。别在这里手抄一份档位名单——手抄的那份在加档时
    /// **不会红**，只会让一次收窄静默失效。</para>
    ///
    /// <para>⚠️ <b>未知档位一律取更严的那一侧</b>（与 `Gate.Decide` 同一条判据：
    /// 宁可漏放，不可错放）。取不到的档位返回 <c>"off"</c>——那是「什么都不许」。</para>
    /// </summary>
    public static string NarrowerPermission(string? parent, string? requested)
    {
        var ladder = Axes.ValuesOf(Axes.Permission);
        if (ladder is null) return "off";

        var p = IndexOf(ladder, parent);
        var r = IndexOf(ladder, requested);

        // 父认不出来 ⇒ 不许派生（`off` 是最严那一档，而它在这张表的第一位）。
        if (p < 0) return ladder[0];

        // 没提要求 ⇒ 就继承父的；提了但认不出 ⇒ 退回父的（不猜、也不提权）。
        if (r < 0) return ladder[p];

        return ladder[Math.Min(p, r)];
    }

    private static int IndexOf(IReadOnlyList<string> ladder, string? value)
    {
        if (value is null) return -1;

        for (var i = 0; i < ladder.Count; i++)
            if (string.Equals(ladder[i], value, StringComparison.Ordinal)) return i;

        return -1;
    }

    /// <summary>
    /// 子会话该继承哪一套轴（`Axes` 那份字典）。
    ///
    /// <para>⚠️ <b>逐条的理由</b>：</para>
    /// <list type="bullet">
    ///   <item><b>权限</b>：取 <see cref="NarrowerPermission"/> 的结果——这是唯一一条**必须**收窄的。</item>
    ///   <item><b>主动性</b>：父在 `goal` 时子落到 `standard`。⚠️ 理由不是安全，是**它没有终点**：
    ///   一个自动续跑的子会话会一直烧钱，而它的父正卡在等它之上——两层都没有自然的停点。</item>
    ///   <item><b>其余</b>（安全 / 验证 / 联网 / effort / 温度）：原样继承。它们不是「授权」，
    ///   而是「这个活儿在什么环境下干」——子会话干的是父交给它的同一件事，环境不该变。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<string, string> InheritAxes(
        IReadOnlyDictionary<string, string> parent, string? requestedPermission)
    {
        var copy = new Dictionary<string, string>(parent, StringComparer.Ordinal)
        {
            [Axes.Permission] = NarrowerPermission(
                parent.GetValueOrDefault(Axes.Permission), requestedPermission),
        };

        // ⚠️ **不调 `GoalMode.IsActive`**：它在 Session 层，而本文件在 Kernel
        //（`ArchitectureTests` 钉着 Kernel 对别层零依赖）。判据本身只有一行
        //（「主动性轴是不是 goal」），所以这里直接读轴 —— **不是第二份实现**，
        // 是同一句话在两个层各需要一次，而中间隔着分层守卫。
        if (copy.TryGetValue(Axes.Initiative, out var initiative)
            && string.Equals(initiative, "goal", StringComparison.Ordinal))
            copy[Axes.Initiative] = "standard";

        return copy;
    }
}

/// <summary>
/// **一个子会话的摘要**（给工具与浮动上下文看）。
///
/// <para>⚠️ <b>只装两样模型真正用得上的东西：id 与状态</b>。标题是给人看的（模型靠 id
/// 指称它），步数与时间戳**绝不能进这里**——它们每步都变，而
/// [浮动上下文是「变化才注入」的](../Session/Stepper.cs)（判据是尾块正文逐字比较），
/// 放进去等于每步写一份 `floating.jsonl`。</para>
/// </summary>
/// <param name="SessionId">子会话 id（模型拿它去调 `tell_child`）。</param>
/// <param name="Status">
/// 它的状态。⚠️ **只取 <see cref="SessionStatus"/> 里那几个档**，且**必须是磁盘上读得到的**
/// ——工具句柄够不着 `CoreHost`，而 `Running` 只写内存。⇒ 子会话「正在跑」在工具这一侧
/// 看起来就是 `Idle`（它落在盘上的那一份），**不要**在这里假装知道它在跑。
/// </param>
public sealed record SubagentChild(string SessionId, string Status);

/// <summary>
/// **这一刻的派生关系快照**——工具能看见的那一份（`01` §10）。
///
/// <para>⚠️ <b>它和 <c>Network</c> / <c>Shell</c> 是同一类东西：一份只读快照</b>
/// （步开始或执行那一刻冻结）。理由也一样——工具必须能在**不碰 store** 的前提下
/// 回答「我还能不能派生」「我有哪些孩子」。</para>
///
/// <para>⚠️ <b>`null` = 没有这份快照</b>（手工构造的句柄、纯单测）。此时 `spawn` 与
/// `tell_child` 一律回一句「这里没有派生上下文」——**不猜**「大概是顶层会话吧」。
/// 猜错的后果是一个本该被深度限制挡住的调用**静默通过**。</para>
/// </summary>
/// <param name="SessionId">本会话 id。</param>
/// <param name="ParentSessionId">父会话 id；`null` = 顶层。</param>
/// <param name="Depth">本会话的深度（顶层 = 0）。</param>
/// <param name="MaxDepth">这棵树的上限。</param>
/// <param name="Children">本会话的子会话（按 id 序数升序，见 `SessionStore.ChildrenOf`）。</param>
public sealed record SubagentScope(
    string SessionId,
    string? ParentSessionId,
    int Depth,
    int MaxDepth,
    IReadOnlyList<SubagentChild> Children)
{
    /// <summary>还能不能派生（判据在 <see cref="Subagent.CanSpawnFrom"/>，这里是转调）。</summary>
    public bool CanSpawn => Subagent.CanSpawnFrom(Depth, MaxDepth);
}

/// <summary>
/// **一次派生请求**——`spawn` 工具跑出来、由推进器/分派器真正兑现的那一件事。
///
/// <para>⚠️ <b>形状与 <see cref="AskRequest"/> 同族</b>：工具**不建会话**（它没有 store 的手），
/// 它只把「要建什么」说清楚，落地在核心。⚠️ 但**子会话 id 由工具预分配**
/// （`Ids.New()`）：这样工具结果里那个 id 就是**将来真的会存在**的那一个，
/// 而模型下一步拿它调 `tell_child` 时不会指向一个不存在的会话。</para>
/// </summary>
/// <param name="ChildSessionId">预分配的子会话 id。⚠️ 工具结果里说的就是它。</param>
/// <param name="Task">交给子会话的第一条用户消息（它会作为一条真的 `MessageEvent` 落盘）。</param>
/// <param name="Title">可选：给人看的名字。</param>
/// <param name="Preset">可选：子会话用哪份系统提示词。</param>
/// <param name="Permission">
/// 模型**请求**的权限档位。⚠️ 它是**请求**不是结果：核心取
/// `min(父, 它)`（见 <see cref="Subagent.NarrowerPermission"/>），所以提权请求无害。
/// </param>
public sealed record SpawnRequest(
    string ChildSessionId, string Task, string? Title, string? Preset, string? Permission);

/// <summary>
/// **一次注入**——父对子说一句，或子对父说一句（`01` §10.1 的「纯注入，双向对称」）。
///
/// <para>⚠️ <b>两个方向共用这一个形状</b>，差别只在 <see cref="TargetSessionId"/> 是子还是父。
/// 拆成两个类型会让「注入」这条机制出现两份实现，而它们迟早会漂
/// （一个方向加了「先追加再入队」的次序，另一个忘了）。</para>
/// </summary>
/// <param name="TargetSessionId">收件人。</param>
/// <param name="Text">说的话。</param>
public sealed record TellRequest(string TargetSessionId, string Text);

namespace Lilara.Core.Kernel;

/// <summary>A remaining task in the session plan. Branches group work; leaves are executable tasks.</summary>
public sealed record PlanNode
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public IReadOnlyList<PlanNode> Children { get; init; } = [];
}

/// <summary>会话的固定 T0 指令。模型可见但不能通过计划编辑工具删除或覆盖。</summary>
public sealed record T0PlanNode
{
    public required string Title { get; init; }
    public required string Body { get; init; }
}

/// <summary>The remaining plan tree. Completed leaves are removed rather than retained as status.</summary>
public sealed record SessionPlan
{
    public required IReadOnlyList<PlanNode> Nodes { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    /// <summary>固定的 T0 工作指令；不属于可推进的剩余节点。</summary>
    public T0PlanNode? T0 { get; init; }
}

/// <summary>
/// **计划在线上只有这一种形状**（2026-10-24）。三个入口共用它：
/// `/state` 的 `plan`、`plan.updated` 的 `plan`、`GET /sessions/{id}/plan` 的 `plan`。
///
/// <para>⚠️ <b>为什么必须共用</b>：头只有一个渲染函数。曾经 `/state` 交的是**节点数组**
/// （`plan.tree`），而 `plan.updated` 交的是 <see cref="SessionPlan"/> **对象**
/// （`{nodes, updatedAt}`）——头拿同一个参数去 `for (const n of …)`，对象没有 `length`
/// ⇒ 计划每变一次，面板就显示成「还没有计划」。那不是显示错误，是把事实说反了。</para>
///
/// <para>⚠️ <b>`Tree` 为 <c>null</c> 表示「没有计划」</b>，而 <see cref="Active"/> 是它的
/// 便捷布尔（等于 `Tree is { Count: > 0 }`）。两者都给，是因为头两种判法都用得上：
/// 画树要 `Tree`，而「有没有计划」这个分支不该逼每个调用方自己写空判断。</para>
///
/// <para>⚠️ <b><see cref="CurrentId"/> 是「最先遇到的叶子」</b>（见 <see cref="PlanTree.Current"/>），
/// 不是「正在做的那一个」——核心不追踪进度，推进一格就是**把它从树里删掉**
/// ⇒ 第一个叶子天然就是当前这一步。</para>
/// </summary>
public sealed record PlanSummary(
    bool Active,
    int Remaining,
    string? CurrentId,
    string? CurrentTitle,
    IReadOnlyList<PlanNode>? Tree,
    T0PlanNode? T0 = null);

/// <summary>模型/头可编辑的普通计划操作；没有 T0 字段，结构上无法通过此命令写 T0。</summary>
public sealed record PlanEdit(string Operation, IReadOnlyList<PlanNode>? Nodes = null,
    string? ParentId = null, int? Index = null, IReadOnlyList<string>? Ids = null);

/// <summary>Shared validation and edit semantics for model tools and user commands.</summary>
public static class PlanTree
{
    public const int MaxNodes = 64;
    public const int MaxDepth = 6;
    public const int MaxTitleChars = 160;
    public const int MaxBodyChars = 2_000;

    public static PlanNode? Current(SessionPlan? plan) => plan is null ? null : FirstLeaf(plan.Nodes);
    public static int Count(IReadOnlyList<PlanNode> nodes) => nodes.Sum(n => 1 + Count(n.Children));

    /// <summary>
    /// 线上的那一份摘要（见 <see cref="PlanSummary"/>）。⚠️ **三个入口都走它**：
    /// `/state` 的 `plan`、`plan.updated` 的 `plan`、`GET /sessions/{id}/plan` 的 `plan`。
    /// 各拼一份的后果是具体的：曾经一处给节点数组、另一处给 `{nodes, updatedAt}` 对象，
    /// 头的同一个渲染函数于是有一半路径把「有计划」显示成「还没有计划」。
    /// </summary>
    public static PlanSummary Summary(SessionPlan? plan)
    {
        var current = Current(plan);
        var hasOrdinary = plan?.Nodes.Count > 0;
        var hasT0 = plan?.T0 is not null;
        return new PlanSummary(
            Active: hasOrdinary || hasT0,
            Remaining: plan is null ? 0 : Count(plan.Nodes),
            CurrentId: current?.Id,
            CurrentTitle: current?.Title,
            Tree: hasOrdinary ? plan!.Nodes : null,
            T0: plan?.T0);
    }

    private static PlanNode? FirstLeaf(IReadOnlyList<PlanNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Count == 0) return node;
            var child = FirstLeaf(node.Children);
            if (child is not null) return child;
        }
        return null;
    }

    public static SessionPlan? EnsureT0(SessionPlan? plan, string title, string body, DateTimeOffset now)
    {
        var fixedT0 = new T0PlanNode { Title = title, Body = body };
        return plan is null
            ? new SessionPlan { Nodes = [], UpdatedAt = now, T0 = fixedT0 }
            : plan with { T0 = fixedT0, UpdatedAt = now };
    }

    /// <summary>移除固定 T0；普通树保留。两者都空时整个 Plan 归 null（与清空普通树一致）。</summary>
    public static SessionPlan? ClearT0(SessionPlan? plan, DateTimeOffset now)
    {
        if (plan?.T0 is null) return plan;
        var cleared = plan with { T0 = null, UpdatedAt = now };
        return cleared.Nodes.Count == 0 ? null : cleared;
    }

    private static SessionPlan? WithT0(SessionPlan? plan, T0PlanNode? t0, DateTimeOffset now) =>
        plan is null && t0 is null ? null : new SessionPlan
        {
            Nodes = plan?.Nodes ?? [], UpdatedAt = now, T0 = t0,
        };

    public static (SessionPlan? Plan, string? Error) Apply(SessionPlan? current, PlanEdit edit, DateTimeOffset now)
    {
        // 普通计划编辑只能改 Nodes；T0 永远从当前会话复制过去。
        if (edit.Operation == "overwrite")
        {
            var (replacement, error) = Create(edit.Nodes ?? [], now);
            if (error is not null) return (null, error);
            return WithT0(replacement, current?.T0, now) is { } plan
                ? (plan, null)
                : (null, null);
        }
        if (edit.Operation == "insert")
        {
            if (edit.Nodes is not { Count: > 0 }) return (null, "insert 需要至少一个节点。");
            var roots = current?.Nodes.ToList() ?? [];
            if (edit.ParentId is null) Insert(roots, edit.Index, edit.Nodes);
            else if (Find(roots, edit.ParentId) is not { } parent) return (null, $"找不到父节点 '{edit.ParentId}'。");
            // ⚠️ **叶子不能再挂东西，而这句话必须说清是哪一种失败**（2026-10-24）：
            // 原先往叶子下插入也回「找不到父节点」——调用方（头 / 模型）会去核对 id 拼写，
            // 而真正的原因是「这一条是叶子」。两种失败给同一个理由 = 一条查不下去的错误。
            else if (parent.Children.Count == 0)
                return (null, $"父节点 '{edit.ParentId}' 是叶子（没有子节点），不能往里插入。要分组请先给它一个子节点，或直接插到根 / 别的分支下。");
            var (inserted, insertError) = Create(roots, now);
            return insertError is not null ? (null, insertError) : (WithT0(inserted, current?.T0, now), null);
        }
        if (edit.Operation == "remove")
        {
            if (edit.Ids is not { Count: > 0 }) return (null, "remove 需要至少一个节点 id。");
            var roots = current?.Nodes.ToList() ?? [];
            foreach (var id in edit.Ids) if (!Remove(roots, id)) return (null, $"找不到节点 '{id}'。");
            Prune(roots);
            var (remaining, error) = Create(roots, now);
            return error is not null ? (null, error) : (WithT0(remaining, current?.T0, now), null);
        }
        return (null, "operation 只能是 overwrite、insert 或 remove。");
    }

    public static (SessionPlan? Plan, string? Error) Advance(SessionPlan? current, string expectedId, DateTimeOffset now)
    {
        if (current is { Nodes.Count: 0, T0: not null })
            return (current, "普通计划已空；固定 T0 任务不是可推进节点。请回顾 T0 并规划下一段。");
        var leaf = Current(current);
        if (leaf is null) return (current, "当前没有待推进的计划节点。");
        if (!string.Equals(leaf.Id, expectedId, StringComparison.Ordinal))
            return (current, $"计划已变化；当前节点是 {leaf.Id}：{leaf.Title}。请按当前计划继续。");
        var roots = current!.Nodes.ToList();
        if (!Remove(roots, expectedId)) return (current, "计划已变化；请重新读取当前计划。");
        Prune(roots);
        var (remaining, error) = Create(roots, now);
        if (error is not null) return (current, error);
        var stored = WithT0(remaining, current.T0, now);
        return (stored, null);
    }

    public static (SessionPlan? Plan, string? Error) CreateWithT0(
        IReadOnlyList<PlanNode> nodes, T0PlanNode? t0, DateTimeOffset now)
    {
        var (plan, error) = Create(nodes, now);
        if (error is not null) return (null, error);
        return (WithT0(plan, t0, now), null);
    }

    public static (SessionPlan? Plan, string? Error) Validate(IReadOnlyList<PlanNode> nodes, DateTimeOffset now) => Create(nodes, now);

    private static (SessionPlan? Plan, string? Error) Create(IReadOnlyList<PlanNode> nodes, DateTimeOffset now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        string? error = null;
        Validate(nodes, 1);
        if (error is not null) return (null, error);
        return nodes.Count == 0 ? (null, null) : (new SessionPlan { Nodes = nodes, UpdatedAt = now }, null);

        void Validate(IReadOnlyList<PlanNode> list, int depth)
        {
            if (depth > MaxDepth) { error = $"计划最多 {MaxDepth} 层。"; return; }
            foreach (var node in list)
            {
                if (++count > MaxNodes) { error = $"计划最多 {MaxNodes} 个节点。"; return; }
                if (string.IsNullOrWhiteSpace(node.Id) || !seen.Add(node.Id)) { error = "节点 id 必须非空且全树唯一。"; return; }
                if (string.IsNullOrWhiteSpace(node.Title) || node.Title.Length > MaxTitleChars) { error = $"标题必须为 1..{MaxTitleChars} 个字符。"; return; }
                if (node.Children.Count == 0 && (string.IsNullOrWhiteSpace(node.Body) || node.Body.Length > MaxBodyChars))
                { error = $"叶子节点正文必须为 1..{MaxBodyChars} 个字符。"; return; }
                if (node.Body?.Length > MaxBodyChars) { error = $"正文最多 {MaxBodyChars} 个字符。"; return; }
                Validate(node.Children, depth + 1);
                if (error is not null) return;
            }
        }
    }

    public static string Render(SessionPlan? plan)
    {
        if (plan is null) return "";
        var lines = new List<string>();
        if (plan.T0 is { } t0)
        {
            lines.Add("▣ T0（固定长期任务；不可由模型移除）");
            lines.Add($"  标题：{t0.Title}");
            lines.Add($"  正文：{t0.Body}");
        }
        if (plan.Nodes.Count == 0)
        {
            if (plan.T0 is not null) lines.Add("普通计划：空");
            return string.Join("\n", lines);
        }
        lines.Add("计划（剩余树；推进完成的当前叶子会将其移除）");
        var current = Current(plan);
        RenderNodes(plan.Nodes, 0, current?.Id, lines);
        return string.Join("\n", lines);
    }

    private static void RenderNodes(IReadOnlyList<PlanNode> nodes, int depth, string? currentId, List<string> lines)
    {
        foreach (var node in nodes)
        {
            var leaf = node.Children.Count == 0;
            lines.Add($"{new string(' ', depth * 2)}{(leaf ? (node.Id == currentId ? "▶" : "○") : "▸")} {node.Title} [{node.Id}]");
            if (leaf) lines.Add($"{new string(' ', depth * 2 + 2)}正文：{node.Body}");
            RenderNodes(node.Children, depth + 1, currentId, lines);
        }
    }

    /// <summary>按 id 找节点（全树）。<c>null</c> = 没有这个 id——与「找到了但它没有子节点」是两件事。</summary>
    private static PlanNode? Find(IReadOnlyList<PlanNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal)) return node;
            if (Find(node.Children, id) is { } found) return found;
        }
        return null;
    }

    private static void Insert(List<PlanNode> target, int? index, IReadOnlyList<PlanNode> nodes) =>
        target.InsertRange(Math.Clamp(index ?? target.Count, 0, target.Count), nodes);

    private static bool InsertUnder(List<PlanNode> nodes, string parentId, int? index, IReadOnlyList<PlanNode> insert)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.Id == parentId)
            {
                if (node.Children.Count == 0) return false;
                var children = node.Children.ToList(); Insert(children, index, insert);
                nodes[i] = node with { Children = children }; return true;
            }
            var copy = node.Children.ToList();
            if (InsertUnder(copy, parentId, index, insert)) { nodes[i] = node with { Children = copy }; return true; }
        }
        return false;
    }

    private static bool Remove(List<PlanNode> nodes, string id)
    {
        var i = nodes.FindIndex(n => n.Id == id);
        if (i >= 0) { nodes.RemoveAt(i); return true; }
        for (i = 0; i < nodes.Count; i++)
        {
            var children = nodes[i].Children.ToList();
            if (!Remove(children, id)) continue;
            nodes[i] = nodes[i] with { Children = children }; return true;
        }
        return false;
    }

    private static void Prune(List<PlanNode> nodes)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var node = nodes[i]; var children = node.Children.ToList(); Prune(children);
            if (node.Children.Count > 0 && children.Count == 0) nodes.RemoveAt(i);
            else if (children.Count != node.Children.Count) nodes[i] = node with { Children = children };
        }
    }
}

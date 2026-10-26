using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

public static class PlanJson
{
    public static (PlanEdit? Edit, string? Error) ParseEdit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "参数必须是对象。");
            var operation = root.TryGetProperty("operation", out var op) && op.ValueKind == JsonValueKind.String ? op.GetString() : null;
            IReadOnlyList<PlanNode>? nodes = null;
            if (root.TryGetProperty("nodes", out var ns))
            {
                if (ns.ValueKind != JsonValueKind.Array) return (null, "nodes 必须是数组。");
                nodes = ns.EnumerateArray().Select(ParseNode).ToList();
            }
            if (operation == "overwrite" && nodes is null) return (null, "overwrite 需要 nodes 数组。");
            var parent = root.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            int? index = root.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n) ? n : null;
            IReadOnlyList<string>? ids = root.TryGetProperty("ids", out var idList) && idList.ValueKind == JsonValueKind.Array
                ? idList.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : null;
            return operation is null ? (null, "缺少 operation。") : (new PlanEdit(operation, nodes, parent, index, ids), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { return (null, "计划参数格式不正确。"); }
    }

    private static PlanNode ParseNode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        string Required(string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new JsonException();
        var id = element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()! : Guid.NewGuid().ToString("N");
        var title = Required("title");
        var body = element.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        var children = element.TryGetProperty("children", out var c)
            ? c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(ParseNode).ToList() : throw new JsonException()
            : [];
        return new PlanNode { Id = id, Title = title, Body = body, Children = children };
    }
}

public sealed class PlanTool : ITool
{
    public const string NameOf = "plan";
    public string Name => NameOf;

    // ⚠️ **只读档就能改计划，这是设计意图，不是漏判**（2026-10-24，用户确认）。
    // `readonly` 档的含义是「只能规划、不碰文件与外部世界」，而**计划正是这个档位要产出的东西**
    // ⇒ 把它提到 `Write` 等于掐掉只读档唯一的产出通道，让那一档只能空谈。
    // ⚠️ 同理**不给它加审批**：计划改动不触碰文件、不发请求，且用户随时能在头上整份覆盖；
    // 加审批只会让「先想清楚再动手」这条工作流每一步都停一次。
    // ⚠️ `off` 档仍然全拒（那是「什么都不许做」，计划也不该写）——这一点由权限闸门自己保证，
    // 不在这里特判。`advance_plan` 同一条理由，见那边的注释。
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;
    public string Description =>
        "维护本会话的剩余计划树。**多步骤、或方向还没定的任务，先在这里写计划**：分支用来分组，"
        + "叶子是要执行的一步（叶子的 body 会作为当前步的说明出现在上下文里，写清要做什么）。"
        + "树每步都会重新渲染给你——它是跨步持久的工作台，不是一次性笔记。"
        + "operation=overwrite 整棵替换普通树（空数组 = 清空），insert 插到根或某个**分支**下"
        + "（插到叶子下会被拒），remove 按 id 删除（含子树）。"
        + "节点 id 可省略（核心会生成）；要在后续编辑里引用某个节点，就用上下文里显示的那个 id。"
        + "上限：64 个节点、6 层、标题 160 字、叶子 body 2000 字。"
        + "⚠️ 用户设置的 T0 是固定长期任务，本工具不能修改或删除它；advance_plan 也移除不了它。";
    public string ParametersSchemaJson => """{"type":"object","properties":{"operation":{"type":"string","enum":["overwrite","insert","remove"]},"nodes":{"type":"array"},"parentId":{"type":"string"},"index":{"type":"integer"},"ids":{"type":"array","items":{"type":"string"}}},"required":["operation"]}""";

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var (edit, error) = PlanJson.ParseEdit(call.ArgumentsJson);
        if (error is not null) return Task.FromResult(ToolText.Of(error));

        // ⚠️ 普通计划工具只能改树里的普通节点。PlanTree.Apply 会保留 Context.Plan.T0。
        var current = (context as SessionToolContext)?.Plan;
        var (next, validation) = PlanTree.Apply(current, edit!, CoreClock.Now());
        if (validation is not null) return Task.FromResult(ToolText.Of(validation));

        var summary = edit!.Operation switch
        {
            "overwrite" => next is null ? "普通计划已清空。" : $"普通计划已替换为 {PlanTree.Count(next.Nodes)} 个节点。",
            "insert" => $"已插入 {PlanTree.Count(edit.Nodes ?? [])} 个普通节点。",
            _ => $"已删除 {edit.Ids?.Count ?? 0} 个普通节点。",
        };
        if (next?.T0 is not null) summary += "固定 T0 任务仍保留。";
        return Task.FromResult(ToolText.Of(summary + "本步结束时生效。"));
    }
}

public sealed class AdvancePlanTool : ITool
{
    public const string NameOf = "advance_plan";
    public string Name => NameOf;
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;
    public string Description =>
        "把当前叶子（计划里带 ▶ 的那条）判定为完成，并**从剩余计划里移除它**——必须提交它的 currentId。"
        + "⚠️ 计划只存剩余：移除不可撤销，所以只在那一项的工作真的做完、且结果已经产出之后才调用。"
        + "本步还有没做完的事、或只是想做下一项但当前项没完成时，都不要推进。"
        + "计划为空时不要调用（用户设置的 T0 不是可推进节点，它不会被移除）。";
    public string ParametersSchemaJson => """{"type":"object","properties":{"currentId":{"type":"string"}},"required":["currentId"]}""";
    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var currentId = ToolArgs.String(call.ArgumentsJson, "currentId");
        if (string.IsNullOrEmpty(currentId)) return Task.FromResult(ToolText.Of("需要当前节点 currentId。"));

        // ⚠️ **同 `plan`：判据只有 `PlanTree.Advance` 一个**。没有这棵树时工具只能回答
        // 「已登记」，而实际是不是当前叶子、有没有计划，它都答不上来——那正是要修的静默丢改动。
        var (next, error) = PlanTree.Advance((context as SessionToolContext)?.Plan, currentId, CoreClock.Now());
        if (error is not null) return Task.FromResult(ToolText.Of(error));

        var after = PlanTree.Current(next);
        return Task.FromResult(ToolText.Of(after is null
            ? "当前叶子已移除；计划已空。本步结束时生效。"
            : $"当前叶子已移除；下一个是 {after.Id}：{after.Title}。本步结束时生效。"));
    }
}

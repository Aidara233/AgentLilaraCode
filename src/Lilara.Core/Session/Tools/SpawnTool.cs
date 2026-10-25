using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `spawn`——**派生子会话**（`01` §10，2026-10-24）。
///
/// <para>⚠️ <b>它不建会话。</b>工具没有那双手（`IToolContext` 是只读的，`04` §5.1），
/// 而真正的 `SessionHandle.Create` 还需要 `PluginHost` / 事件总线 / `Start`——那些都在
/// `CommandDispatcher`。⇒ 它只**说清楚要建什么**，走
/// <see cref="ToolOutcome.Spawn"/> 交回去，落地在核心。这与 `ask` / `release` /
/// `finish_goal` 是**同一个形状**（工具说一句话，推进器/分派器动手）。</para>
///
/// <para>⚠️ <b>但子会话 id 由它预分配</b>（`Ids.New()`）：结果正文里那个 id 于是
/// **将来真的会存在**，模型下一步拿它调 `tell_child` 不会指向空气。反过来说——
/// **所有校验必须在这里做完**（深度 / 派生上下文 / 参数），因为结果一旦落盘就改不了，
/// 而「说了已派出、其实没建」是一条**假话**（`01` §3.6 ①之二）。</para>
///
/// <para>⚠️ <b>它是<b>机制入口</b>，不是「干活的能力」</b>（`BuiltInTools` 那条拆读）：
/// 它开的是「会话能被派生」这扇门，与 `release` 开「上下文能被画线」同一类。
/// ⇒ 它住 `BuiltInTools`，不是插件。</para>
/// </summary>
public sealed class SpawnTool : ITool
{
    /// <summary>
    /// ⚠️ **名字是常量**：推进器要按名字从一批调用里挑出它（`ExecutedCallIds` 那条路），
    /// 而它拿不到实例（工具表是 `IReadOnlyList&lt;ITool&gt;`）。写成两遍就会漂，
    /// 而漂的表现是**子会话一个都不建**——静默（同 `ReleaseTool.NameOf` 的理由）。
    /// </summary>
    public const string NameOf = "spawn";

    public string Name => NameOf;

    /// <summary>
    /// **消耗型**：它不改用户的文件，但它会**再开一个模型循环**——那是花钱、花时间。
    /// ⚠️ 挂 `ReadOnly` 会让一个 `readonly` 档的会话也能派生，而「只读」的语义是
    /// 「不许改变世界」；派生出来的孩子虽然也继承 `readonly`（`Subagent.NarrowerPermission`），
    /// 但它照样烧上游的额度。⇒ 按**代价**分类，不按「动了哪个目录」分。
    /// </summary>
    public PermissionClass PermissionClass => PermissionClass.Consuming;

    public string Description =>
        "派一个子会话去干一件独立的事，然后你继续做自己的。"
        + "子会话看不到你的对话历史，所以 task 要写成一份**自足的任务说明**"
        + "（做什么、在哪个目录、验收标准）。"
        + "它会自己跑，需要问你的时候会来问；你也可以用 tell_child 随时给它补话。"
        + "它落定（或者有事要问你）时，你会收到一条 subagent_report 结果。"
        + "⚠️ 默认只能派生一层：子会话自己不能再派生。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "task": {
              "type": "string",
              "description": "交给子会话的任务说明。它看不到你的历史，所以必须自足。"
            },
            "title": {
              "type": "string",
              "description": "可选：给人看的名字（界面上显示）。"
            },
            "preset": {
              "type": "string",
              "description": "可选：子会话用哪份系统提示词（presets/<id>.md）。"
            },
            "permission": {
              "type": "string",
              "description": "可选：请求的权限档位。只能更窄；给得比你自己宽也不会生效。"
            }
          },
          "required": ["task"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        // ⚠️ **参数错误 ⇒ 一句人话的结果，不抛**（同 `ask` / `read` 的判据）：
        // 抛会让 `tool_use` / `tool_result` 那一对只落一半，而半个在拿到的不是
        // 「少点内容」，是上游 400。
        var task = ToolArgs.String(call.ArgumentsJson, "task");
        if (string.IsNullOrWhiteSpace(task))
            return Task.FromResult(ToolText.Of(
                "缺少 `task`。子会话看不到你的对话历史，所以你要把整件事写清楚："
                + "做什么、在哪个目录、怎么算做完。"));

        // ⚠️ **没有派生上下文 ⇒ 如实说**，不猜「大概是顶层会话吧」。
        // 猜错的方向是一个本该被深度限制挡住的调用**静默通过**。
        if (context.Subagents is not { } scope)
            return Task.FromResult(ToolText.Of("这个会话没有派生上下文，不能派生子会话。"));

        // ⚠️ **深度判据只有一处**（`Subagent.CanSpawnFrom`）：工具这一侧与创建那一侧
        // 各写一个 `depth >= max` 就会漂，而漂的表现是「工具说能派，核心不建」——
        // 模型会反复重试同一个调用。
        if (!scope.CanSpawn)
            return Task.FromResult(ToolText.Of(
                $"已经到派生深度上限（当前第 {scope.Depth} 层、上限 {scope.MaxDepth} 层），"
                + "不能再派生。这件事你自己做，或者交给父会话。"));

        var childId = Ids.New();
        var title = ToolArgs.String(call.ArgumentsJson, "title");

        return Task.FromResult(new ToolOutcome
        {
            Content =
            [
                new Block.TextBlock(
                    $"已派出子会话 {childId}{(string.IsNullOrWhiteSpace(title) ? "" : $"（{title}）")}。"
                    + "它在跑自己的循环；有事会来问你，落定时会给你一条 subagent_report。"
                    + "要补话就调 tell_child。"),
            ],
            Spawn = new SpawnRequest(
                childId,
                task,
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                ToolArgs.String(call.ArgumentsJson, "preset"),
                // ⚠️ **它是「请求」不是「结果」**：核心取 `min(父, 它)`。
                // 所以给一个比自己宽的档位无害——它是提权请求，而提权在本设计里不成立。
                ToolArgs.String(call.ArgumentsJson, "permission")),
        });
    }
}

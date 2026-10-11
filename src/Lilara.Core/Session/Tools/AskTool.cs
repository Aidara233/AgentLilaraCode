using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `ask`——**问用户**（`01` §3.8）。它是模型主动开口的那个工具，
/// 与闸门那条路走的是**同一个** <see cref="AskRequest"/>、同一张卡、同一条恢复路径
/// ——差别只在 <see cref="AskKind"/>（闸门问的是「准不准跑」，它问的是「你怎么想」）。
///
/// <para>⚠️ <b>它声明 <see cref="AsksBeforeRunning"/> = <c>true</c>，而这是它「没有副作用」
/// 这句话的**结构性**表达</b>：`ToolExecutor` 会在「先判全」那一趟跑它一遍、只取它的问题，
/// 而**永远不**让它进入执行那一趟（它没有执行相位）。⇒ 「一次询问一个信封」这条
/// 不需要任何新机制 —— 它就是「跑一次、拿到 Ask、然后整个批次中止」。</para>
///
/// <para>⚠️ <b>参数格式被文档化了两次，两次都是必需的</b>：
/// <see cref="Description"/>（给模型看）与 <see cref="ParametersSchemaJson"/>（给 provider 看）。
/// 两者说不一致时**模型会按 schema 写、按描述理解**——而产物是一张填不对的卡。</para>
/// </summary>
public sealed class AskTool : ITool
{
    public string Name => "ask";

    /// <summary>
    /// ⚠️ <b>只读，而这是刻意的</b>：问本身什么都不改。
    /// 挂 <see cref="PermissionClass.Writing"/> 会让「`standard` 档问用户一个问题」
    /// 也去走闸门 ⇒ 变成**问「能不能问」**——那是个笑话，而且是个真实的死循环风险
    /// （闸门 `Ask` ⇒ 需要用户回答 ⇒ 用户回答要走闸门…）。
    /// </summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    /// <summary>⚠️ 见类型注释：它是「执行前就产出问题」这条的声明。</summary>
    public bool AsksBeforeRunning => true;

    public string Description =>
        "向用户提问，然后**停下来等他回答**。可以一次问多个问题，它们会显示在同一张卡上。"
        + "用户答完之后，答案会作为这次调用的结果回到你手里。"
        + "适合：需要用户做选择、需要只有他知道的信息、或者一个动作要不要做。"
        + "⚠️ 不要用它问「你还好吗」这类寒暄——每次提问都会打断用户。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "questions": {
              "type": "array",
              "description": "要问的问题，按顺序显示。",
              "items": {
                "type": "object",
                "properties": {
                  "id": {
                    "type": "string",
                    "description": "这个问题的标识，回答会按它配对。同一批里必须唯一。"
                  },
                  "question": {
                    "type": "string",
                    "description": "问什么（给用户看的那句话）。"
                  },
                  "type": {
                    "type": "string",
                    "enum": ["choice", "text", "form"],
                    "description": "choice = 从选项里挑；text = 自由输入；form = 一组字段。"
                  },
                  "options": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "type=choice 时必给。两个选项就是一次确认。"
                  },
                  "multi": {
                    "type": "boolean",
                    "description": "type=choice 时：可以多选。省略 = 单选。"
                  },
                  "fields": {
                    "type": "array",
                    "description": "type=form 时必给。",
                    "items": {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" },
                        "label": { "type": "string" },
                        "value": { "type": "string" }
                      },
                      "required": ["name", "label"]
                    }
                  }
                },
                "required": ["id", "question", "type"]
              }
            }
          },
          "required": ["questions"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var (items, error) = Parse(call.ArgumentsJson);

        // ⚠️ **参数写错时给一条说人话的结果，不是问一张空卡**（同 `read` 的四个参数错误）：
        // 一张没有问题的卡会让用户对着一个空白面板发呆，而模型**看不出自己写错了**。
        // ⚠️ 注意它与「问了」走的是**不同**的路：错误 ⇒ 正常结果（这一步照常结束）；
        // 问了 ⇒ 挂起。混起来的话，一次参数写错就把会话**卡在等待里**。
        if (error is not null)
            return Task.FromResult(ToolText.Of(error));

        return Task.FromResult(new ToolOutcome
        {
            // ⚠️ **内容必须给它一个非空值**：`ToolOutcome.Content` 是 `required`，
            // 而这条路径上它**不会被落盘**（`ToolExecutor` 见到 `Ask` 就整批中止）——
            // 空列表在这里是「这个结果什么都没有」，语义上对（问本身不产出内容）。
            Content = [],
            Ask = new AskRequest
            {
                Id = Ids.New(),
                Kind = AskKind.Question,
                Items = items,
                // ⚠️ **空 = 不问「记不记住」**：授权可以记住，问意见没有「记住」这回事
                // （下次还是得问，因为答案本身是内容、不是许可）。
                Remember = [],
            },
        });
    }

    /// <summary>
    /// 从**落盘的那条 `tool_use`** 里解出这次询问的问题（`01` §3.8）。
    ///
    /// <para>⚠️ <b>为什么不能只信内存那份</b>：问题原文本来住在 `SessionHandle.AwaitingInput`
    /// （内存），而**任何一次「从盘重新载入」都会把它丢掉**——不只是重启
    /// （实测 2026-10-08：核心重启后打开页面，问题**是空的**）。
    /// 而它在盘上**本来就有**：那条模型消息必须落盘（否则答完补的 `tool_result` 悬空），
    /// 参数就在里面。⇒ **从落盘字节重新解一遍**，内存那半就成了缓存而不是真相来源。</para>
    ///
    /// <para>⚠️ <b>必须有<b>唯一</b>一份实现</b>：两个调用点——`CommandDispatcher`（渲染答复）
    /// 与 `ControlServer`（渲染卡片）——各写一份的话，就会出现「页面看得见问题、
    /// 而回答时说你没答」这种分叉。⚠️ <b>本仓栽过同形的，而这次是同一批里又栽一次</b>：
    /// 我先只修了 `/state` 那条，回答那条没修，于是实测里答复变成
    /// 「会话在等待期间重启过，原来那批问题没能保留」——**而问题明明就在盘上**。</para>
    /// </summary>
    /// <param name="callId">被挂起的那次调用的协议 id（`PausedAsk.CallId`）。</param>
    /// <param name="permissionLevel">
    /// 当前权限档位。⚠️ <b>只有闸门那条询问用它</b>（问句里要印「当前档位是什么」，
    /// 用户据此判断该不该提权——`01` §3.5：模型不能自提权，只能请求）；
    /// `ask` 那条用不上（它的问题是模型自己写的）。
    /// ⚠️ 传 <c>null</c> 是**合法**的（读点拿不到轴时），那问句会印「未知」——
    /// 差一点信息，但**不编一个档位**（编的那个会让用户提出一个无效的请求）。
    /// </param>
    public static IReadOnlyList<AskItem> ItemsOf(
        Storage.SessionStore store, string projectKey, string sessionId, string callId,
        string? permissionLevel = null)
    {
        foreach (var e in store.ReadTimeline(projectKey, sessionId))
        {
            if (e is not TimelineEvent.MessageEvent m) continue;

            foreach (var b in m.Blocks.OfType<Block.ToolUseBlock>())
            {
                if (!string.Equals(b.CallId, callId, StringComparison.Ordinal)) continue;

                return string.Equals(b.Name, "ask", StringComparison.Ordinal)
                    // `ask` 那条：问题原文在**工具参数**里，解出来就是。
                    ? Parse(b.Arguments).Items
                    // 闸门那条（`Gate.AskFor`）。
                    //
                    // ⚠️ **它不是「解不出来」，是「没有可解的输入」**——而这里原先直接
                    // `return []`，把那件事说成了「问题没留住」。⇒ 页面显示
                    // 「这次询问的问题取不回来了」，而那张**改参数再批准**的卡**整个消失**，
                    // 用户只剩「批准 / 拒绝」两颗按钮（`01` §3.8 明确要求那张卡能改参数）。
                    //
                    // ⚠️ 而这条路径的**唯一入口就是重启之后**：内存那半
                    // （`SessionHandle.AwaitingInput`）在进程活着时是好的，只有从盘载入才会走到这里。
                    // ⚠️ **它是跑真模型实测抓出来的**（2026-10-11）——单元测试碰不到它，
                    // 因为那条路要「挂起之后重开进程」。而在此之前它**根本不可达**
                    // （闸门那两格没有生产者），所以这个洞一直没有露出来。
                    : Gate.SecondHalfOf(b, permissionLevel);
            }
        }

        return [];
    }

    /// <summary>
    /// 解析 <c>questions</c>。返回 (问题列表, 错误话)。⚠️ **两个恰有一个非空**。
    ///
    /// <para>⚠️ <b>宽严同 <see cref="ToolArgs"/>：只认它说的话，不猜</b>——
    /// 但这里**多一步「说清哪里不对」**：`ToolArgs` 只回答「取到值了吗」，
    /// 而模型需要知道**改哪个键**。所以它拿的是 `JsonDocument` 自己走。</para>
    /// </summary>
    internal static (IReadOnlyList<AskItem> Items, string? Error) Parse(string argumentsJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return ([], "`ask` 的参数不是合法 JSON。它要的是 {\"questions\": [...]}。");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("questions", out var qs)
                || qs.ValueKind != JsonValueKind.Array)
            {
                return ([], "`ask` 缺少 `questions` 数组。它要的是 {\"questions\": [...]}。");
            }

            if (qs.GetArrayLength() == 0)
                return ([], "`questions` 是空的——没有问题就不要问。");

            // ⚠️ **上限在解析时就拦**（`01` §3.9）：一张卡是**用户**要挨个填的，
            // 而这里没有协议层的上限兜着（多问是一个信封）。
            // ⚠️ 判据是「拒绝」而不是「截断」：静默丢掉几个问题会让模型以为**它问的都问了**，
            // 那是「多了一条假信息」（`01` §3.6 ①之二）。同 `read` 的 `limit` 写错那条。
            if (qs.GetArrayLength() > ToolLimits.MaxAskItems)
            {
                return ([], $"一次最多问 {ToolLimits.MaxAskItems} 个问题（给的是 {qs.GetArrayLength()} 个）。"
                          + "把它们合起来，或者分几步问。");
            }

            var items = new List<AskItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var q in qs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object)
                    return ([], "`questions` 里每一项都要是一个对象。");

                var id = Str(q, "id");
                var question = Str(q, "question");
                var type = Str(q, "type");

                if (string.IsNullOrWhiteSpace(id))
                    return ([], "每个问题都要有 `id`（回答靠它配对）。");
                if (string.IsNullOrWhiteSpace(question))
                    return ([], $"问题 '{id}' 没有 `question`（给用户看的那句话）。");

                // ⚠️ **id 必须唯一**：重复的 id 会让答案**归错位**，而那是静默的
                // （用户的答案落到另一个问题上）。同 `claim.acquire` 那条「同一 scope 里
                // 两条重叠路径也要命令内部自洽」——这里更硬，因为它直接损坏语义。
                if (!seen.Add(id))
                    return ([], $"`id` 重复了：'{id}'。每个问题要一个不同的 id。");

                var item = type switch
                {
                    "choice" => ChoiceOf(q, id, question),
                    "text" => AskItem.Text(id, question),
                    "form" => FormOf(q, id, question),
                    _ => null,
                };

                if (item is null)
                    return ([], $"问题 '{id}' 的 `type` 是 '{type}'——只认 choice / text / form。");

                items.Add(item);
            }

            return (items, null);
        }
    }

    private static AskItem? ChoiceOf(JsonElement q, string id, string question)
    {
        if (!q.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array
            || opts.GetArrayLength() == 0)
        {
            return null;
        }

        var options = new List<string>();
        foreach (var o in opts.EnumerateArray())
        {
            // ⚠️ 只认字符串：选项是**给人看的标签**，允许数字会让 {"options":[1,2]}
            // 变成一个「值是 1 而标签是空」的怪东西（见 `AskOption` 那两个字段的分工）。
            if (o.ValueKind != JsonValueKind.String) return null;
            options.Add(o.GetString()!);
        }

        var multi = q.TryGetProperty("multi", out var m) && m.ValueKind == JsonValueKind.True;
        return AskItem.Choice(id, question, options, multi);
    }

    private static AskItem? FormOf(JsonElement q, string id, string question)
    {
        if (!q.TryGetProperty("fields", out var fs) || fs.ValueKind != JsonValueKind.Array
            || fs.GetArrayLength() == 0)
        {
            return null;
        }

        var fields = new List<AskField>();
        foreach (var f in fs.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) return null;

            var name = Str(f, "name");
            if (string.IsNullOrWhiteSpace(name)) return null;

            // ⚠️ `label` 缺了就用 `name`——**不拒绝**。理由：label 是**显示**用的，
            // 缺了顶多难看一点，而拒绝会让模型为了一个装饰性字段白跑一趟。
            // （对比 `name`：它缺了就没法归位，那是**语义**问题，所以上面那条是硬的。）
            var label = Str(f, "label") ?? name;
            fields.Add(new AskField(name, label, Str(f, "value")));
        }

        return AskItem.Form(id, question, fields);
    }

    /// <summary>取一个字符串字段；不是字符串（含缺失）⇒ <c>null</c>。</summary>
    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

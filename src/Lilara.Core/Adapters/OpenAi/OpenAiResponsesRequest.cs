using System.Text.Json.Nodes;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// canonical → OpenAI Responses 请求体。
/// ⚠️ 纯函数：同一 `ModelRequest` 必须构造出**逐字节相同**的 JSON（00 §5 铁律 1）。
/// ⚠️ 用 `JsonObject` 显式按序插入——**绝不依赖字典枚举序**。
/// ⚠️ 两个最容易漏的实测事实：字段名是 `max_output_tokens`（不是 `max_tokens`）；
///    每个 input 项**必须带 `"type":"message"`**（少了上游直接报 `upstream_error`）。
///
/// ⚠️ 与 <see cref="OpenAiChatRequest"/> **形状故意不同**，不要「统一」：
/// 控制器实测 Chat 拒收裸字符串*数组*（`["一","二"]` → `invalid_request_error`），
/// 而 Responses 全都收（裸串 / `input_text` 数组 / 多段）。
/// 两条协议的 `content` 形状**不可互换**——这正是 `04` §1.2 让每个适配器各做各的减法的原因。
/// </summary>
public static class OpenAiResponsesRequest
{
    public const string Path = "/v1/responses";

    public static string Build(ModelRequest request)
    {
        if (request.Timeline.Count == 0)
            throw new ArgumentException("时间线为空，无法构造请求", nameof(request));

        var input = new JsonArray();
        foreach (var evt in request.Timeline)
            input.Add(ToItem(evt, request.Protocol));

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_output_tokens"] = request.MaxTokens,
        };

        // ⚠️ 系统提示走顶层 `instructions`（不占一条消息）。核心自带零提示词（04 §1.3），
        // 通常为空——空时**不发这个字段**，而不是发个空串。
        // ⚠️ 判空必须落在**拼接后的文本**上，不能判 `SystemBlocks.Count`：
        // 非空的输入若全是非文本块，拼接结果是空串，发出去就是一个**空的 instructions**。
        var systemText = string.Concat(
            request.SystemBlocks.OfType<Block.TextBlock>().Select(b => b.Text));
        if (systemText.Length > 0)
            body["instructions"] = systemText;

        body["input"] = input;

        return body.ToJsonString(Json.Options);
    }

    /// <summary>
    /// 一条 canonical 消息 → 协议 input 项。
    /// ⚠️ Responses **不暴露推理**（实测：返回体里没有推理字段）→ 推理块丢且不伪造（spec §10.2）。
    /// ⚠️ 诚实说明：`protocol` 在这里**对输出没有可观测影响**——过滤结果只被
    /// `OfType&lt;TextBlock&gt;()` 消费，而推理块永远不是 `TextBlock`。实测：同一时间线分别按
    /// `"openai-responses"`、`"openai-chat"`、`"claude-messages"` 过滤，产出**逐字节相同**。
    /// 保留它只是为了与另两个协议的构造器形状一致、并与能力表保持同一处判断。
    /// </summary>
    private static JsonObject ToItem(TimelineEvent evt, string protocol)
    {
        var message = (TimelineEvent.MessageEvent)evt;
        var role = message.From == Speaker.User ? "user" : "assistant";

        // ⚠️ 先按协议能力过滤推理块——Responses 不承载，丢且不伪造（ReasoningPolicy）
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        // 过滤掉推理块后可能一条不剩（推理模型只思考不落笔），必须早失败。
        if (texts.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        // ⚠️ 用户用 `input_text`、助手用 `output_text`（实测两者上游都收，这是语义正确的分法）。
        // ⚠️ 与 Chat 不同：这里**始终发 part 数组**（单块也是）。实测 Responses 收裸字符串，
        //    但数组对单块/多块是同一条路径——少一个分支，少一处无覆盖的代码。
        var blockType = message.From == Speaker.User ? "input_text" : "output_text";
        var content = new JsonArray(
            texts.Select(t => (JsonNode)new JsonObject { ["type"] = blockType, ["text"] = t }).ToArray());

        return new JsonObject
        {
            ["type"] = "message",   // ⚠️ 必须——少了它上游报 upstream_error（实测）
            ["role"] = role,
            ["content"] = content,
        };
    }
}

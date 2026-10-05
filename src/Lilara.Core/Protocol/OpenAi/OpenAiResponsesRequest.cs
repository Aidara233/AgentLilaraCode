using System.Text.Json.Nodes;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

// canonical → OpenAI Responses 请求体。纯函数：同一 ModelRequest 必须构造出逐字节相同的 JSON；
// 用 JsonObject 显式按序插入，绝不依赖字典枚举序。
// 两个容易漏的实测事实：字段名是 max_output_tokens（不是 max_tokens）；每个 input 项必须带 "type":"message"
// （少了上游报 upstream_error）。
// 与 OpenAiChatRequest 形状故意不同：Chat 拒收裸字符串数组，Responses 全都收——两条协议的 content 形状不可互换。
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

        // 同 Chat：`null` 不写这个键。⚠️ **字段名两条 OpenAI 协议都叫 `temperature`**——
        // 与 `max_tokens` → `max_output_tokens` 那次改名不同，这里不要照抄那个改法。
        if (request.Temperature is { } temperature)
            body["temperature"] = temperature;

        // 系统提示走顶层 instructions（不占一条消息）。空时不发这个字段，而不是发个空串：
        // 判空必须落在拼接后的文本上，非空输入若全是非文本块会拼出空串。
        var systemText = string.Concat(
            request.SystemBlocks.OfType<Block.TextBlock>().Select(b => b.Text));
        if (systemText.Length > 0)
            body["instructions"] = systemText;

        body["input"] = input;

        return body.ToJsonString(Json.Options);
    }

    // 一条 canonical 消息 → 协议 input 项。Responses 不暴露推理（返回体无推理字段）→ 丢且不伪造；
    // protocol 参数在这里对输出无可观测影响（过滤结果只被 OfType<TextBlock>() 消费）。
    private static JsonObject ToItem(TimelineEvent evt, string protocol)
    {
        var message = (TimelineEvent.MessageEvent)evt;
        var role = message.From == Speaker.User ? "user" : "assistant";

        // 先按协议能力过滤推理块——Responses 不承载，丢且不伪造。
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        // 过滤掉推理块后可能一条不剩（推理模型只思考不落笔），必须早失败。
        if (texts.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        // 用户用 input_text、助手用 output_text（上游两者都收）。
        // 始终发 part 数组（单块也是）：数组对单块/多块是同一条路径，少一处无覆盖的代码。
        var blockType = message.From == Speaker.User ? "input_text" : "output_text";
        var content = new JsonArray(
            texts.Select(t => (JsonNode)new JsonObject { ["type"] = blockType, ["text"] = t }).ToArray());

        return new JsonObject
        {
            ["type"] = "message",   // 必须——少了它上游报 upstream_error
            ["role"] = role,
            ["content"] = content,
        };
    }
}

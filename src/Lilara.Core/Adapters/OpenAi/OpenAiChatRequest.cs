using System.Text.Json;
using System.Text.Json.Nodes;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// canonical → OpenAI Chat 请求体。
/// ⚠️ 纯函数：同一 `ModelRequest` 必须构造出**逐字节相同**的 JSON（00 §5 铁律 1）。
/// ⚠️ 用 `JsonObject` 显式按序插入——**绝不依赖字典枚举序**。
/// </summary>
public static class OpenAiChatRequest
{
    public const string Path = "/v1/chat/completions";

    public static string Build(ModelRequest request)
    {
        if (request.Timeline.Count == 0)
            throw new ArgumentException("时间线为空，无法构造请求", nameof(request));

        var messages = new JsonArray();

        // ⚠️ 系统提示放第一条消息（Chat 协议没有独立 system 字段）。
        // 核心自带零提示词（04 §1.3），所以通常走不到这里——但适配器要支持。
        // ⚠️ 判空必须落在**拼接后的文本**上，不能判 `SystemBlocks.Count`：
        // 非空的输入若全是非文本块，拼接结果是空串，发出去就是一个**空 system 消息**
        // （正是 Review Focus #3 要防的「空串 vs 不存在」）。
        var systemText = string.Concat(
            request.SystemBlocks.OfType<Block.TextBlock>().Select(b => b.Text));
        if (systemText.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });

        foreach (var evt in request.Timeline)
            messages.Add(ToMessage(evt, request.Protocol));

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = messages,
        };

        return body.ToJsonString(Json.Options);
    }

    /// <summary>
    /// 一条 canonical 消息 → 协议消息。
    /// ⚠️ Chat **不暴露推理**（实测：返回体里没有推理字段）→ 推理块丢且不伪造（Task 3 / spec §10.2）。
    /// ⚠️ 诚实说明：`protocol` 在这里**对输出没有可观测影响**——过滤结果只被
    /// `OfType&lt;TextBlock&gt;()` 消费，而推理块永远不是 `TextBlock`。实测：
    /// 同一时间线分别按 `"openai-chat"` 与 `"claude-messages"` 过滤，产出**逐字节相同**。
    /// 保留它只是为了与其它两个协议的构造器形状一致、并与能力表保持同一处判断。
    /// </summary>
    private static JsonObject ToMessage(TimelineEvent evt, string protocol)
    {
        var message = (TimelineEvent.MessageEvent)evt;
        var role = message.From == Speaker.User ? "user" : "assistant";

        // ⚠️ 先按协议能力过滤推理块——Chat 不承载，丢且不伪造（ReasoningPolicy）
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        // Review Focus #2 的同类：`content` 为空会被协议拒绝。
        // 过滤掉推理块后可能一条不剩（推理模型只思考不落笔），必须早失败。
        if (texts.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        // 单文本走字符串形式（协议原生形状，最省字节）；多文本走数组。
        // ⚠️⚠️ **未经实测的形状**：数组的**元素类型**。核心自带零提示词、且一次发言通常
        // 只有一块正文，所以走到这里的现实路径极少。OpenAI 官方对 `content` 数组要求的是
        // 「带 type 的内容部件」（`{"type":"text","text":…}`），**不是裸字符串数组**。
        // 本计划实测只覆盖了「字符串或数组」这句话本身，没覆盖元素形状。
        // ⇒ Task 9/10 真联调时必须验证这个分支；若上游 400，就是这里。
        return texts.Count == 1
            ? new JsonObject { ["role"] = role, ["content"] = texts[0] }
            : new JsonObject
            {
                ["role"] = role,
                ["content"] = new JsonArray(texts.Select(t => (JsonNode)t!).ToArray()),
            };
    }
}

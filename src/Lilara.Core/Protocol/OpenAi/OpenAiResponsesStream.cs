using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

/// <summary>
/// 把 Responses 的 SSE 事件攒回一份**与非流式响应同形**的 JSON（理由同
/// <see cref="OpenAiChatStream"/>：映射只有 <see cref="OpenAiResponsesResponse.Map"/> 一处）。
///
/// <para>⚠️ <b>优先用上游自己给的那份完整对象</b>：`response.completed` 里带着整份
/// <c>response</c>（含 usage、推理项的 encrypted_content、内置工具项）。
/// 自己按增量拼一份，会把那些**增量里根本不出现**的东西静默丢掉——它们不是显示内容，
/// 但它们是下一轮要原样回传的状态。</para>
/// </summary>
public static class OpenAiResponsesStream
{
    /// <summary>一帧 `data:` 的解析结果。<c>null</c> = 这一帧没有可见增量。</summary>
    public static ModelDelta? Apply(string data, ResponsesStreamState state)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return null;

        switch (type.GetString())
        {
            case "response.completed":
                if (root.TryGetProperty("response", out var response)
                    && response.ValueKind == JsonValueKind.Object)
                    state.Completed = response.GetRawText();
                return null;

            // 正文增量。`delta` 是这一段文本。
            case "response.output_text.delta":
                return Text(root, state.Text);

            // 推理摘要增量：与正文分开放（头的渲染也是两块分开的）。
            case "response.reasoning_summary_text.delta":
                return Reasoning(root, state.Reasoning);

            // 拒答增量：内容上它仍是「模型说的话」，但最终回复的 refused 判定来自
            // 完整响应里的 status / 内容，这里只把它当正文显示。
            case "response.refusal.delta":
                return Text(root, state.Text);

            default:
                return null;
        }
    }

    private static ModelDelta? Text(JsonElement root, StringBuilder sink)
    {
        if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.String)
            return null;
        if (delta.GetString() is not { Length: > 0 } text) return null;

        sink.Append(text);
        return new ModelDelta(0, "text", text);
    }

    private static ModelDelta? Reasoning(JsonElement root, StringBuilder sink)
    {
        if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.String)
            return null;
        if (delta.GetString() is not { Length: > 0 } text) return null;

        sink.Append(text);
        return new ModelDelta(0, "reasoning", text);
    }

    /// <summary>
    /// 流结束时产出的、与非流式响应同形的 JSON。
    /// <para>⚠️ 上游给了 `response.completed` ⇒ **原样用它**；没给（老旧网关 / 提前断开）
    /// ⇒ 只能用攒到的正文现拼一份，且**没有 usage**（那是上游没报告的，不是 0）。</para>
    /// </summary>
    public static string Finish(ResponsesStreamState state)
    {
        if (state.Completed is { } completed) return completed;

        var content = new JsonArray();
        if (state.Text.Length > 0)
            content.Add(new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = state.Text.ToString(),
            });

        var output = new JsonArray();
        if (content.Count > 0)
            output.Add(new JsonObject { ["type"] = "message", ["content"] = content });

        // 推理摘要同样补一项：不补的话，没有 `response.completed` 的端点上
        // 「模型想过什么」会整段消失（记录里少一块，而且没人看得出来）。
        if (state.Reasoning.Length > 0)
            output.Insert(0, new JsonObject
            {
                ["type"] = "reasoning",
                ["summary"] = new JsonArray(new JsonObject
                {
                    ["type"] = "summary_text",
                    ["text"] = state.Reasoning.ToString(),
                }),
            });

        return new JsonObject
        {
            ["status"] = "completed",
            ["output"] = output,
        }.ToJsonString();
    }
}

/// <summary>一次 Responses 流式响应的累积状态。<b>不是线程安全的</b>。</summary>
public sealed class ResponsesStreamState
{
    public string? Completed { get; set; }

    public StringBuilder Text { get; } = new();
    public StringBuilder Reasoning { get; } = new();
}

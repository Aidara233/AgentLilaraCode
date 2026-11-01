using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

/// <summary>
/// 把 Chat 的 SSE 帧攒回一份**与非流式响应同形**的 JSON。
///
/// <para>⚠️ <b>这是刻意的：映射只有一份实现</b>（<see cref="OpenAiChatResponse.Map"/>）。
/// 流式自己再写一套块级判定，等于让「模型说了什么」有两个出处——两处的判据会在
/// 下次改协议时悄悄分叉，而症状是「流式时某个块丢了」。这里只负责**拼字节**。</para>
///
/// <para>⚠️ <b>工具参数照旧逐字拼</b>：`delta.tool_calls[].function.arguments` 是原文分片，
/// 拼接结果与整份响应里的 `arguments` 逐字相同（`01` §9 那条纪律因此原样成立）。
/// 它们**不进增量**（只供显示的是正文 / 思考），这里只是把它们攒回最终回复。</para>
/// </summary>
public static class OpenAiChatStream
{
    /// <summary>一帧 `data:` 的解析结果。<c>null</c> = 这一帧没有可见增量（例如只带 usage 的尾帧）。</summary>
    public static ModelDelta? Apply(string data, ChatStreamState state)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        // 根守卫：TryGetProperty 在非对象上直接抛。
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            state.Id ??= id.GetString();
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            state.Model ??= model.GetString();

        // usage 只在带 `stream_options.include_usage` 时才出现，通常落在最后一帧。
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            state.Usage = usage.GetRawText();

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || choices[0].ValueKind != JsonValueKind.Object) return null;

        var first = choices[0];

        if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            state.FinishReason = fr.GetString();

        if (!first.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            return null;

        // 思考：DeepSeek 系的 `reasoning_content`（与整份响应同一个字段名）。
        if (delta.TryGetProperty("reasoning_content", out var rc)
            && rc.ValueKind == JsonValueKind.String
            && rc.GetString() is { Length: > 0 } thinking)
        {
            state.Reasoning.Append(thinking);
            return new ModelDelta(0, "reasoning", thinking);
        }

        if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            ApplyToolCalls(calls, state);

        if (delta.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String
            && content.GetString() is { Length: > 0 } text)
        {
            state.Text.Append(text);
            return new ModelDelta(state.Reasoning.Length > 0 ? 1 : 0, "text", text);
        }

        return null;
    }

    /// <summary>流结束时产出的、与非流式响应同形的 JSON。</summary>
    public static string Finish(ChatStreamState state)
    {
        var message = new JsonObject();

        // ⚠️ 空串也写：`OpenAiChatResponse` 的判据是 Length > 0，写不写都由它决定，
        // 而「写了空串」与「没有这个字段」在回传时含义不同（见那边的注释）。
        message["content"] = state.Text.ToString();
        if (state.Reasoning.Length > 0) message["reasoning_content"] = state.Reasoning.ToString();

        if (state.ToolCalls.Count > 0)
        {
            var array = new JsonArray();
            foreach (var call in state.ToolCalls.OrderBy(c => c.Key).Select(c => c.Value))
            {
                // 缺 id / name 的条目**照样写出去**：跳过它们的判据在 `OpenAiChatResponse` 里，
                // 这里再判一次就是第二个出处。
                array.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.ToString(),
                    },
                });
            }
            message["tool_calls"] = array;
        }

        var choice = new JsonObject
        {
            ["message"] = message,
            ["finish_reason"] = state.FinishReason,
        };

        var root = new JsonObject
        {
            ["id"] = state.Id,
            ["model"] = state.Model,
            ["choices"] = new JsonArray(choice),
        };
        if (state.Usage is { } usage) root["usage"] = JsonNode.Parse(usage);

        return root.ToJsonString();
    }

    private static void ApplyToolCalls(JsonElement calls, ChatStreamState state)
    {
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object) continue;
            if (!call.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number)
                continue;

            var slot = state.Slot(index.GetInt32());

            if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                slot.Id ??= id.GetString();

            if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                continue;

            if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                slot.Name ??= name.GetString();

            // 分片拼接：这里**不解析**，原文进、原文出。
            if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                slot.Arguments.Append(args.GetString());
        }
    }
}

/// <summary>一次 Chat 流式响应的累积状态。<b>不是线程安全的</b>：只由读流的那一个循环使用。</summary>
public sealed class ChatStreamState
{
    public string? Id { get; set; }
    public string? Model { get; set; }
    public string? FinishReason { get; set; }
    public string? Usage { get; set; }

    public StringBuilder Text { get; } = new();
    public StringBuilder Reasoning { get; } = new();

    /// <summary>按 `index` 归并的调用分片（同一 index = 同一个调用的不同片段）。</summary>
    public Dictionary<int, ToolCallSlot> ToolCalls { get; } = [];

    internal ToolCallSlot Slot(int index)
    {
        if (!ToolCalls.TryGetValue(index, out var slot)) ToolCalls[index] = slot = new ToolCallSlot();
        return slot;
    }

    public sealed class ToolCallSlot
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}

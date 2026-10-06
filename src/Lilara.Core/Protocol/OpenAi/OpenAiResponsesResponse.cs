using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

// OpenAI Responses 响应 → canonical。纯函数：output 是异构数组，只取 type == "message" 的项，其余（reasoning 等）跳过。
public static class OpenAiResponsesResponse
{
    public static ModelReply Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 根守卫必须最先：TryGetProperty 在非对象根上直接抛，少了它 [] / null / 5 / "hi" 全是崩溃。
        if (root.ValueKind != JsonValueKind.Object)
            return new ModelReply { Blocks = [], StopReason = "unknown", Refused = false };

        var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? "unknown"
            : "unknown";

        // ⚠️ **按遇到次序收集，文本与调用混在同一个列表里**：`output` 是一个有序数组，
        // 而「先说了句话、再调工具」与「先调、再解释」在历史上是不同的东西。
        // 分成两个列表再拼接会让两种次序变得一样（那是**改写了模型说过的话的顺序**）。
        var blocks = new List<Block>();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) continue;

                switch (t.GetString())
                {
                    case "message":
                        blocks.AddRange(TextsOf(item));
                        break;

                    case "function_call":
                        if (FunctionCallOf(item) is { } call) blocks.Add(call);
                        break;

                    // 其余（reasoning 等）跳过——与改动之前一致。
                }
            }
        }

        // 拒答信号不在 status 里（incomplete 是「没生成完」不是拒答）：没有可观测信号，故恒 false。
        // 原版另有 incomplete_details.reason，实测正常与截断两种响应都不含它，截断仍是 "completed"。
        // status 原样透传。
        return new ModelReply { Blocks = blocks, StopReason = status, Refused = false };
    }

    private static List<Block> TextsOf(JsonElement message)
    {
        var texts = new List<Block>();
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return texts;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "output_text"
                && block.TryGetProperty("text", out var txt)
                && txt.ValueKind == JsonValueKind.String
                && txt.GetString() is { Length: > 0 } str)
                texts.Add(new Block.TextBlock(str));
        }

        return texts;
    }

    /// <summary>
    /// `type:"function_call"` 项 → <see cref="Block.ToolUseBlock"/>。
    ///
    /// <para>⚠️ <b>`arguments` 是<b>字符串</b></b>，正好对上我们「参数是逐字 JSON 原文」
    /// 那条纪律 ⇒ **原样取，不解析**（`01` §9 铁律）。</para>
    ///
    /// <para>⚠️ <b>缺 `call_id` / `name` 的项返回 <c>null</c>（跳过）</b>：没有 id 就没法配对，
    /// 落进记录是一条悬空的 `tool_use` ⇒ 下一步上游 400（`01` §3.6）。
    /// 而 `arguments` 缺失给 <c>{}</c>——空对象是合法 JSON，<c>null</c> 会让落盘那道闸抛。</para>
    /// </summary>
    private static Block.ToolUseBlock? FunctionCallOf(JsonElement item)
    {
        if (!item.TryGetProperty("call_id", out var id) || id.ValueKind != JsonValueKind.String) return null;
        if (!item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;

        var args = item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()!
            : "{}";

        return new Block.ToolUseBlock(id.GetString()!, name.GetString()!, args);
    }
}

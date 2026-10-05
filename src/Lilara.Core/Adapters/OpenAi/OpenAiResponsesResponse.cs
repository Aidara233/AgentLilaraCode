using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Adapters.OpenAi;

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

        var texts = new List<string>();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var t) || t.GetString() != "message") continue;
                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "output_text"
                        && block.TryGetProperty("text", out var txt)
                        && txt.ValueKind == JsonValueKind.String
                        && txt.GetString() is { Length: > 0 } str)
                        texts.Add(str);
                }
            }
        }

        // 拒答信号不在 status 里（incomplete 是「没生成完」不是拒答）：没有可观测信号，故恒 false。
        // 原版另有 incomplete_details.reason，实测正常与截断两种响应都不含它，截断仍是 "completed"。
        // status 原样透传。
        var blocks = texts.Count > 0
            ? texts.Select(t => (Block)new Block.TextBlock(t)).ToList()
            : [];

        return new ModelReply { Blocks = blocks, StopReason = status, Refused = false };
    }
}

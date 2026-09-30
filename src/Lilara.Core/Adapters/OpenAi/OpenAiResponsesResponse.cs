using System.Text.Json;
using Lilara.Core.Canonical;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// OpenAI Responses 响应 → canonical。⚠️ 纯函数。
/// ⚠️ `output` 是异构数组——只取 `type == "message"` 的项；其余（reasoning 等）跳过。
/// </summary>
public static class OpenAiResponsesResponse
{
    public static ModelReply Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // ⚠️ 根守卫**必须最先**：TryGetProperty 在非对象根上直接抛（实测，
        // 与 OpenAiChatResponse 同一条事实）。少了它，`[]` / `null` / `5` / `"hi"` 全是崩溃。
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
                if (!item.TryGetProperty("type", out var t) || t.GetString() != "message") continue;
                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "output_text"
                        && block.TryGetProperty("text", out var txt)
                        && txt.ValueKind == JsonValueKind.String
                        && txt.GetString() is { Length: > 0 } str)
                        texts.Add(str);
                }
            }
        }

        // ⚠️ Responses 的拒答信号不在 status 里（`incomplete` 是「没生成完」不是拒答）。
        // 目前没有可靠的拒答指示，故恒 false——有的话如实透传即可。
        //
        // ⚠️ 原版 Responses API 另有 `incomplete_details.reason`（`max_output_tokens` 等），
        // **本实现不读它**——控制器派发时的 Ruling：网关补全端点当时全在报 upstream_error，
        // 该字段无法实测；没有可观测信号就不发明规则（沿用 Task 6 的纪律）。
        // `status` 原样透传，诊断能力不受损。将来实测到再补。
        var blocks = texts.Count > 0
            ? texts.Select(t => (Block)new Block.TextBlock(t)).ToList()
            : [];

        return new ModelReply { Blocks = blocks, StopReason = status, Refused = false };
    }
}

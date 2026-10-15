using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

internal static class UsageJson
{
    public static TokenUsage? Parse(JsonElement root, string propertyName = "usage")
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return null;

        long? Read(params string[] names)
        {
            foreach (var name in names)
            {
                if (usage.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt64(out var number)) return number;
            }
            return null;
        }

        var input = Read("input_tokens", "prompt_tokens");
        var output = Read("output_tokens", "completion_tokens");
        var total = Read("total_tokens");
        var cacheRead = Read("cached_tokens", "cache_read_input_tokens");
        var cacheWrite = Read("cache_creation_input_tokens");

        return new TokenUsage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            ReasoningTokens = ReasoningOf(usage),
            RawJson = usage.GetRawText(),
        };
    }

    private static long? ReasoningOf(JsonElement usage)
    {
        if (!usage.TryGetProperty("output_tokens_details", out var details)
            || details.ValueKind != JsonValueKind.Object) return null;
        return details.TryGetProperty("reasoning_tokens", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) ? number : null;
    }
}

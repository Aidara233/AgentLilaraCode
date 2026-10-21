using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

/// <summary>
/// OpenAI 系（chat / responses）的 usage → <see cref="TokenUsage"/>。
///
/// <para>⚠️ 本文件同时钉住 <c>InputTokens</c> 的<b>口径</b>（`08` §3.1）：它一律是
/// <b>未被缓存覆盖的输入</b>，缓存读取是<b>并列明细</b>，不是它的子集。两族协议本来就差在这一处：</para>
/// <list type="bullet">
///   <item>Anthropic Messages：<c>input_tokens</c> 官方即「未命中部分」，缓存读取/写入另计（不经过本文件）；</item>
///   <item>OpenAI 系：<c>prompt_tokens</c> / <c>input_tokens</c> 是<b>全量输入</b>，命中部分已包含在里面
///   ⇒ 必须减去缓存读取，否则头/汇总把两者相加时会把同一批 token 数两遍。</item>
/// </list>
/// <para>⇒ 归一之后，<c>InputTokens + CacheReadTokens</c> 在两族里都是「这次请求的全部输入」，
/// 命中率的分母才能用同一个式子（见 <see cref="Storage.UsageCall"/> 的派生字段）。</para>
/// </summary>
internal static class UsageJson
{
    /// <param name="inputIncludesCache">
    /// 该协议的 <c>prompt_tokens</c> / <c>input_tokens</c> 是否已包含缓存命中的部分。
    /// OpenAI 的两个端点都是 true；Anthropic Messages 的 usage 不走本函数。
    /// </param>
    public static TokenUsage? Parse(JsonElement root, string propertyName = "usage", bool inputIncludesCache = false)
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

        long? ReadNested(string parent, string name)
        {
            if (!usage.TryGetProperty(parent, out var obj) || obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var number)) return null;
            return number;
        }

        // 两个 details 父对象（Chat 的 `prompt_tokens_details`、Responses 的 `input_tokens_details`）
        // 与平级旧名一起放在**同一个**表达式里：拆成「先父 A 再父 B 再平级」的短路链时，
        // 只要某个祖先返回非 null，平级那个名字就再也读不到。今天两族的父对象互斥，
        // 短路看似无害——但那是「恰好」而不是「构造上成立」，而「恰好」会在下一次加字段时静默失效。
        long? ReadCache(params (string? Parent, string Name)[] slots)
        {
            foreach (var (parent, name) in slots)
            {
                if (parent is null)
                {
                    if (Read(name) is { } flat) return flat;
                }
                else if (ReadNested(parent, name) is { } nested) return nested;
            }
            return null;
        }

        // ⚠️ 顺序就是优先级，且**正式字段在前**：
        //   · OpenAI Responses 用 `input_tokens_details.cached_tokens`；
        //   · OpenAI Chat 用 `prompt_tokens_details.cached_tokens`；
        //   · DeepSeek 在本机网关（2026-10-18 实测账本）里另给一个平级的 `prompt_cache_hit_tokens`；
        //   · 兼容网关会照 Anthropic 的名字给 `cache_read_input_tokens`。
        // 只认 Anthropic 的名字会让每一笔调用都是「缓存读取 0」——那不是「没命中」，是「没读到」。
        var cacheRead = ReadCache(
            ("input_tokens_details", "cached_tokens"),
            ("prompt_tokens_details", "cached_tokens"),
            (null, "prompt_cache_hit_tokens"),
            (null, "cache_read_input_tokens"),
            (null, "cached_tokens"));

        var input = Read("input_tokens", "prompt_tokens");
        var output = Read("output_tokens", "completion_tokens");
        var total = Read("total_tokens");

        // total 各家定义不一（有的含缓存、有的不含），而它一旦不一致就会让归一后的输入出现
        // 负值或重复 ⇒ **以 input/output 为准**，total 只用来补齐缺失的 input。
        // 两者都没有时 total 是 null：未报告不是 0。
        if (total is null && input is not null && output is not null) total = input + output;
        else if (input is null && output is not null && total is not null) input = total - output;

        if (inputIncludesCache && input is not null && cacheRead is not null)
            // 上游若报出 cached > 全量输入，数据自相矛盾；截到 0 让账本至少可加总，
            // 原始形状仍在 RawJson 里，可回去核对。
            input = Math.Max(0, input.Value - cacheRead.Value);

        return new TokenUsage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = Read("cache_creation_input_tokens"),
            ReasoningTokens = ReasoningOf(usage),
            RawJson = usage.GetRawText(),
        };
    }

    /// <summary>
    /// 旧形状（**只在读盘时**用）：OpenAI 的 <c>prompt_tokens</c> / <c>input_tokens</c> 是**全量输入**，
    /// 不做减法会与「缓存读取」相加时重复计算。
    ///
    /// <para>⚠️ 判据是<b>磁盘格式版本</b>，不是协议名：格式 8 之前的 <c>usage.jsonl</c> 行里
    /// OpenAI 系的 <c>inputTokens</c> 是上游原值（含缓存）。只有读盘会走到这里；
    /// 新写入一律由 <see cref="Parse"/> 归一（见 <see cref="TokenUsage"/> 的口径）。
    /// 归一之后，<see cref="TokenUsage.InputTokens"/> + <see cref="TokenUsage.CacheReadTokens"/>
    /// 在两族协议里都等于「这次请求的全部输入」。</para>
    /// </summary>
    public static TokenUsage NormalizeLegacyInput(TokenUsage usage, string protocol)
    {
        if (usage.InputTokens is not { } input || usage.CacheReadTokens is not { } read) return usage;
        return protocol == Kernel.Protocols.ClaudeMessages
            ? usage
            : usage with { InputTokens = Math.Max(0, input - read) };
    }

    // 推理明细有两个名字：OpenAI Responses 的 `output_tokens_details`（正式字段）与
    // DeepSeek 思考模式在 chat 里给的 `completion_tokens_details`（本机网关实测）。
    private static long? ReasoningOf(JsonElement usage)
    {
        foreach (var (parent, name) in ReasoningNames)
        {
            if (!usage.TryGetProperty(parent, out var details) || details.ValueKind != JsonValueKind.Object) continue;
            if (details.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number)) return number;
        }
        return null;
    }

    private static readonly (string Parent, string Name)[] ReasoningNames =
    [
        ("output_tokens_details", "reasoning_tokens"),
        ("completion_tokens_details", "reasoning_tokens"),
    ];
}

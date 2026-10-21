using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

/// <summary>
/// 核心模型调用账本。与 history.jsonl 分离：账本是观测事实，不进入模型上下文。
/// </summary>
public sealed class UsageStore(string dataRoot)
{
    private static readonly ConcurrentDictionary<string, Lock> Locks = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _path = System.IO.Path.Combine(dataRoot, "usage.jsonl");

    public string Path => _path;

    public void Append(UsageCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        // ⚠️ 版本由这里盖，**不靠调用点记得填**：账本三条写路径（会话 / 探针 / 压缩）只要漏一条，
        // 那一行就会被读方当成「格式 8 之前」的旧口径（OpenAI 系输入会被再减一次缓存）。
        var stamped = call.FormatVersion > 0 ? call : call with { FormatVersion = Kernel.Json.FormatVersion };
        var line = JsonSerializer.Serialize(stamped, Kernel.Json.Options) + "\n";
        lock (Locks.GetOrAdd(System.IO.Path.GetFullPath(_path), _ => new Lock()))
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(line);
        }
    }

    public IReadOnlyList<UsageCall> ReadAll()
    {
        if (!File.Exists(_path)) return [];
        var result = new List<UsageCall>();
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<UsageCall>(line, Kernel.Json.Options) is { } call)
                    result.Add(call);
            }
            catch (JsonException)
            {
                // 账本同样是追加文件；尾部半行不能让全部历史不可读。
            }
        }
        return result;
    }

    public IReadOnlyList<UsageCall> ForSession(string sessionId) =>
        ReadAll().Where(c => c.SessionId == sessionId).OrderBy(c => c.StartedAt).ToList();

    public UsageSummary Summarize(UsageFilter? filter = null)
    {
        Func<UsageCall, bool> predicate = filter is null ? _ => true : filter.Matches;
        var calls = ReadAll().Where(predicate).ToList();
        return UsageSummary.From(calls);
    }
}

public sealed record UsageCall
{
    public required string CallId { get; init; }
    public string? ProjectKey { get; init; }
    public string? SessionId { get; init; }
    public int? Step { get; init; }
    public string Purpose { get; init; } = "session";
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required string Protocol { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required string Status { get; init; }
    public TokenUsage? Usage { get; init; }
    public string? ReportedModel { get; init; }
    public string? ResponseId { get; init; }
    public string? RequestFingerprint { get; init; }

    /// <summary>
    /// 这一行写盘时的格式版本（`Json.FormatVersion`）。账本是只追加的，行与行可以来自不同版本，
    /// 所以版本必须<b>落在行上</b>，读方才知道该按哪套口径解释它。
    ///
    /// <para>⚠️ 缺这个键 = 格式 8 之前写的行（那时账本行上没有版本号）⇒ 默认 0，
    /// 读方按旧口径解释（见 `Protocol/UsageJson.NormalizeLegacyInput`）。</para>
    /// </summary>
    public int FormatVersion { get; init; }

    /// <summary>
    /// 从哪个格式起 <see cref="TokenUsage.InputTokens"/> 是「未命中输入」。
    /// ⚠️ 写死成 8、**不跟着 `Json.FormatVersion` 走**：之后每次 +1 都会让 8 的行
    /// 重新落进「旧行」分支，被再减一次缓存 ⇒ 输入被压成 0。
    /// </summary>
    public const int NormalizedInputSinceFormat = 8;

    /// <summary>
    /// 这次请求的全部输入 token = 未命中输入 + 缓存命中（`08` §3.1 的口径）。
    ///
    /// <para>⚠️ <b>缓存写入不进这个分母</b>：写入的是「这次新落进缓存的前缀」，不是被复用的那部分。
    /// 它仍单独留在 <see cref="TokenUsage.CacheWriteTokens"/> 里。</para>
    ///
    /// <para>⚠️ 分母只由两个<b>输入侧</b>字段构成——与输出无关 ⇒ 上游只报了输入、没报输出时命中率照样算得出。
    /// 缺 <see cref="TokenUsage.CacheReadTokens"/> 或 <see cref="TokenUsage.InputTokens"/>
    /// ⇒ <c>null</c>（未报告，不是 0%）。</para>
    /// </summary>
    public long? PromptTokens => Usage is { InputTokens: { } input, CacheReadTokens: { } read }
        ? input + read
        : null;

    /// <summary>
    /// 缓存命中率（0..1）。分母见 <see cref="PromptTokens"/>，分子就是
    /// <see cref="TokenUsage.CacheReadTokens"/>（归一之后「缓存读取」即「命中」，见 `TokenUsage` 的口径）。
    ///
    /// <para>⚠️ 分母为 0 时给 <c>null</c>：0/0 不是「0% 命中」，是「没有可命中的输入」。
    /// 两者在统计页上长得一样，但混在一起会让空账本显示成「缓存全废」。</para>
    ///
    /// <para>⚠️ **按调用算、不按总量算**：总量口径下，一笔没报缓存的调用会把命中率
    /// 稀释成看起来像「命中变差了」。汇总层只累加有分母的调用，见 <see cref="UsageSummary"/>。</para>
    ///
    /// <para>⚠️ 这个派生属性<b>会随记录一起落盘与上线，但从不被读回</b>（只读属性反序列化时被忽略，
    /// 每次访问都从 <see cref="Usage"/> 现算）⇒ 盘上那一行永远是投影，不是第二份真相。</para>
    /// </summary>
    public double? CacheHitRate => Usage is { InputTokens: { } input, CacheReadTokens: { } read }
        && input + read > 0
            ? (double)read / (input + read)
            : null;
}

public sealed record UsageFilter
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? ProjectKey { get; init; }
    public string? SessionId { get; init; }
    public string? Purpose { get; init; }

    internal bool Matches(UsageCall call) =>
        (From is null || call.StartedAt >= From)
        && (To is null || call.StartedAt < To)
        && (ProviderId is null || call.ProviderId == ProviderId)
        && (ModelId is null || call.ModelId == ModelId)
        && (ProjectKey is null || call.ProjectKey == ProjectKey)
        && (SessionId is null || call.SessionId == SessionId)
        && (Purpose is null || call.Purpose == Purpose);
}

public sealed record UsageSummary
{
    public required int Calls { get; init; }
    public required int CallsWithKnownInput { get; init; }
    public required int CallsWithKnownOutput { get; init; }
    public required int CallsWithUnknownUsage { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public long? ReasoningTokens { get; init; }

    /// <summary>
    /// 参与命中率的调用数（即分母已知的那些；它们之间的差就是「没报缓存」的笔数）。
    /// 有这个数，客户端才能说清「命中率是在多少笔上算出来的」——只给一个百分比时，
    /// 「56 笔里只有 3 笔报了缓存」与「全都有报」看起来一样。
    /// </summary>
    public int CallsWithKnownCache { get; init; }

    /// <summary>被缓存覆盖的输入 token 合计（只累加分母已知的调用）。</summary>
    public long? CacheHitTokens { get; init; }

    /// <summary>这些调用的全部输入 = 未命中输入 + 缓存命中。见 <see cref="UsageCall.PromptTokens"/>。</summary>
    public long? PromptTokens { get; init; }

    /// <summary>
    /// 缓存命中率 = <see cref="CacheHitTokens"/> / <see cref="PromptTokens"/>。
    ///
    /// <para>⚠️ <b>分母只收「分母已知」的调用</b>：一笔没报缓存的调用若被算进分母，
    /// 命中率会被它拉低——那看起来像「缓存变差了」，其实是「这笔没报」。未报告的调用
    /// 单独由 <see cref="CallsWithKnownCache"/> 对 <see cref="Calls"/> 的差体现。</para>
    ///
    /// <para>没有任何一笔分母已知 ⇒ <c>null</c>（未报告，不是 0）。</para>
    /// </summary>
    public double? CacheHitRate { get; init; }

    public IReadOnlyList<UsageBucket> ByProvider { get; init; } = [];
    public IReadOnlyList<UsageBucket> ByModel { get; init; } = [];

    internal static UsageSummary From(IReadOnlyList<UsageCall> calls)
    {
        static long? Sum(IEnumerable<long?> values)
        {
            var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return known.Count == 0 ? null : known.Sum();
        }

        var summary = new UsageSummary
        {
            Calls = calls.Count,
            CallsWithKnownInput = calls.Count(c => c.Usage?.InputTokens is not null),
            CallsWithKnownOutput = calls.Count(c => c.Usage?.OutputTokens is not null),
            CallsWithUnknownUsage = calls.Count(c => c.Usage is null
                || (c.Usage.InputTokens is null && c.Usage.OutputTokens is null)),
            InputTokens = Sum(calls.Select(c => c.Usage?.InputTokens)),
            OutputTokens = Sum(calls.Select(c => c.Usage?.OutputTokens)),
            CacheReadTokens = Sum(calls.Select(c => c.Usage?.CacheReadTokens)),
            CacheWriteTokens = Sum(calls.Select(c => c.Usage?.CacheWriteTokens)),
            ReasoningTokens = Sum(calls.Select(c => c.Usage?.ReasoningTokens)),
            CallsWithKnownCache = calls.Count(c => c.CacheHitRate is not null),
            CacheHitTokens = Sum(calls.Where(c => c.CacheHitRate is not null).Select(c => c.Usage?.CacheReadTokens)),
            PromptTokens = Sum(calls.Where(c => c.CacheHitRate is not null).Select(c => c.PromptTokens)),
        };
        summary = summary with
        {
            CacheHitRate = Rate(summary.CacheHitTokens, summary.PromptTokens),
            ByProvider = calls.GroupBy(c => c.ProviderId, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => UsageBucket.From(g.Key, g.ToList())).ToList(),
            ByModel = calls.GroupBy(c => c.ModelId, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => UsageBucket.From(g.Key, g.ToList())).ToList(),
        };
        return summary;
    }

    /// <summary>0/0 是「没有可命中的输入」⇒ <c>null</c>，不是 0%。</summary>
    internal static double? Rate(long? hit, long? prompt) =>
        hit is { } h && prompt is { } p && p > 0 ? (double)h / p : null;
}

public sealed record UsageBucket
{
    public required string Key { get; init; }
    public required int Calls { get; init; }
    public required int CallsWithUnknownUsage { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public int CallsWithKnownCache { get; init; }
    public long? CacheHitTokens { get; init; }
    public long? PromptTokens { get; init; }
    public double? CacheHitRate { get; init; }

    internal static UsageBucket From(string key, IReadOnlyList<UsageCall> calls)
    {
        static long? Sum(IEnumerable<long?> values)
        {
            var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return known.Count == 0 ? null : known.Sum();
        }

        var cacheHit = Sum(calls.Where(c => c.CacheHitRate is not null).Select(c => c.Usage?.CacheReadTokens));
        var prompt = Sum(calls.Where(c => c.CacheHitRate is not null).Select(c => c.PromptTokens));

        return new UsageBucket
        {
            Key = key,
            Calls = calls.Count,
            CallsWithUnknownUsage = calls.Count(c => c.Usage is null
                || (c.Usage.InputTokens is null && c.Usage.OutputTokens is null)),
            InputTokens = Sum(calls.Select(c => c.Usage?.InputTokens)),
            OutputTokens = Sum(calls.Select(c => c.Usage?.OutputTokens)),
            CacheReadTokens = Sum(calls.Select(c => c.Usage?.CacheReadTokens)),
            CacheWriteTokens = Sum(calls.Select(c => c.Usage?.CacheWriteTokens)),
            ReasoningTokens = Sum(calls.Select(c => c.Usage?.ReasoningTokens)),
            CallsWithKnownCache = calls.Count(c => c.CacheHitRate is not null),
            CacheHitTokens = cacheHit,
            PromptTokens = prompt,
            CacheHitRate = UsageSummary.Rate(cacheHit, prompt),
        };
    }
}

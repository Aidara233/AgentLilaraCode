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
        var line = JsonSerializer.Serialize(call, Kernel.Json.Options) + "\n";
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
        };
        summary = summary with
        {
            ByProvider = calls.GroupBy(c => c.ProviderId, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => UsageBucket.From(g.Key, g.ToList())).ToList(),
            ByModel = calls.GroupBy(c => c.ModelId, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => UsageBucket.From(g.Key, g.ToList())).ToList(),
        };
        return summary;
    }
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

    internal static UsageBucket From(string key, IReadOnlyList<UsageCall> calls)
    {
        static long? Sum(IEnumerable<long?> values)
        {
            var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return known.Count == 0 ? null : known.Sum();
        }

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
        };
    }
}

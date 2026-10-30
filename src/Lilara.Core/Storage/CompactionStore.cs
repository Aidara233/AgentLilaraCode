using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

/// <summary>Append-only compaction checkpoints. The original history is never rewritten.</summary>
public sealed class CompactionStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly SessionStore _sessions;

    public CompactionStore(SessionStore sessions) => _sessions = sessions;

    public IReadOnlyList<CompactionCheckpoint> ReadAll(string projectKey, string sessionId)
    {
        var path = Path.Combine(_sessions.SessionDirectory(projectKey, sessionId), "compaction.jsonl");
        if (!File.Exists(path)) return [];
        var result = new List<CompactionCheckpoint>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<CompactionCheckpoint>(line, Json.Options) is { } checkpoint)
                    result.Add(checkpoint);
            }
            catch (JsonException) { }
        }
        return result;
    }

    public void Append(string projectKey, string sessionId, CompactionCheckpoint checkpoint)
    {
        var path = Path.Combine(_sessions.SessionDirectory(projectKey, sessionId), "compaction.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.WriteLine(JsonSerializer.Serialize(checkpoint, Json.Options));
    }

    public CompactionCheckpoint? LatestValid(
        string projectKey, string sessionId, IReadOnlyList<TimelineEvent> history)
    {
        // ⚠️ **空历史 ⇒ 没有可用的检查点**（2026-10-22）：`ToDictionary` 对空序列给的是空字典，
        // 下面那条 `ContainsKey` 永远假 —— 结果虽然一样，但那是在一条**已知为空**的记录上
        // 做「找边界」，而调用含义已经不同了（压缩后保留区可以暂时为空）。
        if (history.Count == 0) return null;

        // ⚠️ **不能 `ToDictionary(e => e.Id)`**：历史里一旦出现重复的事件 id（旧版读数事件用
        // `floating-{turn}` 拼 id，重试会撞），`ToDictionary` 当场抛「同一键已存在」
        // ⇒ 渲染整条挂掉，用户看到的是「会话加载失败」。✅ 这里是**渲染热路径**，
        // 输入的容忍度必须比写入侧的纪律更宽：坏数据只该削弱判断，不该打死读取。
        // ⇒ 取**首次出现**（与下面 `TakeWhile` 的边界语义一致），重复项不参与索引。
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < history.Count; i++) index.TryAdd(history[i].Id, i);

        foreach (var checkpoint in ReadAll(projectKey, sessionId).OrderByDescending(c => c.CreatedAt))
        {
            if (!index.TryGetValue(checkpoint.CoveredThroughEventId, out var boundary)) continue;
            var through = history.Take(boundary + 1).ToList();
            if (string.Equals(ComputeDigest(through), checkpoint.HistoryDigest, StringComparison.Ordinal))
                return checkpoint;
        }
        return null;
    }

    public static string ComputeDigest(IEnumerable<TimelineEvent> events)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", events.Select(TimelineEvent.StableSerialize)));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

public sealed record CompactionCheckpoint
{
    public required string CheckpointId { get; init; }
    public required string CoveredThroughEventId { get; init; }
    public required string HistoryDigest { get; init; }

    /// <summary>
    /// ⚠️ **2026-10-22 起，新写下的检查点<b>不再</b>填这一格**——摘要只在 `compact` 调用的参数里
    /// （模型自己写的那一份、历史里的原文）。字段<b>没删</b>：本版之前写下的行里有它，
    /// 而那是旧检查点唯一的摘要来源（回查见 `ContextCompaction.SummaryOf`）。<c>null</c> ⇒ 盘上键整个不出现。
    /// </summary>
    public string? Summary { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required string SourceCallId { get; init; }
    public long? SummaryTokens { get; init; }
}

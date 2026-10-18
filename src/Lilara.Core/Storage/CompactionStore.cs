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
        var byId = history.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var checkpoint in ReadAll(projectKey, sessionId).OrderByDescending(c => c.CreatedAt))
        {
            if (!byId.ContainsKey(checkpoint.CoveredThroughEventId)) continue;
            var through = history.TakeWhile(e => e.Id != checkpoint.CoveredThroughEventId).Append(
                byId[checkpoint.CoveredThroughEventId]).ToList();
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
    public required string Summary { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string SourceCallId { get; init; }
    public long? SummaryTokens { get; init; }
}

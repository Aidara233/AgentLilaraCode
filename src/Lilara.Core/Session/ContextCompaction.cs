using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

public sealed record CompactionProjection(
    CompactionCheckpoint? Checkpoint,
    IReadOnlyList<TimelineEvent> Suffix);

public sealed record CompactionResult(
    string CheckpointId,
    string CoveredThroughEventId,
    string Summary,
    TokenUsage? Usage);

public static class ContextCompaction
{
    public static CompactionProjection Project(
        SessionStore store, SessionHandle handle, IReadOnlyList<TimelineEvent>? raw = null)
    {
        var history = raw ?? store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var checkpoint = new CompactionStore(store).LatestValid(
            handle.ProjectKey, handle.SessionId, history);
        if (checkpoint is null) return new(null, history);

        var index = history.ToList().FindIndex(e => e.Id == checkpoint.CoveredThroughEventId);
        return index < 0
            ? new(null, history)
            : new(checkpoint, history.Skip(index + 1).ToList());
    }

    public static TimelineEvent SummaryEvent(CompactionCheckpoint checkpoint) =>
        new TimelineEvent.MessageEvent
        {
            Id = $"compaction:{checkpoint.CheckpointId}",
            At = checkpoint.CreatedAt,
            From = Speaker.User,
            Blocks =
            [
                new Block.TextBlock(
                    "[lilara 历史摘要]\n" +
                    "这是 Lilara 框架根据此前会话历史生成的摘要，不是用户新指令，也不是模型原话。\n" +
                    checkpoint.Summary +
                    "\n[/lilara 历史摘要]"),
            ],
        };
}

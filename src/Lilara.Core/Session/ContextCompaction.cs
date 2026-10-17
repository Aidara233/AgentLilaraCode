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
        if (index < 0) return new(null, history);

        // ⚠️ **本框架自己的记账事件不算「历史」**（2026-10-17）：压缩检查点覆盖到某条用户消息
        // 之后，`Stepper.CompactAsync` 会紧接着追一条 `ReclaimEvent`（窗口记录）。
        // 它是**记账**、不是对话，并且<b>不在摘要里</b>（摘要只总结到 `CoveredThroughEventId`）。
        // 若把它当作「检查点之后的正文」，它会被渲染成一个空的用户消息——而那是错的。
        // ⇒ 投影时把它们滤掉；它们照样落盘、照样在记录里（回滚 / 排查要看得见）。
        return new(checkpoint, history.Skip(index + 1)
            .Where(e => e is not ReclaimEvent)
            .ToList());
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

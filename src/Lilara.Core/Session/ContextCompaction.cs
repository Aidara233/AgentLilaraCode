using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

public sealed record CompactionProjection(
    CompactionCheckpoint? Checkpoint,
    IReadOnlyList<TimelineEvent> Suffix);

public static class ContextCompaction
{
    /// <summary>
    /// **摘要就在调用的参数里，不需要再插一条合成的摘要事件**（2026-10-22）。
    ///
    /// <para>⚠️ <b>判据是「记录里真的还有那条调用吗」，不是「检查点有没有摘要字段」</b>：
    /// 本版之后的检查点不写摘要字段，而当它的 `compact` 调用**落在保留区内**（它是交界处那条），
    /// 模型下一步会带着这段摘要去请求 —— 再插一条 `From = Speaker.User` 的合成消息，
    /// 就是同一段话出现两遍，且其中一遍是**假用户消息**。</para>
    ///
    /// <para>⚠️ <b>找不到 ⇒ 回落</b>：本版之前的检查点、以及「检查点覆盖到了那条调用却不在后缀里」的
    /// 异常形态，都必须照样看得到摘要——那是它代表的全部历史，丢了就是模型什么都不知道。
    /// 这条路径的摘要来自 <see cref="CompactionCheckpoint.Summary"/>（那些行里还带着它）。</para>
    ///
    /// <para>⚠️ <b>渲染<b>不</b>改写那条调用的参数</b>：摘要就在参数里、模型自己看得见，
    /// 而记录是事实——把参数换成一行注记会让「模型当时写了什么」在渲染里消失，
    /// 得不偿失（与回收注记不同的地方在于：这里没有省下任何字节，那份摘要本来就该在）。</para>
    /// </summary>
    public static bool SummarySitsInCall(
        CompactionCheckpoint checkpoint, IReadOnlyList<TimelineEvent> suffix) =>
        suffix.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .Any(call => string.Equals(call.CallId, checkpoint.SourceCallId, StringComparison.Ordinal)
                         && string.Equals(call.Name, Tools.CompactTool.NameOf, StringComparison.Ordinal));

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
        // 之后，`Stepper` 会紧接着追一条 `ReclaimEvent`（窗口记录）。
        // 它是**记账**、不是对话，并且<b>不在摘要里</b>（摘要只总结到 `CoveredThroughEventId`）。
        // 若把它当作「检查点之后的正文」，它会被渲染成一个空的用户消息——而那是错的。
        // ⇒ 投影时把它们滤掉；它们照样落盘、照样在记录里（回滚 / 排查要看得见）。
        return new(checkpoint, history.Skip(index + 1)
            .Where(e => e is not ReclaimEvent)
            .ToList());
    }

    /// <summary>
    /// 一份检查点的**摘要正文**（2026-10-22）。
    ///
    /// <para>⚠️ <b>来源分两代，而判据只有一个：「记录里有没有那条调用」。</b>
    /// 新检查点的摘要在 `compact` 调用的<b>参数</b>里（记录是事实）；旧检查点没有对应的调用形态，
    /// 摘要只在本行那个（已不写的）<see cref="CompactionCheckpoint.Summary"/> 里。</para>
    ///
    /// <para>⚠️ 两处都在时取**记录里**的那份：检查点里的副本一旦与记录分叉，
    /// 渲染出来的就是一段模型自己没写过、却有盘面依据的话。工具名在这里是字面量——
    /// 一致性由 `CompactTool.NameOf` 这一处常量兜着（这里直接用它，不再抄一个字符串）。</para>
    /// </summary>
    /// <param name="recorded">**记录**那一份时间线（未经投影、未经回收）。</param>
    public static string? SummaryOf(CompactionCheckpoint checkpoint, IReadOnlyList<TimelineEvent> recorded)
    {
        foreach (var evt in recorded.OfType<TimelineEvent.MessageEvent>())
        {
            foreach (var call in evt.Blocks.OfType<Block.ToolUseBlock>())
            {
                if (!string.Equals(call.CallId, checkpoint.SourceCallId, StringComparison.Ordinal)) continue;
                if (!string.Equals(call.Name, Tools.CompactTool.NameOf, StringComparison.Ordinal)) continue;

                return Tools.ToolArgs.String(call.Arguments, "summary");
            }
        }

        return checkpoint.Summary;
    }

    /// <summary>把摘要包成一条**合成**的用户消息——只在旧检查点（参数里没有摘要）时用。</summary>
    public static TimelineEvent SummaryEvent(CompactionCheckpoint checkpoint, string summary) =>
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
                    summary +
                    "\n[/lilara 历史摘要]"),
            ],
        };
}

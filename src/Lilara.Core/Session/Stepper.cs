using Lilara.Core.Adapters;
using Lilara.Core.Persistence;
using Lilara.Core.Records;

namespace Lilara.Core.Session;

/// <summary>
/// 推进器：跑一步 = 渲染 → 调用 → 执行 → 落盘（02 §2）。
///
/// ⚠️ 步是持久化的原子单位（01 §4）。这里的次序是刻意的：
///   1. 用户消息**先落盘**——它是事实，模型调用失败也抹不掉
///   2. 模型输出在内存里合并完，**落盘只落一次**
///   3. state.json 最后写——崩在 2 和 3 之间只会让 turn 计数偏小，历史不损坏
/// </summary>
public sealed class Stepper(SessionStore store, IModelAdapter adapter)
{
    private readonly SessionStore _store = store;
    private readonly IModelAdapter _adapter = adapter;

    public async Task<ModelReply> StepAsync(
        SessionHandle handle, string userText, CancellationToken ct)
    {
        // 「now 冻结在步开始」（00 §2.5）——同一步内重复渲染必须逐字节相同
        var at = DateTimeOffset.UtcNow;

        handle.State = handle.State with { Status = "Running" };

        // 1. 用户消息先落盘
        _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            At = at,
            From = Speaker.User,
            Blocks = [new Canonical.Block.TextBlock(userText)],
        });

        // 2. 渲染：读回完整时间线 → canonical（核心永远发全量，00 §6）
        var request = new ModelRequest
        {
            ModelId = handle.Spec.Model.ModelId,
            MaxTokens = handle.Spec.Model.MaxTokens,
            Effort = handle.Spec.Model.Effort,
            SystemBlocks = [], // 04 §1.3：核心自带零内容
            Timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId),
        };

        // 3. 调用（抛异常时直接传播——这一步没完成，Turn 不动）
        var reply = await _adapter.CallAsync(request, ct);

        // 4. 落盘：拒答不写空模型消息（Review Focus #2）
        if (!reply.Refused && reply.Blocks.Count > 0)
        {
            _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
            {
                At = at,
                From = Speaker.Model,
                Blocks = reply.Blocks,
            });
        }

        // ⚠️ Turn 递增**在拒答分支之外**：拒答是成功返回的、完成的一步，
        // 只是没有内容可写。让它和「抛异常」一样不计数，
        // 会让 state.json 里「拒答过一次」与「从没跑过」无法区分。
        handle.State = handle.State with
        {
            Status = "Idle",
            Turn = handle.State.Turn + 1,
        };
        handle.SaveState(_store);

        return reply;
    }
}

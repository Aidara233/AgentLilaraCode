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

    /// <summary>
    /// 构造时定下的适配器。⚠️ 这个只读访问器存在的唯一理由是**可测性**：
    /// `StepperResolver` 的契约就是「解析出来的是不是占位适配器」，
    /// 而适配器是私有字段时那条契约没法断言。它不是给运行时用的。
    /// ⚠️ 表达式别名，不是第二个字段——同一个事实只能有一份存储。
    /// </summary>
    public IModelAdapter Adapter => _adapter;

    /// <summary>
    /// 一步 = 渲染 → 调用 → 落盘（02 §2）。
    ///
    /// <para>⚠️ **它不再追加用户消息**（Task 4）。按 `01` §6.1 的次序，
    /// 「入队(x) → 若 x 是事件，**立即追加进记录**」——那次追加归 `CommandDispatcher`，
    /// 因为**入队与追加是同一个动作的两半**：一旦分开，
    /// 「已经落盘但还没入队」或「已入队但没落盘」这两种中间态就会各自丢消息。
    /// 这里的 <paramref name="blocks"/> 是**取空的整个队列**（可能来自多条消息）。</para>
    ///
    /// <para>⚠️ 次序仍是刻意的（`01` §4）：模型输出在内存合并完只落一次盘；
    /// `state.json` 最后写——崩在中间只让 turn 偏小，历史不损坏。</para>
    /// </summary>
    public async Task<ModelReply> StepAsync(
        SessionHandle handle, IReadOnlyList<Canonical.Block> blocks, CancellationToken ct)
    {
        // 「now 冻结在步开始」（00 §2.5）——同一步内重复渲染必须逐字节相同
        var at = DateTimeOffset.UtcNow;

        // ⚠️ 先记下落盘的那份，失败了原样放回去。
        // 调用方 catch 之后继续用**同一个把手**：不回滚的话，
        // 用户刚看着一步失败，`status` 却报 Running——而磁盘上从来没写过 Running。
        // 「内存是落盘的镜像」这条不变量在这里也必须成立。
        var persisted = handle.State;
        handle.State = handle.State with { Status = "Running" };

        ModelReply reply;
        try
        {
            // 渲染：读回完整时间线 → canonical（核心永远发全量，00 §6）
            var request = new ModelRequest
            {
                ModelId = handle.Spec.Model.ModelId,
                MaxTokens = handle.Spec.Model.MaxTokens,
                // ⚠️ 用**适配器实际会说的**协议，不是 spec 里那份快照。
                // 适配器是按**当前** provider 定义选的（`StepperResolver`），而 spec 的协议是
                // 建会话时的快照——`provider set --protocol` 一改，两者就分叉。
                // 分叉的后果不是报错，是 `ReasoningPolicy` 按快照判断「能不能回放」，
                // 于是**在唯一能承载推理的那条链路上把推理静默丢掉**（复审实测的路径：
                // 会话建于 openai-chat，之后把 provider 改成 claude-messages）。
                // ⇒ 单一真相来源放在适配器上（`IModelAdapter.Protocol`）。
                Protocol = _adapter.Protocol,
                Effort = handle.Spec.Model.Effort,
                SystemBlocks = [], // 04 §1.3：核心自带零内容
                Timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId),
            };

            // 调用（抛异常时直接传播——这一步没完成，Turn 不动）
            reply = await _adapter.CallAsync(request, ct);
        }
        catch
        {
            // 回到落盘的那份。⚠️ 用捕获的引用回放，不去读磁盘：
            // 读盘本身可能失败，那会把原始的失败原因盖掉。
            handle.State = persisted;
            throw;
        }

        // 落盘：拒答不写空模型消息（Review Focus #2）
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

    /// <summary>
    /// 一步：**追加 + 入队 + 推进**——`01` §6.1 那条复合动作的完整形态。
    ///
    /// <para>⚠️ 它保留下来是为了**让既有测试的语义不被稀释**：
    /// 那些测试断言的是「用户消息会落盘」，而 Task 4 把「追加」挪去了
    /// <see cref="Hosting.CommandDispatcher"/>。删掉这个入口就等于删掉那些断言；
    /// 而它们守的是一条**真的不变量**（用户消息是事实，模型调用失败也抹不掉）。
    /// ⇒ 生产者路径（dispatcher）走「追加 + 入队 + 取空队列」，
    /// 这个壳子给「我就想跑一步」的调用方用。</para>
    /// </summary>
    public Task<ModelReply> StepAsync(SessionHandle handle, string userText, CancellationToken ct)
    {
        var blocks = new List<Canonical.Block> { new Canonical.Block.TextBlock(userText) };

        // ⚠️ 与 dispatcher 同一条次序：**先追加再入队**，然后取空。
        _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UtcNow,
            From = Speaker.User,
            Blocks = blocks,
        });
        handle.Enqueue(blocks);

        return StepAsync(handle, handle.DrainQueue(), ct);
    }
}

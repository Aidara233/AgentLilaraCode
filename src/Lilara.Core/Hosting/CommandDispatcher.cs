using Lilara.Core.Adapters;
using Lilara.Core.Control;
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Records;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Hosting;

/// <summary>
/// 命令的**唯一执行点**（04 §1.1）。
///
/// ⚠️ 它存在的理由是**结构性**的：控制台不只是「另一个前端」，它与头
/// **走同一条命令通道、同一个命令集、同一个回执**（04 §1.1）。
/// 今天 Repl 直连 handler，头要接进来就得复制一份语义——而**复制语义正是
/// 本项目付过代价的那类缺陷**（多处副本只改一处）。
///
/// <para>⚠️ **它不打印任何东西。** 回执与结果都**返回**给调用方，由前端决定怎么显示。
/// 这正是头能复用它的前提——同样的回执，控制台印一行字，头渲染成一块 UI。</para>
///
/// <para>⚠️ **分工**：文本形态的守卫（-- 开头 / 末尾缺值 / 未知标志）住在**控制台的解析器**里——
/// 那是控制台专属语法，搬进来会把它泄漏到线上协议。而**语义**校验（未知协议 / 未知 auth）
/// 在这里，否则线上 provider.upsert 能写进一个控制台写不进的坏值。</para>
/// </summary>
public sealed class CommandDispatcher(
    CoreHost host, EventBus bus, Func<SessionHandle, Stepper> resolveStepper)
{
    private readonly CoreHost _host = host;
    private readonly EventBus _bus = bus;
    private readonly Func<SessionHandle, Stepper> _resolveStepper = resolveStepper;

    /// <summary>
    /// 分派一条命令。
    /// <para><paramref name="awaitCompletion"/>：
    /// **控制台传 true**——它没有视图（03 §8.1），唯一的输出就是这次的返回值，
    /// 所以「命令投递即返回」在本地**退化成同步拿回执**（04 §1.1 明确允许）。
    /// **控制面传 false**——立刻回 ack，模型回话经**事件**流出。</para>
    /// </summary>
    public async Task<DispatchResult> DispatchAsync(
        CoreCommand command, string commandId, bool awaitCompletion, CancellationToken ct)
    {
        switch (command)
        {
            case CoreCommand.ProviderUpsert p:
                return HandleProviderUpsert(p, commandId);

            case CoreCommand.SessionCreate:
                return HandleSessionCreate(commandId);

            case CoreCommand.SessionActivate a:
                return HandleSessionActivate(a, commandId);

            case CoreCommand.SessionSend s:
                return await HandleSessionSendAsync(s, commandId, awaitCompletion, ct);

            case CoreCommand.SessionStart st:
                return HandleSessionStart(st, commandId);

            case CoreCommand.SessionInterrupt i:
                return HandleSessionInterrupt(i, commandId);

            default:
                return Reject(commandId, $"认不出的命令：{command.GetType().Name}");
        }
    }

    private DispatchResult HandleProviderUpsert(CoreCommand.ProviderUpsert p, string id)
    {
        // ⚠️ 语义校验在这里，不在控制台——否则线上能写进坏值。
        if (p.Protocol is not null && !Protocols.All.Contains(p.Protocol))
            return Reject(id, $"未知协议 '{p.Protocol}'。支持：{string.Join(" / ", Protocols.All)}");

        // ⚠️ 大小写是**静默改行为**：AuthSchemes.Bearer 的字面量是小写 "bearer"，
        // 而适配器判的是序号相等。手写 Bearer（大写）会被悄悄当成 x-api-key 发出去。
        if (!string.IsNullOrEmpty(p.Auth)
            && p.Auth != AuthSchemes.ApiKey && p.Auth != AuthSchemes.Bearer)
            return Reject(id, $"未知认证方式 '{p.Auth}'。支持：{AuthSchemes.ApiKey} / {AuthSchemes.Bearer}");

        // ⚠️ 先读旧定义，**保留本次没提到的字段**。整文件覆盖会抹掉手写配置
        // （Endpoint / Modality / DefaultModel / Protocol 都是操作者配置）。
        var existing = _host.Providers.Load(p.Id);

        // ⚠️「存得下、发不出」那个旧顾虑，对**从命令进来的值**已由上面两道闸解决。
        // 但**手写进 providers/<id>.json 的未知协议绕过了闸**，而下面的 existing?.Protocol
        // 会让它**原样保留**——那一半由 AdapterFactory 在**发消息时**给人话兜底。
        var effectiveProtocol = p.Protocol ?? existing?.Protocol ?? Protocols.ClaudeMessages;

        _host.Providers.Save(new ProviderDefinition
        {
            Id = p.Id,
            Protocol = effectiveProtocol,
            ApiKey = p.ApiKey,
            DefaultModel = p.Model ?? existing?.DefaultModel ?? "claude-opus-5",
            Modality = existing?.Modality ?? "vision",
            Endpoint = p.Endpoint ?? existing?.Endpoint,
            AuthScheme = string.IsNullOrEmpty(p.Auth) ? existing?.AuthScheme : p.Auth,
        });

        // ⚠️ **失效点必须在这里**，不能挂在控制台的 IsProviderSetCommand 上——
        // 那是个**字符串**判据，线上的 provider.upsert 走不到它 ⇒ 缓存**静默失效**，
        // 用户改了 provider 却还在用旧的（Repl 那段注释警告的同一个 bug，换了个入口）。
        _host.InvalidateSteppers();

        return new DispatchResult(new Ack.Accepted { CommandId = id, Note = effectiveProtocol });
    }

    private DispatchResult HandleSessionCreate(string id)
    {
        var provider = _host.Providers.List().FirstOrDefault();
        if (provider is null)
            return Reject(id, "先配提供商：provider set <id> --key <key>");

        var model = _host.Providers.ToSelection(provider, null, "high", 16000);
        var handle = SessionHandle.Create(_host.Store, _host.WorkingDirectory, model);

        // 登记进激活表——快照端点据此避免读到盘上过期的 Idle（见 CoreHost.TryGetActivated）。
        _host.Register(handle);

        // ⚠️ 分配型命令**必须**回传新身份，否则后续命令无从指涉（03 §2 的唯一例外）。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = handle.SessionId });
    }

    private DispatchResult HandleSessionActivate(CoreCommand.SessionActivate a, string id)
    {
        if (!TryFind(a.SessionId, out _, out var rejection)) return rejection!;

        // ⚠️ 激活**只是载入内存**。它**不许**去写「控制台的当前会话」——
        // 那是控制台自己的概念（Repl.Current）。头点一下 B 就改掉控制台的当前会话，
        // 用户在控制台敲的下一句就会跑到 B 去。两者必须分开。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = a.SessionId });
    }

    private async Task<DispatchResult> HandleSessionSendAsync(
        CoreCommand.SessionSend s, string id, bool awaitCompletion, CancellationToken ct)
    {
        if (!TryFind(s.SessionId, out var handle, out var rejection)) return rejection!;

        // ⚠️ **次序照 `01` §6.1 原文**：
        //   入队(x) → 若 x 是事件，**立即追加进记录** → 安排一次「启动」（去重）
        // 追加与入队必须**紧挨着**：一旦分开，「已落盘未入队」或「已入队未落盘」
        // 这两种中间态就会各自丢消息。
        //
        // ⚠️ **这两步刻意不拿闸**（放在闸外）——这是「合并是队列的副产品」能成立的**前提**。
        // 第一次实测就抓到了：把入队放进闸里，第二条 send 会**卡在闸上**、
        // 根本没机会入队，于是「3 次入队 → 第一次取空队列」永远不成立，
        // 连发 3 条真的调了 **3 次模型**（`call=3`）。
        // 文档要的是「后两次是 no-op」，而 no-op 的前提是**它们先进得来**。
        //
        // ⚠️ 那并发安全吗？——**安全**，靠的是各自那一层的锁：
        //   · 追加：`HistoryLog.Append` 自带**按路径**的锁（防字节交错）
        //   · 入队：`SessionHandle.Enqueue` 自带队列锁
        // ⇒ 短临界区各自负责，不必被「整步」那把大闸罩着。
        var at = DateTimeOffset.UtcNow;

        _host.Store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            At = at,
            From = Speaker.User,
            Blocks = s.Blocks,
        });
        handle.Enqueue(s.Blocks);

        // ⚠️ **事件从 dispatcher 发，不从 `Stepper` 发**——这是「控制台的命令也产生事件」的实现。
        // `03` §8.1 只说控制台**不订阅**，没说它的命令不产生事件。
        // 若事件在 `Stepper` 里发、或者干脆由控制面自己发，那么**控制台敲的那句话
        // 在头上是看不见的**——头的视图会**静默**少一步，而且它没办法发现。
        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.MessageAppended(handle.SessionId, "user", s.Blocks, at));

        // ⚠️ **启动去重**（`01` §6.1「会话 Running？→ no-op」）。
        // 已经有一个推进在飞时，本次只入队、**不再安排一次推进**。
        if (!handle.TryEnterStep())
            return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = s.SessionId });

        var accepted = new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = s.SessionId });

        if (awaitCompletion)
        {
            // 控制台：**同步拿结果**（`04` §1.1 明确允许「本地退化成同步拿回执」）。
            // 它的失败要**抛给调用方**——控制台打印一行人话、REPL 继续活着。
            var reply = await RunStepAsync(handle, ct, throwOnFailure: true);
            return accepted with { Reply = reply };
        }

        // ⚠️ 控制面：**立刻回执，步在后台跑**（`03` §1 判据 2：命令投递即返回、结果走事件）。
        // 这不只是「协议好看」——**没有它，`session.interrupt` 无事可做**：
        // 步要是在命令里同步跑完，那条 interrupt 命令根本轮不到执行
        // （它的"当前步"在它到达之前就已经结束了）。
        _ = Task.Run(() => RunStepAsync(handle, CancellationToken.None, throwOnFailure: false),
            CancellationToken.None);

        return accepted;
    }

    /// <summary>
    /// 跑一步：闸 → 事件 → 推进。**调用方必须先 `TryEnterStep` 拿到那把标志。**
    ///
    /// <para>⚠️ **取消与失败必须分开**（这是本方法的要害）：
    /// 用户按了中断不是失败，**不能报 `error`**——否则每次正常中断都变成一条红色告警，
    /// 头会开始忽略 `error`，而那正是它最该看的东西。
    /// 判据是 <see cref="Cancellation.ShouldWrap"/> 的镜像：看**是不是调用方取消的**。</para>
    /// </summary>
    private async Task<ModelReply?> RunStepAsync(SessionHandle handle, CancellationToken ct, bool throwOnFailure)
    {
        // ⚠️ **CTS 每步一个**，且**登记进宿主**（`interrupt` 靠它找到"当前那个步"）。
        // 一个长命的 CTS 会让 `interrupt` 落在空闲期时把**下一次**的 token 变成已取消 ⇒
        // 适配器判「调用方取消」⇒ 不包装 ⇒ 裸 `OperationCanceledException` 冒到前端。
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _host.SetCts(handle.SessionId, stepCts);

        var gate = _host.GateFor(handle.SessionId);
        try
        {
            await gate.WaitAsync(stepCts.Token);

            // ⚠️ **`step.begin` 是「在跑」的唯一信号源**，不是 `session.state`——
            // 后者是磁盘的镜像，而**磁盘从不存 Running**（`Stepper` 只把它写进内存）。
            // 缺了它，头**连轮询都看不出在跑**。
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.StepBegin(handle.SessionId, handle.State.Turn + 1, DateTimeOffset.UtcNow));

            // 取空队列——**合并是队列的副产品**：第一次推进就把后来那几条一起拿去渲染。
            var blocks = handle.DrainQueue();
            var reply = await _resolveStepper(handle).StepAsync(handle, blocks, stepCts.Token);

            // ⚠️ 模型消息**由这里发**（`Stepper` 不碰事件总线——它是纯推进器）。
            // 判据跟 `Stepper` 落盘时一致：拒答 / 空块不产生模型消息。
            if (!reply.Refused && reply.Blocks.Count > 0)
                _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                    new WireEvent.MessageAppended(handle.SessionId, "model", reply.Blocks, DateTimeOffset.UtcNow));

            PublishStepEnd(handle, reply);
            return reply;
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested)
        {
            // ⚠️ **中断不是失败。** 用户消息已经落盘了（那是事实），模型那半没写成，
            // `Turn` 不递增——这是一次**没完成的步**，不是一个错。
            // ⇒ 发 `state` 回到 Idle，**不发 `error`**。
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.StateChanged(handle.SessionId, "Idle", handle.State.Turn));

            if (throwOnFailure) throw new DispatchException("这一步被中断了。");
            return null;
        }
        catch (Exception ex)
        {
            // 真失败：**走事件，不走回执**（`03` §3 的分界线）。
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.Error(handle.SessionId, ex.Message));

            // ⚠️ 后台路**必须吞掉异常**：`Task.Run` 里逃出去的异常是
            // **未观测异常**，在 .NET 上会**直接终结进程**。一个上游 400
            // 不该让整个核心死掉。
            if (throwOnFailure) throw new DispatchException(ex.Message, ex);
            return null;
        }
        finally
        {
            _host.SetCts(handle.SessionId, null);   // 先摘掉，再让 using 释放
            handle.ExitStep();
            if (gate.CurrentCount == 0) gate.Release();
        }
    }

    /// <summary>
    /// ⚠️ **`step.end` 必须在 `Turn` 自增之后**（`Stepper` 已经加过了），
    /// 且**在拒答分支之外**——拒答也走这一步（`Turn` 照样加一）。
    /// 挂在「模型消息之后」会**漏掉拒答**，于是头看着一次拒答像「什么都没发生」，
    /// 而且它**永远等不到 `step.end`**，会以为那一步卡住了。
    /// </summary>
    private void PublishStepEnd(SessionHandle handle, ModelReply reply)
    {
        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.StepEnd(handle.SessionId, handle.State.Turn, reply.Refused, reply.StopReason));

        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.StateChanged(handle.SessionId, handle.State.Status, handle.State.Turn));
    }

    private DispatchResult HandleSessionStart(CoreCommand.SessionStart s, string id)
    {
        if (!TryFind(s.SessionId, out _, out var rejection)) return rejection!;

        // Task 4 接上队列语义（01 §6.1「启动」）。当前 send 已直接完成一步，故此处是 no-op。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = s.SessionId });
    }

    private DispatchResult HandleSessionInterrupt(CoreCommand.SessionInterrupt i, string id)
    {
        if (!TryFind(i.SessionId, out var handle, out var rejection)) return rejection!;

        // ⚠️ **两个条件都必需**，缺一不可：
        //   ① **该会话确实在跑**（`IsRunning`）——否则这次 interrupt 是在打断一个
        //      **还没开始的**步。那正是「长命 CTS」会造成的灾难：一个常驻的 CTS
        //      被 Cancel 之后，**下一次**步的 token 出生即已取消 ⇒
        //      适配器判「调用方取消」⇒ 不包装 ⇒ 裸 `OperationCanceledException`
        //      冒到前端（REPL 只接 `ModelCallFailedException`）⇒ **打死常驻进程**。
        //   ② **CTS 非空**——它是「当前那个步」的句柄，空说明步刚结束、还没来得及摘。
        // ⇒ 只有「在跑 **且** 有句柄」时才 Cancel。空闲时的 interrupt 是 **no-op**，
        //   但**仍然回 ack**（命令本身合法——它在语义上等于「停下」，而没在跑就是已经停下了）。
        if (handle.IsRunning && _host.CtsFor(i.SessionId) is { } cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 竞态：步刚好在这一瞬间结束并释放了它。**不是错误**——
                // 我们本来要做的就是「让它停」，它已经停了。吞掉，回 ack。
            }
        }

        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = i.SessionId });
    }

    /// <summary>找会话：先看激活表，miss 才扫项目目录回落到磁盘载入。</summary>
    private bool TryFind(string sessionId, out SessionHandle handle, out DispatchResult? rejection)
    {
        handle = null!;
        rejection = null;

        if (_host.TryGetActivated(sessionId, out var activated))
        {
            handle = activated;
            return true;
        }

        // 扫项目目录：找不到就是**命令不合法**（会话 id 写错了）——rejected 的正当用法。
        foreach (var dir in ProjectDirs())
        {
            var projectKey = Path.GetFileName(dir);
            if (_host.Store.TryReadSpec(projectKey, sessionId) is null) continue;

            handle = _host.Activate(projectKey, sessionId);
            return true;
        }

        rejection = Reject(sessionId, $"会话 '{sessionId}' 不存在。");
        return false;
    }

    private IEnumerable<string> ProjectDirs()
    {
        var root = _host.Store.ProjectsRoot;
        return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
    }

    private static DispatchResult Reject(string id, string reason) =>
        new(new Ack.Rejected { CommandId = id, Reason = reason });
}

/// <summary>
/// 命令执行中的**业务**失败（模型 400、上游 503、提供商失联）。
/// ⚠️ 它与 <see cref="Ack.Rejected"/> 是**两回事**：rejected 是「你的命令写错了」，
/// 这个是「命令没错，但这次没成」。走 error 事件，不走回执（03 §3）。
/// </summary>
public sealed class DispatchException(string message, Exception? inner = null)
    : Exception(message, inner);

using Lilara.Core.Protocol;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Hosting;

/// <summary>
/// 命令的唯一执行点。控制台与头走同一条命令通道、同一个命令集、同一个回执。
/// 它不打印任何东西——回执与结果都返回给调用方，由前端决定怎么显示。
/// 文本形态的守卫（`--` 开头 / 末尾缺值 / 未知标志）住在控制台的解析器里；
/// 语义校验（未知协议 / 未知 auth）在这里，否则线上 `provider.upsert` 能写进坏值。
/// </summary>
public sealed class CommandDispatcher(
    CoreHost host, EventBus bus, Func<SessionHandle, Stepper> resolveStepper)
{
    private readonly CoreHost _host = host;
    private readonly EventBus _bus = bus;
    private readonly Func<SessionHandle, Stepper> _resolveStepper = resolveStepper;

    /// <summary>
    /// 分派一条命令。
    /// <paramref name="awaitCompletion"/>：控制台传 true——它没有视图，唯一的输出就是这次的返回值，
    /// 「命令投递即返回」于是在本地退化成同步拿回执；控制面传 false——立刻回 ack，模型回话经事件流出。
    /// </summary>
    public async Task<DispatchResult> DispatchAsync(
        CoreCommand command, string commandId, bool awaitCompletion, CancellationToken ct)
    {
        // 兜底：这里是常驻进程唯一的最后一道闸——handler 逃出的任何异常都会掀翻
        // 同时托管控制台与控制面的那个进程（调用它的 `Program.cs` 那一层没有任何 catch）。
        // 两个 `when` 都不是可选的：取消要原样冒出去（`Stepper` 的中断路依赖它传导）、
        // `DispatchException` 是既有的「业务失败」出口也必须原样冒出去（把它折成 `rejected`
        // 会让「上游 503」变成「你的命令写错了」）⇒ 兜底只接意外异常，不接那两条已定义的路。
        try
        {
            switch (command)
            {
                case CoreCommand.ProviderUpsert p:
                    return HandleProviderUpsert(p, commandId);

                case CoreCommand.ProviderDelete d:
                    return HandleProviderDelete(d, commandId);

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

                case CoreCommand.SessionSetAxis x:
                    return HandleSessionSetAxis(x, commandId);

                case CoreCommand.ClaimAcquire a: return HandleClaimAcquire(a, commandId);
                case CoreCommand.ClaimRelease r: return HandleClaimRelease(r, commandId);

                default:
                    return Reject(commandId, $"认不出的命令：{command.GetType().Name}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DispatchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Reject(commandId, $"命令执行时出错：{ex.Message}");
        }
    }

    /// <summary>
    /// `id` 会变成文件名（`ProviderStore.PathFor(id) => Path.Combine(_root, $"{id}.json")`）
    /// ⇒ 判据是「它是不是一个单纯的文件名」，不是「它像不像 ASCII 标识符」。
    /// `Path.GetFileName` 是平台相关的：Windows 上它把 `a/b` 也当分隔符 ⇒ 拒绝——
    /// 数据根可能被同步到别的平台，这正是要的。
    /// `internal static` 是刻意的：测试与 provider-schema 直接钉它的真值表。
    /// </summary>
    internal static bool IsSafeProviderId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        // 挡路径语法：`Path.GetFileName("../escaped")` = `escaped` ≠ `../escaped` ⇒ 拒绝。
        && id == Path.GetFileName(id)
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        // `.` / `..` 由这一句挡（`Path.GetFileName("..")` 就是 `..`）——不冗余，删了会把它们放回来。
        && id is not "." and not ".."
        // 设备名另算：`NUL` 词法上是合法文件名，上面几条都抓不到它——见其注释。
        && !IsWindowsDeviceName(id);

    /// <summary>
    /// Windows 设备名：真二进制里是「未捕获异常掀翻常驻进程」（`COM1` ⇒ `\\.\COM1` 抛
    /// `FileNotFoundException`；`NUL` ⇒ `\\.\NUL` 抛 `UnauthorizedAccessException`）。
    /// 取「第一个点之前」那一段，不是 `Path.GetFileNameWithoutExtension`——`NUL.txt.txt.json`
    /// 同样是设备名。判据大小写不敏感 + `TrimEnd()`，且是**相等不是前缀**（`NULx` / `COM10` /
    /// `console` / `connect` 都是正常文件）；数字只有 **1–9 与 `¹²³`**（`COM⁴`、全角 `COM１`、
    /// 阿拉伯-印度 `COM١` 都是正常文件——别按「数字」直觉扩，会把判据开得比系统宽）。
    /// `CONIN$` / `CONOUT$` 是伪设备，裸 `CONIN` / `CONOUT` 是正常文件。
    /// Linux 上 `NUL` 是正常文件名，这里照样拒绝（数据根可能被同步到别的平台）。
    /// </summary>
    private static bool IsWindowsDeviceName(string id)
    {
        // 「第一个点之前」+ 去尾随空白：`NUL.` / `NUL.txt.txt.json` 都归到 `NUL`。
        var dot = id.IndexOf('.');
        var stem = (dot < 0 ? id : id[..dot]).TrimEnd();

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            // 带 `$` 的伪设备——裸 `CONIN` / `CONOUT` 是正常文件。
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        // 相等，不是前缀（`COM1x` / `COM10` / `LPT10` 正常写入）；数字只有 1–9 与 `¹²³`。
        if (stem.Length != 4) return false;
        var head = stem[..3];
        if (!head.Equals("COM", StringComparison.OrdinalIgnoreCase)
            && !head.Equals("LPT", StringComparison.OrdinalIgnoreCase))
            return false;
        return stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³';
    }

    private DispatchResult HandleProviderUpsert(CoreCommand.ProviderUpsert p, string id)
    {
        // id 先过闸，且在 Protocol / Auth 之前；判据住在 dispatcher
        // （控制台与头是两个前端，放前端会被另一个绕过）。
        if (!IsSafeProviderId(p.Id))
            return Reject(id, UnsafeProviderIdReason(p.Id));

        // 语义校验在这里，不在控制台——否则线上能写进坏值。
        if (p.Protocol is not null && !Protocols.All.Contains(p.Protocol))
            return Reject(id, $"未知协议 '{p.Protocol}'。支持：{string.Join(" / ", Protocols.All)}");

        // 大小写静默改行为：`AuthSchemes.Bearer` 的字面量是小写 "bearer"，适配器判序号相等——
        // 手写大写 `Bearer` 会被悄悄当成 x-api-key 发出去。
        if (!string.IsNullOrEmpty(p.Auth)
            && p.Auth != AuthSchemes.ApiKey && p.Auth != AuthSchemes.Bearer)
            return Reject(id, $"未知认证方式 '{p.Auth}'。支持：{AuthSchemes.ApiKey} / {AuthSchemes.Bearer}");

        // 先读旧定义，保留本次没提到的字段（Endpoint / Modality / DefaultModel / Protocol 都是操作者配置）。
        var existing = _host.Providers.Load(p.Id);

        // 新 provider 必须给 key，已有 provider 省略即保留——两条方向不对称。
        // 空串与缺失等价（都是「没给」），这条等价只写在 `ProviderDefinition.HasKey` 一处，
        // 别在这里再拼一遍 `IsNullOrEmpty` / `is null`。
        // 这条要在 `Save` 之前：拒绝时盘上不许留下半成品。
        if (!ProviderDefinition.HasKey(p.ApiKey) && existing is null)
            return Reject(id, $"新提供商 '{p.Id}' 必须给 --key。");

        // 手写进 providers/<id>.json 的未知协议绕过上面的闸，existing?.Protocol 会原样保留——
        // 那一半由 AdapterFactory 在发消息时给人话兜底。
        var effectiveProtocol = p.Protocol ?? existing?.Protocol ?? Protocols.ClaudeMessages;

        _host.Providers.Save(new ProviderDefinition
        {
            Id = p.Id,
            Protocol = effectiveProtocol,
            // `ProviderDefinition.ApiKey` 是 `required`——保留它，别去掉（去掉会让别的构造点静默漏填）。
            // 空串与缺失等价 ⇒ 用上面已载入的旧 key；判据走 `ProviderDefinition.HasKey`。
            // 下面这个 `!` 与上面的守卫耦合：改守卫条件时连带复核这一行。
            ApiKey = ProviderDefinition.HasKey(p.ApiKey) ? p.ApiKey! : existing!.ApiKey,
            DefaultModel = p.Model ?? existing?.DefaultModel ?? "claude-opus-5",
            Modality = existing?.Modality ?? "vision",
            Endpoint = p.Endpoint ?? existing?.Endpoint,
            AuthScheme = string.IsNullOrEmpty(p.Auth) ? existing?.AuthScheme : p.Auth,
        });

        // 失效点必须在这里，不能挂在控制台的字符串判据 `IsProviderSetCommand` 上——
        // 线上 `provider.upsert` 走不到它 ⇒ 缓存静默失效。
        _host.InvalidateSteppers();

        return new DispatchResult(new Ack.Accepted { CommandId = id, Note = effectiveProtocol });
    }

    /// <summary>坏 id 的拒绝理由只此一份——`upsert` 与 `delete` 共用同一个闸，两处各写一句会漂。</summary>
    private static string UnsafeProviderIdReason(string id) =>
        $"提供商 id '{id}' 不合法：它会变成 providers/ 下的文件名，"
        + "必须是**非空**、**不含路径语法**（分隔符 / `..` / 非法字符）、"
        + "且**不是 Windows 设备名**（`NUL` / `CON` / `COM1` …）的单纯文件名。";

    /// <summary>
    /// 删掉一个提供商（`provider.delete`）。
    ///
    /// 同一个闸，且必须在任何文件系统调用之前：`ProviderStore.Delete` 走同一个 `PathFor(id)`
    /// ⇒ 坏 id 会让它在 `providers/` 之外动手，而删除不可逆。不存在 ⇒ 拒绝，不是幂等成功。
    /// 删一个正被会话引用的 provider **不阻止**——用户可能就是要删掉坏配置；
    /// 删完 `InvalidateSteppers()` 让后果可见（那个会话下次发消息会走「绑定的提供商已不存在」那条路）。
    ///
    /// 两道防线别混为一谈：挡住 `NUL` 这类的是 `ProviderStore.Delete` 里的 `File.Exists`
    /// （实测它对 `NUL.json` 返回 false 且不抛 ⇒ `File.Delete` 走不到；`File.Delete` 对 `NUL`/`CON`
    /// 抛 `UnauthorizedAccessException`，对 `COM1.json` 不抛）；挡住 `/abs` 绝对路径的是本方法上面那道闸
    /// （`PathFor("/abs")` 解析成 `/abs.json`，`Path.Combine` 的第二段带根会丢掉第一段）。设备名那一臂
    /// 是纵深防御，留着是因为没有它会回一句假话——把被拒的非法 id 说成「不存在」。
    /// </summary>
    private DispatchResult HandleProviderDelete(CoreCommand.ProviderDelete d, string id)
    {
        if (!IsSafeProviderId(d.Id))
            return Reject(id, UnsafeProviderIdReason(d.Id));

        // 判据走 `ProviderStore.Delete` 的返回值（文件层），别改成 `Load(d.Id) is null`——
        // `Load` 把损坏的文件也算「不存在」，坏配置会永远删不掉。
        // 接两种异常：`FileShare.Read` 不含 `FILE_SHARE_DELETE`，被并发读按住时 sharing violation
        // 抛 `IOException`、没带 DELETE 的句柄抛 `UnauthorizedAccessException`——
        // 把「暂时占用」说成「不存在」是假话，所以这里讲出真正的原因（忙）。
        bool deleted;
        try
        {
            deleted = _host.Providers.Delete(d.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reject(id, $"无法删除提供商 '{d.Id}'：文件正被占用（可能有并发读）。稍后再试。");
        }

        if (!deleted)
            return Reject(id, $"没有提供商 '{d.Id}'，无从删除。");

        // 失效点必须在这里，不能挂在控制台的字符串判据上——线上 `provider.delete` 走不到它 ⇒ 缓存静默失效。
        _host.InvalidateSteppers();

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    private DispatchResult HandleSessionCreate(string id)
    {
        var provider = _host.Providers.List().FirstOrDefault();
        if (provider is null)
            return Reject(id, "先配提供商：provider set <id> --key <key>");

        var model = _host.Providers.ToSelection(provider, null, "high", 16000);
        var handle = SessionHandle.Create(_host.Store, _host.WorkingDirectory, model);

        // 登记进激活表——快照端点据此避免读到盘上过期的 Idle。
        _host.Register(handle);

        // `session.created` 必须发：头的「全局会话列表」是派生的（对每个 pk 取 sessions 并集，
        // 核心不存它）。同时发到项目 topic（头订的是它，它还不可能知道新会话的 id）与会话 topic。
        _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
            new WireEvent.SessionCreated(handle.SessionId, handle.ProjectKey));
        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.SessionCreated(handle.SessionId, handle.ProjectKey));

        // 分配型命令必须回传新身份，否则后续命令无从指涉。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = handle.SessionId });
    }

    private DispatchResult HandleSessionActivate(CoreCommand.SessionActivate a, string id)
    {
        if (!TryFind(a.SessionId, out _, out var notFound)) return Reject(id, notFound!);

        // 激活只是载入内存，不许写「控制台的当前会话」（那是 `Repl.Current` 自己的概念）——
        // 否则头点一下 B 就改掉控制台的当前会话，用户在控制台敲的下一句会跑到 B 去。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = a.SessionId });
    }

    private async Task<DispatchResult> HandleSessionSendAsync(
        CoreCommand.SessionSend s, string id, bool awaitCompletion, CancellationToken ct)
    {
        if (!TryFind(s.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        // 追加与入队必须紧挨着——分开会产生「已落盘未入队」/「已入队未落盘」两种中间态，各自丢消息。
        // 这两步刻意不拿闸：合并是队列的副产品，而 no-op 的前提是后来的 send 先进得来。
        // 并发安全靠各自那层的锁：`HistoryLog.Append` 自带按路径的锁，`SessionHandle.Enqueue` 自带队列锁。
        var at = DateTimeOffset.UtcNow;

        _host.Store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            At = at,
            From = Speaker.User,
            Blocks = s.Blocks,
        });
        handle.Enqueue(s.Blocks);

        // 事件从 dispatcher 发，不从 `Stepper` 发——否则控制台敲的那句话在头上看不见
        // （控制台不订阅，但它的命令照样产生事件；缺了它头的视图会静默少一步）。
        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.MessageAppended(handle.SessionId, "user", s.Blocks, at));

        // 启动去重：已有推进在飞时，本次只入队、不再安排一次推进。
        if (!handle.TryEnterStep())
            return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = s.SessionId });

        var accepted = new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = s.SessionId });

        if (awaitCompletion)
        {
            // 控制台：同步拿结果，失败抛给调用方（打印一行人话、REPL 继续活着）。
            var reply = await RunStepAsync(handle, ct, throwOnFailure: true);
            return accepted with { Reply = reply };
        }

        // 控制面：立刻回执、步在后台跑——否则 `session.interrupt` 无事可做
        // （步若在命令里同步跑完，那条 interrupt 在它的「当前步」结束前根本轮不到执行）。
        _ = Task.Run(() => RunStepAsync(handle, CancellationToken.None, throwOnFailure: false),
            CancellationToken.None);

        return accepted;
    }

    /// <summary>
    /// 跑一步：闸 → 事件 → 推进。调用方必须先 `TryEnterStep` 拿到那把标志。
    /// 取消与失败必须分开——用户按了中断不是失败，不能报 `error`
    /// （否则每次正常中断都变成红色告警，头会开始忽略 `error`，而那正是它最该看的东西）。
    /// </summary>
    private async Task<ModelReply?> RunStepAsync(SessionHandle handle, CancellationToken ct, bool throwOnFailure)
    {
        // CTS 每步一个，且登记进宿主（`interrupt` 靠它找到「当前那个步」）——
        // 长命的 CTS 会让空闲期的 interrupt 把下一次的 token 变成已取消。
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _host.SetCts(handle.SessionId, stepCts);

        var gate = _host.GateFor(handle.SessionId);

        // 「我拿到了闸」必须是显式记录的，不能靠 `gate.CurrentCount == 0` 去猜——
        // 那判的是闸的当前状态，不是本方法的持有记录；用状态代理所有权本来就是错的。
        var acquired = false;
        try
        {
            await gate.WaitAsync(stepCts.Token);
            acquired = true;

            // `step.begin` 是「在跑」的唯一信号源，不是 `session.state`——磁盘从不存 Running。
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.StepBegin(handle.SessionId, handle.State.Turn + 1, DateTimeOffset.UtcNow));

            // 取空队列——合并是队列的副产品。
            var blocks = handle.DrainQueue();
            var reply = await _resolveStepper(handle).StepAsync(handle, blocks, stepCts.Token);

            // 模型消息由这里发（`Stepper` 不碰事件总线）；判据与它落盘时一致：拒答 / 空块不产生模型消息。
            if (!reply.Refused && reply.Blocks.Count > 0)
                _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                    new WireEvent.MessageAppended(handle.SessionId, "model", reply.Blocks, DateTimeOffset.UtcNow));

            PublishStepEnd(handle, reply);
            return reply;
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested)
        {
            // 中断不是失败：用户消息已落盘、Turn 不递增 ⇒ 发 `state` 回到 Idle，不发 `error`。
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.StateChanged(handle.SessionId, "Idle", handle.State.Turn));

            if (throwOnFailure) throw new DispatchException("这一步被中断了。");
            return null;
        }
        catch (Exception ex)
        {
            // 真失败走事件，不走回执。
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.Error(handle.SessionId, ex.Message));

            // 后台路必须吞掉异常：自 .NET 4.5 起未观测的任务异常默认被吞、不终结进程
            // （`TaskScheduler.UnobservedTaskException` 只记日志）——它只是到一个没人看的地方去了。
            // 必须显式捕获并发成 `error` 事件，否则头只会看到「步开始了却永远等不到 `step.end`」。
            if (throwOnFailure) throw new DispatchException(ex.Message, ex);
            return null;
        }
        finally
        {
            _host.SetCts(handle.SessionId, null);   // 先摘掉，再让 using 释放
            handle.ExitStep();
            if (acquired) gate.Release();
        }
    }

    /// <summary>
    /// `step.end` 必须在 `Turn` 自增之后（`Stepper` 已经加过了），且在拒答分支之外——
    /// 拒答也走这一步（`Turn` 照样加一）；挂在「模型消息之后」会漏掉拒答，
    /// 头看着一次拒答像「什么都没发生」，而且永远等不到 `step.end`。
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
        if (!TryFind(s.SessionId, out _, out var notFound)) return Reject(id, notFound!);

        // 当前 send 已直接完成一步，故此处是 no-op。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = s.SessionId });
    }

    private DispatchResult HandleSessionInterrupt(CoreCommand.SessionInterrupt i, string id)
    {
        if (!TryFind(i.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        // 两个条件都必需：该会话确实在跑（否则是在打断还没开始的步）、且 CTS 非空
        // （它是「当前那个步」的句柄，空说明步刚结束、还没来得及摘）。
        // 只有「在跑 且 有句柄」时才 Cancel；空闲时的 interrupt 是 no-op，但**仍然回 ack**
        // （命令本身合法，语义上等于「停下」，而没在跑就是已经停下了）。
        if (handle.IsRunning && _host.CtsFor(i.SessionId) is { } cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 竞态：步刚好在这一瞬间结束并释放了它。不是错误——我们要的就是「让它停」，它已经停了。
            }
        }

        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = i.SessionId });
    }

    /// <summary>
    /// 设置一条轴。拒绝理由的顺序是刻意的：会话 → 轴 → 值
    /// （反过来，「会话不存在 + 轴名也写错」会报「轴不认识」，把用户引向错的方向）。
    /// 找会话走 <see cref="TryFind"/>，不是只看激活表——核心重启之后会话在盘上、不在内存里，
    /// 只看激活表会回一句假话「不存在」，而它存在、只是没被激活。
    /// </summary>
    private DispatchResult HandleSessionSetAxis(CoreCommand.SessionSetAxis x, string id)
    {
        if (!TryFind(x.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        if (!Axes.IsKnown(x.Axis))
            return Reject(id, $"没有 '{x.Axis}' 这条轴。可用的：{string.Join("|", Axes.Names)}");

        // 合法值清单从表里拼——手抄一份会在加档时静默过期，而这条错误信息是用户唯一的线索。
        if (!Axes.TryParse(x.Axis, x.Value, out var canonical))
            return Reject(id, $"'{x.Axis}' 不取 '{x.Value}'。可用的：{Axes.LegalValuesText(x.Axis)}");

        // `!` 是 `Load` / `Create` 的契约：它们保证 `State.Axes` 非空，
        // `TryFind` 给的 handle 只可能来自这两条来路（含回落分支的 `CoreHost.Activate` ⇒ `Load`）。

        // 相同值不是迁移 ⇒ 不发事件。
        if (handle.State.Axes!.TryGetValue(x.Axis, out var current) && current == canonical)
            return new DispatchResult(new Ack.Accepted { CommandId = id });

        // 改内存 + 落盘必须走 `UpdateStateAndPersist`（同一个临界区）：`setAxis` 刻意不拿会话闸，
        // 而 `Stepper` 的收尾也做一次「读 State → with → 写回」——两条交错会整份盖掉，
        // 丢 `Turn`，或把状态永久卡在 `Running`。
        // 换的是 `handle.State` 这个字段，不是 `handle` 本身——激活表存的是引用，
        // 换实例会让 dispatcher 与快照端点状态分叉。
        // `persistedStatus: "Idle"`：本命令不拿会话闸，能在整个模型调用里发生，那时内存是 `Running`、磁盘从不存它。
        handle.UpdateStateAndPersist(_host.Store, s =>
        {
            var next = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal) { [x.Axis] = canonical };
            return s with { Axes = next };
        }, persistedStatus: "Idle");

        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.AxisChanged(handle.SessionId, x.Axis, canonical));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    /// <summary>
    /// 找会话走 <see cref="TryFind"/>，不是只看激活表——核心重启之后会话在盘上、不在内存里，
    /// 只看激活表会回一句假话「没有会话」。本文件任何「按 id 取会话」都必须走 `TryFind`。
    /// </summary>
    private DispatchResult HandleClaimAcquire(CoreCommand.ClaimAcquire c, string id)
    {
        if (!TryFind(c.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);
        if (c.Scope.Count == 0) return Reject(id, "没给要占的路径。用法：claim.acquire(sessionId, scope: [...])");

        var store = new ClaimStore(handle.Spec.WorkingDirectory);

        // `scope` 是列表 ⇒ 先整体校验、再整体写入：任一条与他人冲突就整条命令 rejected、
        // 一个都不写——部分写入会让用户看到「占了一半」，而命令语义是「占这片区域」。
        var paths = c.Scope.Select(p => Claims.Normalize(p, handle.Spec.WorkingDirectory)).ToList();

        // 读-改-写必须在 `store.Update` 的同一把锁里——分开的 `All()`/`Save()` 会静默丢更新。
        Claim? clash = null;
        var updated = store.Update(current =>
        {
            var list = current.ToList();

            // 本次新增先攒在 `additions` 里，最后才合并进 `list`——边判边 `Add` 时，
            // 第 2 条冲突会连第 1 条一起被写进去（「一个都不写」只对单元素 scope 成立）。
            var additions = new List<Claim>();
            foreach (var path in paths)
            {
                // 判据看「已有 + 本次已接受的」——同一 scope 里两条重叠路径也要命令内部自洽，否则会自己和自己冲突。
                var against = list.Concat(additions);

                // 冲突 ⇒ rejected 且点名持有者——只回一句「已被占用」，用户不知道去找谁。
                clash = Claims.FindConflict(against, c.SessionId, path);
                if (clash is not null) return list;

                // 幂等：自己已经占着这条（含同一 scope 里重复写两次）就跳过
                if (against.Any(x => x.SessionId == c.SessionId && x.Path == path)) continue;
                additions.Add(new Claim(c.SessionId, handle.ProjectKey, path, DateTimeOffset.UtcNow));
            }

            list.AddRange(additions);
            return list;
        });

        if (clash is not null)
            return Reject(id, $"'{clash.Path}' 已被会话 {clash.SessionId} 占用。");

        // 只为真的新增的（`updated.Added`）发事件——无迁移则无事件；
        // 为 `paths` 里每条都发会让幂等重占也发事件，头据此把没有的变化渲染成有。
        foreach (var claim in updated.Added)
            _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
                new WireEvent.ClaimAcquired(c.SessionId, claim.Path));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    private DispatchResult HandleClaimRelease(CoreCommand.ClaimRelease c, string id)
    {
        if (!TryFind(c.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        var store = new ClaimStore(handle.Spec.WorkingDirectory);
        var paths = c.Scope.Select(p => Claims.Normalize(p, handle.Spec.WorkingDirectory)).ToList();

        // 逃生口：不检查占用者是不是自己——模型失控时，用户要能从**别的**会话里把锁摘掉。
        // 同样在 `Update` 的锁里（读-改-写不可分）。
        var updated = store.Update(current =>
        {
            var list = current.ToList();
            foreach (var path in paths) list.RemoveAll(x => x.Path == path);
            return list;
        });

        // 只为真正被删掉的（`updated.Removed`）发事件——为 scope 里每条都发，
        // 会给根本没被删的路径也发（它本来就不在）。
        foreach (var claim in updated.Removed)
            _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
                new WireEvent.ClaimReleased(c.SessionId, claim.Path));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    /// <summary>
    /// 找会话：先看激活表，miss 才扫项目目录回落到磁盘载入。
    /// miss 时 out 的是理由字符串而非拼好的回执——<see cref="Ack.CommandId"/> 是发起方的 id，
    /// 与会话 id 是两个东西，在这里拼 `Reject` 只能用会话 id 当 `CommandId`（错），
    /// 所以把理由交回调用方，由它用自己的命令 id 去 `Reject`。
    /// </summary>
    private bool TryFind(string sessionId, out SessionHandle handle, out string? notFoundReason)
    {
        handle = null!;
        notFoundReason = null;

        if (_host.TryGetActivated(sessionId, out var activated))
        {
            handle = activated;
            return true;
        }

        // 扫项目目录：找不到就是命令不合法（会话 id 写错了）——rejected 的正当用法。
        foreach (var dir in ProjectDirs())
        {
            var projectKey = Path.GetFileName(dir);
            if (_host.Store.TryReadSpec(projectKey, sessionId) is null) continue;

            handle = _host.Activate(projectKey, sessionId);
            return true;
        }

        notFoundReason = $"会话 '{sessionId}' 不存在。";
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
/// 与 <see cref="Ack.Rejected"/> 是两回事：rejected 是「你的命令写错了」，
/// 这个是「命令没错，但这次没成」。走 error 事件，不走回执。
/// </summary>
public sealed class DispatchException(string message, Exception? inner = null)
    : Exception(message, inner);

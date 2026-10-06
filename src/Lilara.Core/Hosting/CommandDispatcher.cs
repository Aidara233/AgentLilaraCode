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
/// <param name="createAdapter">
/// 按 provider 定义造适配器。**可选，默认就是生产那条路**（<see cref="AdapterFactory.Create"/>）。
/// 存在的理由与 <paramref name="resolveStepper"/> 一样：`provider.test` 要真发一次请求，
/// 不给测试留注入口就只能靠打真网络——那不是单元测试。
/// </param>
public sealed class CommandDispatcher(
    CoreHost host,
    EventBus bus,
    Func<SessionHandle, Stepper> resolveStepper,
    Func<ProviderDefinition, IModelAdapter>? createAdapter = null)
{
    private readonly CoreHost _host = host;
    private readonly EventBus _bus = bus;
    private readonly Func<SessionHandle, Stepper> _resolveStepper = resolveStepper;
    private readonly Func<ProviderDefinition, IModelAdapter> _createAdapter =
        createAdapter ?? AdapterFactory.Create;

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

                case CoreCommand.ProviderTest t:
                    return await HandleProviderTestAsync(t, commandId, ct);

                case CoreCommand.SessionCreate c:
                    return HandleSessionCreate(c, commandId);

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

                case CoreCommand.SessionConfigure c:
                    return HandleSessionConfigure(c, commandId);

                // ---- 销毁面 ----
                case CoreCommand.SessionDeactivate d:
                    return HandleSessionDeactivate(d, commandId);

                case CoreCommand.SessionArchive a:
                    return HandleSessionArchive(a, commandId);

                case CoreCommand.SessionDelete del:
                    return HandleSessionDelete(del, commandId);

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
    /// 「合法 id 长什么样」的人话，**与谓词同处一地**——照 <c>Axes.LabelOf</c> /
    /// <c>EndpointResolver.Describe</c> 的先例（标签住在定义旁边）。
    ///
    /// <para>⚠️ 它曾经**只住在** `/provider-schema` 那条 `MapGet` 里（离谓词很远）。改谓词的人
    /// 看不见它 ⇒ 那句人话会静默变成假话，而头的表单文案与拒绝理由都是照它写的。</para>
    /// </summary>
    internal const string ProviderIdRule =
        "单个文件名：不含路径分隔符，不能是 . 或 ..，也不是 CON/NUL/COM1/CONIN$ 这类 Windows 设备名";

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
            // 加法式清单：没提到就保留旧的那份（与其余字段同一条「省略 = 保留」）。
            Models = p.Models ?? existing?.Models,
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

    /// <summary>
    /// 连通性自检：用该提供商的配置发一次最小请求，把结果翻成一句人话。
    ///
    /// ⚠️ **失败不走 `Rejected`**：按 `03` §3，`rejected` 只用于**命令本身不合法**
    /// （provider 不存在 / id 不合法 ⇒ 那两条才是 Rejected）。「连不上 / 401 / 上游 5xx」是
    /// **业务结果**，走 `Accepted` 的 `Note`——否则一次网络抖动会被说成「你的命令写错了」。
    ///
    /// ⚠️ **超时必须短**：控制台是同步等的（`awaitCompletion: true`），
    /// 用调用方的 token 等一个坏端点会让提示符看起来卡死。10 秒是「连不上」与「模型慢」的分界。
    /// </summary>
    private async Task<DispatchResult> HandleProviderTestAsync(
        CoreCommand.ProviderTest t, string id, CancellationToken ct)
    {
        // 同一个闸，且在任何网络动作之前：坏 id 会变成 providers/ 下的文件名，而我们要拿它读盘拿定义。
        if (!IsSafeProviderId(t.Id))
            return Reject(id, UnsafeProviderIdReason(t.Id));

        // 「损坏的文件也算不存在」在这里是对的：测试一个读不出来的配置，结论就是「配不出来」。
        var provider = _host.Providers.Load(t.Id);
        if (provider is null)
            return Reject(id, $"没有提供商 '{t.Id}'。先 `provider set {t.Id} --key <key>` 配一个。");

        // 适配器是从 provider 定义现造的（不走 stepper 缓存）——自检的意义就是验证**当前配置**，
        // 拿缓存里那份可能过期的适配器等于没测。
        IModelAdapter adapter;
        try
        {
            adapter = _createAdapter(provider);
        }
        catch (Exception ex) when (ex is ModelCallFailedException or ArgumentException)
        {
            return new DispatchResult(new Ack.Accepted { CommandId = id, Note = $"✗ 配不出适配器：{ex.Message}" });
        }

        // 端点必须在请求之前印出来——「生效端点必须说得出来」那条硬约束（EndpointResolver 的契约）。
        var where = adapter.Endpoint is { } ep
            ? $"{ep.Url}（{EndpointResolver.Describe(ep.Source)}）"
            : "（占位适配器，无端点）";

        // 最小请求：一条固定探针 + 小 max_tokens。
        // ⚠️ 时间线不能空——`ClaudeRequestBuilder` 对空时间线直接抛（「时间线为空」）。
        var probe = new ModelRequest
        {
            ModelId = provider.DefaultModel,
            MaxTokens = 16,
            Protocol = adapter.Protocol,
            Effort = Axes.ValuesOf(Axes.Effort)![0],   // 取最低档：自检不该挑贵的跑
            // 自检**不带温度**：探针要验的是「这个供应商通不通」，不是「用户此刻的采样偏好」。
            // 带上它反而会让「自检失败」多一种可疑原因（某个模型不认某个温度）。
            Temperature = null,
            SystemBlocks = [],
            Timeline =
            [
                new TimelineEvent.MessageEvent
                {
                    Id = Ids.New(),
                    At = CoreClock.Now(),
                    From = Speaker.User,
                    Blocks = [new Block.TextBlock("ping")],
                },
            ],
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var reply = await adapter.CallAsync(probe, timeout.Token);
            return new DispatchResult(new Ack.Accepted
            {
                CommandId = id,
                Note = $"✓ 通（{provider.DefaultModel} @ {where}）"
                    + (reply.Refused ? "，但模型拒答了这次探针" : ""),
            });
        }
        catch (Exception ex)
        {
            // 超时与真失败分开说：前者是「端点在，但没在 10 秒内答」，后者是「根本连不上 / 被拒」。
            var why = timeout.IsCancellationRequested && !ct.IsCancellationRequested
                ? "10 秒内没有响应（端点没起，或上游太慢）"
                : ex.Message;
            return new DispatchResult(new Ack.Accepted { CommandId = id, Note = $"✗ 不通（{where}）：{why}" });
        }
    }

    /// <summary>
    /// 建会话。落在哪个项目由 <see cref="CoreCommand.SessionCreate.Cwd"/> 决定
    /// （不给就回落核心的工作目录）。
    ///
    /// <para>⚠️ **没有「按项目键建」那一半**，理由见 `SessionCreate` 的注释：
    /// 键推不回目录，那个形状会在半路失败。</para>
    /// </summary>
    private DispatchResult HandleSessionCreate(CoreCommand.SessionCreate c, string id)
    {
        var provider = _host.Providers.List().FirstOrDefault();
        if (provider is null)
            return Reject(id, "先配提供商：provider set <id> --key <key>");

        var workingDirectory = _host.WorkingDirectory;

        if (!string.IsNullOrWhiteSpace(c.Cwd))
        {
            // ⚠️ **不存在 ⇒ 拒绝，不建目录**：建目录是策略，核心不做策略（`00` §1.3）。
            // 但**全新目录天然可用**——`ProjectKey.FromPath` 是纯函数，不要求它先存在过。
            try
            {
                workingDirectory = Path.GetFullPath(c.Cwd);
            }
            catch (Exception ex)
            {
                return Reject(id, $"cwd '{c.Cwd}' 不是合法路径：{ex.Message}");
            }

            if (!Directory.Exists(workingDirectory))
                return Reject(id, $"目录不存在：{workingDirectory}。核心不替你建目录。");
        }

        var model = _host.Providers.ToSelection(provider, null, "high", 16000);
        var handle = SessionHandle.Create(_host.Store, workingDirectory, model, Trimmed(c.Title));

        // 登记进激活表——快照端点据此避免读到盘上过期的 Idle。
        _host.Register(handle);

        // `session.created` 必须发：头的「全局会话列表」是派生的（对每个 pk 取 sessions 并集，
        // 核心不存它）。同时发到项目 topic（头订的是它，它还不可能知道新会话的 id）与会话 topic。
        // ⚠️ 项目 topic 用的是 **handle 的** pk，不是核心工作目录的—— `--project` 建到别处时
        // 两者不同，而头订的正是新会话那个项目的流。
        _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
            new WireEvent.SessionCreated(handle.SessionId, handle.ProjectKey));
        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.SessionCreated(handle.SessionId, handle.ProjectKey));

        // 分配型命令必须回传新身份，否则后续命令无从指涉。
        // Note 是给**人**看的一句补充（`Ack.Accepted.Note` 的契约：头不该依赖它）——
        // 这里交项目键，因为「建到哪个项目去了」正是本命令新增的那个自由度。
        return new DispatchResult(new Ack.Accepted
        {
            CommandId = id,
            SessionId = handle.SessionId,
            Note = handle.ProjectKey,
        });

        // 空白 → null（`Title` 可空是**语义**：`null` =「没起名」，不是「格式兼容」的需要。
        // 别把 "" 存进去——那会让「没起名」与「起了个空名字」变成两份数据）。
        static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
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
        var at = CoreClock.Now();

        _host.Store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            Id = Ids.New(),
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
                new WireEvent.StepBegin(handle.SessionId, handle.State.Turn + 1, CoreClock.Now()));

            // 取空队列——合并是队列的副产品。
            var blocks = handle.DrainQueue();
            var reply = await _resolveStepper(handle).StepAsync(handle, blocks, stepCts.Token);

            // 模型消息由这里发（`Stepper` 不碰事件总线）；判据与它落盘时一致：拒答 / 空块不产生模型消息。
            if (!reply.Refused && reply.Blocks.Count > 0)
                _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                    new WireEvent.MessageAppended(handle.SessionId, "model", reply.Blocks, CoreClock.Now()));

            PublishStepEnd(handle, reply);
            return reply;
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested)
        {
            // 中断不是失败：用户消息已落盘、Turn 不递增 ⇒ 发 `state` 回到 Idle，不发 `error`。
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.StateChanged(handle.SessionId, SessionStatus.Idle, handle.State.Turn));

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
        // `diskStatus: SessionStatus.Idle`：本命令不拿会话闸，能在整个模型调用里发生，
        // 那时内存是 `Running`、而磁盘从不存它。⚠️ 注意这个参数与 `change` **刻意不同**：
        // 内存留 `Running`（这一步还在跑），磁盘钉 `Idle`。
        handle.UpdateStateAndPersist(_host.Store, s =>
        {
            var next = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal) { [x.Axis] = canonical };
            return s with { Axes = next };
        }, diskStatus: SessionStatus.Idle);

        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.AxisChanged(handle.SessionId, x.Axis, canonical));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    /// <summary>
    /// 改会话的模型选择（换绑供应商 / 换模型）。
    ///
    /// <para>⚠️ **不拿会话闸、`IsRunning` 也不拒绝**——与 <see cref="HandleSessionSetAxis"/> 同一条理由。
    /// 但**后果与 setAxis 不同，必须说清**：进行中的那一步**已经构造完 <c>ModelRequest</c> 了**，
    /// 所以改绑**落在下一步**，不是立刻。「立刻生效」是假的——别让下一个人以为改了就马上换掉在途的调用。</para>
    ///
    /// <para>⚠️ 换供应商后**必须** `InvalidateSteppers()`：stepper 按 **sessionId** 缓存
    ///（`CoreHost.StepperFor`），里面那把适配器是**旧供应商**的（旧 key、旧端点）。
    /// 不失效的话请求会照旧发去老地方——那是「数据发给了别人」级别的事，不是显示问题。
    /// ⚠️ 但**只在 `ProviderId` 真的变了才失效**：`Stepper.StepAsync` 每步从 spec 现读
    /// `ModelId`，所以只换模型不需要失效，白清一次会把别的会话的缓存也连累掉。</para>
    /// </summary>
    private DispatchResult HandleSessionConfigure(CoreCommand.SessionConfigure c, string id)
    {
        if (!TryFind(c.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        // 两个都没给 ⇒ 这不是一次「成功的 no-op」，是一次没说清的命令。拒绝，别静默成功。
        if (string.IsNullOrWhiteSpace(c.ProviderId) && string.IsNullOrWhiteSpace(c.ModelId))
            return Reject(id, "没说要改什么。用法：session configure <会话> [--provider <id>] [--model <id>]");

        // 换绑到一个**不存在**的供应商必须拒绝。⚠️ 这一条是本命令存在的主要理由之一：
        // `provider.delete` 刻意不阻止删除被引用的供应商（理由见 HandleProviderDelete），
        // 于是「会话绑的供应商没了」是设计好的落点——而**这条命令是唯一的补救**。
        // 放行一个指向不存在供应商的绑定，等于把那个坏状态**又写了一遍**。
        ProviderDefinition? target = null;
        if (!string.IsNullOrWhiteSpace(c.ProviderId))
        {
            target = _host.Providers.Load(c.ProviderId);
            if (target is null)
                return Reject(id, $"没有提供商 '{c.ProviderId}'。先 `provider set {c.ProviderId} --key <key>`，或 `provider list` 看看有哪些。");
        }

        var before = handle.Spec.Model;

        // 两个字段都是「null = 保留旧值」——照 `provider.upsert` 的语义。
        // ⚠️ **判断与落盘一起放进 `UpdateSpecAndPersist` 的锁内**（值级 `Func`）。
        // 原来这三行在锁外、拿的是 `handle.Spec.Model` 那一份 —— 两次并发 configure
        // 会各自读到**同一代**旧值、各自算、各自写回，后写的把前写的整份盖掉（丢更新）。
        // 值级形态下第二条从第一条的结果接着算，那个丢更新在结构上不存在了。
        var changed = false;
        var next = handle.UpdateSpecAndPersist(_host.Store, s =>
        {
            var b = s.Model;
            var m = b with
            {
                ProviderId = string.IsNullOrWhiteSpace(c.ProviderId) ? b.ProviderId : c.ProviderId!,
                ModelId = string.IsNullOrWhiteSpace(c.ModelId) ? b.ModelId : c.ModelId!,
                // Protocol 是**供应商定义的快照**，换供应商时必须跟着重取，否则它与 ProviderId 不同代。
                // ⚠️ 它**不是**「现在真的走哪条协议」——`Stepper` 取适配器的 Protocol（provider 定义
                // 可以被 `provider set --protocol` 改掉）。这里只是让快照跟上此刻的定义。
                Protocol = target?.Protocol ?? b.Protocol,
            };
            changed = m != b;
            return s with { Model = m };
            // ⚠️ 没变时**也写了盘**（`UpdateSpecAndPersist` 是无条件的）。这是代价，不是疏漏：
            // 想省掉这次写就得把「比不比得出差别」挪到锁外判，而那正是上面刚关掉的那个丢更新窗口。
            // 内容逐字节相同的 `spec.json` 重写一次没有可观测后果（`AtomicFile` 是整份原子替换）。
        }).Model;

        // 同值不是迁移 ⇒ 不发事件（与 `session.setAxis` 同一条约定）。
        if (!changed)
            return new DispatchResult(new Ack.Accepted { CommandId = id });

        // ⚠️ 失效点在这里，不能挂在控制台的字符串判据上——线上 `session.configure` 走不到它 ⇒ 缓存静默失效。
        // `providerChanged` 与 `changed` 都在锁内定，两个都是「这一代里真的变了没有」。
        if (next.ProviderId != before.ProviderId) _host.InvalidateSteppers();

        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.SpecChanged(handle.SessionId, next));

        return new DispatchResult(new Ack.Accepted { CommandId = id, Note = $"{next.ProviderId} / {next.ModelId}" });
    }

    /// <summary>
    /// 把会话摘出内存。**磁盘零改动**（激活是内存账，`state.json` 从不存它）。
    ///
    /// <para>⚠️ 这条命令**只在会话确实激活着**的时候有意义，所以它的拒绝理由分两种：
    /// 「不存在」与「本来就没激活」——**别把它们并成一句**，用户要能分辨自己敲错了 id
    /// 还是那一刀砍在空气上。</para>
    /// </summary>
    private DispatchResult HandleSessionDeactivate(CoreCommand.SessionDeactivate c, string id)
    {
        if (!_host.TryGetActivated(c.SessionId, out var handle))
            return Reject(id, _host.Store.FindSession(c.SessionId) is null
                ? $"会话 '{c.SessionId}' 不存在。"
                : $"会话 '{c.SessionId}' 本来就没激活。");

        if (IsRunning(handle))
            return Reject(id, RunningRefusal(c.SessionId));

        _host.Deactivate(c.SessionId);
        // ⚠️ 只丢**这一个**会话的 stepper——它下次被激活时 `StepperFor` 会重新解析。
        // 不丢的话缓存里那把还握着旧 provider 的 key/端点（同 `session.configure` 的理由）。
        _host.ForgetStepper(c.SessionId);

        // 会话还在（磁盘上完好、还能再激活）⇒ 两条流都发，与控制台/头各自的订阅面无关。
        PublishDestruction(handle.ProjectKey, handle.SessionId,
            new WireEvent.SessionDeactivated(handle.SessionId, handle.ProjectKey), toSessionTopic: true);

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    /// <summary>
    /// 归档 / 取消归档。**原地标记**——目录不动、记录一条不少、`ListSessions` **照列**它。
    ///
    /// <para>⚠️ **幂等不许当成功**：「想归档时已经归档」与「想取消时本来就没归档」
    /// 都 <c>Rejected</c>。写成 no-op 的话用户永远分不清「存进去了」和「什么都没发生」——
    /// 而这条命令的全部意义就是「它到底存没存」。</para>
    /// </summary>
    private DispatchResult HandleSessionArchive(CoreCommand.SessionArchive c, string id)
    {
        var found = _host.Store.FindSession(c.SessionId);
        if (found is null) return Reject(id, $"会话 '{c.SessionId}' 不存在。");

        var (projectKey, spec) = found.Value;

        // ⚠️ handle 只拿来判「在没在跑」与「要不要走同一把锁」——
        // 没激活时**不**为了改一个字段去 `Activate`（那会产生一份用户没要求的内存副本）。
        var live = _host.TryGetActivated(c.SessionId, out var h) ? h : null;

        if (IsRunning(live)) return Reject(id, RunningRefusal(c.SessionId));

        // `Archived` 三态：null=归档、false=取消。**不要**写成 `c.Archived == true`——
        // 那会把「没给」与「给定 false」混成同一个分支（本记录的注释里有完整理由）。
        var archive = c.Archived != false;
        var wanted = archive ? CoreClock.Now() : (DateTimeOffset?)null;

        if (archive && spec.ArchivedAt is not null)
            return Reject(id, $"会话 '{c.SessionId}' 已经归档了。要取消：session archive {c.SessionId} --unarchive");

        if (!archive && spec.ArchivedAt is null)
            return Reject(id, $"会话 '{c.SessionId}' 本来就没归档。");

        // ⚠️ 两条路，取决于它**当下激活着没有**：
        //  · 激活着 ⇒ 走 handle 的 `UpdateSpecAndPersist`（同一把 `_gate`，与 `configure` 互斥）
        //  · 没激活 ⇒ 直接写盘。**这是安全的**：没有 handle 就没有别的东西在改它
        //    （激活表存的就是那个 handle），而 `_host.Store` 的进程内写者只有这两条路。
        var patched = live is not null
            ? live.UpdateSpecAndPersist(_host.Store, s => s with { ArchivedAt = wanted })
            : Direct(spec with { ArchivedAt = wanted });
        var next = patched.ArchivedAt;

        PublishDestruction(projectKey, c.SessionId,
            new WireEvent.SessionArchived(c.SessionId, projectKey, next), toSessionTopic: true);

        return new DispatchResult(new Ack.Accepted
        {
            CommandId = id,
            Note = next is null ? "已取消归档" : "已归档",
        });

        // 不激活、只落盘的那条路。返回**落盘的那份**（与 `UpdateSpecAndPersist` 的约定一致：
        // 别在调用点再读一遍，那可能已经是下一次改动后的值）。
        SessionSpec Direct(SessionSpec patched)
        {
            _host.Store.WriteSpec(projectKey, patched);
            return patched;
        }
    }

    /// <summary>
    /// **删掉会话**：目录、记录、状态一起。本仓唯一不可逆的动作。
    ///
    /// <para>⚠️ 顺序是**先摘声明、后删目录**，不能反：声明住在会话的**工作目录**里，
    /// 而那个路径只有 `spec.json` 里才有——目录删了就要不回地址了。</para>
    /// </summary>
    private DispatchResult HandleSessionDelete(CoreCommand.SessionDelete c, string id)
    {
        // ⚠️ 「存在」走 `SessionExists`（看目录）而不是 `FindSession`（要读得出 spec）：
        // 损坏的 `spec.json` 会让后者说「不存在」，而那恰恰是最该删得掉的一类
        // （同 `provider.delete` 用文件层返回值而不是 `Load` 的那条教训）。
        var located = _host.Store.FindSession(c.SessionId);
        if (located is null)
        {
            // 目录在、但 spec 读不出来（损坏）⇒ 只删目录，没有工作目录可拿来摘声明。
            foreach (var pk in _host.Store.ListProjects())
            {
                if (!_host.Store.SessionExists(pk, c.SessionId)) continue;

                if (!TryDelete(pk, c.SessionId, out var why)) return Reject(id, why!);
                PublishDestruction(pk, c.SessionId,
                    new WireEvent.SessionDeleted(c.SessionId, pk), toSessionTopic: false);
                return new DispatchResult(new Ack.Accepted
                {
                    CommandId = id,
                    Note = "spec.json 读不出来（已损坏），只删了目录——它占的声明无从查找，可能残留。",
                });
            }

            return Reject(id, $"会话 '{c.SessionId}' 不存在。");
        }

        var (projectKey, spec) = located.Value;

        var live = _host.TryGetActivated(c.SessionId, out var h) ? h : null;
        if (IsRunning(live)) return Reject(id, RunningRefusal(c.SessionId));

        // 声明全在**它自己的工作目录**里（`ClaimStore` 按工作目录分区、`claim.acquire`
        // 用的就是 `handle.Spec.WorkingDirectory`）⇒ 只需要建**一个** store。
        // ⚠️ 别为了「保险」去扫所有项目：那会在别人的目录上做读-改-写。
        var released = new ClaimStore(spec.WorkingDirectory)
            .Update(current => current.Where(x => x.SessionId != c.SessionId))
            .Removed;

        if (!TryDelete(projectKey, c.SessionId, out var reason)) return Reject(id, reason!);

        // 释放了哪几条要**点名**：用户可能正靠那几条锁挡着另一个会话。
        foreach (var claim in released)
            _bus.Publish(EventBus.ProjectTopic(projectKey),
                new WireEvent.ClaimReleased(c.SessionId, claim.Path));

        PublishDestruction(projectKey, c.SessionId,
            new WireEvent.SessionDeleted(c.SessionId, projectKey), toSessionTopic: false);

        return new DispatchResult(new Ack.Accepted
        {
            CommandId = id,
            Note = released.Count == 0
                ? "无声明需要释放"
                : $"已释放声明：{string.Join("、", released.Select(x => x.Path))}",
        });

        bool TryDelete(string pk, string sid, out string? why)
        {
            try
            {
                if (_host.Store.DeleteSession(pk, sid)) { why = null; return true; }
                why = $"会话 '{sid}' 不存在。";
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 「暂时占用」与「删掉了」是两回事，说清楚是前者（同 `provider.delete` 的取舍）。
                why = $"无法删除会话 '{sid}'：目录正被占用（可能有并发读）。稍后再试。";
                return false;
            }
        }
    }

    /// <summary>
    /// 三条销毁命令共用的「在跑就不许动」。**判据是 `Running` 而不是「在不在激活表」**——
    /// `session.create` 走 `Register()` 登记，用激活表判会让**刚建的会话删不掉**。
    ///
    /// <para>为什么不是「原地掐断」：`Stepper` 的收尾会 `UpdateStateAndPersist` 把 `state.json` 写回去，
    /// 目录就在那一刻被重新造出来——留下一个**有 state、没 spec** 的目录，
    /// 而 `ListSessions` **静默跳过**它（下次同 id 建会话时 `WriteSpec` 会翻出这个孤儿 state）。
    /// **半删状态比一句拒绝坏得多。**</para>
    ///
    /// <para>`handle is null` ⇒ **没在跑**：推进必须持有 handle
    /// （`RunStepAsync` 的第一个参数就是它，而它只可能来自 `TryFind` / `Activate`）。
    /// 所以这里不需要再去读盘。</para>
    /// </summary>
    private static bool IsRunning(SessionHandle? handle) =>
        handle is not null && (handle.IsRunning || handle.State.Status == SessionStatus.Running);

    private static string RunningRefusal(string sessionId) =>
        $"会话 '{sessionId}' 正在跑，不能归档 / 去激活 / 删除。先 `session interrupt {sessionId}`。";

    /// <summary>
    /// 发一条销毁面的事件。<paramref name="toSessionTopic"/> 只有
    /// <see cref="WireEvent.SessionDeleted"/> 走 <c>false</c>——会话没了，
    /// 会话流上的订阅者**已经无处可投**（详见那条事件的注释）。
    /// </summary>
    private void PublishDestruction(string projectKey, string sessionId, WireEvent evt, bool toSessionTopic)
    {
        if (toSessionTopic) _bus.Publish(EventBus.SessionTopic(sessionId), evt);
        _bus.Publish(EventBus.ProjectTopic(projectKey), evt);
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
                additions.Add(new Claim(c.SessionId, handle.ProjectKey, path, CoreClock.Now()));
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
        // ⚠️ 扫的这一步住在 `SessionStore.FindSession`（`ControlServer.TryFind` 走同一处）——
        // 两处各写一份会漂，而「按 id 找会话」正是最不该漂的那件事。
        if (_host.Store.FindSession(sessionId) is not { } found)
        {
            notFoundReason = $"会话 '{sessionId}' 不存在。";
            return false;
        }

        handle = _host.Activate(found.ProjectKey, sessionId);
        return true;
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

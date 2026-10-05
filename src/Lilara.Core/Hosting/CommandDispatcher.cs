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

            case CoreCommand.SessionSetAxis x:
                return HandleSessionSetAxis(x, commandId);

            case CoreCommand.ClaimAcquire a: return HandleClaimAcquire(a, commandId);
            case CoreCommand.ClaimRelease r: return HandleClaimRelease(r, commandId);

            default:
                return Reject(commandId, $"认不出的命令：{command.GetType().Name}");
        }
    }

    /// <summary>
    /// ⚠️ **id 会变成文件名**（`ProviderStore.PathFor(id) => Path.Combine(_root, $"{id}.json")`）
    /// ⇒ 它必须**不含路径语法**。判据是「**它是不是一个单纯的文件名**」，
    /// 不是「它像不像 ASCII 标识符」——后者会把 `本地` 这类合法 id 一起挡掉
    /// （实测的教训方向：白名单型判据总会漏掉用户正当的输入）。
    /// ⚠️ 实测坑：`../escaped` 让文件落在**数据根**（`providers/` 之外）——
    /// 控制台曾回「已保存」，而文件根本没进 `providers/`。
    ///
    /// <para>⚠️ **`Path.GetFileName` 是平台相关的**（.NET 的既有性质，不是本项目的选择）：
    /// Windows 上它把 `a/b` 也当分隔符 ⇒ 拒绝；这正是**要的**（数据根可能被同步到别的平台）。
    /// ⇒ 因此补了一条针对 `/` 的用例，且**用 `Path` 自己的词法**、不自己拼字符类——
    /// 同一个仓库里 `Claims.Normalize` 已经吃过「自己拼分隔符」的亏。</para>
    ///
    /// <para>⚠️ **`internal static` 是刻意的**：T4 的 provider-schema 要把它**交出去**
    /// 作为 id 的规则，所以它必须能被单独引用；本项目的测试也直接钉它的真值表。</para>
    /// </summary>
    internal static bool IsSafeProviderId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        // ⚠️ 这一句挡的是**路径语法**：`a/b` / `a\b` / `/abs` / `C:\abs` / `../escaped`
        //    （`Path.GetFileName("../escaped")` = `escaped` ≠ `../escaped` ⇒ 拒绝）。
        && id == Path.GetFileName(id)
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        // ⚠️ **`.` 与 `..` 由这一句挡，不是上一句**——实测 `Path.GetFileName("..")` 就是 `..`、
        //    `Path.GetFileName(".")` 就是 `.` ⇒ 上一句对它们是**放行**。
        //    ⚠️ **它不冗余，别删**：删掉就把 `.` / `..` 放回来了（注释把守卫从代码里删掉的那种缺陷）。
        //    ⚠️ 代价实测：`PathFor("..")` 得到 `...json`（段里没有分隔符）⇒ 今天仍落在 `providers/` 内，
        //    不构成穿越——它是**纵深防御**；但 `PathFor` 一旦不拼 `.json`，它立刻变成穿越。
        && id is not "." and not ".."
        // ⚠️ 设备名另算：`NUL` 词法上是个完全合法的文件名，上面几条一条都抓不到它——见其注释。
        && !IsWindowsDeviceName(id);

    /// <summary>
    /// ⚠️ **Windows 设备名：真二进制里是「未捕获异常掀翻常驻进程」**——实测（真二进制 + 临时数据根）：
    /// <list type="bullet">
    /// <item>`provider set COM1 --key k` ⇒ 未捕获 `FileNotFoundException`（`\\.\COM1`）⇒ **REPL 直接死**，
    ///   后续命令不再执行；盘上无文件。</item>
    /// <item>`provider set NUL --key k` ⇒ 未捕获 `UnauthorizedAccessException`（`\\.\NUL`）⇒ **同样掀翻进程**。</item>
    /// </list>
    /// ⚠️ 别把这条记成「回 ✓ 而盘上无文件」——那是**裸** `File.WriteAllText(<dir>/"NUL.json", …)` 的行为
    /// （实测确实不抛、`GetFiles` 看不见、`File.Exists` 为 false）。`ProviderStore.Save` 走的是
    /// `AtomicFile`（先写 `.tmp` 再 `Move`），**路径不同、结局不同**：`Move` 找不到那个 `.tmp` ⇒ 抛。
    /// **探针 ≠ 被测物，要验证就调那个函数。**
    ///
    /// <para>⚠️ **取「第一个点之前」那一段，不是 `Path.GetFileNameWithoutExtension`。**
    /// 实测 `NUL.txt.txt.json` **同样是设备名**（一样抛、一样掀翻进程），而 `GetFileNameWithoutExtension`
    /// 给出 `NUL.txt.txt` ⇒ 会**放行**它。判据要盖过系统的怪癖，不是刚好合上它。</para>
    ///
    /// <para>⚠️ 判定 = **大小写不敏感**（实测 `nul.json` 一样是设备名）+ `TrimEnd()`（`NUL  .json` 同样是）
    /// + **相等、不是前缀**——实测 `NULx` / `COM1x` / `COM10` / `LPT10` / `console` / `connect`
    /// 都是**正常文件**，前缀型会把这一大片正当 id 一起误杀；保留表只有 **1–9 与 `¹²³`**
    /// （`COM0` / `LPT0` / `COM10` / `LPT10` 实测都能正常写入）。</para>
    ///
    /// <para>⚠️ **`CONIN$` / `CONOUT$` 是伪设备；裸 `CONIN` / `CONOUT` 是正常文件**——
    /// 实测（`ProviderStore.Save`）：前者抛 `IOException` / `UnauthorizedAccessException`，
    /// 后者正常写入。⇒ 只挡带 `$` 的那两个，**别顺手把裸名字也挡了**（那是误杀）。</para>
    ///
    /// <para>⚠️ **`¹` `²` `³`（U+00B9 / B2 / B3）也算数字，但别的「数字」写法不算**——
    /// 实测 `COM¹` 抛异常，而 `COM⁴` `COM⁵` `COM⁹`（U+2074…）、全角 `COM１`（U+FF11）、
    /// 阿拉伯-印度 `COM١`（U+0661）**都是正常文件**。
    /// ⇒ **只加这三个，别扩**。按「数字」的直觉去扩，会把判据开得比系统**宽**——
    /// 而那是另一个方向的错（与「前缀型」同一族：误杀）。</para>
    ///
    /// <para>⚠️ **Linux 上 `NUL` 是正常文件名，这里照样拒绝**——与 `/` 那条同一套理
    /// （数据根可能被同步到别的平台），**这是刻意的**。</para>
    /// </summary>
    private static bool IsWindowsDeviceName(string id)
    {
        // 「第一个点之前」+ 去尾随空白：`NUL.` / `NUL  .json` / `NUL.txt.txt.json` 都归到 `NUL`。
        var dot = id.IndexOf('.');
        var stem = (dot < 0 ? id : id[..dot]).TrimEnd();

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            // ⚠️ 带 `$` 的伪设备：**裸 `CONIN` / `CONOUT` 是正常文件**，别顺手加。
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        // ⚠️ **相等**，不是前缀（实测 `COM1x` 正常写入）；数字只有 **1–9 与 `¹²³`**
        //    （`COM0` / `LPT0` / `COM10` / `LPT10` / `COM⁴` 实测都能正常写入）。
        if (stem.Length != 4) return false;
        var head = stem[..3];
        if (!head.Equals("COM", StringComparison.OrdinalIgnoreCase)
            && !head.Equals("LPT", StringComparison.OrdinalIgnoreCase))
            return false;
        return stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³';
    }

    private DispatchResult HandleProviderUpsert(CoreCommand.ProviderUpsert p, string id)
    {
        // ⚠️ **id 先过闸，且必须在 Protocol / Auth 之前**：它是最底层的那一条——
        // 一个坏 id 会让**后面所有**校验都变得没有意义（文件压根落错地方）。
        // ⚠️ 判据住在 dispatcher，不在控制台——控制台与头是两个前端，
        // 放前端会被另一个绕过（本项目的规范，G16）。
        if (!IsSafeProviderId(p.Id))
            return Reject(id, $"提供商 id '{p.Id}' 不合法：它会变成 providers/ 下的文件名，"
                + "必须是**非空**、**不含路径语法**（分隔符 / `..` / 非法字符）、"
                + "且**不是 Windows 设备名**（`NUL` / `CON` / `COM1` …）的单纯文件名。");

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

        // ⚠️ **`session.created` 必须发**——`05` §6.1 要求头的「全局会话列表」随事件更新，
        // 而那份列表是**派生**的（对每个 pk 取 sessions 的并集，核心不存它）。
        // 第一版**声明并注册了这个事件类型却从没发布**，于是：在控制台敲 `session create`，
        // 已经打开的页面**看不见**这个新会话，而且不会自愈。
        // ⚠️ 它同时发到**项目 topic**（跨会话那一层）与**会话 topic**——
        // 头订的是前者（它还不可能知道这个新会话的 id），而后者让「刚订阅上就活起来」成立。
        _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
            new WireEvent.SessionCreated(handle.SessionId, handle.ProjectKey));
        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.SessionCreated(handle.SessionId, handle.ProjectKey));

        // ⚠️ 分配型命令**必须**回传新身份，否则后续命令无从指涉（03 §2 的唯一例外）。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = handle.SessionId });
    }

    private DispatchResult HandleSessionActivate(CoreCommand.SessionActivate a, string id)
    {
        if (!TryFind(a.SessionId, out _, out var notFound)) return Reject(id, notFound!);

        // ⚠️ 激活**只是载入内存**。它**不许**去写「控制台的当前会话」——
        // 那是控制台自己的概念（Repl.Current）。头点一下 B 就改掉控制台的当前会话，
        // 用户在控制台敲的下一句就会跑到 B 去。两者必须分开。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = a.SessionId });
    }

    private async Task<DispatchResult> HandleSessionSendAsync(
        CoreCommand.SessionSend s, string id, bool awaitCompletion, CancellationToken ct)
    {
        if (!TryFind(s.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

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

        // ⚠️ **「我拿到了闸」必须是显式记录的，不能靠 `gate.CurrentCount == 0` 去猜。**
        // 第一版在 finally 里写 `if (gate.CurrentCount == 0) gate.Release();`——
        // 那判的是**闸的当前状态**，不是**本方法的持有记录**。窄路径：
        //   `WaitAsync` 在**没拿到**闸时抛 OCE（已取消的 token）⇒ 走到 finally
        //   ⇒ `ExitStep()` **先**放行了下一次推进，新步拿到闸（count→0）
        //   ⇒ 本步读到 0（那是**别人的**）⇒ 多还一次 ⇒ 第三个步也能进来。
        // 一次虚假 `Release` 就打破 `02` §3.2「会话内严格串行」这条正确性条款，
        // 后果是两步并发写 `handle.State`（`Turn` 丢更新）与并发 `DrainQueue`。
        // ⚠️ 纪律：**没实际跑出过复现**（窗口只有几条指令宽），这是靠**代码审查**定的——
        // 用状态代理所有权本来就是错的，不需要复现才修。
        var acquired = false;
        try
        {
            await gate.WaitAsync(stepCts.Token);
            acquired = true;

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

            // ⚠️ 后台路**必须吞掉异常**：`Task.Run` 里逃出去的是**未观测异常**。
            // ⚠️ 理由要写准：自 .NET 4.5 起未观测的任务异常**默认被吞**、**不终结进程**
            //   （`TaskScheduler.UnobservedTaskException` 只记日志）。所以**不是**「会打死进程」。
            // 真正的理由是：那条异常**到一个没人看的地方去了**——头只会看到
            //   「步开始了但永远等不到 `step.end`」，而用户拿到的是次静默失败。
            // ⇒ 必须**显式**捕获并**发成 `error` 事件**，让失败**可见**。
            if (throwOnFailure) throw new DispatchException(ex.Message, ex);
            return null;
        }
        finally
        {
            _host.SetCts(handle.SessionId, null);   // 先摘掉，再让 using 释放
            handle.ExitStep();
            // ⚠️ 按**记录**释放，不按状态猜——见上面 `acquired` 的注释。
            if (acquired) gate.Release();
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
        if (!TryFind(s.SessionId, out _, out var notFound)) return Reject(id, notFound!);

        // Task 4 接上队列语义（01 §6.1「启动」）。当前 send 已直接完成一步，故此处是 no-op。
        return new DispatchResult(new Ack.Accepted { CommandId = id, SessionId = s.SessionId });
    }

    private DispatchResult HandleSessionInterrupt(CoreCommand.SessionInterrupt i, string id)
    {
        if (!TryFind(i.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

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

    /// <summary>
    /// 设置一条轴。⚠️ 三个拒绝理由的**顺序**是刻意的：会话 → 轴 → 值。
    /// 反过来的话，「会话不存在 + 轴名也写错」会报「轴不认识」，把用户引向错的方向。
    ///
    /// <para>⚠️ 找会话走 <see cref="TryFind"/>，**不是**只看激活表：核心重启之后会话
    /// **在盘上、不在内存里**，只看激活表会回一句**假话**——「不存在」，
    /// 而它存在，只是没被激活。`TryFind` 的回落分支会 `Activate` 把它载进来。</para>
    /// </summary>
    private DispatchResult HandleSessionSetAxis(CoreCommand.SessionSetAxis x, string id)
    {
        if (!TryFind(x.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        if (!Axes.IsKnown(x.Axis))
            return Reject(id, $"没有 '{x.Axis}' 这条轴。可用的：{string.Join("|", Axes.Names)}");

        // ⚠️ 合法值清单**从表里拼**（G8）——手抄一份会在加档时静默过期，
        // 而这条错误信息是用户唯一的线索。
        if (!Axes.TryParse(x.Axis, x.Value, out var canonical))
            return Reject(id, $"'{x.Axis}' 不取 '{x.Value}'。可用的：{Axes.LegalValuesText(x.Axis)}");

        // ⚠️ `!` 是 **`Load` / `Create` 的契约**（与 `Stepper` 读 effort 那句同源）：
        // 它们保证 `State.Axes` 非空（见 `SessionState.Axes` 的注释）。`TryFind` 给的
        // handle 只可能来自这两条来路——激活表里那份本来就是 `Create` / `Load` 的产物，
        // 回落分支走的是 `CoreHost.Activate` ⇒ `Load`；能拿到 null 的只有手工
        // `new SessionHandle`，那不是运行时路径。

        // ⚠️ 相同值不是迁移 ⇒ 不发事件（`01` §5）。见测试里的理由。
        if (handle.State.Axes!.TryGetValue(x.Axis, out var current) && current == canonical)
            return new DispatchResult(new Ack.Accepted { CommandId = id });

        // ⚠️ **改内存 + 落盘必须走 `UpdateStateAndPersist`（同一个临界区）**，不能写成
        // `handle.State = handle.State with { Axes = next }; handle.SaveAxes(store);`——
        // 那两句之间没有锁，而 `Stepper` 的收尾也做一次「读 `State` → `with` → 写回」
        // （它拿的会话闸**挡不住 `setAxis`**，因为 `setAxis` 刻意不拿闸）。两条交错时，
        // 后写的会把先写的**整份**盖掉：丢 `Turn`，或把状态永久卡在 `Running`（终审 I2）。
        //
        // ⚠️ **换的是 `handle.State` 这个字段，不是 `handle` 本身。** `SessionState` 是 record，
        // `with` 换出一份带新轴的副本；而 `SessionHandle` 是**可变的活对象**，
        // 宿主的激活表里存的是**它**的引用（`CoreHost._activated`）——
        // 写 `handle = handle with { … }` 或 `new SessionHandle` 会让表里那份指向**旧实例**，
        // 于是 dispatcher 与快照端点各读各的，**状态分叉**（`Repl.Current` 的 setter 同源）。
        //
        // ⚠️ `persistedStatus: "Idle"` ＝「钉回磁盘语义」（见 `SessionHandle.SaveAxes` 的注释）：
        // 本命令不拿会话闸，能在**整个模型调用**里发生，那时内存是 `Running`，而磁盘从不存它。
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
    /// ⚠️ **找会话走 `TryFind`，不是只看激活表。** 核心重启之后会话**在盘上、不在内存里**，
    /// 只看激活表会回一句**假话**——「没有会话 '…'」，而它存在，只是没被激活。
    ///
    /// <para>⚠️ **这条纪律的来路**：`HandleSessionSetAxis` 踩过同一个坑，在 `9c3bd0d` 修过
    /// （`TryGetActivated` → `TryFind`）。⇒ **本文件的任何「按 id 取会话」都必须走
    /// <see cref="TryFind"/>**——写新处理器时照抄这一行，别自己拼。</para>
    /// </summary>
    private DispatchResult HandleClaimAcquire(CoreCommand.ClaimAcquire c, string id)
    {
        if (!TryFind(c.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);
        if (c.Scope.Count == 0) return Reject(id, "没给要占的路径。用法：claim.acquire(sessionId, scope: [...])");

        var store = new ClaimStore(handle.Spec.WorkingDirectory);

        // ⚠️ **`scope` 是列表（`01` §12.4）⇒ 先整体校验，再整体写入**：
        // 任一条与他人冲突就**整条命令回 rejected**，**一个都不写**——
        // 部分写入会让用户看到"占了一半"，而命令语义是"占这片区域"。
        var paths = c.Scope.Select(p => Claims.Normalize(p, handle.Spec.WorkingDirectory)).ToList();

        // ⚠️ **读-改-写必须在 `store.Update` 的**同一把锁**里**（见它的注释）：
        // 两个会话同时 acquire 时，分开的 `All()`/`Save()` 会**丢更新**（静默）。
        Claim? clash = null;
        var updated = store.Update(current =>
        {
            var list = current.ToList();

            // ⚠️ **本次新增先攒在 `additions` 里，最后才合并进 `list`。**
            // 初版是「边判边 `list.Add`」，于是多元素 scope 时：
            // 第 1 条 `Add` 进去、第 2 条冲突 ⇒ `return list` 返回的是**已含第 1 条的**列表
            // ⇒ `Update` 判「变了」⇒ **把第 1 条写了进去**。
            // 那时「一个都不写」只对**单元素** scope 成立——而注释当时声称的是无条件成立。
            var additions = new List<Claim>();
            foreach (var path in paths)
            {
                // ⚠️ 判据要看「已有 **+ 本次已接受的**」：同一 scope 里写两条相互重叠的路径
                //（如 `["src", "src/a.cs"]`）也必须在**命令内部**自洽，否则会自己和自己冲突。
                var against = list.Concat(additions);

                // ⚠️ **冲突 ⇒ rejected 且点名持有者**（`01` §12.2）。这句话是「你的工作集会被作废」
                // 在界面上唯一能被看见的地方——只回一句「已被占用」，用户不知道去找谁。
                clash = Claims.FindConflict(against, c.SessionId, path);
                if (clash is not null) return list;      // ⚠️ 返回**原始** `list`（不含任何 additions）

                // 幂等：自己已经占着这条（含同一 scope 里重复写两次）就跳过
                if (against.Any(x => x.SessionId == c.SessionId && x.Path == path)) continue;
                additions.Add(new Claim(c.SessionId, handle.ProjectKey, path, DateTimeOffset.UtcNow));
            }

            list.AddRange(additions);
            return list;
        });

        if (clash is not null)
            return Reject(id, $"'{clash.Path}' 已被会话 {clash.SessionId} 占用。");

        // ⚠️ **只为**真的新增的**发事件**（`01` §5：无迁移则无事件）。
        // 初版是「为 `paths` 里每条都发」——于是**幂等重占也发事件**（复审实测：第二次相同
        // acquire 多出一条事件），头据此把没有的变化渲染成有。
        foreach (var claim in updated.Added)
            _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
                new WireEvent.ClaimAcquired(c.SessionId, claim.Path));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    private DispatchResult HandleClaimRelease(CoreCommand.ClaimRelease c, string id)
    {
        // ⚠️ 同上：`TryFind`，不是 `TryGetActivated`。
        if (!TryFind(c.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        var store = new ClaimStore(handle.Spec.WorkingDirectory);
        var paths = c.Scope.Select(p => Claims.Normalize(p, handle.Spec.WorkingDirectory)).ToList();

        // ⚠️ **逃生口**（`03` §3）：不检查占用者是不是自己——模型失控时，
        // 用户要能从一个**别的**会话里把锁摘掉。
        // ⚠️ 同样在 `Update` 的锁里（读-改-写不可分）。
        var updated = store.Update(current =>
        {
            var list = current.ToList();
            foreach (var path in paths) list.RemoveAll(x => x.Path == path);
            return list;
        });

        // ⚠️ **只为真正被删掉的发事件**（`01` §5）。
        // 初版是「为 scope 里每条都发」——复审实测 `["q1","never-held"]` 发了**2 条**事件，
        // 其中一条路径**根本没被删**（它本来就不在）。那与这段自己引的「无迁移则无事件」矛盾。
        foreach (var claim in updated.Removed)
            _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
                new WireEvent.ClaimReleased(c.SessionId, claim.Path));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    /// <summary>
    /// 找会话：先看激活表，miss 才扫项目目录回落到磁盘载入。
    ///
    /// <para>⚠️ **miss 时 out 的是「理由字符串」，不是拼好的回执**：回执的
    /// <see cref="Ack.CommandId"/> 是**发起这条命令的那一方**给的 id（`Ack.CommandId` 的契约是
    /// 「命令 id 由头生成，核心原样带回——头靠它把『我发过的那条』对上」），而 `sessionId`
    /// 是**另一个**东西。在这里拼 `Reject` 只能用会话 id 当 `CommandId`（错误），
    /// 所以把理由交回调用方，由它用自己的命令 id 去 `Reject`。</para>
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

        // 扫项目目录：找不到就是**命令不合法**（会话 id 写错了）——rejected 的正当用法。
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
/// ⚠️ 它与 <see cref="Ack.Rejected"/> 是**两回事**：rejected 是「你的命令写错了」，
/// 这个是「命令没错，但这次没成」。走 error 事件，不走回执（03 §3）。
/// </summary>
public sealed class DispatchException(string message, Exception? inner = null)
    : Exception(message, inner);

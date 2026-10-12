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

    // 预设的读写点。**从数据根现造**（与 `Reclaim` 那处 `new BlobStore(DataRoot)` 同形）：
    // 它无状态、只包一个路径前缀，做成字段就得给 `CoreHost` 再开一个属性，
    // 而那个属性没有任何别的地方要用。
    private readonly PresetStore _presets = new(host.DataRoot);

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

                // ---- 预设：系统块从哪来（`01` §3.1；`14` 未决项 6）----
                case CoreCommand.PresetSet ps: return HandlePresetSet(ps, commandId);
                case CoreCommand.PresetDelete pd: return HandlePresetDelete(pd, commandId);
                case CoreCommand.PresetList: return HandlePresetList(commandId);

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

                // ---- 回收（触发源是手动；`01` §8.0.2）----
                case CoreCommand.SessionGc g:
                    return HandleSessionGc(g, commandId);

                case CoreCommand.ClaimAcquire a: return HandleClaimAcquire(a, commandId);
                case CoreCommand.ClaimRelease r: return HandleClaimRelease(r, commandId);

                case CoreCommand.ApprovalRespond a: return HandleApprovalRespond(a, commandId);

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
    /// 写一份预设（`&lt;数据根&gt;/presets/&lt;id&gt;.md`）。
    ///
    /// <para>⚠️ <b>它不碰任何会话、也不失效任何东西</b>：会话存的是**引用**
    /// （`SessionSpec.SystemPromptPreset`），而这次改的是被引用的那个文件 ⇒
    /// 引用方的 `spec` 一个字没变，下一次渲染自己去读就会拿到新内容。
    /// ⇒ 没有会话级事件可发，也没有缓存要失效（`Stepper` 里那次读是**每步**发生的，
    /// 见 `SystemBlock.Render` 的注释）。</para>
    /// </summary>
    private DispatchResult HandlePresetSet(CoreCommand.PresetSet p, string id)
    {
        if (!IsSafeProviderId(p.Id))
            return Reject(id, UnsafeProviderIdReason(p.Id));

        // ⚠️ **空内容是合法的**（= 纯聊天），所以这里**不判** `IsNullOrEmpty`。
        // 判了的话「我要纯聊天」会变成一条被拒的命令，而它是最该好走的那条路。
        _presets.Save(p.Id, p.Content);

        // 回执说清「写进去了什么长度」，因为空内容与有内容的成功**长得一样**——
        // 而用户敲完 `preset set default ""` 之后最想确认的就是「我真的存了个空的吗」。
        return new DispatchResult(new Ack.Accepted
        {
            CommandId = id,
            Note = p.Content.Length == 0
                ? $"'{p.Id}' 已写成空预设——用它的会话不带系统提示词（纯 chat）。"
                : $"'{p.Id}' 已写入（{p.Content.Length} 字符）。",
        });
    }

    /// <summary>删一份预设。判据与 <see cref="HandleProviderDelete"/> 同源（同一个 id 闸、同样不幂等）。</summary>
    private DispatchResult HandlePresetDelete(CoreCommand.PresetDelete d, string id)
    {
        if (!IsSafeProviderId(d.Id))
            return Reject(id, UnsafeProviderIdReason(d.Id));

        bool deleted;
        try
        {
            deleted = _presets.Delete(d.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reject(id, $"无法删除预设 '{d.Id}'：文件正被占用（可能有并发读）。稍后再试。");
        }

        if (!deleted)
            return Reject(id, $"没有预设 '{d.Id}'，无从删除。");

        // ⚠️ 引用它的会话**不报错**（`PresetDelete` 的注释说了为什么）——它们下一次渲染
        // 会发现文件不在，回落到内置模板。回执里点一句，免得用户以为删了个没人用的东西。
        return new DispatchResult(new Ack.Accepted
        {
            CommandId = id,
            Note = $"'{d.Id}' 已删除。引用它的会话不受影响，会回落到内置系统提示词。",
        });
    }

    /// <summary>
    /// 列出有哪些预设。
    ///
    /// <para>⚠️ <b>走线，不像 `provider list` 那样做本地查询</b>：头只有一个入口
    /// （命令 + 快照两层），没有第二套本地查询通道；而头要用它做下拉。
    /// ⇒ 结果走 `Note`（同 `provider.test` 的用法）。空表也是一条**成功**的回执——
    /// 「一个都没有」不是错误，是初始状态。</para>
    /// </summary>
    private DispatchResult HandlePresetList(string id)
    {
        var ids = _presets.List();

        return new DispatchResult(new Ack.Accepted
        {
            CommandId = id,
            Note = ids.Count == 0
                ? "还没有预设。`preset set <id> <内容>` 建一个；不建的话会话用核心内置的那份。"
                : string.Join(" / ", ids),
        });
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
        // ⚠️ **时间线刻意非空**（不是因为它必须非空——三条链路都允许「空时间线 + 有槽位」）。
        // 自检要验的是**这个供应商通不通**，而一条固定的用户消息让探针在任何协议下
        // 都有一条明确的输入；走「靠槽位撑住空时间线」那条路会把自检变成
        // 「顺便验一下槽位装配」，而那是另一件事（装配坏了不该表现为「供应商不通」）。
        var probe = new ModelRequest
        {
            ModelId = provider.DefaultModel,
            MaxTokens = 16,
            Protocol = adapter.Protocol,
            Effort = Axes.ValuesOf(Axes.Effort)![0],   // 取最低档：自检不该挑贵的跑
            // 自检**不带温度**：探针要验的是「这个供应商通不通」，不是「用户此刻的采样偏好」。
            // 带上它反而会让「自检失败」多一种可疑原因（某个模型不认某个温度）。
            Temperature = null,
            // 自检**三块槽位全空**：探针要验的是「这个供应商通不通」。
            // 带上系统块会让「自检失败」多一种可疑原因（用户那份预设写坏了？），
            // 而那与「连通性」是两件事——同上面那句「自检不带温度」的判据。
            // ⚠️ 顺带一提：这里**不能**用 `PromptSlots.Empty` 之外的任何东西，
            // 因为自检跑在**任何会话之外**，压根没有会话的轴值可渲染。
            Slots = Session.PromptSlots.Empty,
            // 自检**不发工具表**：探针要验的是「这个供应商通不通」。
            // 带上工具会多一种可疑的失败原因（某个模型不认 tools、或工具 schema 有问题），
            // 而那与「连通性」是两件事——同上面那句「自检不带温度」的判据。
            Tools = [],
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

        // ⚠️ 只拒 `<= 0`（`TimeSpan.Zero` 非法，见 `SessionSpec.AskTimeout`）；
        // 合法性的**唯一**判据在下面那两行，`SessionHandle.Create` 不重判。
        if (c.AskTimeoutMinutes is { } mins && mins <= 0)
            return Reject(id, $"`--ask-timeout` 要大于 0 分钟（给的是 {mins}）。要「不超时」就别给这个参数。");

        var handle = SessionHandle.Create(
            _host.Store, workingDirectory, model, Trimmed(c.Title), Trimmed(c.Preset),
            askTimeout: c.AskTimeoutMinutes is { } m ? TimeSpan.FromMinutes(m) : null);

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

    /// <summary>
    /// **回收一次**（`01` §8.0.2）：重算集合 → 换渲染 → 发事件。磁盘一字不动。
    ///
    /// <para>⚠️ <b>它拿会话闸</b>（与退订 / 归档那三条同一个理由）：回收改的是
    /// **要发给模型的字节**。若与在跑的步交错，那个步可能一半用旧形态、一半用新形态——
    /// 而前缀的一致性正是整套经济学的地基（`00` §0）。⇒ 要么整个步在旧的形态下跑完，
    /// 要么整个在旧的形态下跑完之后才换。</para>
    ///
    /// <para>⚠️ <b>成果那句话只说一次</b>（2026-10-06 改）：它<b>既进回执的 <c>Note</c>、
    /// 又随 <see cref="WireEvent.SessionReclaimed"/> 发出去</b>。两个去处不是冗余——
    /// 回执给「发命令的那个客户端」（控制台、脚本），事件给「盯着这条流的每个头」。
    /// 原先事件只带条数，而<b>条数到不了用户眼前</b>：回执先到、事件后到，
    /// 头处理事件时要重拉历史，那一步会清空时间线，把回执刚画上的那行一起抹掉
    /// ⇒ 页面一个字都不说。⇒ 现在头把这句话画在<b>重拉之后</b>，位置就不重要了。</para>
    ///
    /// <para>⚠️ <b>它<b>不</b>搬动磁盘、也<b>不</b>碰库</b>：`Decide` 只读。
    /// ⚠️ <b>而 2026-10-11 起它也不再改任何内存</b>——判定搬进了渲染
    /// （`TimelineRender.Render` 每次现算，见那里的注释）⇒ 这条命令的全部效果
    /// 是「**算一遍、把结果说出去**」。它因此**可逆、无副作用**，随时可以再触发一次。</para>
    /// </summary>
    private DispatchResult HandleSessionGc(CoreCommand.SessionGc g, string id)
    {
        if (!TryFind(g.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        // ⚠️ **它不再持闸**（2026-10-11）：它原先要在 `gate` 里写一份内存账（`SetReclaimed`），
        // 所以必须与推进互斥。现在它**只观察**——读一遍记录、算一遍判定、说个数
        // ⇒ 与 `Stepper` 的 `ReadTimeline` 同级，不碰任何可变状态。
        //
        // ⚠️ **判定不再由它落进任何地方**：它原先写到 `handle` 上那份**内存账**里，
        // 而那份账**只在有人喊的时候才更新** ⇒ 陈旧的那份会让注记上给出的 id
        // **在下一个渲染窗口里取不回**（判据与后果见 `TimelineRender.Render` 的注释）。
        // ⇒ 判定现在住在渲染里（每次现算），这条命令因此只是个**观察口**：
        // 它的独有价值是「立刻算一次并把结果说出去」，**不是任何东西的前提**。
        // ⚠️ **它算的是「可恢复」那一档**（`Reclaim.Decide`），**不含点杀**——
        // 点杀要一个 `IToolContext`，而这条命令的用途是「看一眼收了多少」，
        // 不是「冻结这一步的世界」（那件事归 `Stepper`）。
        var count = Reclaim.Decide(
            _host.Store.ReadTimeline(handle.ProjectKey, handle.SessionId),
            new Storage.BlobStore(_host.Store.DataRoot)).Count;

        // ⚠️ **回执里必须说结果**，不能只回一个「收到了」：回收常常**一条都收不了**
        // （都在保护带里 / 内容指向的对象不在库里）——那时渲染一字未变，
        // 而用户看到的就是「**按了没反应**」，与「坏了」长得一模一样。
        // `Note` 正是给「业务结果」用的（`provider.test` 就是这么用的），把条数说了就分得开。
        var reason = count == 0
            ? "没有可回收的结果（都在保护带里，或者内容已经不在库里）"
            : $"回收了 {count} 条结果";

        // ⚠️ **没有它头就看不见**：回收不改盘，头看的是核心渲染的那份形态 ⇒
        // 不告诉它一声，它会一直显示回收之前的（而且完全看不出来）。
        // ⚠️ 只是**换个落点**，话还是上面那一句——不在这里另写一份（同一件事只说一次）。
        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.SessionReclaimed(handle.SessionId, reason));

        return new DispatchResult(new Ack.Accepted
        {
            CommandId = id,
            SessionId = g.SessionId,
            Note = reason,
        });
    }

    private async Task<DispatchResult> HandleSessionSendAsync(
        CoreCommand.SessionSend s, string id, bool awaitCompletion, CancellationToken ct)
    {
        if (!TryFind(s.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        // ⚠️ **撞上「正在等回答」⇒ 先顶替那次询问**（`01` §3.8 的 `Superseded`）。
        //
        // ⚠️ **不顶替的话，下一条请求会 400**，而**这次是实测的**（2026-10-08）：
        // 会话挂起时那条模型消息（含 `tool_use`）**已经落了盘**（必须落，否则答完补的
        // `tool_result` 悬空），而它的配对结果**要等用户答完才有**。
        // 用户在这时直接打字（而不是点卡片）⇒ 队列里多一条用户消息 ⇒ 这一步渲染出来的
        // 请求里那条 `tool_use` **后面没有 `tool_result`** ⇒ 上游原话：
        // 「`tool_use` ids were found without `tool_result` blocks immediately after」。
        //
        // ⇒ 顶替的做法：**先补一条说得清的结果把悬空的那个配对补上**，
        // 再把这条用户消息当普通输入处理（走下面那条路）。
        // ⚠️ 判据是「用户**没有回答**那个问题，而是说了别的」——**不能假装他答了**
        // （那会让模型以为它问的都得到了答复，而真相是没人理它）。
        if (handle.State.Status == SessionStatus.WaitingForInput && handle.State.Ask is { } superseded)
        {
            _host.Store.AppendEvent(handle.ProjectKey, handle.SessionId, Stepper.Supersede(
                superseded, string.Concat(s.Blocks.OfType<Block.TextBlock>().Select(b => b.Text))));

            handle.AwaitingInput = null;
            handle.UpdateStateAndPersist(_host.Store, st => st with
            {
                Status = SessionStatus.Idle,
                Ask = null,
            }, diskStatus: SessionStatus.Idle);

            _bus.Publish(EventBus.ApprovalsTopic,
                new WireEvent.ApprovalResolved(handle.SessionId, superseded.AskId, "superseded"));
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.ApprovalResolved(handle.SessionId, superseded.AskId, "superseded"));
        }

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

        // ⚠️ **用户输入把链长度归零**：熔断算的是「自上次用户输入以来跑了多少步」
        // （见 `SessionHandle.ConsecutiveToolSteps` 的注释——不归零的话，
        // 一个用久了的会话会在用户**正常提问**时触发熔断）。
        handle.ResetToolSteps();

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

        // ⚠️ 声明在 try **外面**：循环的续推发生在 finally 之后（那边得先 `ExitStep`），
        // 所以结果要活过 try 这个作用域。
        ModelReply? reply = null;
        try
        {
            await gate.WaitAsync(stepCts.Token);
            acquired = true;

            // `step.begin` 是「在跑」的唯一信号源，不是 `session.state`——磁盘从不存 Running。
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.StepBegin(handle.SessionId, handle.State.Turn + 1, CoreClock.Now()));

            ModelReply replyForLoop;
            Stepper.StepOutcome step;

            if (handle.TakeResume() is { } pending)
            {
                // ---- 答复之后直接执行（`01` §3.8）----
                //
                // ⚠️ **它走的仍是这一步的整条仪式**（闸 / `step.begin` / 事件 / 落盘 / 循环续推）
                // ——这正是它**不另开一条路**的理由：另开一条会把这些一条条抄一遍，
                // 而抄漏一条的表现是「答复之后那个会话看起来正常，而它推不动了」。
                step = await _resolveStepper(handle).ResumeAsync(
                    handle, pending.Calls, pending.Reply, stepCts.Token, pending.Paused);

                // ⚠️ 答复那条路**不发模型消息**（没有模型消息可发）——`ResumeAsync` 交回的
                // `Reply` 是那个「说得出名字的空回复」，喂给下面的事件与循环判断正好。
                reply = step.Reply;
                replyForLoop = step.Reply;
            }
            else
            {
                // 取空队列——合并是队列的副产品。
                var blocks = handle.DrainQueue();
                step = await _resolveStepper(handle).StepAsync(handle, blocks, stepCts.Token);
                reply = step.Reply;
                replyForLoop = step.Reply;

                // 模型消息由这里发（`Stepper` 不碰事件总线）；判据与它落盘时一致：拒答 / 空块不产生模型消息。
                if (!reply.Refused && reply.Blocks.Count > 0)
                    _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                        new WireEvent.MessageAppended(handle.SessionId, "model", reply.Blocks, CoreClock.Now()));
            }

            // ⚠️ **挂起也要发事件**（`01` §3.8）：一条头收不到的问题 = 会话被**静默挂起**。
            // ⚠️ 它同时投**两条流**（`approvals` 与这条会话流），理由见
            // `WireEvent.ApprovalRequested` 的注释。⚠️ 在 `StepEnd` **之前**发——
            // 头的顺序应当是「这一步结束了，而它停在等一个回答上」。
            if (step.Refused is { } asked)
            {
                var requested = new WireEvent.ApprovalRequested(
                    handle.SessionId, asked.Id, asked.Kind, asked.Items,
                    asked.Remember, step.DeadlineAt);

                _bus.Publish(EventBus.ApprovalsTopic, requested);
                _bus.Publish(EventBus.SessionTopic(handle.SessionId), requested);
            }

            PublishStepEnd(handle, replyForLoop);

            // ⚠️ **这里刻意不 `return`**：要让 `finally` 先跑（它 `ExitStep()` 放掉那把标志，
            // 而下一步要重新拿它）。返回值在 finally 之后的续推那一步给出。
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

        // ---- agent 循环：模型调了工具 ⇒ 再推进一步（`01` §4）----
        //
        // ⚠️ **这里是循环的落点，而它不是新机制**：`01` §4 定义「步 = 一次模型调用 + 其工具执行」
        // ⇒「模型又调了工具」= 再一次入队 + 启动，走的就是 §6.1 已有的那条路。
        // ⚠️ 而**形状上也只可能是这样**：`finally` 刚跑完、这把标志已经放掉，
        // 下面这次重新 `TryEnterStep()` 是一条普通的新步——不需要「步内循环」那种
        // 会打破状态机的东西。
        if (reply is null) return null;   // 取消 / 失败那两条路（它们的 `error` 事件已经在上面发过）

        // ⚠️ **挂起不是「调了工具」**（`01` §3.8）。
        //
        // ⚠️ **这一格漏掉过一次，而它是实测抓出来的**（2026-10-08，真模型 + 真网关）：
        // 模型调 `ask` ⇒ `Stepper` 挂起、一条结果都没落 ⇒ 状态 `WaitingForInput`，
        // 可这里**照样**看见 `reply.Blocks` 里那个 `tool_use` ⇒ 接着推下一步
        // ⇒ 下一次请求里那条 `tool_use` **后面没有 `tool_result`** ⇒ 上游原话
        // 「`tool_use` ids were found without `tool_result` blocks immediately after」。
        // ⇒ **那个会话在用户答完之前一点都推不动**，而它当时看起来完全正常。
        //
        // ⇒ 判据是**状态**，不是「回复里有没有 tool_use」：挂起是一种**刻意的停顿**，
        // 而循环的继续权归「等到了回答」那个时刻（`HandleApprovalRespond` 里那次续跑）。
        if (handle.State.Status == SessionStatus.WaitingForInput) return reply;

        return await ContinueIfToolCalledAsync(handle, reply, throwOnFailure);
    }

    /// <summary>
    /// 这一步的模型调了工具吗？调了就**接着推下一步**。
    ///
    /// <para>⚠️ <b>三个提前返回，每条都有理由</b>：</para>
    /// <list type="number">
    ///   <item><b>没调工具</b> ⇒ 循环正常结束（这是绝大多数情形）。</item>
    ///   <item><b>没拿到那把标志</b> ⇒ 已经有别的推进在排队了。⚠️ **不重新入队**：
    ///   队列里已经有东西（那条推进就是为此排的），再塞一次等于凭空多推一步。</item>
    ///   <item><b>撞上熔断</b> ⇒ 见 <see cref="MaxConsecutiveToolSteps"/>。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>不重入队也要推</b>：`启动(s)` 的判据里「队列非空」是**必要条件**，
    /// 而工具循环**没有新输入**（`01` §6.2 的「自主续跑」正是这个意思）⇒ 队列是空的。
    /// ⇒ 直接调这一步的推进入口，不走「入队 + 启动」，免得为「触发一次推进」塞一条假待办
    /// （那条假待办会被当成一条真的要发给用户的消息）。</para>
    ///
    /// <para>⚠️ <b>它会让 <c>turn</c> 一步一增</b>：一次 <c>send</c> 里模型调了两轮工具，
    /// `turn` 就 +2、+3。这是**对的**——步是持久化的原子单位，每一次模型调用都是一步。
    /// ⇒ 头看到的「会话推进了几步」与实际调用次数一致，而不是与用户按了几次回车一致。</para>
    /// </summary>
    private async Task<ModelReply?> ContinueIfToolCalledAsync(
        SessionHandle handle, ModelReply reply, bool throwOnFailure)
    {
        if (!reply.Blocks.OfType<Kernel.Block.ToolUseBlock>().Any()) return reply;

        // ⚠️ **熔断的阈值是占位的**（`01` §14 #14 明确写着阈值与触发后的行为**待定**）。
        // 取一个保守值只为「不让它无限跑」——今天没有任何一档会自动续跑，
        // 所以这条**在真实路径上还打不到**；它防的是将来接上「目标」档那天的失控。
        if (handle.ConsecutiveToolSteps >= MaxConsecutiveToolSteps)
        {
            _bus.Publish(EventBus.SessionTopic(handle.SessionId),
                new WireEvent.Error(handle.SessionId,
                    $"连续 {MaxConsecutiveToolSteps} 步都在调工具，已停下——这多半是循环打转（`01` §6.2 的熔断）。"));
            return reply;
        }

        if (!handle.TryEnterStep()) return reply;

        handle.BumpToolStep();

        if (throwOnFailure)
            return await RunStepAsync(handle, CancellationToken.None, throwOnFailure: true);

        // 控制面那条路：**后台接着跑**（与 `HandleSessionSendAsync` 同一个形状）——
        // 同步等会让 HTTP/WS 的调用方卡住整条工具链，而 `session.interrupt` 也失去落点。
        _ = Task.Run(() => RunStepAsync(handle, CancellationToken.None, throwOnFailure: false),
            CancellationToken.None);

        return reply;
    }

    /// <summary>
    /// 「目标」档的**最大连续步数熔断**（`01` §6.2 规矩 1 / §14 #14）。
    ///
    /// <para>⚠️ <b>阈值是占位的</b>：§14 #14 把「阈值与触发后的行为」列为**未定项**。
    /// 这里取一个保守值（够跑完一次正常的多轮工具使用，又不至于失控），
    /// <b>并如实标注它是占位</b>——本仓栽过「把占位值当成定论写进文档」。</para>
    /// </summary>
    private const int MaxConsecutiveToolSteps = 24;

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

        // 都没给 ⇒ 这不是一次「成功的 no-op」，是一次没说清的命令。拒绝，别静默成功。
        if (string.IsNullOrWhiteSpace(c.ProviderId) && string.IsNullOrWhiteSpace(c.ModelId)
            && c.AskTimeoutMinutes is null)
        {
            return Reject(id, "没说要改什么。用法：session configure <会话> "
                + "[--provider <id>] [--model <id>] [--ask-timeout <分钟>]");
        }

        // ⚠️ **超时必须 > 0**（`SessionSpec.AskTimeout` 的注释明写 `TimeSpan.Zero` 非法：
        // 「等 0 分钟」读不出意思）。在这里拒，而不是让它悄悄等于「立刻超时」——
        // 后者会让一次写错的参数看起来像「用户故意不让我等」。
        if (c.AskTimeoutMinutes is { } mins && mins <= 0)
            return Reject(id, $"`--ask-timeout` 要大于 0 分钟（给的是 {mins}）。要「不超时」就别给这个参数。");

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
            // ⚠️ 超时这一格也要判「变没变」，否则「只改超时」会落成 `changed == false`
            // ⇒ 不发事件、回执里也没有那句话，而盘上**真的变了**（静默生效）。
            var timeoutChanged = c.AskTimeoutMinutes is { } t && s.AskTimeout != TimeSpan.FromMinutes(t);

            var m = b with
            {
                ProviderId = string.IsNullOrWhiteSpace(c.ProviderId) ? b.ProviderId : c.ProviderId!,
                ModelId = string.IsNullOrWhiteSpace(c.ModelId) ? b.ModelId : c.ModelId!,
                // Protocol 是**供应商定义的快照**，换供应商时必须跟着重取，否则它与 ProviderId 不同代。
                // ⚠️ 它**不是**「现在真的走哪条协议」——`Stepper` 取适配器的 Protocol（provider 定义
                // 可以被 `provider set --protocol` 改掉）。这里只是让快照跟上此刻的定义。
                Protocol = target?.Protocol ?? b.Protocol,
            };
            changed = m != b || timeoutChanged;
            // ⚠️ 超时那一格与 `Model` 是**两个字段**，但同在一次 `UpdateSpecAndPersist` 里改：
            // 两条独立的写会各自读-改-写，后写的把前写的整份盖掉（丢更新）。
            return s with
            {
                Model = m,
                // ⚠️ 「没给」= 保留旧值（同上面两个字段的语义）。
                // ⇒ 想**取消**超时要怎么表达？今天没有这个入口——如实记着（`01` §14 #13）。
                // 不发明一个 `--ask-timeout none`，因为「没有值」与「值叫 none」在这里
                // 没法从一个 `double?` 里分辨（本仓那条「一个值承担两种含义」的老毛病）。
                AskTimeout = c.AskTimeoutMinutes is { } mins ? TimeSpan.FromMinutes(mins) : s.AskTimeout,
            };
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
            new WireEvent.SpecChanged(handle.SessionId, next, handle.Spec.SystemPromptPreset));

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
    /// **回答一次询问**（`01` §3.8）。挂起的会话从这里、也只有从这里解除。
    ///
    /// <para>⚠️ <b>它<b>不</b>同步跑下一步</b>：合成结果 + 落盘 + 释放那把标志之后，
    /// 走的是**与 `session.send` 同一条**的异步推进（`Task.Run` + `RunStepAsync`）。
    /// 理由也同它：同步等会让 WS/HTTP 的调用方卡住一整条工具链，
    /// 而 `session.interrupt` 失去落点（那时步在命令里已经跑完了）。</para>
    ///
    /// <para>⚠️ <b>次序：先落结果、再释放标志、最后才续跑</b>。反过来（提前续跑）
    /// 那一次推进拿不到闸（旧标志还在）⇒ `TryEnterStep` 返回 false ⇒ 这条答复**被静默丢掉**。</para>
    /// </summary>
    private DispatchResult HandleApprovalRespond(CoreCommand.ApprovalRespond c, string id)
    {
        if (!TryFind(c.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        // 没在等 ⇒ rejected（**不是**幂等成功）：用户对着一条已经答过的卡再点一次，
        // 回一句「已受理」会让他以为那次答复生效了。
        if (handle.State.Ask is not { } paused || handle.State.Status != SessionStatus.WaitingForInput)
            return Reject(id, $"会话 '{c.SessionId}' 现在没有在等回答。");

        // 那批问题原文住在内存里。⚠️ **重启之后它没了**（`01` §3.8 认下的代价）——
        // 那时代码算不出该问什么，只能合成一句「原问题没能保留」让用户重启这一步。
        var awaiting = handle.AwaitingInput;

        // ⚠️ askId 对不上 = 时代的眼泪（用户对着**上一张**卡点的）。拒绝，
        // 而不是「当作最新的那张」——后者会把一次陈旧的批准**安到另一个调用上**。
        if (!string.Equals(paused.AskId, c.AskId, StringComparison.Ordinal))
            return Reject(id, $"这次答复对应的询问（{c.AskId}）不是当前等在会话上的那一次（{paused.AskId}）。");

        // ---- 超时（`01` §3.8）：**盘面自证，不是定时器** ----
        // ⚠️ 看它的人只有这里（以及将来 `session.send` 那条 Superseded 路）。
        // 过期 ⇒ **用户的答案不采用**（它来晚了），按超时合成。
        var expired = paused.DeadlineAt is { } dl && CoreClock.Now() >= dl;
        var cancelled = string.Equals(c.Outcome, "cancel", StringComparison.Ordinal);

        // ⚠️ **批准 = 直接执行**（2026-10-11 改）。
        //
        // <para>⚠️ <b>原先走的是「让模型重新发起」</b>：批准只落一句「请重新发起它，
        // 这次会放行」，而重发要模型**再走一次往返**。那个方案的代价在实测里全露出来了：
        // ① 多一次模型调用；② 多一个失败面（模型可能不重发）；③ **用户改过的参数要绕一圈
        // 经过模型才被执行**——而那是用户的决定，模型可能重新排版、甚至丢掉改动
        //（`ArgumentsAmended` 那一格本来是为此存在的，而它在 `src/` 里**零读取点**：
        // 写进去了、没人读，设计意图与实现对不上）。</para>
        //
        // <para>⚠️ <b>关键的判据是「批准只对这一次调用说话」</b>：它**不写记忆**
        //（记忆是「以后这一类别再问了」）。没有记忆 ⇒ 模型重发同一批时闸门**照样会问**
        // ——那正是「批了又弹」的活锁。⇒ 直接执行把它从根上去掉。</para>
        //
        // <para>⚠️ **拒绝 / 超时 / 作废不走这里**：那时「这些调用被一起作废了」是真话，
        // 落结果、不执行（`Stepper.Blocked` 给每一条都补上配对）。</para>
        // ⚠️ **被盖住的是哪几条、以及用户答了什么**——两个 Kind 的判据不一样（`01` §3.8）：
        //
        //   · **提问（`Question`）** ⇒ 只有那一条（那个 `ask` 调用本身）。它的答案
        //     由核心拼进结果（`ToolExecutor` 的 `Kind: Question` 分支），工具一个字不碰。
        //   · **授权（`Permission`）且批准** ⇒ **等待中的全部**。⚠️ 这是刻意的：
        //     那张授权卡盖住的每一条都还没执行过，而「用户按了批准」就是对**这一批**说的。
        //     ⇒ 全部执行、全部配对，`01` §14 #31 ① 那个「其余几条悬着」的缺口就此关掉。
        //   · **拒绝 / 超时 / 作废 / 没批准** ⇒ 空（一条都不跑，走下面那条落结果的路）。
        // ⚠️ 写上目标类型：下面那个嵌套条件表达式里既有集合表达式又有整型/字符串混排，
        // 不写的话编译器推不出共同类型（CS0173）——而**不是**「风格问题」。
        //
        // ⚠️ **内存那半可能是 null**（从盘载入过 / 重启过）⇒ 那时**从盘上解**
        // （`Stepper.RecordedCalls`），**不许凭空造一条**：编出来的那条参数是空的，
        // 而工具会拿到一个空参数对象——实测里那句「缺少 `path` 参数」就是这么来的。
        // ⚠️ 与 `AskTool.ItemsOf` 同一个形状：**内存是缓存，盘上那条 `tool_use` 才是真相**。
        var waiting = awaiting?.Calls
            ?? Stepper.RecordedCalls(_host.Store, handle.ProjectKey, handle.SessionId, paused.CallId);

        IReadOnlyList<string> covered =
            (expired || cancelled || (paused.Kind == AskKind.Permission && !IsApproved(c)))
                ? []
                : paused.Kind == AskKind.Question
                    ? [paused.CallId]
                    : waiting.Count > 0 ? [.. waiting.Select(x => x.CallId)] : [paused.CallId];

        var reply = new Lilara.Core.Session.Tools.ToolExecutor.Reply(
            paused.Kind, covered,
            paused.Kind == AskKind.Question
                ? Session.Tools.AskTool.ItemsOf(_host.Store, handle.ProjectKey, handle.SessionId, paused.CallId)
                : [],
            c.Answers);

        // ---- 往下走（批准 / 提问），还是就此收尾（拒 / 超时 / 作废）----
        //
        // ⚠️ **判据按 Kind 分**（`01` §3.8）：
        //   · **提问** ⇒ 一律往下走（用户答的就是内容，没有「拒」这回事）。
        //   · **授权** ⇒ 只有**批准**才往下走；没批 / 超时 / 作废落一句拒绝结果、就此收尾。
        var goesDownstream = paused.Kind == AskKind.Question || IsApproved(c);

        // ⚠️ **批准 = 直接执行**（2026-10-11 改）。
        //
        // <para>⚠️ <b>原先走的是「让模型重新发起」</b>：批准只落一句「请重新发起它，
        // 这次会放行」，而重发要模型**再走一次往返**。代价三条：
        // ① 多烧一次模型调用；② 多一个失败面（模型可能不重发）；
        // ③ **用户改过的参数要绕一圈经过模型才被执行**——而那是用户的决定，
        // 模型可能重新排版、甚至丢掉改动（`ArgumentsAmended` 那一格本来是为此存在的，
        // 而它在 `src/` 里**零读取点**：写进去了、没人读）。</para>
        //
        // <para>⚠️ <b>提问那条也走这里</b>：它的答案是内容，而「让模型转述一遍」
        // 同样多一次往返、且会让答案在转述里走样 ⇒ 核心直接拼进结果。</para>
        if (!expired && !cancelled && goesDownstream)
        {
            var edited = c.Answers.TryGetValue("arguments", out var a) ? a : null;

            // ⚠️ **记一条批准**（`01` §3.8）。⚠️ <b>它的角色变了</b>（2026-10-11）：
            // 从前它是「批准生效」的**唯一机制**（重发要靠它放行），于是「没写它就活锁」；
            // 现在批准**直接执行**（见下面那段），它退化成**一个优化**——
            // 「同一个调用本合同参数再来一次就别问了」。
            //
            // <para>⚠️ 判据是「记忆的粒度本来就是『这一次调用』」——用户裁定过
            // 「这一次」是**默认**。勾选**不是**「要不要记住」，是「要不要把粒度放宽到整个工具」。</para>
            var store = new ApprovalStore(_host.Store.DataRoot, handle.ProjectKey, handle.SessionId);
            store.Add(new Approval(paused.ToolName, paused.ArgsHash,
                ToolWide: c.Remember is { Length: > 0 } rm
                          && string.Equals(rm, nameof(AskMemory.Tool), StringComparison.Ordinal),
                At: CoreClock.Now()));

            // ⚠️ **参数改写在这里生效**：把用户改的那份换进要执行的调用
            // ⇒ 「用户改的就是执行的」（`Stepper.Amended`）。提问那条没有参数改写（`edited` 恒 null）。
            var calls = waiting.Count == 0
                ? new List<Block.ToolUseBlock> { new(paused.CallId, paused.ToolName, edited ?? "{}") }
                : [.. Stepper.Amended(waiting, paused, edited)];

            handle.QueueResume(new SessionHandle.PendingResume(calls, reply, paused));
            ClearPause(handle);

            _bus.Publish(EventBus.SessionTopic(c.SessionId),
                new WireEvent.ApprovalResolved(c.SessionId, c.AskId, c.Outcome));
            _bus.Publish(EventBus.ApprovalsTopic,
                new WireEvent.ApprovalResolved(c.SessionId, c.AskId, c.Outcome));

            // ⚠️ **拿不到闸就不续跑**（已经有一次推进在飞）。
            // ⚠️ 拿不到时那次 `QueueResume` **留在槽里**，会被那次在飞的推进的**下一次**
            // `TakeResume` 取走：它取走的正是「用户刚答的那一件」，语义对。
            if (!handle.TryEnterStep())
                return new DispatchResult(new Ack.Accepted { CommandId = id, Note = "已记录答复，稍后执行。" });

            _ = Task.Run(() => RunStepAsync(handle, CancellationToken.None, throwOnFailure: false),
                CancellationToken.None);

            return new DispatchResult(new Ack.Accepted { CommandId = id, Note = "已批准，正在执行。" });
        }

        // ---- 拒 / 超时 / 作废：落结果、不执行 ----
        //
        // ⚠️ **这三条走到这里时「这批不跑了」是真话** ⇒ 每条等待中的调用各配一条结果
        // （`Stepper.Blocked`）。⚠️ **批准不走这里**（它从上面那条直接执行的路出去了），
        // 所以原先那句「批准那条仍只配一条、是认下的代价」**已经不成立了**——
        // 现在**四条收尾每条都给全部被挡下的调用配对**。
        // 提问那条没有「拒」这回事；走到这里的**只有授权被否**。
        var (text, amended) = expired
            ? (TimeoutText(paused.Kind, handle.Spec.AskTimeout), null)
            : cancelled
                ? ("用户把这次询问作废了。", null)
                : Reply(c, paused, handle, awaiting);

        _host.Store.AppendEvent(handle.ProjectKey, handle.SessionId,
            Stepper.Answer(paused, text, amended));

        // ⚠️ `awaiting` 可能是 null（从盘载入过、内存那半没了）⇒ 那时数不出条数，
        // **不编一个数**（同 `Reply` 里那条既有的判断），退回单条行为。
        if (awaiting is { Calls.Count: > 1 })
        {
            var said = expired
                ? "（这一批里的这次调用和它一起被作废了：等待超时，没有人回答。）"
                : cancelled
                    ? "（这一批里的这次调用和它一起被作废了：用户把这次询问整个作废了。）"
                    : "（这一批里的这次调用和它一起被作废了：用户拒绝了。）";

            _host.Store.AppendEvents(handle.ProjectKey, handle.SessionId,
                Stepper.Blocked(awaiting.Calls, first: 1, said));
        }

        ClearPause(handle);

        _bus.Publish(EventBus.SessionTopic(c.SessionId), new WireEvent.ApprovalResolved(c.SessionId, c.AskId, c.Outcome));
        _bus.Publish(EventBus.ApprovalsTopic, new WireEvent.ApprovalResolved(c.SessionId, c.AskId, c.Outcome));

        // ⚠️ **作废与超时都走这里**：结果落完了，但**不续跑**——这批不跑了。
        // ⚠️ 超时那条也**不续跑**：续跑没有意义（那个调用没执行，而模型没在等新东西）。
        if (cancelled || expired)
        {
            return new DispatchResult(new Ack.Accepted
            {
                CommandId = id,
                Note = cancelled ? "已作废这次询问。" : "已经超时了，按超时处理。",
            });
        }

        // ⚠️ `TryEnterStep` 在这里：拿不到就**不续跑**（已经有一次推进在飞）。
        // 与 `ContinueIfToolCalledAsync` 那条同样处理——不需要重新入队，因为
        // 那条在飞的推进本来就会读到刚落的那条结果。
        if (!handle.TryEnterStep())
            return new DispatchResult(new Ack.Accepted { CommandId = id, Note = "已记录答复。" });

        _ = Task.Run(() => RunStepAsync(handle, CancellationToken.None, throwOnFailure: false),
            CancellationToken.None);

        return new DispatchResult(new Ack.Accepted
        {
            CommandId = id,
            Note = expired ? "已经超时了，按超时处理。" : null,
        });
    }

    /// <summary>
    /// **用户批没批**（`01` §3.8）。⚠️ <b>整个核心判这一件事只有这一处</b>。
    ///
    /// <para>⚠️ <b>它被抽出来，是因为原先有两个副本</b>（2026-10-11）：一份在
    /// <see cref="Reply"/> 里决定落哪句话，另一份本来要写在
    /// <see cref="HandleApprovalRespond"/> 里决定「要不要给同批其余调用补配对」。
    /// 两份判据的漂法很具体——**头与控制台发的东西不一样**（头原先把 `decision` 整个漏了，
    /// 而控制台一直在发），所以「批没批」这件事在两边各判一次时，**头那一侧全判错**。
    /// ⇒ 本仓那条「两层判据必须共用一个谓词」在这里的形态就是：**谓词只有一个**。</para>
    ///
    /// <para>⚠️ 三个拼法都收（`allow` / `approve` / `yes`）：它们都是「同意」的自然说法，
    /// 而**判定放宽不需要用户知道**。⚠️ 空字典 ⇒ 没批（这是**默认**，不是特例）：
    /// 缺 `decision` 就是没点头，而「没点头」的后果是拒绝——方向是安全的那一侧。</para>
    /// </summary>
    private static bool IsApproved(CoreCommand.ApprovalRespond c) =>
        c.Answers.TryGetValue("decision", out var d) && (d == "allow" || d == "approve" || d == "yes");

    /// <summary>
    /// **挂起态清干净（内存 + 盘）**（`01` §3.8）。⚠️ **与「拿到闸」无关**：这一步不跑推进。
    ///
    /// <para>⚠️ <b>批准与拒 / 超时 / 作废四条路都要它，所以抽成一处</b>：两份实现会漂，
    /// 而漂的后果是**某一条路上那个会话永远卡在 `WaitingForInput`**
    /// （磁盘上那个状态是合法的，所以重启也救不回来）。</para>
    /// </summary>
    private void ClearPause(SessionHandle handle)
    {
        handle.AwaitingInput = null;
        handle.UpdateStateAndPersist(_host.Store, s => s with
        {
            Status = SessionStatus.Idle,
            Ask = null,
        }, diskStatus: SessionStatus.Idle);
    }

    /// <summary>
    /// 超时时那条结果（`01` §3.8）。⚠️ <b>两种 Kind 的后果不同</b>（用户裁定）：
    /// 授权默认**拒绝**；问意见返回一句「没有人回答」。
    /// </summary>
    private static string TimeoutText(AskKind kind, TimeSpan? timeout)
    {
        var waited = timeout is { } t ? t.TotalMinutes.ToString("0.#") + " 分钟" : "一段时间";

        return kind == AskKind.Permission
            ? $"这次调用被拒绝了：等你回答了 {waited}，没有人回答。"
            : $"没有人回答（等了 {waited}）。";
    }

    /// <summary>
    /// 把一次答复算成最终正文（`01` §3.8）。⚠️ <b>它<b>不</b>执行那个工具</b>。
    ///
    /// <para>⚠️ <b>「批准」的后果是「下一步带着批准去执行」</b>——而那个「下一步」是
    /// **模型**走的：它看到这条结果，重新发起调用，而那时闸门因为记忆/档位放行。
    /// ⇒ 本方法只落一条**说得清的答复**，不去替模型跑那个工具。
    /// 这样「谁执行工具」只有一个答案（`ToolExecutor`），而这里不会长成第二个执行点。</para>
    ///
    /// <para>⚠️ 判据是「这一步的产物是**答复**，不是**执行**」。把执行塞进来会让
    /// 「一条结果可能对应一次真的副作用」变成隐式的，而 `01` §3.6 那套配对纪律
    /// 是围绕「结果的产出者是 `ToolExecutor`」写的。</para>
    /// </summary>
    private (string Text, string? Amended) Reply(
        CoreCommand.ApprovalRespond c, PausedAsk paused, SessionHandle handle, SessionHandle.PendingAsk? awaiting)
    {
        var kind = string.Equals(c.Outcome, "modify", StringComparison.Ordinal) ? "modify" : c.Outcome;

        if (kind == "modify")
            return ("你把参数改了但还没有批准。请用改好的参数重新发起这一次调用。", null);

        // ---- 授权 ----
        if (paused.Kind == AskKind.Permission)
        {
            // ⚠️ **批准走不到这里**（它在上面那条直接执行的路出去了）⇒ 只剩「拒」。
            _ = handle;
            return ("用户拒绝了这次调用。" + Reason(c), null);
        }

        // ---- 问意见：答案就是内容 ----
        //
        // ⚠️ **问题从盘上解，不从内存**（`01` §3.8）：内存那半（`handle.AwaitingInput`）
        // 只在「本进程亲手挂起过」时才在——**任何一次「从盘重新载入」都会丢掉它**。
        // 而问题原文本来就在盘上（那条模型消息必须落，否则答完补的结果悬空）。
        //
        // ⚠️ 这条**是实测逼出来的**（2026-10-08）：我只修了 `/state` 那条（页面看得见问题了），
        // 忘了这里 ⇒ 用户答完之后拿到的是「会话在等待期间重启过，原来那批问题没能保留」
        // ——**而问题明明就在盘上**。⇒ 两处必须共用 `AskTool.ItemsOf` 那一份实现。
        var items = Session.Tools.AskTool.ItemsOf(_host.Store, handle.ProjectKey, handle.SessionId, paused.CallId);

        if (items.Count == 0)
        {
            // ⚠️ 解不出来才认输：要么是**重启前内存那半丢了且盘上也没有**
            //（闸门那条询问没有工具参数），要么是模型的参数形状坏了。
            // **如实说**，不编答案——编一个空值比这坏得多（同 §3.6 ①之二）。
            return ("用户回答了，但这次询问的问题没能取回来 ⇒ 这次答复对不上任何一项。"
                  + "请重新问一次。", null);
        }

        var lines = new List<string>();
        foreach (var item in items)
        {
            // ⚠️ **缺项如实写「未回答」**，不编一个空值——那是「多了一条假信息」（`01` §3.6 ①之二）。
            lines.Add(c.Answers.TryGetValue(item.Id, out var v) ? $"{item.Id}: {v}" : $"{item.Id}: （未回答）");
        }

        // ⚠️ **同一批里还有别的调用被这次询问挡下了**：多问是一个信封，而记法要求
        // 一条 `tool_use` 一条 `tool_result`（`01` §3.8）。⇒ 只能配一条，其余的
        // 由这句话交代给模型，它自己去重发。**本批认下的代价，不是设计目标。**
        //
        // ⚠️ 判据里那个 `awaiting is` 的 null 检查**不是防御性写法**：内存那半可能是 null
        // （从盘载入过），而**问题本身这时已经从盘上解出来了**——所以这里只是「数不到
        // 同批还有几条」，而不是「不知道该问什么」。数不到就不说那句，不编一个数。
        if (awaiting is not null && awaiting.Calls.Count > 1)
        {
            lines.Add($"（同一批里还有 {awaiting.Calls.Count - 1} 次调用被这次询问一起挡下了，"
                    + "它们没有执行——请重新发起。）");
        }

        return (string.Join("\n", lines), null);
    }

    /// <summary>拒绝时带上用户给的理由（`01` §3.8：拒绝理由比「档位不允许」有用得多）。</summary>
    private static string Reason(CoreCommand.ApprovalRespond c) =>
        c.Answers.TryGetValue("reason", out var r) && !string.IsNullOrWhiteSpace(r)
            ? $"理由：{r}"
            : "";

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

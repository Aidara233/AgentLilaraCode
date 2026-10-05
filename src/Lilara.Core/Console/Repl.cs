using Lilara.Core.Protocol;
using Lilara.Core.Kernel;
using Lilara.Core.Hosting;
using Lilara.Core.Storage;
using Lilara.Core.Session;

namespace Lilara.Core.Console;

// 命令面：同步收发、没有视图、不订阅。把一行文本解析成 CoreCommand 交给 dispatcher，
// 再把回执格式化成人看的字；命令的语义一条都不在这里。
public sealed class Repl(
    CoreHost host,
    CommandDispatcher dispatcher,
    Func<SessionHandle, Stepper> resolveStepper,
    TextWriter output)
{
    private readonly CoreHost _host = host;
    private readonly CommandDispatcher _dispatcher = dispatcher;
    private readonly Func<SessionHandle, Stepper> _resolveStepper = resolveStepper;
    private readonly TextWriter _out = output;

    // 控制台自己的「当前会话」概念，不是核心状态（只有控制台动作能设它）；
    // setter 会把 handle 登记进宿主激活表——不登记会让同一会话出现两个实例、状态分叉。
    public SessionHandle? Current
    {
        get => _current;
        set
        {
            _current = value;
            if (value is not null) _host.Register(value);
        }
    }

    private SessionHandle? _current;

    // 纯控制台的显示刷新判据（provider set 之后要重印端点那一行）；
    // 只影响一行字印不印，不影响正确性。provider delete 有意不扩进来。
    public static bool IsProviderSetCommand(string line) =>
        line.Trim().StartsWith("provider set ", StringComparison.Ordinal);

    public async Task<bool> ExecuteAsync(string line, CancellationToken ct)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return true;

        // quit / exit 是控制台本地命令，不是核心命令集的一部分（头没有「退出」这条）。
        if (trimmed is "quit" or "exit") return false;

        // 查询走快照那一层（控制台只用「命令 + 快照」两层）。
        if (trimmed == "history") { PrintHistory(); return true; }
        if (trimmed == "status") { PrintStatus(); return true; }
        // 判据与解析器共用（IsClaimList）：各写一份会漂 ⇒ claim LIST 被当成消息发给模型。
        if (CoreCommandParser.IsClaimList(trimmed)) { PrintClaims(); return true; }
        // 同 claim list：本地盘点，不走命令通道。判据共用 ⇒ 差一个空格不会被当成消息。
        if (CoreCommandParser.IsProviderList(trimmed)) { PrintProviders(); return true; }
        if (CoreCommandParser.IsSessionList(trimmed)) { PrintSessions(); return true; }

        var (parsed, parseError) = CoreCommandParser.Parse(trimmed, Current?.SessionId);
        if (parseError is not null) { _out.WriteLine(parseError); return true; }

        // 不是命令形态 ⇒ 当作一条消息发给当前会话。
        var command = parsed ?? AsMessage(trimmed);
        if (command is null) return true;   // AsMessage 已经印过引导语

        return await DispatchAsync(command, ct);
    }

    // 把一行普通文本变成「发给当前会话的消息」。没会话时印的引导语要一次把两步都讲完。
    private CoreCommand? AsMessage(string text)
    {
        if (Current is not null)
            return new CoreCommand.SessionSend(Current.SessionId, [new Block.TextBlock(text)]);

        _out.WriteLine(_host.Providers.List().Count == 0
            ? "还没有配置提供商。先执行 `provider set <id> --key <key>`，再执行 `session create` 建会话。"
            : "还没有会话。执行 `session create` 建一个。");
        return null;
    }

    // 按命令种类决定「谁做动作」与「怎么印」；这段 switch 只管控制台形态，语义全在 dispatcher。
    private async Task<bool> DispatchAsync(CoreCommand command, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var result = await _dispatcher.DispatchAsync(command, id, awaitCompletion: true, ct);

            if (result.Ack is Ack.Rejected rejected)
            {
                _out.WriteLine(rejected.Reason);
                return true;
            }

            var accepted = (Ack.Accepted)result.Ack;

            switch (command)
            {
                case CoreCommand.ProviderUpsert:
                    _out.WriteLine($"✓ 提供商 '{((CoreCommand.ProviderUpsert)command).Id}' 已保存"
                        + $"（{accepted.Note}）。");
                    break;

                // 删成功要吭一声：Accepted.Reply 是 null，不加这条 case 会落进 default 一声不吭。
                case CoreCommand.ProviderDelete del:
                    _out.WriteLine($"✓ 提供商 '{del.Id}' 已删除。");
                    break;

                case CoreCommand.SessionCreate:
                    // 控制台的「当前会话」只在这里（控制台自己的动作）被设。
                    Current = _host.Activated.First(h => h.SessionId == accepted.SessionId);
                    _out.WriteLine($"✓ 会话 {accepted.SessionId} 已创建。");
                    break;

                // 轴改了立刻重印状态（策略那一行在 PrintStatus 里）。
                // 印的是命令带的会话 id，不是 Current：axis set 可指向别的会话，且 Current 可能为 null。
                case CoreCommand.SessionSetAxis set:
                    _out.WriteLine($"✓ {set.SessionId} 的轴已更新");
                    if (Current is not null && Current.SessionId == set.SessionId) PrintStatus();
                    break;

                // 改绑也是「立刻重印」那一类：右栏显示的模型选择刚变了。
                // ⚠️ Note 里是 `provider / model`——**不是**「请求已经换过去了」：
                // 在途那一步跑的还是旧供应商（见 HandleSessionConfigure 的注释）。
                case CoreCommand.SessionConfigure cfg:
                    _out.WriteLine($"✓ {cfg.SessionId} 的模型选择已更新（{accepted.Note}）");
                    if (Current is not null && Current.SessionId == cfg.SessionId) PrintStatus();
                    break;

                // 声明成功要吭一声：Accepted.Reply 是 null，否则用户分不清「占了」与「没反应」。
                case CoreCommand.ClaimAcquire acquire:
                    _out.WriteLine($"✓ 已占 {string.Join("、", acquire.Scope)}");
                    break;

                case CoreCommand.ClaimRelease release:
                    _out.WriteLine($"✓ 已释放 {string.Join("、", release.Scope)}");
                    break;

                // 连通性自检的结果在 Note 里（成功与「通了但不通」都走 Accepted——
                // 按 03 §3，「连不上」是业务结果不是命令不合法，见 HandleProviderTest）。
                case CoreCommand.ProviderTest test:
                    _out.WriteLine(accepted.Note ?? $"提供商 '{test.Id}' 已测试。");
                    break;

                default:
                    PrintReply(result.Reply);
                    break;
            }

            return true;
        }
        catch (DispatchException ex)
        {
            // 业务失败（模型 400 / 上游 503）：一行人话，REPL 继续活着。这不是 rejected。
            _out.WriteLine($"✗ {ex.Message}");
            return true;
        }
        catch (ModelCallFailedException ex)
        {
            // 建会话 / 解析适配器阶段的失败同样一行人话。
            _out.WriteLine($"✗ {ex.Message}");
            return true;
        }
    }

    private void PrintReply(ModelReply? reply)
    {
        if (reply is null) return;

        if (reply.Refused)
        {
            _out.WriteLine("⚠️ 模型拒绝了这次请求（stop_reason: refusal）。");
            return;
        }

        _out.WriteLine($"⏺  {SessionQueries.ModelBody(reply.Blocks)}");

        // 三条链路各有截断用词：max_tokens / length / incomplete；未知词一律不提示。
        if (reply.StopReason is "max_tokens" or "length" or "incomplete")
            _out.WriteLine($"（输出被截断了：{reply.StopReason}）");
    }

    private void PrintHistory()
    {
        if (Current is null) { _out.WriteLine("还没有会话。用 `session create` 建一个。"); return; }

        var timeline = SessionQueries.HistoryOf(_host, Current);
        if (timeline.Count == 0) { _out.WriteLine("（历史为空）"); return; }

        foreach (var evt in timeline)
        {
            var message = (TimelineEvent.MessageEvent)evt;
            _out.WriteLine(message.From == Speaker.User
                ? $"你  {SessionQueries.UserBody(message.Blocks)}"
                : $"⏺  {SessionQueries.ModelBody(message.Blocks)}");
        }
    }

    private void PrintStatus()
    {
        if (Current is null) { _out.WriteLine("还没有会话。用 `session create` 建一个。"); return; }

        // 走 SessionQueries：直接读磁盘会在步进行中报 Idle。
        var state = SessionQueries.StateOf(_host, Current);
        _out.WriteLine($"会话 {Current.SessionId} · {state.Status} · 第 {state.Turn} 步");

        // 模型来自 spec（建会话时冻结），effort 来自轴（会变）——两个来源，别混。
        // `!` 与索引器是 Load / Create 的契约（CompleteAxes 保证 Axes 非空且五键齐全）。
        _out.WriteLine($"模型 {Current.Spec.Model.ModelId} · effort {state.Axes![Axes.Effort]}");
        _out.WriteLine("策略 " + string.Join(" · ",
            Axes.Names.Select(n => $"{n}={state.Axes!.GetValueOrDefault(n, "?")}")));
        _out.WriteLine($"目录 {Current.Spec.WorkingDirectory}");
    }

    private void PrintClaims()
    {
        // 声明落在工作目录，所以要有一个会话来问它的工作目录。
        if (Current is null) { _out.WriteLine("还没有选中会话。"); return; }

        var claims = new ClaimStore(Current.Spec.WorkingDirectory).All();
        if (claims.Count == 0) { _out.WriteLine("（没有声明）"); return; }

        foreach (var c in claims) _out.WriteLine($"{c.SessionId}  {c.Path}");
    }

    // 盘点提供商。**只印 Url，不印 ApiKey**——key 能改不能看（用户裁定）。
    // 端点的来源也要印：这是「生效端点必须说得出来」那条硬约束的落点（EndpointResolver 的契约）。
    private void PrintProviders()
    {
        var providers = _host.Providers.List();
        if (providers.Count == 0) { _out.WriteLine("（还没有配置提供商）"); return; }

        foreach (var p in providers)
        {
            var endpoint = EndpointResolver.Resolve(p.Endpoint);
            _out.WriteLine($"{p.Id} · {p.Protocol} · {p.DefaultModel}"
                + $" · {endpoint.Url}（{EndpointResolver.Describe(endpoint.Source)}）"
                + (p.HasApiKey ? " · 有 key" : " · ⚠️ 无 key"));
        }
    }

    // 盘点本项目下的会话。`已激活` 与 `当前` 是两个不同的事实：
    // 前者是「在内存激活表里」，后者是「控制台的 Current 指着它」——别合并成一个词。
    private void PrintSessions()
    {
        var projectKey = ProjectKey.FromPath(_host.WorkingDirectory);
        var ids = _host.Store.ListSessions(projectKey);
        if (ids.Count == 0) { _out.WriteLine("（本项目下还没有会话）"); return; }

        var activated = _host.Activated.Select(h => h.SessionId).ToHashSet(StringComparer.Ordinal);

        foreach (var sid in ids)
        {
            var spec = _host.Store.TryReadSpec(projectKey, sid);
            var state = _host.Store.ReadState(projectKey, sid);

            _out.WriteLine($"{sid} · 第 {state.Turn} 步 · {spec?.Model.ModelId ?? "?"}"
                + (activated.Contains(sid) ? " · 已激活" : "")
                + (sid == Current?.SessionId ? " · 当前" : ""));
        }
    }
}

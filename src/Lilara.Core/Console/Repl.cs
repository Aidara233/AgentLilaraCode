using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Records;
using Lilara.Core.Session;

namespace Lilara.Core.Console;

/// <summary>
/// 命令面（04 §1.1）。⚠️ **它不是头**：同步收发、没有视图、不订阅、不流式渲染。
///
/// <para>⚠️ **它的新形状**：它现在是控制面的一个**客户端适配器**——
/// 把一行文本解析成 <see cref="CoreCommand"/>，交给 <see cref="CommandDispatcher"/>，
/// 再把回执**格式化**成人看的字。命令的语义**一条都不在这里**（那些在 dispatcher 里，
/// 头走同一套）。这里只剩两样控制台专有的东西：
/// ① **文本解析**（`--flag value` 那种人写风格，含它那几道守卫）；
/// ② **显示**（回执与结果怎么印）。</para>
///
/// <para>⚠️ 它因此**减掉的只有视图**：命令是全部，事件不订阅，快照按需拉。</para>
/// </summary>
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

    /// <summary>
    /// ⚠️ **这是控制台自己的概念**，不是核心的状态。核心只知道「激活表」。
    /// `session.activate` **不许**写它——否则头点一下 B，用户在控制台敲的下一句就跑 B 去了。
    ///
    /// <para>⚠️ setter 会**顺带登记进宿主激活表**。这不是顺手做好事，是**正确性**：
    /// 不登记的话，dispatcher 找不到它就会从磁盘**另载入一个实例**，
    /// 于是同一个会话有两个 `SessionHandle`（`State` 是可变属性）⇒ 状态分叉。
    /// 宿主的 `TryGetActivated` 本来就承诺「同一会话同一个实例」，
    /// 而控制台是唯一能绕过 `Activate()` 直接塞 handle 的地方。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 「这条命令可能改变提供商集合吗」——**纯控制台**的显示刷新判据
    /// （`provider set` 之后要重印端点那一行）。
    ///
    /// ⚠️ **它不再是核心的缓存失效判据**。失效点是 `CommandDispatcher` 的 `provider.upsert`
    /// ——挂在字符串判据上会让线上的 `provider.upsert` 走不到它，缓存就**静默失效**。
    /// 换句话说：这个谓词现在只影响**一行字印不印**，不影响正确性。
    /// </summary>
    public static bool IsProviderSetCommand(string line) =>
        line.Trim().StartsWith("provider set ", StringComparison.Ordinal);

    public async Task<bool> ExecuteAsync(string line, CancellationToken ct)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return true;

        // ⚠️ 只有 `quit` / `exit` 是控制台**本地**的命令——退出这个循环是进程的事，
        // 不是核心命令集的一部分（头没有「退出」这条）。
        if (trimmed is "quit" or "exit") return false;

        // 查询走快照那一层（03 §8.1：控制台只用「命令 + 快照」两层）。
        if (trimmed == "history") { PrintHistory(); return true; }
        if (trimmed == "status") { PrintStatus(); return true; }

        var (parsed, parseError) = CoreCommandParser.Parse(trimmed);
        if (parseError is not null) { _out.WriteLine(parseError); return true; }

        // ⚠️ 不是命令形态 ⇒ 它是**一条消息**，发给当前会话。这是控制台的主用法。
        var command = parsed ?? AsMessage(trimmed);
        if (command is null) return true;   // AsMessage 已经印过引导语

        return await DispatchAsync(command, ct);
    }

    /// <summary>
    /// 把一行普通文本变成「发给当前会话的消息」。
    /// ⚠️ 引导语要**一次把两步都讲完**：只提「没配提供商」的话，
    /// 配了提供商但没建会话的人看不到下一步；只提「没会话」则反之。
    /// 用户会卡在回环里（01 §8.1：核心不替你建会话）。
    /// </summary>
    private CoreCommand? AsMessage(string text)
    {
        if (Current is not null)
            return new CoreCommand.SessionSend(Current.SessionId, [new Block.TextBlock(text)]);

        _out.WriteLine(_host.Providers.List().Count == 0
            ? "还没有配置提供商。先执行 `provider set <id> --key <key>`，再执行 `session create` 建会话。"
            : "还没有会话。执行 `session create` 建一个。");
        return null;
    }

    /// <summary>
    /// 按命令的种类决定「谁做那个动作」与「怎么印」。
    /// ⚠️ 这段 switch **只负责控制台的形态**——命令的语义全在 dispatcher 里。
    /// </summary>
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

                case CoreCommand.SessionCreate:
                    // ⚠️ 控制台的「当前会话」在这里被设——**只有控制台自己的动作**能设它。
                    Current = _host.Activated.First(h => h.SessionId == accepted.SessionId);
                    _out.WriteLine($"✓ 会话 {accepted.SessionId} 已创建。");
                    break;

                // ⚠️ 轴改了要**立刻重印状态**——策略那一行就在 `PrintStatus` 里，
                // 而用户在控制台改完轴最想知道的就是「现在是什么」。
                //
                // ⚠️ 印的是**命令带的那个会话 id**，不是 `Current`：`axis set` 可以指向
                // 任意一个已存在的会话。用 `Current!.SessionId` 有两个真缺陷——
                // ① 改的是别的会话时印错人；② `Current` 为 null（核心起来时没选中会话，
                // 但盘上有会话可达）时**直接 NRE**，而它不在下面那两个 catch 里，会掀翻 REPL。
                case CoreCommand.SessionSetAxis set:
                    _out.WriteLine($"✓ {set.SessionId} 的轴已更新");
                    // 只有改的**就是**当前会话时重印状态；否则印的是另一个会话的数字，张冠李戴。
                    if (Current is not null && Current.SessionId == set.SessionId) PrintStatus();
                    break;

                default:
                    PrintReply(result.Reply);
                    break;
            }

            return true;
        }
        catch (DispatchException ex)
        {
            // ⚠️ 业务失败（模型 400 / 上游 503）——一行人话，REPL 继续活着。
            // 它**不是** rejected：命令没写错，是这次没成。
            _out.WriteLine($"✗ {ex.Message}");
            return true;
        }
        catch (ModelCallFailedException ex)
        {
            // 建会话 / 解析适配器阶段的失败仍然是「这一步没成」，同样一行人话。
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

        // ⚠️ 三条链路**各有各的截断用词**：Claude 说 max_tokens、Chat 说 length、
        // Responses 说 incomplete。只认第一个的话，另外两条的截断会**完全不提示**——
        // 用户看着「（这一步没有正文输出）」以为是模型没话说，其实是 token 烧完了。
        // ⚠️ 原样比对，不在上游就叫别的词时臆造（未知词一律不提示）。
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

        // ⚠️ 走 SessionQueries（先查内存激活表）——直接读磁盘会在步进行中报 Idle。
        var state = SessionQueries.StateOf(_host, Current);
        _out.WriteLine($"会话 {Current.SessionId} · {state.Status} · 第 {state.Turn} 步");

        // ⚠️ **模型来自 spec（建会话时冻结，不会变），effort 来自轴（会变）——两个来源，别混。**
        // 旧代码这里印的是 `Spec.Model.Effort`，那是**冻结的快照**：`axis set effort` 一改，
        // 它就和请求里实际发的 effort 分叉——控制台说还是旧值。同一个「设了没用」换到**显示层**。
        // ⚠️ `!` 与索引器是 `Load` / `Create` 的契约（`CompleteAxes` 保证 `Axes` 非空且五键齐全），
        // `Stepper` 读 effort 用的是同一套写法。
        _out.WriteLine($"模型 {Current.Spec.Model.ModelId} · effort {state.Axes![Axes.Effort]}");
        _out.WriteLine("策略 " + string.Join(" · ",
            Axes.Names.Select(n => $"{n}={state.Axes!.GetValueOrDefault(n, "?")}")));
        _out.WriteLine($"目录 {Current.Spec.WorkingDirectory}");
    }
}

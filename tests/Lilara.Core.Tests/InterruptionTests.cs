using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **打断的识别与收尾**（`01` §12.8 / §5）。
///
/// <para>⚠️ <b>它收的是三件看着无关的事</b>：<b>询问超时</b>（算了 `deadline` 而没人等）·
/// <b>进程被强杀</b>（记录里一条 <c>tool_use</c> 没有配对的结果）· <b>会话被踢出内存</b>
/// （没有任何东西在替它往下走）。三者的触发条件不同，而**盘上留下的痕迹是同一种**。</para>
///
/// <para>⚠️ <b>断言落在<b>记录</b>与<b>请求</b>上</b>：收尾补的是**事实**
/// （一条说得清的结果落盘），所以这一批与 `StaleTests` 不同——它<b>要看盘</b>。</para>
/// </summary>
public class InterruptionTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "lilara-interrupt-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();
    private readonly Stepper _stepper;
    private readonly SessionHandle _handle;

    public InterruptionTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);

        _store = new SessionStore(Path.Combine(_tmp, "data"));
        _stepper = new Stepper(_store, _adapter);

        _handle = SessionHandle.Create(_store, _work, Model());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 清理失败不该让测试红 */ }
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    };

    private static Block.ToolUseBlock Use(string callId) =>
        new(callId, "read", """{"path":"a.txt"}""");

    /// <summary>
    /// 一条**模型消息**——它装着一个 `tool_use`。
    ///
    /// <para>⚠️ <b>`tool_use` 必须与**这块文本**同处一条消息</b>：`Stepper` 只在
    /// 「回复里有块」时才落那条模型消息（拒答 / 空块不落）。只给一个 `tool_use`
    /// 会让它落不进盘 ⇒ 那个「崩在步中」的形状**根本摆不出来**
    /// （实测栽过：测试绿着，而被测的那条路一步都没走到）。</para>
    /// </summary>
    private static TimelineEvent.MessageEvent Model(string callId) => new()
    {
        Id = Ids.New(), At = CoreClock.Now(), From = Speaker.Model,
        Blocks = [new Block.TextBlock("我去读一下。"), Use(callId)],
    };

    private static TimelineEvent.MessageEvent User() => new()
    {
        Id = Ids.New(), At = CoreClock.Now(), From = Speaker.User,
        Blocks = [new Block.TextBlock("随便说点什么")],
    };

    private static TimelineEvent.ToolResultEvent Result(string callId, string id) => new()
    {
        Id = id, At = CoreClock.Now(), CallId = callId, Content = [new Block.TextBlock("内容")],
    };

    private IReadOnlyList<TimelineEvent> Record =>
        _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId);

    // ---- 一、配对规则（纯函数，不碰盘）----

    /// <summary>
    /// ⚠️ **末尾那一条永远是断裂**——不管它后面有没有东西。而**有配对的那一条不是**。
    /// </summary>
    [Fact]
    public void ADanglingToolUseIsInterrupted_WhetherOrNotATurnFollows()
    {
        var prefix = Interruption.NoteIdPrefix;

        // ① 末尾悬空：`[模型(c1)]`
        Assert.True(Interruption.WasInterrupted([Model("c1")], "c1", prefix));

        // ② 悬空之后是另一个说话的回合：`[模型(c1), 用户]`
        Assert.True(Interruption.WasInterrupted([Model("c1"), User()], "c1", prefix));

        // ③ 配对齐全：`[模型(c1), 结果(c1)]` —— 这**不是**断裂
        Assert.False(Interruption.WasInterrupted(
            [Model("c1"), Result("c1", Ids.New())], "c1", prefix));
    }

    /// <summary>
    /// ⚠️ **收尾补出来的那条<b>就是</b>配对**——判据**认不出**它是补丁。
    ///
    /// <para>⚠️ <b>这不是漏了，是刻意的</b>（2026-10-12 改）：我原先给它加过一条
    /// 「带前缀的结果不算配对」的规则，理由是要让「这次执行被作废了」在记录里读得出来。
    /// 而**那个标记没有做**（未与用户对齐，且它要落盘 = 单向门）⇒ 那条规则成了
    /// **一条认不出自己的判据**：补丁看起来仍然悬空 ⇒ **每一次恢复都再补一条**
    /// （实测：同一条调用被补两次、三次）。</para>
    ///
    /// <para>⚠️ 代价如实说：**「这一次没跑成」在记录里读不出来**，只能靠结果里那句话。
    /// 换来的是**收敛**——收尾跑完之后判据一定答「不是断裂」。</para>
    /// </summary>
    [Fact]
    public void OurOwnRepairResult_CountsAsACounterpart_SoTheRuleConverges()
    {
        var prefix = Interruption.NoteIdPrefix;
        var patched = Result("c1", prefix + Ids.New());

        // 补过之后 ⇒ 不是断裂（**这就是收敛**：下一趟不会再补一条）
        Assert.False(Interruption.WasInterrupted([Model("c1"), patched], "c1", prefix));

        // ⚠️ 而**真正**没有配对的那条照样判真——上面那条不能宽到把「没配对」也放过去。
        Assert.True(Interruption.WasInterrupted([Model("c1")], "c1", prefix));

        // ⚠️ 前缀只是**id 的形状**（它仍然要留着：`Recovery` 拿它当「这条是我补的」的痕迹），
        // 判定本身**不再看它**——所以一条**没有前缀**的普通结果当然也算配对。
        Assert.False(Interruption.WasInterrupted(
            [Model("c1"), Result("c1", Ids.New())], "c1", prefix));
    }

    /// <summary>
    /// ⚠️ **结果落在调用<b>前面</b> ⇒ 仍然算配对**——那是「批次的落盘顺序没有保证」
    /// （`HistoryLog.AppendMany` 那条既有注释）在**这条规则**上的形态。
    ///
    /// <para>⚠️ 而它与「另一个回合」的边界是**两件事**，别混：前者判**不是断裂**，
    /// 后者判**断裂**。所以扫到末尾**不会**自动判真——末尾也要回头找过那条配对。</para>
    /// </summary>
    [Fact]
    public void AResultThatLandedBeforeItsCall_StillCounts()
    {
        var prefix = Interruption.NoteIdPrefix;
        var pair = Result("c1", Ids.New());

        // 配对在前、调用在后（例如那条模型消息被**回收**之后又重放了一遍）
        Assert.False(Interruption.WasInterrupted([pair, Model("c1")], "c1", prefix));

        // 末尾悬空，而**前面**有同一批的配对结果 ⇒ **不该**被它救活
        // （`consumed` 是按 `callId` 记的，而 `callId` 在一次会话里会重复出现）
        Assert.True(Interruption.WasInterrupted([pair, User(), Model("c1")], "c1", prefix));
    }

    /// <summary>
    /// ⚠️ **记录里根本没有这条调用 ⇒ 假**。「找不到」与「悬着」是两件事：
    /// 前者**不该**让收尾凭空补一条结果。
    /// </summary>
    [Fact]
    public void AnUnknownCallId_IsNotInterrupted()
    {
        Assert.False(Interruption.WasInterrupted(
            [Model("c1"), Result("c1", Ids.New())], "c99", Interruption.NoteIdPrefix));
    }

    // ---- 二、崩在步中：收尾补的那条 ----

    /// <summary>
    /// ⚠️ **本批的核心一条**：记录已经落到了那一步的模型消息，而 `state.json` 还落后一格
    /// （崩在两次写之间）⇒ 下一次带输入的推进之前，**补一条说得清的结果**。
    ///
    /// <para>⚠️ 三个断言各自钉一件事，**缺一个都还有别的东西能瞒过去**：
    /// ① id <b>带前缀</b>（否则它看起来像真的跑完了）；② `turn` 与记录条数<b>对上了</b>
    /// （否则那一步永远处于「落后一格」）；③ 内容说的是**没有执行**（否则模型会以为有结果）。</para>
    /// </summary>
    [Fact]
    public async Task ACrashMidStep_IsRepairedWithANote_AndTheTurnCatchesUp()
    {
        // 手工摆出那次崩，**形状照 `Stepper` 真实写下的那两次写**：
        //   ① 记录里已经有**用户那条**（`send` 落的）+ 模型消息（含 `tool_use`）；
        //   ② `state.json` 里 `Turn` 还是**上一格**，而 `PendingIntent` 说
        //      「那个 `Turn` 已经算过、欠 `c1` 一条结果」。
        // ⚠️ **用户那条必须在**：生产里 `send` 是「追加记录 + 入队」一次做完的
        // （`HandleSessionSendAsync` 里紧挨着两行），崩不出「用户消息悬在末尾」那种状态。

        // ⚠️ **不许为了摆这个形状去退 `SessionHandle.State.Turn`**：`Stepper` 结尾那一步
        // 会用**内存里的** `Turn` 算它自己的 `PendingIntent.Turn`
        // ⇒ 把那个数弄歪，下一次挂起/落盘写出来的痕迹就是错的（实测栽过：
        // 退了一格之后，`Stepper` 把 `Turn` 写成了它自己的下一格而记录已经多走一格，
        // 于是收尾在**同一个 `Turn`** 上再补一条）。
        // ⇒ 只动那个痕迹字段，`Turn` 留着不动（它就是落后一格的那一格）。
        _handle.UpdateState(s => s with { PendingIntent = new PendingIntent(s.Turn + 1, "c1") });
        _handle.UpdateStateAndPersist(_store, s => s, diskStatus: SessionStatus.Idle);

        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, User());
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, Model("c1"));

        // 用户回来发了一句 ⇒ 收尾在装配请求之前跑。
        // ⚠️ **这一步必须是「带输入的推进」**（队列里真的取到块）——`BeforeStep` 的 ① 那一支
        // 就是挂在它上面的（实测栽过：传了空队列，于是收尾根本没动手，
        // 而那条断言看起来只是「少了一条记录」）。
        _adapter.Reply("我接着说。");
        await _stepper.StepAsync(_handle, [new Block.TextBlock("接着来"), new Block.TextBlock("还有一句")], CancellationToken.None);

        var repair = Assert.Single(Record.OfType<TimelineEvent.ToolResultEvent>());
        Assert.Equal("c1", repair.CallId);
        Assert.StartsWith(Interruption.NoteIdPrefix, repair.Id, StringComparison.Ordinal);
        Assert.Contains("没有执行", TextOf(repair));

        // ⚠️ **`turn` 是「下一次」的 1-based 号，而记录里的非结果条数是「已经跑过几步」**
        // ⇒ 两者差 1，那正是既有语义（`StepperTests.Step_AdvancesTurnCounter`）。
        // 这一步跑完之后的形状：`[用户(那次崩的), 模型, 补的, 用户(这次), 模型]`
        // ⇒ 非结果 **3** 条（`StepAsync` 拿到的 `blocks` 是**队列内容**，它**不**追加用户消息
        // ——那次追加归 `CommandDispatcher.HandleSessionSendAsync`，见 `Stepper` 的类注释）。
        var steps = Record.Count(e => e is not TimelineEvent.ToolResultEvent);
        Assert.Equal(3, steps);
        Assert.Equal(steps, _handle.State.Turn);

        // 收尾只发生一次：痕迹清了。
        Assert.Null(_handle.State.PendingIntent);
    }

    /// <summary>
    /// ⚠️ **幂等**：收尾**不叠加**。跑第二遍时，第一遍补的那条要**被认出来**——
    /// 而这件事**不是**靠痕迹（它已经被清了），是靠**那条结果的 id 前缀**
    /// （`Interruption` 那条规则的实测形态）。
    ///
    /// <para>⚠️ 探针：只测「同一个 handle 收两次」是**不够**的——痕迹一清，
    /// 第二次连门都进不去，那条前缀根本没用上。⇒ 这里**再摆一次**那个痕迹
    /// （模拟「又崩了一次」），让第二次**真的走到补丁那一步**。</para>
    /// </summary>
    [Fact]
    public async Task TheRepairIsNotAppliedTwice()
    {
        // ⚠️ **摆痕迹时**不动 `Turn`：`Stepper` 结尾会把「这一步跑完之后的步数」写下去
        //（`Turn = s.Turn + 1`），而记录里已经多了一条用户 + 一条模型 ⇒ 再写一个更小的数
        // 只会让两者**继续**错位，于是**每一条消息都要补一次**（实测栽过）。
        // ⇒ 判据那一半靠**记录与 `Turn` 的自然差额**（这里就是那笔提前落下的追加）。
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, User());
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, Model("c1"));
        _handle.UpdateStateAndPersist(_store, s => s with
        {
            PendingIntent = new PendingIntent(s.Turn + 1, "c1"),
        }, diskStatus: SessionStatus.Idle);

        _adapter.Reply("第一次。");
        await _stepper.StepAsync(_handle, [new Block.TextBlock("来")], CancellationToken.None);
        Assert.Single(Record.OfType<TimelineEvent.ToolResultEvent>());

        // ⚠️ **再摆一次痕迹**：模拟「同一处又崩一次」。这一趟必须**认得出**上一条是自己补的，
        // 否则它会再补一条（= `R:c1` 出现两次）。
        // ⚠️ **第二次要造的是「另一个回合的末尾又悬了」**：再落一条**模型消息**（它自带
        // 一条悬空的 `tool_use`），并把痕迹指向它。
        // ⚠️ **不必推 `Turn`**：`steps` 刚好多走一格（那条新模型消息），
        // 而 `Turn` 停在上一轮——两个判据各自成立，且都**不是**靠上一轮那个补丁。
        // ⚠️ 那条前缀判据真正吃上劲的地方在**上一轮的收尾那一刻**：记录末尾是
        // `[模型(c1), 补丁(c1)]` —— 末尾仍然悬空，而 `WasInterrupted` 认得出那条补丁
        // ⇒ 答「不是断裂」⇒ **不再补第二条**。
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, Model("c2"));
        _handle.UpdateStateAndPersist(_store, s => s with
        {
            PendingIntent = new PendingIntent(s.Turn + 1, "c2"),
        }, diskStatus: SessionStatus.Idle);

        _adapter.Reply("第二次。");
        await _stepper.StepAsync(_handle, [new Block.TextBlock("再来")], CancellationToken.None);

        // ⚠️ **判据是「一条 <c>c1</c> 一条 <c>c2</c>」**，不是「总共一条」：
        // 第二轮**确实该**补一条（那是一条新的断裂）。这条测试要挡的是
        // **同一条调用被补两次**——而那个形状只可能是「上一轮的补丁没被认出来」。
        var results = Record.OfType<TimelineEvent.ToolResultEvent>().ToList();
        Assert.Single(results, r => r.CallId == "c1");
        Assert.Single(results, r => r.CallId == "c2");
    }

    /// <summary>
    /// ⚠️ **空推进不补**——工具循环的续跑、或某个后台步的尾巴也会推进，
    /// 而**没有人说过话**。那种步去补配对等于**替用户答了他没答的问题**。
    ///
    /// <para>判据取自 `01` §5：「超时/断裂由**任何试图恢复这个会话的动作**发现」，
    /// 而那个动作的判据是**有人真的在使用它**。⚠️ 而 `session.start`（显式启动）
    /// 是**例外**——它本身就是「有人要它动」，所以它走另一条路（那一条留给
    /// 「用户手点继续」的形态）。</para>
    /// </summary>
    [Fact]
    public async Task AnEmptyResume_DoesNotRepairAnything()
    {
        _handle.UpdateState(s => s with { PendingIntent = new PendingIntent(s.Turn + 1, "c1") });
        _handle.UpdateStateAndPersist(_store, s => s, diskStatus: SessionStatus.Idle);
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, User());
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, Model("c1"));

        _adapter.Reply("没新输入。");
        await _stepper.StepAsync(_handle, [], CancellationToken.None);

        // ⚠️ **判据是「没有补那一条」**，不是「痕迹还在」：这一趟是**完整跑完的一步**
        // （它自己有模型调用要落），而 `Stepper` 结尾会把痕迹清掉
        // ——那是「这一步的两个产物都落成了」的正常收尾，与补不补配对无关。
        Assert.Empty(Record.OfType<TimelineEvent.ToolResultEvent>());

        // ⚠️ **再看一眼那条悬空**：它**原样**留在记录里（这正是「空推进不替用户答」的意思）。
        Assert.True(Interruption.WasInterrupted(
            [.. Record], "c1", Interruption.NoteIdPrefix));
    }

    // ---- 三、询问到期 ----

    /// <summary>
    /// ⚠️ **没有任何定时器**（`01` §5「超时是盘面自证」）⇒ 到期只能由**一次带输入的推进**发现。
    /// 发现之后落的是一条**合成的答复**，而且那批调用**每条都要配对**。
    /// </summary>
    [Fact]
    public async Task AnExpiredAsk_IsAnsweredOnTheNextStep_AndEveryCallIsPaired()
    {
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, User());
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, Model("c1"));

        // 手工摆一个**已经过期**的挂起（`Permission` ⇒ 超时后是「拒」）。
        _handle.UpdateStateAndPersist(_store, s => s with
        {
            Status = SessionStatus.WaitingForInput,
            Ask = new PausedAsk(
                "ask-1", AskKind.Permission, "c1", "read", "hash",
                CoreClock.Now() - TimeSpan.FromMinutes(1)),
        }, diskStatus: SessionStatus.WaitingForInput);

        _adapter.Reply("那我换个方式。");
        await _stepper.StepAsync(_handle, [new Block.TextBlock("算了")], CancellationToken.None);

        Assert.Equal(SessionStatus.Idle, _handle.State.Status);
        Assert.Null(_handle.State.Ask);

        var result = Assert.Single(Record.OfType<TimelineEvent.ToolResultEvent>());
        Assert.Equal("c1", result.CallId);
        Assert.Contains("没有人回答", TextOf(result));
    }

    /// <summary>
    /// ⚠️ **没到期就不动手**——`DeadlineAt` 是未来的时刻时，那个会话**还在等**，
    /// 而合成答复等于**替用户提前拒绝**。
    /// </summary>
    [Fact]
    public async Task AnAskThatIsNotDueYet_IsLeftAlone()
    {
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, User());
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, Model("c1"));

        _handle.UpdateStateAndPersist(_store, s => s with
        {
            Status = SessionStatus.WaitingForInput,
            Ask = new PausedAsk(
                "ask-1", AskKind.Permission, "c1", "read", "hash",
                CoreClock.Now() + TimeSpan.FromHours(1)),
        }, diskStatus: SessionStatus.WaitingForInput);

        _adapter.Reply("先聊别的。");
        await _stepper.StepAsync(_handle, [new Block.TextBlock("算了")], CancellationToken.None);

        Assert.NotNull(_handle.State.Ask);
        Assert.Empty(Record.OfType<TimelineEvent.ToolResultEvent>());
    }

    private static string TextOf(TimelineEvent e) =>
        string.Concat(((TimelineEvent.ToolResultEvent)e).Content.OfType<Block.TextBlock>().Select(b => b.Text));
}

/// <summary>
/// **活跃性凭证**（`01` §11.6）——「有没有东西在替它往下走」。
///
/// <para>⚠️ <b>它挡的是实测过的一个症状</b>：被 `session.deactivate` 踢出内存之后，
/// 快照照样把它列出来（`ListSessions` 读盘），而**没有任何东西在推进它**
/// ⇒ 头看到的是一个「活着的死会话」。凭证就是那个「活着」的判据。</para>
/// </summary>
public sealed class LiveSessionsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "lilara-live-" + Guid.NewGuid().ToString("N")[..8]);

    public LiveSessionsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 清理失败不该让测试红 */ }
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    };

    private SessionHandle NewSession()
    {
        var work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(work);
        return SessionHandle.Create(new SessionStore(Path.Combine(_tmp, "data")), work, Model());
    }

    /// <summary>
    /// ⚠️ **三个来源各钉一条**：`IsRunning`（这一刻有一趟在飞）、队列非空、
    /// 答复等着被执行。缺任何一个都会漏掉一整类「有人在动它」。
    /// </summary>
    [Fact]
    public void IsLive_CoversAllThreeSourcesOfWork()
    {
        var live = new LiveSessions();
        var handle = NewSession();
        Assert.False(live.IsLive(handle));           // 干净的新会话：没人在动它

        handle.Enqueue([new Block.TextBlock("排队里的话")]);
        Assert.True(live.IsLive(handle));            // ① 队列非空

        handle.DrainQueue();
        Assert.False(live.IsLive(handle));

        Assert.True(handle.TryEnterStep());
        Assert.True(live.IsLive(handle));            // ② 有一趟在飞
        handle.ExitStep();
        Assert.False(live.IsLive(handle));

        handle.QueueResume(new SessionHandle.PendingResume(
            [], new Session.Tools.ToolExecutor.Reply(
                AskKind.Question, [], [], new Dictionary<string, string>(StringComparer.Ordinal)),
            new PausedAsk("a", AskKind.Question, "c1", "ask", "h", null)));
        Assert.True(live.IsLive(handle));            // ③ 答复等着被执行
    }

    /// <summary>
    /// ⚠️ **凭证与 handle 是两半，缺一不可**：`session.create` 走 `Register` 登记，
    /// **不经过** `Mark` ⇒ 只看凭证会漏掉「刚建出来、队列里已经有话」的会话。
    /// </summary>
    [Fact]
    public void TheCredential_IsCheckedAlongsideTheHandle()
    {
        var live = new LiveSessions();
        var handle = NewSession();

        live.Mark(handle.SessionId);
        Assert.True(live.IsLive(handle));

        live.Clear(handle.SessionId);
        Assert.False(live.IsLive(handle));
    }

    /// <summary>
    /// ⚠️ **`Clear` 与 `Forget` 不是一件事**：前者是「这一趟跑完了」的**配对**动作，
    /// 后者是「这个会话不在内存里了」。用 `Clear` 顶替 `Forget` 会让计数与真实推进次数脱钩。
    /// </summary>
    [Fact]
    public void Forget_RemovesTheCredential_WhileClearOnlyBalancesIt()
    {
        var live = new LiveSessions();
        var handle = NewSession();

        live.Mark(handle.SessionId);
        live.Clear(handle.SessionId);                      // 配平：那一趟跑完了
        Assert.False(live.IsLive(handle.SessionId));

        live.Mark(handle.SessionId);
        live.Forget(handle.SessionId);                     // 踢出内存
        Assert.False(live.IsLive(handle.SessionId));
        Assert.Empty(live.Snapshot());
    }

    /// <summary>
    /// ⚠️ **`session.deactivate` 会撤凭证**——那是「谁在跑」与「谁在内存里」保持一致的落点。
    /// </summary>
    [Fact]
    public void Deactivate_AlsoRevokesTheCredential()
    {
        var host = new CoreHost(Path.Combine(_tmp, "data"), Path.Combine(_tmp, "work"));
        var handle = NewSession();
        host.Register(handle);
        host.Live.Mark(handle.SessionId);

        Assert.True(host.Deactivate(handle.SessionId));
        Assert.False(host.Live.IsLive(handle.SessionId));
    }

    /// <summary>
    /// ⚠️ **「上一次是不是崩了」的真值表**——三个格子，一个也不能错。
    ///
    /// <para>⚠️ <b>方向写反了<b>不报错</b></b>（实测时我第一版就是 `!File.Exists`）：
    /// 「正常退出」会被判成崩溃、「强杀」被判成正常，而两条路的**唯一**可观差别
    /// 就是那句横幅 ⇒ 没有这条测试，**没有任何东西会红**。</para>
    /// </summary>
    [Fact]
    public void TheCrashMarker_ReadsTheTruthTable()
    {
        var root = Path.Combine(_tmp, "marker");
        var work = Path.Combine(_tmp, "marker-work");
        Directory.CreateDirectory(work);

        // ① **全新数据根**（连 `projects/` 都没有）⇒ 不算崩。
        // ⚠️ 这一格是给「第一次启动」的：那时报「上一次崩了」是**一句假话**。
        Assert.False(new CoreHost(root, work).RecoveredFromACrash);

        // ② 用过（有 `projects/`）、而心跳文件**不在** ⇒ 上一次是**正常退出**的。
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        Assert.False(new CoreHost(root, work).RecoveredFromACrash);

        // ③ 心跳文件**在** ⇒ 上一次**没走到那条正常退出的路**（强杀 / 断电）。
        File.WriteAllText(CoreClock.HeartbeatPath(root), "");
        Assert.True(new CoreHost(root, work).RecoveredFromACrash);
    }
}

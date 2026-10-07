using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// **激活是一次 GC 事件**（`01` §8.3 / §12.4 / §11.7）——`00` §4 那四个触发源里
/// 「激活」是第一个被接上的。
///
/// <para>⚠️ 它为什么值钱：`session.deactivate` 会把 handle 连同**回收集合**一起丢掉
/// （`SessionHandle._reclaimed` 是内存字段），而「逐条的回收状态**不落盘**」是刻意的
/// ——它是加载期派生的：**边界 + 策略 ⇒ 结论**。⇒ 今天「重启即重新决定」是
/// 「**重新决定只有你手动喊**」；本条把这些路径接上。</para>
///
/// <para>⚠️ <b>触发源只负责「开门」，回收只有一处</b>：这里断言的是
/// 「激活之后 <c>handle.Reclaimed</c> 是对的」，而算它的是**同一个** `Reclaim.Decide`
/// （`ReclaimTests` 已经把它单独钉住了）。</para>
/// </summary>
public class ActivationReclaimTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-actgc-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-actgcwork-" + Guid.NewGuid().ToString("N"));

    private readonly SessionStore _store;
    private readonly CoreHost _host;

    public ActivationReclaimTests()
    {
        _store = new SessionStore(_root);
        _host = new CoreHost(_root, _work);
        // ⚠️ **造 dispatcher 就是接线**（见 `CommandDispatcher` 的构造器）——
        // 所以这一行不是「顺手造一个」，它就是被测的那件事。丢掉它，下面每条都得红
        // （那正是我们要的：接线漏了不能被静默吞掉）。
        _ = new CommandDispatcher(_host, new EventBus(), h => new Stepper(_store, new Fakes.FakeAdapter()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "claude",
        ModelId = "claude-opus-5",
        Protocol = Protocols.ClaudeMessages,
        Effort = "high",
        MaxTokens = 16000,
    };

    /// <summary>
    /// 建一个**会落盘的**会话，往里写 <paramref name="results"/> 条工具结果
    /// （多写 <see cref="Reclaim.ProtectedRecentResults"/> 条，好让保护带之外有东西可收），
    /// 然后把它**从激活表里摘掉**——模拟「重启」或「上次 deactivate 过」。
    ///
    /// <para>返回时：盘上有 <paramref name="results"/> 条结果，而**内存里一条都没有**。</para>
    /// </summary>
    private (string ProjectKey, string SessionId) SeededSession(int results)
    {
        var created = SessionHandle.Create(_store, _work, Model());
        var (pk, sid) = (created.ProjectKey, created.SessionId);

        var events = new List<TimelineEvent>();
        var blobs = new BlobStore(_root);
        for (var i = 0; i < results; i++)
            events.Add(new TimelineEvent.ToolResultEvent
            {
                Id = $"r{i}",
                At = CoreClock.Now(),
                CallId = $"call-{i}",
                // 走**写入侧**的规则（含进库），与生产路径同形。
                Content = Reclaim.Materialize([new Block.TextBlock($"结果 {i}")], blobs),
            });

        _store.AppendEvents(pk, sid, events);

        // 摘干净：`Create` 把它登记进了激活表（走的是 `Register`）。
        _host.Deactivate(sid);
        Assert.False(_host.TryGetActivated(sid, out _), "前提：摘掉之后激活表里没有它");

        return (pk, sid);
    }

    // ---- 四条触发路径，一条都不能漏 -----------------------------------------

    /// <summary>
    /// ⚠️ **`CoreHost.Activate` 是主路径**：头条会话时**根本不发 `session.activate`**
    /// （它走控制面 `TryFind` 的 miss 回落）。挂在命令上会让主路径一次都碰不到。
    /// </summary>
    [Fact]
    public void Activate_RecomputesOnTheMainPath()
    {
        var (pk, sid) = SeededSession(Reclaim.ProtectedRecentResults + 3);

        var handle = _host.Activate(pk, sid);

        Assert.NotEmpty(handle.Reclaimed);
    }

    /// <summary>
    /// `TryGetActivated` 已经是 `Activate` 的同一条路（`ControlServer.TryFind` 与
    /// `CommandDispatcher.TryFind` 的 miss 回落都汇到 `Activate`）——这里钉住
    /// 「先 miss、再 Activate」这个**整体行为**，因为那才是调用方看到的形状。
    /// </summary>
    [Fact]
    public void Activate_OnASnapshotMiss_Recomputes()
    {
        var (pk, sid) = SeededSession(Reclaim.ProtectedRecentResults + 3);

        Assert.False(_host.TryGetActivated(sid, out _));
        var handle = _host.Activate(pk, sid);
        Assert.NotEmpty(handle.Reclaimed);
    }

    /// <summary>
    /// 控制台那条命令（`session.activate`）走的是 `HandleSessionActivate` → `TryFind`
    /// → miss → `Activate` ⇒ 同一个触发点。
    /// </summary>
    [Fact]
    public async Task ActivateCommand_Recomputes()
    {
        var (pk, sid) = SeededSession(Reclaim.ProtectedRecentResults + 3);

        var d = new CommandDispatcher(_host, new EventBus(),
            h => new Stepper(_store, new Fakes.FakeAdapter()));
        var r = await d.DispatchAsync(new CoreCommand.SessionActivate(sid), "a1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.True(_host.TryGetActivated(sid, out var handle));
        Assert.NotEmpty(handle.Reclaimed);
    }

    // ---- 已激活的不重算（幂等 + 不拿假的「激活」事件污染） -------------------

    /// <summary>
    /// ⚠️ **已经激活的不再触发**。`GetOrAdd` 在并发下可能**白造一个再丢掉**，
    /// 所以判据是 `ReferenceEquals`（存进去的是不是我造的那个），不是「进了这个方法」。
    /// </summary>
    [Fact]
    public void Activate_OnAnAlreadyActivatedSession_DoesNotRecompute()
    {
        var (pk, sid) = SeededSession(Reclaim.ProtectedRecentResults + 3);

        var first = _host.Activate(pk, sid);
        var computed = first.Reclaimed;

        // 手工把它清空——若第二次调用**真的重算**，它会被填回来。
        first.SetReclaimed(new HashSet<string>());

        var second = _host.Activate(pk, sid);

        Assert.Same(first, second);
        Assert.Empty(second.Reclaimed);
        Assert.NotEmpty(computed);   // 第一次确实算过（否则上面那条断言是空的）
    }

    // ---- 「会随别的东西变」的那些：只钉形状，不钉具体条数 --------------------

    /// <summary>
    /// ⚠️ **结果条数不进断言**（本仓栽过：`Assert.Contains("回收了", …)` 因为内容变了就红）。
    /// 这里钉的是**关系**：收的条数 = 保护带之外那些**内容够**的结果条数。
    /// </summary>
    [Fact]
    public void Activate_ReclaimsExactlyWhatDecideSays()
    {
        var (pk, sid) = SeededSession(Reclaim.ProtectedRecentResults + 3);

        // 拿同一个判定独立算一遍（**同一个谓词**，不是另写一份判据）。
        var expected = Reclaim.Decide(_store.ReadTimeline(pk, sid), new BlobStore(_root));

        var handle = _host.Activate(pk, sid);

        Assert.True(expected.SetEquals(handle.Reclaimed),
            $"激活算出来的应与 Decide 逐条相同：期望 {expected.Count} 条，实得 {handle.Reclaimed.Count} 条");
    }

    /// <summary>
    /// 一条工具结果都没有的会话（纯 chat）⇒ 集合为空，且**不抛**。
    /// </summary>
    [Fact]
    public void Activate_OnASessionWithNoToolResults_IsEmptyAndDoesNotThrow()
    {
        var created = SessionHandle.Create(_store, _work, Model());
        _host.Deactivate(created.SessionId);

        var handle = _host.Activate(created.ProjectKey, created.SessionId);

        Assert.Empty(handle.Reclaimed);
    }

    /// <summary>
    /// ⚠️ **deactivate → 再激活**要重新算出来——这正是本批修的那个洞
    /// （`deactivate` 把集合整个丢掉，而「逐条的状态不落盘」⇒ 没人替它重新决定）。
    /// </summary>
    [Fact]
    public void DeactivateThenActivate_Recomputes()
    {
        var (pk, sid) = SeededSession(Reclaim.ProtectedRecentResults + 3);

        var first = _host.Activate(pk, sid);
        Assert.NotEmpty(first.Reclaimed);

        _host.Deactivate(sid);
        var again = _host.Activate(pk, sid);

        Assert.NotEmpty(again.Reclaimed);
    }
}

using Lilara.Core.Kernel;
using Lilara.Core.Adapters;
using Lilara.Core.Persistence;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

public class SessionHandleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-handle-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;

    public SessionHandleTests() => _store = new SessionStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "claude",
        ModelId = "claude-opus-5",
        Protocol = Protocols.ClaudeMessages,
        Effort = "high",
        MaxTokens = 16000,
    };

    [Fact]
    public void Create_WritesSpecAndStateToDisk()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.True(File.Exists(handle.SpecPath));
        Assert.True(File.Exists(handle.StatePath));
        Assert.Equal(0, handle.State.Turn);
        Assert.Equal("Idle", handle.State.Status);
    }

    [Fact]
    public void Create_RecordsTheWorkingDirectoryInTheSpec()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.Equal(Path.GetFullPath(@"C:\work"), handle.Spec.WorkingDirectory);
    }

    [Fact]
    public void Load_RestoresSpecAndState()
    {
        var created = SessionHandle.Create(_store, @"C:\work", Model());

        var loaded = SessionHandle.Load(_store, created.ProjectKey, created.SessionId);

        // record 自动相等对集合成员用引用相等，故不能整记录比较 Pending。
        Assert.Equal(created.Spec, loaded.Spec);
        Assert.Equal(created.State.SessionId, loaded.State.SessionId);
        Assert.Equal(created.State.Status, loaded.State.Status);
        Assert.Equal(created.State.Turn, loaded.State.Turn);
        Assert.Empty(loaded.State.Pending);
    }

    [Fact]
    public void SaveState_PersistsTurnCounter()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());
        handle.State = handle.State with { Turn = 3 };

        handle.SaveState(_store);

        Assert.Equal(3, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }

    // setAxis 不拿会话闸（要求即时生效），其读-改-写必须与 Stepper 的 Turn 自增共用一个 UpdateState 临界区。
    [Fact]
    public async Task UpdateState_TwoWriters_AxisAndTurn_BothSurvive()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());

        const int rounds = 200_000;
        using var barrier = new Barrier(2);

        // 角色 A：复刻 `session.setAxis` 的读-改-写
        var setAxisRole = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                barrier.SignalAndWait();
                handle.UpdateState(s => s with
                {
                    Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal)
                    { [Axes.Safety] = i % 2 == 0 ? "dev" : "prod" },
                });
            }
        });

        // 角色 B：复刻 `Stepper` 收尾的读-改-写
        var stepperRole = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                barrier.SignalAndWait();
                handle.UpdateState(s => s with { Status = "Idle", Turn = s.Turn + 1 });
            }
        });

        // 必须 await Task.WhenAll：Task.WaitAll 触发 xUnit1031（-warnaserror 下即 error）。
        await Task.WhenAll(setAxisRole, stepperRole);

        Assert.Equal(rounds, handle.State.Turn);                        // Turn 一次都没丢
        Assert.Equal(Axes.Names.Count, handle.State.Axes!.Count);        // 轴表没被盖成残缺的一份
        Assert.Contains(handle.State.Axes[Axes.Safety], new[] { "dev", "prod" });
    }

    [Fact]
    public void Create_ProducesDistinctSessionIdsPerCall()
    {
        var a = SessionHandle.Create(_store, @"C:\work", Model());
        var b = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.NotEqual(a.SessionId, b.SessionId);
        Assert.Equal(2, _store.ListSessions(a.ProjectKey).Count);
    }

    [Fact]
    public void Create_LeavesNoListableButUnloadableSessionIfItDiesMidway()
    {
        // 模拟「崩在两次写之间」；只调 Create() 测不出顺序，必须手工造出中间态。
        var projectKey = Lilara.Core.Kernel.ProjectKey.FromPath(@"C:\work");
        var mid = _store.SessionDirectory(projectKey, "999999999999");

        // 只写 state，不写 spec —— 正是「先 state 后 spec」崩溃后的残骸。
        Directory.CreateDirectory(mid);
        _store.WriteState(projectKey, new SessionState
        {
            SessionId = "999999999999",
            Status = "Idle",
            Turn = 0,
            Pending = [],
        });

        // 隐形：ListSessions 看不到它，于是没人会去 Load 它。
        Assert.Empty(_store.ListSessions(projectKey));
        Assert.Null(_store.TryReadSpec(projectKey, "999999999999"));
    }

    // 轴的 effort 必须与 spec 一致，否则 Stepper 从轴读会与建会话意图分叉（出生即分叉）。
    // 种子只有落盘才活得过重启，否则 Load 会补默认值把 low 静默改回 high。
    [Fact]
    public void Create_SeedsTheEffortAxisFromTheSpec_AndItSurvivesAReload()
    {
        var created = SessionHandle.Create(_store, @"C:\work", Model() with { Effort = "low" });

        Assert.Equal("low", created.State.Axes![Axes.Effort]);

        var reloaded = SessionHandle.Load(_store, created.ProjectKey, created.SessionId);
        Assert.Equal("low", reloaded.State.Axes![Axes.Effort]);
    }

    // spec 里表里没有的 effort 拼写错误不能种进轴：头会渲染出一个没有任何选项被选中的下拉。
    [Fact]
    public void Create_WithAnUnknownEffortInTheSpec_FallsBackToTheTableDefault()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model() with { Effort = "xhihg" });

        Assert.Equal(Axes.Defaults()[Axes.Effort], handle.State.Axes![Axes.Effort]);
    }

    // spec 里的大小写变体要规范化再入轴：ToEffort 的 switch 区分大小写，头的下拉按小写匹配。
    [Fact]
    public void Create_NormalizesTheEffortCaseFromTheSpec()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model() with { Effort = "HIGH" });

        Assert.Equal("high", handle.State.Axes![Axes.Effort]);
    }

    // 轴与 Turn 住同一份 State，别拆成两份（拆了 Turn 会被抹成 0 且无报错）。
    // 判据用 Turn 而不是 Status——磁盘上写过的状态只有 "Idle"，抹掉它这条测试也发现不了。
    [Fact]
    public void SaveAxes_WritesTheAxisWithoutDisturbingTurn()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());
        handle.State = handle.State with
        {
            Turn = 3,
            Axes = new Dictionary<string, string>(handle.State.Axes!, StringComparer.Ordinal)
            {
                [Axes.Safety] = "prod",
            },
        };

        handle.SaveAxes(_store);

        var reread = _store.ReadState(handle.ProjectKey, handle.SessionId);
        Assert.Equal(3, reread.Turn);
        Assert.Equal("prod", reread.Axes![Axes.Safety]);
    }
}

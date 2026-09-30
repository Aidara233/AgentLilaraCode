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

        // ⚠️ 不能写 Assert.Equal(created.State, loaded.State)：Pending 是集合，
        // record 的自动相等对集合成员用引用相等，那条断言必挂。
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
        // ⚠️ 这条测试直接模拟「崩在两次写之间」，验证残留物是**隐形**的而不是致命的。
        // 只调 Create() 测不出顺序——必须手工造出那个中间态。
        var projectKey = Lilara.Core.Projects.ProjectKey.FromPath(@"C:\work");
        var mid = _store.SessionDirectory(projectKey, "999999999999");

        // 只写 state，不写 spec —— 正是「先 state 后 spec」崩溃后的残骸
        Directory.CreateDirectory(mid);
        _store.WriteState(projectKey, new SessionState
        {
            SessionId = "999999999999",
            Status = "Idle",
            Turn = 0,
            Pending = [],
        });

        // 隐形：ListSessions 看不到它，于是没人会去 Load 它
        Assert.Empty(_store.ListSessions(projectKey));
        Assert.Null(_store.TryReadSpec(projectKey, "999999999999"));
    }

    /// <summary>
    /// ⚠️ **轴的 effort 从 spec 播种**，不是取表里的硬编码默认值 `high`。
    /// 不播种的话，提供商配 `effort=low` 时会造出「Spec 说 low、轴说 high」的会话——
    /// **出生即分叉**，而 `Stepper` 从轴读 ⇒ 用户从没设过轴，effort 却变了。
    ///
    /// <para>⚠️ 第二次 <see cref="SessionHandle.Load"/> 是**这条测试的另一半**：
    /// 种子只有落盘才活得过重启（`Create` 里那一份 `state` 直接被 `WriteState`）；
    /// 不落盘的话，重启后 `State.Axes` 是 null，`Load` 会补一份默认值把 `low`
    /// **静默改回** `high`。</para>
    /// </summary>
    [Fact]
    public void Create_SeedsTheEffortAxisFromTheSpec_AndItSurvivesAReload()
    {
        var created = SessionHandle.Create(_store, @"C:\work", Model() with { Effort = "low" });

        Assert.Equal("low", created.State.Axes![Axes.Effort]);

        var reloaded = SessionHandle.Load(_store, created.ProjectKey, created.SessionId);
        Assert.Equal("low", reloaded.State.Axes![Axes.Effort]);
    }

    /// <summary>
    /// ⚠️ spec 里的 effort 可能是**表里没有的拼写错误**（`ClaudeRequestBuilder.ToEffort` 会为它抛异常）。
    /// 那种值**不能**种进轴：头会渲染出一个「没有任何选项被选中」的下拉，
    /// 比「悄悄用默认值」更坏——用户看到的是一个无法解释的空档。
    /// </summary>
    [Fact]
    public void Create_WithAnUnknownEffortInTheSpec_FallsBackToTheTableDefault()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model() with { Effort = "xhihg" });

        Assert.Equal(Axes.Defaults()[Axes.Effort], handle.State.Axes![Axes.Effort]);
    }

    /// <summary>
    /// ⚠️ **轴与 `Turn` / `Status` 住同一份 `State`**（`SessionState.Axes`）。
    /// 这条守的是「别把它们拆成两份」：一旦 `SaveAxes` 改成从轴另构造一个 `SessionState`，
    /// `Turn` / `Status` 会被抹成 0 / Idle，而这一步**没有任何报错**——
    /// 只是重启后会话「失忆」，且磁盘上看不出是哪次写坏的。
    /// </summary>
    [Fact]
    public void SaveAxes_WritesTheAxisWithoutDisturbingTurnAndStatus()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());
        handle.State = handle.State with
        {
            Turn = 3,
            Status = "Running",
            Axes = new Dictionary<string, string>(handle.State.Axes!, StringComparer.Ordinal)
            {
                [Axes.Safety] = "prod",
            },
        };

        handle.SaveAxes(_store);

        var reread = _store.ReadState(handle.ProjectKey, handle.SessionId);
        Assert.Equal(3, reread.Turn);
        Assert.Equal("Running", reread.Status);
        Assert.Equal("prod", reread.Axes![Axes.Safety]);
    }
}

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

    /// <summary>
    /// ⚠️ **I2 的交错，用「两条 `Task` + `Barrier`」的形状钉住**（终审 I2）。
    ///
    /// <para>两个角色各自复刻一处**真实写点**：
    /// **A = `session.setAxis`**（读 `State` → `with { Axes }` → 写回），
    /// **B = `Stepper` 收尾**（读 `State` → `with { Status, Turn+1 }` → 写回）。
    /// 两处**不共享别的锁**——`setAxis` 刻意不拿会话闸（`01` §3.5 要求即时生效），
    /// 而 `Stepper` 拿的那把闸**挡不住 `setAxis`**。</para>
    ///
    /// <para>⚠️ **旧写法（`handle.State = handle.State with { … }`）下这条实测红**：
    /// 200000 轮里 `Turn` 只剩约 **11.6 万**——**约四成更新被整份盖掉**
    ///（每一次丢的是一次「后写的把先写的整份替换掉」）。
    /// 现在两处都走 `UpdateState`（同一把锁）⇒ 两个字段都不丢。</para>
    ///
    /// <para>⚠️ **判据必须是「`Turn` 恰好等于轮数」**，不是区间：这两个角色**只**经 `UpdateState`
    /// 写 `State`、没有别的写点，所以精确值可以要求；用区间会掩盖「丢一点也算过」。</para>
    /// </summary>
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

        // ⚠️ **`await Task.WhenAll`，不是 `Task.WaitAll`**——后者在本项目里是一条
        // **编译错误**（xUnit1031：测试方法不得用阻塞式 task 操作，`-warnaserror` 下即 error）。
        // ⚠️ 断言与轮数（200_000）**一字未动**：这条测试的意图是「两个写者交错 20 万轮、
        // `Turn` 一次都不丢」，换掉等待方式不改变那个意图。
        // ⚠️ **别改回 `Task.WaitAll`**：`dotnet build` 增量时分析器不重跑，
        // 于是「0 警告」在改回去之后**照样打印得出来**——而全量构建是红的。
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
    /// ⚠️ **轴的 effort 必须与 spec 一致，否则 `Stepper` 从轴读会与建会话时的意图分叉。**
    /// 不播种的话，一个 spec 说 `low` 的会话，轴会是表默认值 `high`——**出生即分叉**，
    /// 而用户从没设过轴。
    ///
    /// <para>⚠️ **今天没有任何代码路径能产出 `effort != "high"` 的 spec**：`ProviderDefinition`
    /// 没有 effort 字段，唯一的建会话调用点写死 `"high"`（`CommandDispatcher.cs:114`）。
    /// 所以这条测试**用 `with { Effort = "low" }` 直接构造 spec** 来钉规则本身——
    /// 它守的是「将来接上按提供商配 effort 时不会分叉」，而不是描述今天的行为。</para>
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
    /// ⚠️ spec 里的大小写变体要**规范化**再入轴（`CompleteAxes` 用 `Axes.TryParse`）。
    /// 原样存 `"HIGH"` 会同时坏两件事：`ClaudeRequestBuilder.ToEffort` 的 switch
    /// **区分大小写** ⇒ 请求时抛异常；头的下拉按表里的小写拼法匹配 ⇒ 一个选项都选不中。
    /// </summary>
    [Fact]
    public void Create_NormalizesTheEffortCaseFromTheSpec()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model() with { Effort = "HIGH" });

        Assert.Equal("high", handle.State.Axes![Axes.Effort]);
    }

    /// <summary>
    /// ⚠️ **轴与 `Turn` 住同一份 `State`**（`SessionState.Axes`）。
    /// 这条守的是「别把它们拆成两份」：一旦 `SaveAxes` 改成从轴另构造一个 `SessionState`，
    /// `Turn` 会被抹成 0，而这一步**没有任何报错**——只是重启后会话「失忆」，
    /// 且磁盘上看不出是哪次写坏的。
    ///
    /// <para>⚠️ **判据用 `Turn` 而不是 `Status`**：`"Running"` 只存在于内存
    /// （`Stepper.cs:51` 那个 `with`），**磁盘上写过的状态只有 `"Idle"`**——
    /// 写点共三处：`SessionHandle.Create` 的播种、`Stepper` 的收尾、
    /// `SessionHandle.SaveAxes`（`session.setAxis` 的写回点；它写整份 `State`，
    /// 所以**先把 `Status` 钉回 `Idle`**，见那里的注释）。构造一个
    /// `Status = "Running"` 的 state 去落盘，等于在测一个真实数据里不存在的形状。
    /// 而 `Status = "Idle"` 又恰好是 `SessionState` 的默认语义，**抹掉它这条测试也发现不了**
    /// ⇒ 拿它当判据是假覆盖。`Turn` 的默认值是 0，抹掉会立刻显形。</para>
    /// </summary>
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

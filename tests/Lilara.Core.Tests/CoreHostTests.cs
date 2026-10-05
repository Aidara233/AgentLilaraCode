using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.Claude;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Hosting;
using Lilara.Core.Storage;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

public class CoreHostTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-host-" + Guid.NewGuid().ToString("N"));

    private const string Work = @"C:\work";

    public CoreHostTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SessionHandle NewSession(string providerId = "p1", string protocol = Protocols.ClaudeMessages)
    {
        var store = new SessionStore(_root);
        var providers = new ProviderStore(_root);
        providers.Save(new ProviderDefinition
        {
            Id = providerId, Protocol = protocol, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        return SessionHandle.Create(store, Work, providers.ToSelection(
            providers.Load(providerId)!, null, "high", 16000));
    }

    // 适配器必须按该会话自己的 provider 解析——否则会把 B 的历史发到 A 的端点上。
    [Fact]
    public void StepperFor_ResolvesEachSessionsOwnProvider()
    {
        var host = new CoreHost(_root, Work);
        var claude = NewSession("claude-prov", Protocols.ClaudeMessages);
        var chat = NewSession("chat-prov", Protocols.OpenAiChat);

        var a = host.StepperFor(claude);
        var b = host.StepperFor(chat);

        Assert.IsType<ClaudeAdapter>(a.Adapter);
        Assert.IsType<OpenAiChatAdapter>(b.Adapter);
    }

    [Fact]
    public void StepperFor_CachesPerSession()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();

        Assert.Same(host.StepperFor(h), host.StepperFor(h));
    }

    // provider set 之后缓存必须失效，否则会继续用旧 provider。
    [Fact]
    public void InvalidateSteppers_DropsTheCache()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();

        var before = host.StepperFor(h);
        host.InvalidateSteppers();

        Assert.NotSame(before, host.StepperFor(h));
    }

    [Fact]
    public void Activate_IsIdempotentAndReturnsTheSameInstance()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();

        Assert.Same(host.Activate(h.ProjectKey, h.SessionId), host.Activate(h.ProjectKey, h.SessionId));
    }

    // 快照端点据此决定「查内存还是回落磁盘」。
    [Fact]
    public void TryGetActivated_IsFalseBeforeActivationAndTrueAfter()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();

        Assert.False(host.TryGetActivated(h.SessionId, out _));

        host.Activate(h.ProjectKey, h.SessionId);

        Assert.True(host.TryGetActivated(h.SessionId, out var got));
        Assert.Equal(h.SessionId, got.SessionId);
    }

    // 闸每会话一把；共用一把会让多会话退化成串行。
    [Fact]
    public void GateFor_IsPerSessionNotGlobal()
    {
        var host = new CoreHost(_root, Work);

        Assert.Same(host.GateFor("s1"), host.GateFor("s1"));
        Assert.NotSame(host.GateFor("s1"), host.GateFor("s2"));
    }

    [Fact]
    public void CtsFor_IsPerSessionAndCanBeCleared()
    {
        var host = new CoreHost(_root, Work);
        using var cts = new CancellationTokenSource();

        Assert.Null(host.CtsFor("s1"));

        host.SetCts("s1", cts);
        Assert.Same(cts, host.CtsFor("s1"));

        host.SetCts("s1", null);
        Assert.Null(host.CtsFor("s1"));
    }

    // ---- 销毁面要用的三个 ----

    // ⚠️ **不能拿激活表当「删得掉吗」的判据**：`session.create` 走 `Register()` 登记，
    // 新会话**一定**在激活表里 ⇒ 那样写会让「刚建的会话删不掉」。
    // `StatusOf` 的读盘那半就是为了盖住这一条。
    [Fact]
    public void StatusOf_ReadsTheDisk_WhenTheSessionIsNotActivated()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();

        // 落盘一份 `Running`（磁盘上正常不会存它，但作为「读盘那半真的读了盘」的判据最好用：
        // 内存里没有这一项，返回值只可能来自磁盘）。
        host.Store.WriteState(h.ProjectKey, h.State with { Status = "Running" });

        Assert.False(host.TryGetActivated(h.SessionId, out _));
        Assert.Equal("Running", host.StatusOf(h.SessionId));
    }

    // 内存优先：`Running` 只写内存、从不落盘，所以**只有**走内存那一臂才看得到它。
    [Fact]
    public void StatusOf_PrefersTheInMemoryCopy()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();
        host.Register(h);

        h.UpdateState(s => s with { Status = "Running" });

        Assert.Equal("Running", host.StatusOf(h.SessionId));
        Assert.Equal("Idle", host.Store.ReadState(h.ProjectKey, h.SessionId).Status);   // 磁盘上仍是 Idle
    }

    [Fact]
    public void StatusOf_OnAnUnknownSession_IsIdle()
    {
        var host = new CoreHost(_root, Work);

        Assert.Equal("Idle", host.StatusOf("nope"));
    }

    [Fact]
    public void Deactivate_DropsTheActivatedCopy_ButLeavesTheDiskAlone()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();
        host.Register(h);

        Assert.True(host.Deactivate(h.SessionId));
        Assert.False(host.TryGetActivated(h.SessionId, out _));

        // 第二次就是「它本来就没激活」——调用方要能分辨这两个。
        Assert.False(host.Deactivate(h.SessionId));

        // 存储侧零改动：磁盘从不存「激活」。
        Assert.NotNull(host.Store.TryReadSpec(h.ProjectKey, h.SessionId));
    }

    // 只丢**这一个**会话的缓存——用全量 `InvalidateSteppers` 去顶替会让别的会话白掉一次缓存。
    [Fact]
    public void ForgetStepper_DropsOnlyThatSession()
    {
        var host = new CoreHost(_root, Work);
        var a = NewSession();
        var b = NewSession();
        host.Register(a);
        host.Register(b);

        var firstA = host.StepperFor(a);
        var firstB = host.StepperFor(b);

        host.ForgetStepper(a.SessionId);

        Assert.NotSame(firstA, host.StepperFor(a));
        Assert.Same(firstB, host.StepperFor(b));
    }
}

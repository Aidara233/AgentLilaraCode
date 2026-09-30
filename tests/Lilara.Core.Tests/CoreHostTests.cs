using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
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

    /// <summary>
    /// ⚠️ **这条是本任务存在的理由**（复审在设计阶段点出的数据外泄路径）：
    /// 两个会话绑**不同** provider（不同 key、不同端点）时，若共用一把 stepper，
    /// 对 B 发消息会把 **B 的全量历史发到 A 的端点上**——正是
    /// `StepperResolver` 那段注释点名要防的「等于把数据发给了别人」。
    /// ⇒ 适配器必须按**该会话自己的** provider 解析。
    /// </summary>
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

    /// <summary>同一个会话重复取必须拿到**同一个** Stepper（缓存，别每次新建适配器）。</summary>
    [Fact]
    public void StepperFor_CachesPerSession()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();

        Assert.Same(host.StepperFor(h), host.StepperFor(h));
    }

    /// <summary>
    /// ⚠️ `provider set` 之后缓存必须失效——否则改了 provider 还在用旧的。
    /// 真实失效点在 `CommandDispatcher`（Task 3），这里只钉住 `InvalidateSteppers` 本身有效。
    /// </summary>
    [Fact]
    public void InvalidateSteppers_DropsTheCache()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();

        var before = host.StepperFor(h);
        host.InvalidateSteppers();

        Assert.NotSame(before, host.StepperFor(h));
    }

    /// <summary>激活是幂等的：同一个会话重复激活拿同一个实例（否则状态会分叉）。</summary>
    [Fact]
    public void Activate_IsIdempotentAndReturnsTheSameInstance()
    {
        var host = new CoreHost(_root, Work);
        var h = NewSession();

        Assert.Same(host.Activate(h.ProjectKey, h.SessionId), host.Activate(h.ProjectKey, h.SessionId));
    }

    /// <summary>没激活过的会话查不到——快照端点据此决定「查内存还是回落磁盘」。</summary>
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

    /// <summary>📌 **每会话一把**——不同会话必须是**不同**的闸，否则多会话退化成串行。</summary>
    [Fact]
    public void GateFor_IsPerSessionNotGlobal()
    {
        var host = new CoreHost(_root, Work);

        Assert.Same(host.GateFor("s1"), host.GateFor("s1"));
        Assert.NotSame(host.GateFor("s1"), host.GateFor("s2"));
    }

    /// <summary>CTS 也是每会话一份，且可清空（中断后必须置空，见 Task 7）。</summary>
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
}

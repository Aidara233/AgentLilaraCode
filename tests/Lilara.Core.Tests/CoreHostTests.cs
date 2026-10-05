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
}

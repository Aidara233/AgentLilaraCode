using Lilara.Core.Console;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

public class ReplTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-repl-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-replwork-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly ProviderStore _providers;
    private readonly FakeAdapter _adapter = new();
    private readonly StringWriter _out = new();

    public ReplTests()
    {
        _store = new SessionStore(_root);
        _providers = new ProviderStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private Repl NewRepl() => new(_store, _providers, new Stepper(_store, _adapter), _work, _out);

    /// <summary>配好提供商，并**显式**建一个会话——核心不自动建（01 §8.1）。</summary>
    private Repl ReplWithSession()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "claude",
            Protocol = "claude-messages",
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        });

        var repl = NewRepl();
        repl.Current = SessionHandle.Create(
            _store, _work, _providers.ToSelection(_providers.Load("claude")!, null, "high", 16000));
        return repl;
    }

    [Fact]
    public async Task ProviderSet_PersistsDefinition()
    {
        var repl = NewRepl();

        await repl.ExecuteAsync(
            "provider set claude --key sk-abc --model claude-opus-5", CancellationToken.None);

        var saved = _providers.Load("claude");
        Assert.NotNull(saved);
        Assert.Equal("sk-abc", saved!.ApiKey);
        Assert.Equal("claude-opus-5", saved.DefaultModel);
        Assert.Equal("claude-messages", saved.Protocol);
    }

    [Fact]
    public async Task NoSession_CoreDoesNotCreateOneOnItsOwn()
    {
        // ⚠️ 01 §8.1：核心启动后不建任何会话
        var repl = NewRepl();

        await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.Null(repl.Current);
        Assert.Empty(_adapter.Calls);
        Assert.Contains("session create", _out.ToString());
    }

    [Fact]
    public async Task SessionCreate_MakesTheSessionCurrent()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "claude",
            Protocol = "claude-messages",
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        });
        var repl = NewRepl();

        await repl.ExecuteAsync("session create", CancellationToken.None);

        Assert.NotNull(repl.Current);
        Assert.Single(_store.ListSessions(repl.Current!.ProjectKey));
    }

    [Fact]
    public async Task PlainText_IsSentToTheModel()
    {
        _adapter.Reply("你好！");
        var repl = ReplWithSession();

        await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.Contains("你好！", _out.ToString());
    }

    [Fact]
    public async Task History_ReplaysPersistedTimeline()
    {
        _adapter.Reply("你好！");
        var repl = ReplWithSession();
        await repl.ExecuteAsync("你好", CancellationToken.None);
        _out.GetStringBuilder().Clear();

        await repl.ExecuteAsync("history", CancellationToken.None);

        var text = _out.ToString();
        Assert.Contains("你好", text);
        Assert.Contains("你好！", text);
    }

    [Fact]
    public async Task Quit_ReturnsFalse()
    {
        var repl = NewRepl();

        Assert.False(await repl.ExecuteAsync("quit", CancellationToken.None));
    }

    [Fact]
    public async Task SendBeforeProvider_PrintsGuidanceInsteadOfCrashing()
    {
        // Review Focus #6：没配 provider 时给人话，不吐栈
        var repl = NewRepl();

        await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.Contains("provider", _out.ToString());
        Assert.Empty(_adapter.Calls);
    }

    [Fact]
    public async Task ModelFailure_PrintsOneLineAndKeepsReplAlive()
    {
        _adapter.Throws(new Lilara.Core.Adapters.ModelCallFailedException("上游 503。这是临时的，重试即可。"));
        var repl = ReplWithSession();

        var keepGoing = await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.True(keepGoing);
        Assert.Contains("上游 503", _out.ToString());
    }

    [Theory]
    [InlineData("provider set claude --key k", true)]
    [InlineData("  provider set claude --key k  ", true)]   // 前导空白要认
    [InlineData("providers set x", false)]                  // 前缀近似但不同
    [InlineData("history", false)]
    public void IsProviderSetCommand_MatchesOnlyTheRealCommand(string line, bool expected)
    {
        Assert.Equal(expected, Repl.IsProviderSetCommand(line));
    }
}

using Lilara.Core.Adapters;
using Lilara.Core.Hosting;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Kernel;
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

    // 控制台现在是控制面的一个客户端适配器：文本 → 命令 → dispatcher → 格式化回执。
    private Repl NewRepl()
    {
        var host = new CoreHost(_root, _work);
        var dispatcher = new CommandDispatcher(host, new EventBus(), _ => new Stepper(_store, _adapter));
        return new Repl(host, dispatcher, _ => new Stepper(_store, _adapter), _out);
    }

    // 配好提供商，并显式建一个会话——核心不自动建。
    private Repl ReplWithSession()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "claude",
            Protocol = Protocols.ClaudeMessages,
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        });

        var repl = NewRepl();
        repl.Current = SessionHandle.Create(
            _store, _work, _providers.ToSelection(_providers.Load("claude")!, null, "high", 16000));
        return repl;
    }

    // 与 ReplWithSession 同一套接线，但把 dispatcher 与 handle 也交出来：
    // 那条「status 印轴的 effort」测试要绕开控制台解析、直接改轴。
    private (Repl Repl, CommandDispatcher Dispatcher, SessionHandle Session) NewHarness()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "claude",
            Protocol = Protocols.ClaudeMessages,
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        });

        var host = new CoreHost(_root, _work);
        var dispatcher = new CommandDispatcher(host, new EventBus(), _ => new Stepper(_store, _adapter));
        var repl = new Repl(host, dispatcher, _ => new Stepper(_store, _adapter), _out);
        var session = SessionHandle.Create(
            _store, _work, _providers.ToSelection(_providers.Load("claude")!, null, "high", 16000));
        repl.Current = session;   // setter 会把 handle 登记进宿主激活表
        return (repl, dispatcher, session);
    }

    // 控制台没有 --endpoint 参数，手写 providers/<id>.json 是配置端点的唯一途径；
    // 整文件覆盖会把它抹掉 ⇒ 用户被静默退回环境变量那一档。
    [Fact]
    public async Task ProviderSet_PreservesAHandConfiguredEndpoint()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "claude",
            Protocol = Protocols.ClaudeMessages,
            ApiKey = "sk-old",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
            Endpoint = "http://127.0.0.1:15721",
        });

        var repl = NewRepl();
        await repl.ExecuteAsync("provider set claude --key sk-new", CancellationToken.None);

        var saved = _providers.Load("claude");
        Assert.Equal("sk-new", saved!.ApiKey);                  // 本次指定的被更新
        Assert.Equal("http://127.0.0.1:15721", saved.Endpoint); // 没提到的被保留
    }

    // Protocol 与 Endpoint / Modality 一样是操作者配置：本次没提到就原样保留；
    // 手写文件里的未知协议会活下来，直到发消息时由 AdapterFactory 给人话拒绝。
    [Fact]
    public async Task ProviderSet_KeepsModalityAndHandWrittenProtocolWhenNotMentioned()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "claude",
            Protocol = "hand-written-bogus",
            ApiKey = "sk-old",
            DefaultModel = "claude-opus-5",
            Modality = "audio-only",         // 操作者配置，必须活下来
        });

        var repl = NewRepl();
        await repl.ExecuteAsync("provider set claude --key sk-new", CancellationToken.None);

        var saved = _providers.Load("claude");
        Assert.Equal("hand-written-bogus", saved!.Protocol);  // 未提到 → 原样保留
        Assert.Equal("audio-only", saved.Modality);
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
        // 落盘的字面量是磁盘格式，不能改；同时钉住「用的是同一个来源」，判断串写在两处的话改 ProtocolId 只会改到一处。
        Assert.Equal(ClaudeAdapter.ProtocolId, saved.Protocol);
    }

    [Fact]
    public async Task ProviderSet_HonoursProtocolFlag()
    {
        var repl = NewRepl();

        await repl.ExecuteAsync(
            "provider set gw --key sk-x --model deepseek-flash --protocol openai-chat --endpoint http://localhost:5129 --auth bearer",
            CancellationToken.None);

        var saved = _providers.Load("gw")!;
        Assert.Equal(Protocols.OpenAiChat, saved.Protocol);
        Assert.Equal("http://localhost:5129", saved.Endpoint);
        Assert.Equal(AuthSchemes.Bearer, saved.AuthScheme);
    }

    [Fact]
    public async Task ProviderSet_DefaultsToClaudeWhenProtocolOmitted()
    {
        // 缺省保持既有行为，不破坏已经配好的用法
        var repl = NewRepl();

        await repl.ExecuteAsync("provider set c --key sk-x", CancellationToken.None);

        Assert.Equal(Protocols.ClaudeMessages, _providers.Load("c")!.Protocol);
    }

    [Fact]
    public async Task ProviderSet_RejectsAnUnknownProtocolWithoutSaving()
    {
        var repl = NewRepl();

        await repl.ExecuteAsync("provider set bad --key k --protocol gemini", CancellationToken.None);

        Assert.Null(_providers.Load("bad"));
        Assert.Contains("gemini", _out.ToString());
    }

    // provider delete <id>：文件真的没了，而且用户看得见。
    // 两个判据缺一不可：① 盘上没了；② 印了一句话——落进 default 的 PrintReply(null) 会一声不吭。
    [Fact]
    public async Task ProviderDelete_RemovesTheFileAndSaysSo()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "gw",
            Protocol = Protocols.ClaudeMessages,
            ApiKey = "sk-x",
            DefaultModel = "m",
            Modality = "vision",
        });

        var repl = NewRepl();
        await repl.ExecuteAsync("provider delete gw", CancellationToken.None);

        Assert.Null(_providers.Load("gw"));                  // 盘上真没了
        Assert.Contains("已删除", _out.ToString());          // 而且吭了一声
        Assert.Contains("gw", _out.ToString());              // 点名删的是哪一个
    }

    [Fact]
    public async Task ProviderSet_RejectsAFlagAsAValue()
    {
        // 判据是「值长得像不像标志位」，不是「位置上像不像」；两条输入都必须拒绝且不落盘。
        // 两半各按各自新增的输出断言（_out 是实例级、NewRepl() 不重置），否则第二半会被第一半的文字满足。
        var a = NewRepl();
        var outA = _out.ToString();

        await a.ExecuteAsync("provider set w1 --key --model", CancellationToken.None);

        Assert.Null(_providers.Load("w1"));
        Assert.Contains("缺少值", _out.ToString()[outA.Length..]);

        var b = NewRepl();
        var outB = _out.ToString();

        await b.ExecuteAsync("provider set w2 --key --model m", CancellationToken.None);

        Assert.Null(_providers.Load("w2"));
        Assert.Contains("缺少值", _out.ToString()[outB.Length..]);
    }

    [Fact]
    public async Task ProviderSet_RejectsAnUnknownFlagWithoutSaving()
    {
        // 未知标志被 switch 忽略（无 default）：--protcol（拼错一个字母）会照常保存并回 ✓，协议悄悄落成 claude-messages。
        var repl = NewRepl();

        await repl.ExecuteAsync("provider set w --key k --protcol openai-chat", CancellationToken.None);

        Assert.Null(_providers.Load("w"));
        Assert.Contains("--protcol", _out.ToString());
    }

    [Fact]
    public async Task ProviderSet_RejectsATrailingFlagWithNoValue()
    {
        // 末尾那个标志位必须被检查：--protocol / --endpoint 末尾缺值时否则会照常保存并回 ✓，
        // 协议悄悄落成默认值 / 端点被丢掉、请求落回兜底地址。
        var a = NewRepl();
        var outA = _out.ToString();

        await a.ExecuteAsync("provider set w1 --key k --protocol", CancellationToken.None);

        Assert.Null(_providers.Load("w1"));
        Assert.Contains("--protocol", _out.ToString()[outA.Length..]);

        var b = NewRepl();
        var outB = _out.ToString();

        await b.ExecuteAsync("provider set w2 --key k --endpoint", CancellationToken.None);

        Assert.Null(_providers.Load("w2"));
        Assert.Contains("--endpoint", _out.ToString()[outB.Length..]);
    }

    [Fact]
    public async Task ProviderSet_RejectsAnUnknownAuthWithoutSaving()
    {
        // --auth Bearer（大写）与 AuthSchemes.Bearer（"bearer"）不相等，会被静默当成 x-api-key；未知值必须拒绝且不落盘。
        var repl = NewRepl();

        await repl.ExecuteAsync("provider set bad --key k --auth Bearer", CancellationToken.None);

        Assert.Null(_providers.Load("bad"));
        Assert.Contains("Bearer", _out.ToString());
    }

    // 重新配置（不带 --model / --protocol）时必须保留旧的这两项，否则「改 key」会静默重置模型与协议。
    [Fact]
    public async Task ProviderSet_KeepsExistingModelAndProtocolWhenNotMentioned()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "gw",
            Protocol = Protocols.OpenAiChat,
            ApiKey = "sk-old",
            DefaultModel = "deepseek-flash",
            Modality = "vision",
        });

        var repl = NewRepl();
        await repl.ExecuteAsync("provider set gw --key sk-new", CancellationToken.None);

        var saved = _providers.Load("gw")!;
        Assert.Equal("sk-new", saved.ApiKey);
        Assert.Equal(Protocols.OpenAiChat, saved.Protocol);   // 没提到的协议必须活下来
        Assert.Equal("deepseek-flash", saved.DefaultModel);   // 模型同理
    }

    [Fact]
    public async Task NoSession_CoreDoesNotCreateOneOnItsOwn()
    {
        // 核心启动后不建任何会话
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
            Protocol = Protocols.ClaudeMessages,
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
        // 没配 provider 时给人话，不吐栈
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

    // Repl catch 之后继续用同一个把手：步骤失败时内存里的 Status 必须回到 Idle，否则 status 会报 Running。
    [Fact]
    public async Task ModelFailure_LeavesStatusAtIdleNotRunning()
    {
        _adapter.Throws(new Lilara.Core.Adapters.ModelCallFailedException("上游 503"));
        var repl = ReplWithSession();
        await repl.ExecuteAsync("你好", CancellationToken.None);
        _out.GetStringBuilder().Clear();

        await repl.ExecuteAsync("status", CancellationToken.None);

        var text = _out.ToString();
        Assert.Contains("Idle", text);
        Assert.DoesNotContain("Running", text);
    }

    // 只有推理块、没有正文的回复不能印一个光秃秃的 ⏺。
    [Fact]
    public async Task ReasoningOnlyReply_PrintsAPlaceholderInsteadOfAnEmptyBody()
    {
        _adapter.ReplyBlocks(new Block.ReasoningBlock("sig", "想了半天"));
        var repl = ReplWithSession();

        await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.Contains("（这一步没有正文输出）", _out.ToString());
    }

    // 回放时同理：推理块回合在 history 里不该只剩一个孤零零的 ⏺。
    [Fact]
    public async Task History_ShowsAPlaceholderForReasoningOnlyTurns()
    {
        _adapter.ReplyBlocks(new Block.ReasoningBlock("sig", "想了半天"));
        var repl = ReplWithSession();
        await repl.ExecuteAsync("你好", CancellationToken.None);
        _out.GetStringBuilder().Clear();

        await repl.ExecuteAsync("history", CancellationToken.None);

        Assert.Contains("（这一步没有正文输出）", _out.ToString());
    }

    // session status 印的必须是轴的当前值，不是 Spec.Model.Effort（冻结的快照）；
    // 判据是输出文本——断言 handle.State.Axes["effort"] 没有区分力（修复前后它都是新值）。
    [Fact]
    public async Task Status_PrintsTheAxisEffort_NotTheFrozenSpecOne()
    {
        var (repl, dispatcher, session) = NewHarness();

        // 先确认起点不是 max，否则「印了新值」与「印了旧值」看不出区别。
        await repl.ExecuteAsync("status", CancellationToken.None);
        Assert.Contains("effort high", _out.ToString());
        _out.GetStringBuilder().Clear();

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "effort", "max"), "a1", true, CancellationToken.None);

        await repl.ExecuteAsync("status", CancellationToken.None);

        Assert.Contains("effort max", _out.ToString());
    }

    // 控制台改轴要回一句话 + 立刻重印状态那一行；少了 SessionSetAxis 那条 case 会落进 default
    // ⇒ PrintReply(null) ⇒ 一声不吭。判据是输出文本。
    [Fact]
    public async Task AxisSet_PrintsAckAndRefreshesStatus()
    {
        var (repl, _, session) = NewHarness();

        await repl.ExecuteAsync($"axis set {session.SessionId} effort max", CancellationToken.None);

        var text = _out.ToString();
        Assert.Contains("轴已更新", text);
        Assert.Contains("effort max", text);   // 重印的是轴的新值，用户马上看到「现在是什么」
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

    // claim list 是控制台的查询（不做 claim.list 命令），所以必须自己读 claims.json 并印出来。
    [Fact]
    public async Task ClaimList_PrintsTheClaimsInTheWorkingDirectory()
    {
        var (repl, _, session) = NewHarness();
        new Lilara.Core.Persistence.ClaimStore(session.Spec.WorkingDirectory)
            .Save([new Lilara.Core.Kernel.Claim("other", "pk", @"c:\repo\src", DateTimeOffset.UnixEpoch)]);

        await repl.ExecuteAsync("claim list", CancellationToken.None);

        // 断言用钉专属措辞：会话 id 与路径都要看得见，否则「印了一行空表」也算过。
        Assert.Contains("other", _out.ToString());
        Assert.Contains("src", _out.ToString());
    }

    // 没有声明时印「（没有声明）」，不是空白——空白让用户分不清「没有」与「坏了」。
    [Fact]
    public async Task ClaimList_WithNoClaims_SaysSo()
    {
        var (repl, _, _) = NewHarness();

        await repl.ExecuteAsync("claim list", CancellationToken.None);

        Assert.Contains("没有声明", _out.ToString());
    }

    // claim 成功时要吭一声：Ack.Accepted.Reply 是 null，不加分支会落进 default: PrintReply(null) ⇒ 静默成功。
    // NewHarness() 已把 repl.Current 设好，所以 claim src 走命令通道能成功。
    [Fact]
    public async Task Claim_OnSuccess_SaysWhatWasTaken()
    {
        var (repl, _, _) = NewHarness();

        await repl.ExecuteAsync("claim src/foo", CancellationToken.None);

        Assert.Contains("已占", _out.ToString());
        Assert.Contains("src/foo", _out.ToString());
    }

    // 释放的回执分支也要单独钉：只测 ClaimAcquire 那条分支时，把 ClaimRelease 的分支删掉照样全绿（变异存活）。
    [Fact]
    public async Task ClaimRelease_OnSuccess_SaysWhatWasReleased()
    {
        var (repl, _, _) = NewHarness();
        await repl.ExecuteAsync("claim src/foo", CancellationToken.None);   // 先占上
        _out.GetStringBuilder().Clear();

        await repl.ExecuteAsync("claim release src/foo", CancellationToken.None);

        Assert.Contains("已释放", _out.ToString());
        Assert.Contains("src/foo", _out.ToString());
    }

    // claim list 在没有当前会话时印人话，不是空白、不是抛。
    [Fact]
    public async Task ClaimList_WithoutACurrentSession_SaysSo()
    {
        var (repl, _, _) = NewHarness();
        repl.Current = null;

        await repl.ExecuteAsync("claim list", CancellationToken.None);

        Assert.Contains("还没有选中会话", _out.ToString());
    }

    // claims.json 被别的进程独占时控制台不许崩：File.ReadAllText 对 FileShare.None 抛 IOException，
    // 而 ClaimStore.All() 初版只接 JsonException ⇒ 异常逃出 claim list ⇒ 打死 REPL。编辑器打开着那个文件就会发生。
    [Fact]
    public async Task ClaimList_WhenTheFileIsLocked_SaysSomethingInsteadOfCrashing()
    {
        var (repl, _, session) = NewHarness();
        var dir = Path.Combine(session.Spec.WorkingDirectory, ".lilara");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "claims.json");

        // 用 FileShare.None 独占——模拟编辑器/别的进程打开着它。
        using var hold = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        // 不许抛。
        await repl.ExecuteAsync("claim list", CancellationToken.None);

        Assert.Contains("没有声明", _out.ToString());   // 读不到 ⇒ 当作空
    }

    // claim LIST 不许被当成消息发出去：解析器认它是查询、而 Repl 的守卫曾用精确匹配 ⇒ 放行 ⇒ 消息真的发给了模型。
    // 判据是「适配器没被调用」——那才是没落回消息的直接证据。
    [Fact]
    public async Task ClaimList_InAnyUpperCase_IsNotSentAsAMessage()
    {
        var (repl, _, session) = NewHarness();
        new Lilara.Core.Persistence.ClaimStore(session.Spec.WorkingDirectory)
            .Save([new Lilara.Core.Kernel.Claim("other", "pk", @"c:\repo\src", DateTimeOffset.UnixEpoch)]);

        await repl.ExecuteAsync("claim LIST", CancellationToken.None);

        Assert.Contains("other", _out.ToString());   // 真的印了声明列表
        // 拦下来了 ⇒ 没有命令被派发 ⇒ 适配器没被碰。
        Assert.Empty(_adapter.Calls);
    }
}

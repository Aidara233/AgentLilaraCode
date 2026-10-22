using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Network;
using Lilara.Core.Protocol;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

/// <summary>
/// `network.configure` / `network.test`。⚠️ 与 `provider.upsert` 同一条合并语义：
/// null = 保留；唯一例外是空串 key = 清掉。
/// </summary>
public class NetworkCommandTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-netcmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-netcmdwork-" + Guid.NewGuid().ToString("N"));

    private readonly FakeNetworkTransport _transport = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private CommandDispatcher NewDispatcher()
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        return new CommandDispatcher(host, new EventBus(),
            h => new Stepper(host.Store, new FakeAdapter()), networkTestTransport: _transport);
    }

    private static Task<DispatchResult> RunAsync(CommandDispatcher d, CoreCommand command) =>
        d.DispatchAsync(command, Guid.NewGuid().ToString("N")[..8], false, CancellationToken.None);

    private NetworkConfig Stored() => new NetworkStore(_root).Load();

    private static CoreCommand.NetworkConfigure Configure(
        bool? enabled = null, string? key = null, string? endpoint = null,
        int? results = null, int? timeout = null, int? bytes = null,
        IReadOnlyList<string>? allow = null) =>
        new(enabled, key, endpoint, results, timeout, bytes, allow);

    [Fact]
    public async Task Configure_PersistsAndMerges()
    {
        var d = NewDispatcher();

        var first = await RunAsync(d, Configure(
            enabled: true, key: "tvly-1", endpoint: "https://api.example.test/search",
            results: 7, timeout: 33, bytes: 8192, allow: ["Wiki.Intra"]));
        Assert.IsType<Ack.Accepted>(first.Ack);

        var cfg = Stored();
        Assert.True(cfg.Enabled);
        Assert.Equal("tvly-1", cfg.Search.ApiKey);
        Assert.Equal("https://api.example.test/search", cfg.Search.Endpoint);
        Assert.Equal(7, cfg.Search.MaxResults);
        Assert.Equal(33, cfg.Fetch.TimeoutSeconds);
        Assert.Equal(8192, cfg.Fetch.MaxBytes);
        Assert.Equal(["wiki.intra"], cfg.Fetch.AllowDomains);

        // 只改条数：其余全部保留（包括 key）。
        var second = await RunAsync(d, Configure(results: 3));
        Assert.IsType<Ack.Accepted>(second.Ack);

        cfg = Stored();
        Assert.Equal(3, cfg.Search.MaxResults);
        Assert.Equal("tvly-1", cfg.Search.ApiKey);
        Assert.Equal("https://api.example.test/search", cfg.Search.Endpoint);
    }

    [Fact]
    public async Task Configure_EmptyKey_ClearsIt()
    {
        var d = NewDispatcher();
        await RunAsync(d, Configure(key: "tvly-1"));

        await RunAsync(d, Configure(key: ""));

        Assert.Null(Stored().Search.ApiKey);
    }

    [Theory]
    [InlineData("ftp://example.test/search")]
    [InlineData("not a url")]
    public async Task Configure_BadEndpoint_IsRejected(string endpoint)
    {
        var d = NewDispatcher();
        var result = await RunAsync(d, Configure(endpoint: endpoint));

        Assert.IsType<Ack.Rejected>(result.Ack);
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(99, null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, null, 1024)]     // 小于 MinFetchBytes
    public async Task Configure_OutOfRange_IsRejected(int? results, int? timeout, int? bytes)
    {
        var d = NewDispatcher();
        var result = await RunAsync(d, Configure(results: results, timeout: timeout, bytes: bytes));

        Assert.IsType<Ack.Rejected>(result.Ack);
    }

    [Fact]
    public async Task Test_WhenDisabled_SaysSoWithoutCallingTransport()
    {
        var d = NewDispatcher();

        var result = await RunAsync(d, new CoreCommand.NetworkTest());

        var ack = Assert.IsType<Ack.Accepted>(result.Ack);
        Assert.Contains("未启用", ack.Note);
        Assert.Empty(_transport.Queries);
    }

    [Fact]
    public async Task Test_WhenConfigured_ReportsSuccess()
    {
        var d = NewDispatcher();
        await RunAsync(d, Configure(enabled: true, key: "tvly-1"));

        var result = await RunAsync(d, new CoreCommand.NetworkTest());

        var ack = Assert.IsType<Ack.Accepted>(result.Ack);
        Assert.StartsWith("✓", ack.Note);
        Assert.Single(_transport.Queries);
    }

    [Fact]
    public async Task Test_Failure_IsAnAcceptedAckWithAMessage()
    {
        _transport.OnSearch = (_, _, _) => throw new NetworkException("搜索被限流（429）。");

        var d = NewDispatcher();
        await RunAsync(d, Configure(enabled: true, key: "tvly-1"));

        var result = await RunAsync(d, new CoreCommand.NetworkTest());

        var ack = Assert.IsType<Ack.Accepted>(result.Ack);
        Assert.StartsWith("✗", ack.Note);
        Assert.Contains("429", ack.Note);
    }

    // 轴是每会话的，但命令面要能把它拨到联网档（头就是这么做的）。
    [Fact]
    public async Task NetworkAxis_AcceptsAllFourLevels()
    {
        var d = NewDispatcher();
        var created = await RunAsync(d, new CoreCommand.SessionCreate());
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        foreach (var level in new[] { "off", "search", "fetch", "all" })
        {
            var ack = await d.DispatchAsync(
                new CoreCommand.SessionSetAxis(sid, Axes.Network, level),
                Guid.NewGuid().ToString("N")[..8], false, CancellationToken.None);
            Assert.IsType<Ack.Accepted>(ack.Ack);
        }
    }
}

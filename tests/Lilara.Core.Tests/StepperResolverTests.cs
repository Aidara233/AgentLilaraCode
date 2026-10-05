using Lilara.Core.Kernel;
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

// 「全新跑 → provider set → 发消息」那条首次使用路径；只构造适配器，不联网。
public class StepperResolverTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-resolver-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly ProviderStore _providers;

    public StepperResolverTests()
    {
        _store = new SessionStore(_root);
        _providers = new ProviderStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Save(string id, string protocol = ClaudeAdapter.ProtocolId) =>
        _providers.Save(new ProviderDefinition
        {
            Id = id,
            Protocol = protocol,
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        });

    [Fact]
    public void Resolve_WithNoProvider_ReturnsPlaceholder()
    {
        var stepper = StepperResolver.Resolve(_store, _providers);

        Assert.IsType<UnconfiguredAdapter>(stepper.Adapter);
    }

    [Fact]
    public void Resolve_WithOneProvider_ReturnsConfiguredAdapter()
    {
        Save("claude");

        var stepper = StepperResolver.Resolve(_store, _providers);

        Assert.IsType<ClaudeAdapter>(stepper.Adapter);
    }

    // 不许退回任意一个提供商——可能是另一个 key + 另一个端点，等于把数据发给了别人。
    [Fact]
    public void Resolve_WithUnknownPreferredId_DoesNotSubstituteAnotherProvider()
    {
        Save("claude");

        var stepper = StepperResolver.Resolve(_store, _providers, preferredProviderId: "ghost");

        Assert.IsType<UnconfiguredAdapter>(stepper.Adapter);
    }

    // 占位适配器必须点名缺的是哪个提供商。
    [Fact]
    public async Task Resolve_WithUnknownPreferredId_PlaceholderNamesTheMissingProvider()
    {
        Save("claude");
        var stepper = StepperResolver.Resolve(_store, _providers, preferredProviderId: "ghost");

        var ex = await Assert.ThrowsAsync<ModelCallFailedException>(() =>
            stepper.Adapter.CallAsync(
                new ModelRequest
                {
                    ModelId = "claude-opus-5",
                    MaxTokens = 16000,
                    Protocol = Protocols.ClaudeMessages,
                    Effort = "high",
                    SystemBlocks = [],
                    Timeline = [],
                },
                CancellationToken.None));

        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Resolve_WithKnownPreferredId_UsesIt()
    {
        // 顺序刻意：List() 按 Id 升序，aaa 的协议未知会让 AdapterFactory.Create 抛，
        // 故断言拿到 ClaudeAdapter 能区分「用了 preferred」与「用了任意一个」。
        Save("aaa", protocol: "bogus-proto");
        Save("zzz");

        var stepper = StepperResolver.Resolve(_store, _providers, preferredProviderId: "zzz");

        Assert.IsType<ClaudeAdapter>(stepper.Adapter);
    }
}

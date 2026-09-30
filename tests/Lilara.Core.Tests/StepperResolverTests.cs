using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 「全新跑 → provider set → 发消息」那条首次使用路径的关键逻辑。
/// ⚠️ 这些测试只**构造**适配器、从不 CallAsync——不联网。
/// </summary>
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

    [Fact]
    public void Resolve_WithUnknownPreferredId_FallsBackToAnyProvider()
    {
        Save("claude");

        var stepper = StepperResolver.Resolve(_store, _providers, preferredProviderId: "ghost");

        Assert.IsType<ClaudeAdapter>(stepper.Adapter);
    }

    [Fact]
    public void Resolve_WithKnownPreferredId_UsesIt()
    {
        // ⚠️ 顺序是刻意的：`List()` 按 Id 升序，所以「退回任意一个」会先拿到 `aaa`，
        // 而 `aaa` 的协议是未知的——`AdapterFactory.Create` 会抛。
        // 于是「断言拿到 ClaudeAdapter」能区分「用了 preferred」与「用了任意一个」。
        Save("aaa", protocol: "bogus-proto");
        Save("zzz");

        var stepper = StepperResolver.Resolve(_store, _providers, preferredProviderId: "zzz");

        Assert.IsType<ClaudeAdapter>(stepper.Adapter);
    }
}

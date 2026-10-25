using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Storage;
using CanonicalBlock = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Contract.Tests;

/// <summary>
/// 宿主契约：只经过 <see cref="KernelRuntime"/> 与 <c>public</c> 类型完成一次完整流程。
///
/// <para>⚠️ 这些用例的价值不在「覆盖逻辑」——同一批行为在 <c>Lilara.Core.Tests</c> 里测得更细。
/// 它们的价值是**编译期证明**：ALP 的 KernelBridge 只会拿到这些入口。
/// 任何一步被迫用 internal 才能写出来，这里先红。</para>
/// </summary>
public sealed class KernelRuntimeContractTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-contract-" + Guid.NewGuid().ToString("N"));

    private readonly string _work = Path.Combine(
        Path.GetTempPath(), "lilara-contract-work-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_work);
    }

    private static void TryDelete(string path)
    {
        // Windows 上刚 Disposed 的句柄可能还差一拍才松手。
        for (var attempt = 0; attempt < 3 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { Thread.Sleep(20); }
        }
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
    public async Task CreateStepAndRead_WorkThroughThePublicFacade()
    {
        Directory.CreateDirectory(_work);
        using var runtime = KernelRuntime.Open(_root, _work);

        var handle = runtime.CreateSession(Model(), title: "contract");
        Assert.Equal(ProjectKey.FromPath(Path.GetFullPath(_work)), handle.ProjectKey);

        var adapter = new EchoAdapter();
        var outcome = await runtime.StepAsync(handle,
            [new CanonicalBlock.TextBlock("你好")], adapter, cancellationToken: CancellationToken.None);

        Assert.Equal("echo: 你好", ((CanonicalBlock.TextBlock)outcome.Reply.Blocks[0]).Text);
        Assert.Equal(1, handle.State.Turn);

        var timeline = runtime.ReadTimeline(handle);
        Assert.Equal(2, timeline.Count);
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
        Assert.Equal(Speaker.Model, ((TimelineEvent.MessageEvent)timeline[1]).From);

        // 落盘也要看得见：门面读的是 store，不是内存缓存。
        Assert.Equal(1, runtime.Store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }

    [Fact]
    public async Task Reopen_ResumesThePersistedSession()
    {
        Directory.CreateDirectory(_work);
        string projectKey;
        string sessionId;

        using (var runtime = KernelRuntime.Open(_root, _work))
        {
            var handle = runtime.CreateSession(Model(), title: "contract");
            projectKey = handle.ProjectKey;
            sessionId = handle.SessionId;
            await runtime.StepAsync(handle,
                [new CanonicalBlock.TextBlock("第一轮")], new EchoAdapter(),
                cancellationToken: CancellationToken.None);
        }

        using var reopened = KernelRuntime.Open(_root, _work);
        var loaded = reopened.LoadSession(projectKey, sessionId);

        Assert.Equal(1, loaded.State.Turn);
        Assert.Equal(2, reopened.ReadTimeline(loaded).Count);
        Assert.Equal("contract", reopened.Store.ReadSpec(projectKey, sessionId).Title);
    }

    [Fact]
    public void Open_HoldsTheDataRootUntilDisposed()
    {
        Directory.CreateDirectory(_work);

        var runtime = KernelRuntime.Open(_root, _work);
        try
        {
            Assert.Throws<DataRootHeldException>(() => KernelRuntime.Open(_root, _work));
        }
        finally
        {
            runtime.Dispose();
        }

        // 释放之后必须能重新打开——否则「一设备一核心」会退化成「一进程一次」。
        using var again = KernelRuntime.Open(_root, _work);
        Assert.Equal(Path.GetFullPath(_root), again.DataRoot);
    }

    /// <summary>
    /// Bridge 的 provider 映射路径：ALP 配置 → <see cref="ProviderDefinition"/> →
    /// <see cref="AdapterFactory"/>。⚠️ 这里只造对象、不发请求。
    /// </summary>
    [Fact]
    public void AdapterFactory_MapsAProviderDefinitionWithoutInternals()
    {
        var provider = new ProviderDefinition
        {
            Id = "local",
            Protocol = Protocols.ClaudeMessages,
            ApiKey = "test-key",
            DefaultModel = "claude-opus-5",
            Modality = "text",
            Endpoint = "http://127.0.0.1:9/unused",
        };

        var adapter = AdapterFactory.Create(provider);
        Assert.Equal(Protocols.ClaudeMessages, adapter.Protocol);
        Assert.Equal("http://127.0.0.1:9/unused", adapter.Endpoint?.Url);
    }

    /// <summary>最小适配器：只依赖公开契约，回显最后一条用户文本。</summary>
    private sealed class EchoAdapter : IModelAdapter
    {
        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            var lastUserText = request.Timeline
                .OfType<TimelineEvent.MessageEvent>()
                .Where(m => m.From == Speaker.User)
                .SelectMany(m => m.Blocks.OfType<CanonicalBlock.TextBlock>())
                .LastOrDefault()?.Text ?? "";

            return Task.FromResult(new ModelReply
            {
                Blocks = [new CanonicalBlock.TextBlock("echo: " + lastUserText)],
                StopReason = "end_turn",
                Refused = false,
            });
        }
    }
}

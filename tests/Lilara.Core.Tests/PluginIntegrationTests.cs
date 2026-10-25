using System.Text.Json;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Plugins;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.Claude;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Session;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

public sealed class PluginIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-plugin-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-plugin-work-" + Guid.NewGuid().ToString("N"));
    public PluginIntegrationTests() => Directory.CreateDirectory(_work);
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
    }

    private (CoreHost Host, CommandDispatcher Commands, EventBus Bus, FakeAdapter Adapter) Harness(DemoPlugin? plugin)
    {
        var host = new CoreHost(_root, _work);
        if (plugin is not null) host.Plugins.Register(plugin);
        host.Providers.Save(new ProviderDefinition { Id = "p", Protocol = Protocols.ClaudeMessages,
            ApiKey = "test", DefaultModel = "m", Modality = "vision" });
        var bus = new EventBus();
        var adapter = new FakeAdapter();
        return (host, new CommandDispatcher(host, bus, _ => new Stepper(host.Store, adapter, plugins: host.Plugins)), bus, adapter);
    }

    private static async Task<SessionHandle> Create(CoreHost host, CommandDispatcher commands)
    {
        var result = await commands.DispatchAsync(new CoreCommand.SessionCreate(), "create", false, CancellationToken.None);
        var id = Assert.IsType<Ack.Accepted>(result.Ack).SessionId!;
        Assert.True(host.TryGetActivated(id, out var handle));
        return handle;
    }

    private static Task<DispatchResult> Set(CommandDispatcher d, SessionHandle h, string axis, string value) =>
        d.DispatchAsync(new CoreCommand.SessionSetAxis(h.SessionId, axis, value), "set", false, CancellationToken.None);
    private static string Text(IEnumerable<Block> blocks) => string.Join("\n", blocks.OfType<Block.TextBlock>().Select(b => b.Text));

    /// <summary>
    /// 请求里**模型看得见的全部文本**——包括末尾那对合成的 `floating_context` 调用 + 结果
    /// （2026-10-17：工具表里有 `floating_context` 时，浮动块不再走 `Slots.Tail`，
    /// 它住在时间线末尾的合成对里）。
    /// ⚠️ 只读 `Slots.Tail` 的断言在本批之后**只在带工具表的会话里**恒为空——
    /// 这里的推进器刻意不带工具表，所以它实际读的是尾槽位（回退路径）。
    /// 两条都读：这个助手钉的是「模型看得见」，不是「它从哪条路来」。
    /// </summary>
    private static string RequestText(ModelRequest request) => string.Join("\n",
        Text(request.Slots.System), Text(request.Slots.Premier), Text(request.Slots.Tail),
        string.Join("\n", request.Timeline.Select(e => e switch
        {
            TimelineEvent.ToolResultEvent r => Text(r.Content),
            TimelineEvent.MessageEvent m => Text(m.Blocks),
            _ => "",
        })));

    [Fact]
    public async Task Change_Save_Restart_NextStep_ChangesFloatingOnly_AndAllProtocolsCarryIt()
    {
        var (host, commands, bus, adapter) = Harness(new DemoPlugin());
        var h = await Create(host, commands);
        Assert.Equal("brief", h.State.PluginAxes!["demo.style"]);
        Assert.Equal("1", Assert.Single(h.Spec.Plugins!).Version);
        adapter.Reply("one");
        await new Stepper(host.Store, adapter, plugins: host.Plugins).StepAsync(h, "hello", CancellationToken.None);
        var before = adapter.Calls.Single();
        using var sub = bus.Subscribe(EventBus.SessionTopic(h.SessionId));
        Assert.IsType<Ack.Accepted>((await Set(commands, h, "demo.style", "DETAILED")).Ack);
        Assert.True(sub.Reader.TryRead(out var envelope));
        Assert.Equal("detailed", Assert.IsType<WireEvent.AxisChanged>(envelope.Payload).Value);
        Assert.IsType<Ack.Accepted>((await Set(commands, h, "demo.style", "detailed")).Ack);
        Assert.False(sub.Reader.TryRead(out _));
        Assert.IsType<Ack.Rejected>((await Set(commands, h, "demo.style", "invalid")).Ack);
        Assert.IsType<Ack.Accepted>((await Set(commands, h, Axes.Verify, "strict")).Ack);

        var (fresh, _, _, nextAdapter) = Harness(new DemoPlugin());
        var loaded = fresh.Activate(h.ProjectKey, h.SessionId);
        Assert.Equal("detailed", loaded.State.PluginAxes!["demo.style"]);
        nextAdapter.Reply("two");
        await new Stepper(fresh.Store, nextAdapter, plugins: fresh.Plugins).StepAsync(loaded, "again", CancellationToken.None);
        var after = nextAdapter.Calls.Single();
        // ⚠️ 读 **RequestText**（槽位 + 时间线），不读 `Slots.Tail`：这里的推进器不带工具表，
        // 尾块退回槽位；若哪天它带上工具表，尾块会搬到末尾合成对里，那样断言仍然成立。
        Assert.Contains("style=brief", RequestText(before));
        Assert.Contains("style=detailed", RequestText(after));
        Assert.Contains("Keep the answer concise.", RequestText(before));
        Assert.Contains("Explain the reasoning with concrete examples.", RequestText(after));
        Assert.DoesNotContain("Keep the answer concise.", RequestText(after));
        Assert.Contains("验证未通过时不得声称完成", RequestText(after));
        Assert.Contains("浮动上下文", Text(after.Slots.System));
        Assert.Equal(Text(before.Slots.System), Text(after.Slots.System));
        Assert.Equal(Text(before.Slots.Premier), Text(after.Slots.Premier));
        Assert.DoesNotContain("style=", JsonSerializer.Serialize(fresh.Store.ReadTimeline(h.ProjectKey, h.SessionId), Json.Options));
        var history = JsonSerializer.Serialize(fresh.Store.ReadTimeline(h.ProjectKey, h.SessionId), Json.Options);
        Assert.DoesNotContain("Explain the reasoning with concrete examples.", history);
        Assert.DoesNotContain("Keep the answer concise.", history);

        using var chat = JsonDocument.Parse(OpenAiChatRequest.Build(after with { Protocol = Protocols.OpenAiChat }));
        using var responses = JsonDocument.Parse(OpenAiResponsesRequest.Build(after with { Protocol = Protocols.OpenAiResponses }));
        Assert.Contains("style=detailed", Strings(chat.RootElement));
        Assert.Contains("style=detailed", Strings(responses.RootElement));
        foreach (var content in new[] { Strings(chat.RootElement), Strings(responses.RootElement) })
        {
            Assert.Contains("Explain the reasoning with concrete examples.", content);
            Assert.DoesNotContain("Keep the answer concise.", content);
            Assert.Contains("验证未通过时不得声称完成", content);
            Assert.Contains("浮动上下文是 Lilara 核心", content);
        }
        var claude = ClaudeRequestBuilder.Build(after);
        var claudeText = string.Join("\n", claude.Messages.SelectMany(m =>
        {
            if (m.Content.TryPickString(out var value)) return new[] { value! };
            Assert.True(m.Content.TryPickContentBlockParams(out var blocks));
            return blocks!.Select(b => b.TryPickText(out var t) ? t!.Text : "");
        }));
        Assert.Contains("style=detailed", claudeText);
        Assert.Contains("Explain the reasoning with concrete examples.", claudeText);
        Assert.DoesNotContain("Keep the answer concise.", claudeText);
        Assert.Contains("验证未通过时不得声称完成", claudeText);
        using var claudeJson = JsonDocument.Parse(JsonSerializer.Serialize(claude));
        Assert.Contains("浮动上下文是 Lilara 核心", Strings(claudeJson.RootElement));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2")]
    public async Task MissingOrDifferentVersion_PreservesData_RejectsEditingAndModelCalls(string? version)
    {
        var (host, commands, _, _) = Harness(new DemoPlugin());
        var original = await Create(host, commands);
        await Set(commands, original, "demo.style", "detailed");
        var statePath = Path.Combine(original.Directory, "state.json");
        var saved = File.ReadAllText(statePath);
        var (fresh, d, _, adapter) = Harness(version is null ? null : new DemoPlugin(version));
        var loaded = fresh.Activate(original.ProjectKey, original.SessionId);
        Assert.Equal("detailed", loaded.State.PluginAxes!["demo.style"]);
        Assert.NotEmpty(PluginSessionView.Problems(fresh.Plugins, loaded.Spec));
        Assert.IsType<Ack.Rejected>((await Set(d, loaded, "demo.style", "brief")).Ack);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Stepper(fresh.Store, adapter, plugins: fresh.Plugins).StepAsync(loaded, Array.Empty<Block>(), CancellationToken.None));
        Assert.Empty(adapter.Calls);
        Assert.Equal(saved, File.ReadAllText(statePath));
    }

    [Fact]
    public async Task StepSnapshotIsConsistent_AndFailedCallDoesNotUndoConcurrentAxisChanges()
    {
        var plugin = new DemoPlugin();
        var (host, commands, _, adapter) = Harness(plugin);
        var h = await Create(host, commands);
        plugin.OnPrompt = _ =>
        {
            Assert.IsType<Ack.Accepted>(Set(commands, h, Axes.Effort, "max").GetAwaiter().GetResult().Ack);
            Assert.IsType<Ack.Accepted>(Set(commands, h, Axes.Verify, "strict").GetAwaiter().GetResult().Ack);
            Assert.IsType<Ack.Accepted>(Set(commands, h, "demo.style", "detailed").GetAwaiter().GetResult().Ack);
        };
        adapter.ReplyWith(request =>
        {
            Assert.Equal("high", request.Effort);
            Assert.Contains("style=brief", RequestText(request));
            Assert.Contains("Keep the answer concise.", RequestText(request));
            Assert.DoesNotContain("Explain the reasoning with concrete examples.", RequestText(request));
            Assert.Contains("相关编译、测试或检查", RequestText(request));
            Assert.DoesNotContain("验证未通过时不得声称完成", RequestText(request));
            Set(commands, h, "demo.style", "brief").GetAwaiter().GetResult();
            throw new InvalidOperationException("upstream failed");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Stepper(host.Store, adapter, plugins: host.Plugins).StepAsync(h, "hello", CancellationToken.None));
        Assert.Equal("brief", h.State.PluginAxes!["demo.style"]);
        Assert.Equal("brief", host.Store.ReadState(h.ProjectKey, h.SessionId).PluginAxes!["demo.style"]);
        Assert.Equal("max", h.State.Axes![Axes.Effort]);
        Assert.Equal(SessionStatus.Idle, h.State.Status);
    }

    [Fact]
    public async Task LegacySessionDoesNotAutomaticallyEnableNewPlugins_AndWritesCurrentVersion()
    {
        var (host, commands, _, _) = Harness(null);
        var h = await Create(host, commands);
        // 当前格式 → 更旧的格式。⚠️ 别写死两个版本号：这条测的是「旧格式会升级到现在」，
        // 写成字面量就会在每次 +1 时红一次，而红的原因与它要守的性质无关。
        var current = Lilara.Core.Kernel.Json.FormatVersion;
        var legacy = current - 1;
        foreach (var name in new[] { "spec.json", "state.json" })
        {
            var path = Path.Combine(h.Directory, name);
            File.WriteAllText(path,
                File.ReadAllText(path).Replace($"\"version\":{current}", $"\"version\":{legacy}"));
        }
        var plugin = new DemoPlugin();
        var (fresh, d, _, adapter) = Harness(plugin);
        var loaded = fresh.Activate(h.ProjectKey, h.SessionId);
        Assert.Null(loaded.Spec.Plugins);
        Assert.Null(loaded.State.PluginAxes);
        Assert.IsType<Ack.Rejected>((await Set(d, loaded, "demo.style", "detailed")).Ack);
        adapter.Reply("ok");
        await new Stepper(fresh.Store, adapter, plugins: fresh.Plugins).StepAsync(loaded, "hello", CancellationToken.None);
        Assert.Equal(0, plugin.Calls);
        Assert.Equal(Lilara.Core.Kernel.Json.FormatVersion, fresh.Store.ReadState(h.ProjectKey, h.SessionId).Version);
    }

    [Fact]
    public async Task ReloadRestoresBoundDefaultsAndPreservesUnknownPluginValues()
    {
        var (host, commands, _, _) = Harness(new DemoPlugin());
        var h = await Create(host, commands);
        host.Store.WriteState(h.ProjectKey, h.State with { PluginAxes = new Dictionary<string, string> { ["removed.mode"] = "kept" } });
        var loaded = SessionHandle.Load(host.Store, h.ProjectKey, h.SessionId);
        Assert.Equal("brief", loaded.State.PluginAxes!["demo.style"]);
        Assert.Equal("kept", loaded.State.PluginAxes["removed.mode"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)loaded.State.PluginAxes)["demo.style"] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)loaded.Spec.Plugins![0].Defaults)["demo.style"] = "changed");
    }

    [Fact]
    public async Task MissingPluginRemainsVisibleInHttpMetadataAndSlotsReportProblem()
    {
        var (host, commands, _, _) = Harness(new DemoPlugin());
        var h = await Create(host, commands);
        await Set(commands, h, "demo.style", "detailed");
        var (fresh, d, bus, _) = Harness(null);
        await using var app = await new ControlServer(fresh, d, bus, 0).StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(10) };
        using var state = JsonDocument.Parse(await client.GetStringAsync($"/sessions/{h.SessionId}/state"));
        Assert.Equal("detailed", state.RootElement.GetProperty("axes").GetProperty("demo.style").GetString());
        var definition = Assert.Single(state.RootElement.GetProperty("axisDefinitions").EnumerateArray(),
            a => a.GetProperty("name").GetString() == "demo.style");
        Assert.False(definition.GetProperty("available").GetBoolean());
        Assert.Contains("missing", state.RootElement.GetProperty("slots").GetProperty("problem").GetString());
        await app.StopAsync();
    }

    [Fact]
    public async Task HttpSnapshotsExposeMetadataValuesAndTheSamePluginSlots()
    {
        var (host, commands, bus, _) = Harness(new DemoPlugin());
        var h = await Create(host, commands);
        await Set(commands, h, "demo.style", "detailed");
        await using var app = await new ControlServer(host, commands, bus, 0).StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(10) };
        using var axes = JsonDocument.Parse(await client.GetStringAsync("/axes"));
        Assert.Contains(axes.RootElement.GetProperty("axes").EnumerateArray(), a => a.GetProperty("name").GetString() == "demo.style");
        using var state = JsonDocument.Parse(await client.GetStringAsync($"/sessions/{h.SessionId}/state"));
        Assert.Equal("detailed", state.RootElement.GetProperty("axes").GetProperty("demo.style").GetString());
        Assert.Contains("style=detailed", Strings(state.RootElement.GetProperty("slots")));
        Assert.Contains("Explain the reasoning with concrete examples.", Strings(state.RootElement.GetProperty("slots")));
        Assert.Contains("浮动上下文", Strings(state.RootElement.GetProperty("slots")));
        await app.StopAsync();
    }

    private static string Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Array => string.Join("\n", e.EnumerateArray().Select(Strings)),
        JsonValueKind.Object => string.Join("\n", e.EnumerateObject().Select(p => Strings(p.Value))),
        _ => "",
    };

    private sealed class DemoPlugin(string version = "1") : IAxisPlugin
    {
        public PluginIdentity Identity { get; } = new("demo", version);
        public IReadOnlyList<AxisDefinition> Axes { get; } = [new AxisDefinition
        { Id = "demo.style", Label = "Style", Values = ["brief", "detailed"], DefaultValue = "brief",
            LevelDescriptions = new Dictionary<string, string>
            { ["brief"] = "Keep the answer concise.", ["detailed"] = "Explain the reasoning with concrete examples." } }];
        public IReadOnlyList<AxisCapability> Capabilities => [AxisCapability.None];
        public Action<AxisSnapshot>? OnPrompt { get; set; }
        public int Calls { get; private set; }
        public IReadOnlyList<PromptContribution> Prompt(AxisSnapshot snapshot)
        {
            Calls++;
            OnPrompt?.Invoke(snapshot);
            return [
                new() { PluginId = "demo", Key = "system", Slot = PromptSlot.System, Stable = true, Text = "system-marker" },
                new() { PluginId = "demo", Key = "primary", Slot = PromptSlot.Primary, Stable = true, Text = "primary-marker" },
                new() { PluginId = "demo", Key = "style", Slot = PromptSlot.Floating, Text = "style=" + snapshot.Values["demo.style"] }];
        }
    }
}

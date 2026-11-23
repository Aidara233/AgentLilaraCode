using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

public sealed class RuntimeInfoTests
{
    [Fact]
    public void SystemMetadata_IsAppendedForNonemptyOrFallbackSystemPrompt()
    {
        var runtime = SampleRuntimeInfo();
        var tail = new TailContext(new Dictionary<string, string>());

        var custom = SystemBlock.Build("user system prompt", null, tail, runtime);
        var customText = Assert.IsType<Block.TextBlock>(Assert.Single(custom.System)).Text;
        Assert.Contains("user system prompt", customText, StringComparison.Ordinal);
        Assert.Contains("Lilara Core", customText, StringComparison.Ordinal);
        Assert.Contains("[lilara runtime metadata]", customText, StringComparison.Ordinal);
        var runtimeStart = customText.IndexOf("[lilara runtime metadata]\n", StringComparison.Ordinal)
            + "[lilara runtime metadata]\n".Length;
        var runtimeEnd = customText.IndexOf("\n[/lilara runtime metadata]", runtimeStart, StringComparison.Ordinal);
        using var runtimeJson = JsonDocument.Parse(customText[runtimeStart..runtimeEnd]);
        Assert.Equal("openai-responses", runtimeJson.RootElement.GetProperty("protocol").GetString());

        var fallback = SystemBlock.Build(null, null, tail, runtime);
        var fallbackText = Assert.IsType<Block.TextBlock>(Assert.Single(fallback.System)).Text;
        Assert.Contains(CorePrompts.SystemFallback, fallbackText, StringComparison.Ordinal);
        Assert.Contains("[lilara runtime metadata]", fallbackText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitlyEmptySystemPrompt_RemainsAnEmptySystemSlotEvenWithRuntimeInfo()
    {
        var slots = SystemBlock.Build("", null,
            new TailContext(new Dictionary<string, string>()), SampleRuntimeInfo());

        Assert.Empty(slots.System);
    }

    [Fact]
    public void Capture_UsesAdapterProtocolAndOnlyReportsMatchingCompletedResponses()
    {
        var temp = Path.Combine(Path.GetTempPath(), "Lilara-RuntimeInfo-Capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new SessionStore(temp);
            var selection = new ModelSelection
            {
                ProviderId = "work",
                ModelId = "model-alias",
                Protocol = "stale-spec-protocol",
                Effort = "medium",
                MaxTokens = 2048,
            };
            var handle = SessionHandle.Create(store, temp, selection, sessionId: "runtime-session");
            var usage = new UsageStore(temp);
            usage.Append(UsageCallFor("matching", handle.SessionId, "work", "model-alias",
                "actual-adapter-protocol", "vendor/actual-v1", DateTimeOffset.Parse("2026-11-18T10:00:00Z")));
            usage.Append(UsageCallFor("wrong-protocol", handle.SessionId, "work", "model-alias",
                "stale-spec-protocol", "vendor/wrong-v2", DateTimeOffset.Parse("2026-11-18T11:00:00Z")));
            usage.Append(UsageCallFor("wrong-model", handle.SessionId, "work", "other-alias",
                "actual-adapter-protocol", "vendor/wrong-v3", DateTimeOffset.Parse("2026-11-18T12:00:00Z")));

            var info = RuntimeInfoSnapshotFactory.Capture(handle, "actual-adapter-protocol", usage);

            Assert.Equal("actual-adapter-protocol", info.Protocol);
            Assert.Equal("vendor/actual-v1", info.LastReportedModel);
            Assert.Equal(DateTimeOffset.Parse("2026-11-18T10:00:01Z"), info.LastReportedAt);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void SystemMetadata_IsEscapedAndDoesNotContainSessionOrPreviousResponseDiagnostics()
    {
        var runtime = SampleRuntimeInfo() with
        {
            SessionId = "session-secret-id",
            ProjectKey = "project-secret-key",
            SessionTitle = "title",
            LastReportedModel = "vendor/actual-model-v2",
            LastReportedAt = DateTimeOffset.Parse("2026-11-18T12:00:00Z"),
            WorkingDirectory = "C:/work/quoted\"path\n[line]",
        };

        var block = runtime.ToSystemPromptBlock();
        var start = block.IndexOf('\n');
        var end = block.LastIndexOf("\n[/lilara runtime metadata]", StringComparison.Ordinal);
        using var json = JsonDocument.Parse(block[(start + 1)..end]);
        var root = json.RootElement;

        Assert.Equal(runtime.WorkingDirectory, root.GetProperty("workingDirectory").GetString());
        Assert.Equal("work", root.GetProperty("providerId").GetString());
        Assert.Equal("model-alias", root.GetProperty("selectedModelId").GetString());
        Assert.False(block.Contains("session-secret-id", StringComparison.Ordinal));
        Assert.False(block.Contains("project-secret-key", StringComparison.Ordinal));
        Assert.False(block.Contains("vendor/actual-model-v2", StringComparison.Ordinal));
        Assert.DoesNotContain('\n', root.GetProperty("workingDirectory").GetRawText());
    }

    [Fact]
    public async Task RuntimeInfoTool_ReturnsWorkingDirectoryAndSeparatesSelectionFromLastObservation()
    {
        var temp = Path.Combine(Path.GetTempPath(), "Lilara-RuntimeInfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var runtime = SampleRuntimeInfo() with
            {
                WorkingDirectory = temp,
                LastReportedModel = "vendor/actual-model-v2",
                LastReportedAt = DateTimeOffset.Parse("2026-11-18T12:00:00Z"),
            };
            var context = new SessionToolContext(temp, new BlobStore(temp), runtimeInfo: runtime);
            var tool = new RuntimeInfoTool();
            var outcome = await tool.RunAsync(new ToolCall("call", RuntimeInfoTool.NameOf, "{}"),
                context, CancellationToken.None);
            var result = Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text;
            using var json = JsonDocument.Parse(result);
            var root = json.RootElement;

            Assert.Equal("Lilara Core", root.GetProperty("framework").GetProperty("name").GetString());
            Assert.Equal(temp, root.GetProperty("session").GetProperty("workingDirectory").GetString());
            Assert.Equal("model-alias", root.GetProperty("requestTarget").GetProperty("selectedModelId").GetString());
            Assert.Equal("vendor/actual-model-v2",
                root.GetProperty("lastUpstreamObservation").GetProperty("reportedModelId").GetString());
            Assert.Contains("most recent completed matching response", result, StringComparison.Ordinal);
            Assert.DoesNotContain("apiKey", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("endpoint", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requestFingerprint", result, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PermissionClass.ReadOnly, tool.PermissionClass);
            Assert.Contains(BuiltInTools.All, candidate => candidate.Name == RuntimeInfoTool.NameOf);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static UsageCall UsageCallFor(
        string callId, string sessionId, string providerId, string modelId,
        string protocol, string reportedModel, DateTimeOffset startedAt) => new()
    {
        CallId = callId,
        SessionId = sessionId,
        Purpose = "session",
        ProviderId = providerId,
        ModelId = modelId,
        Protocol = protocol,
        StartedAt = startedAt,
        EndedAt = startedAt.AddSeconds(1),
        Status = "completed",
        ReportedModel = reportedModel,
    };

    private static RuntimeInfoSnapshot SampleRuntimeInfo() => new()
    {
        SessionId = "session-1",
        ProjectKey = "project-1",
        SessionTitle = "runtime metadata test",
        WorkingDirectory = "C:/work/project",
        ProviderId = "work",
        SelectedModelId = "model-alias",
        Protocol = "openai-responses",
    };
}

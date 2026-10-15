using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

public sealed class UsageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-usage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static TokenUsage Usage(long input, long output) => new()
    {
        InputTokens = input,
        OutputTokens = output,
        TotalTokens = input + output,
    };

    [Fact]
    public void Append_ReadAndAggregateByProviderAndModel()
    {
        var store = new UsageStore(_root);
        store.Append(new UsageCall
        {
            CallId = "a",
            ProjectKey = "p",
            SessionId = "s1",
            ProviderId = "alpha",
            ModelId = "m1",
            Protocol = "chat",
            StartedAt = DateTimeOffset.Parse("2026-10-15T00:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-10-15T00:00:01Z"),
            Status = "completed",
            Usage = Usage(10, 4),
        });
        store.Append(new UsageCall
        {
            CallId = "b",
            ProjectKey = "p",
            SessionId = "s2",
            ProviderId = "beta",
            ModelId = "m2",
            Protocol = "responses",
            StartedAt = DateTimeOffset.Parse("2026-10-15T00:01:00Z"),
            Status = "failed",
        });

        var summary = store.Summarize();

        Assert.Equal(2, summary.Calls);
        Assert.Equal(1, summary.CallsWithUnknownUsage);
        Assert.Equal(10, summary.InputTokens);
        Assert.Equal(4, summary.OutputTokens);
        Assert.Collection(summary.ByProvider,
            first => { Assert.Equal("alpha", first.Key); Assert.Equal(10, first.InputTokens); },
            second => { Assert.Equal("beta", second.Key); Assert.Null(second.InputTokens); });
        Assert.Collection(summary.ByModel,
            first => Assert.Equal("m1", first.Key),
            second => Assert.Equal("m2", second.Key));
    }

    [Fact]
    public void ReadAll_SurvivesRestartAndKeepsUnknownDistinctFromZero()
    {
        var first = new UsageStore(_root);
        first.Append(new UsageCall
        {
            CallId = "zero",
            ProviderId = "p",
            ModelId = "m",
            Protocol = "chat",
            StartedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
        });

        var restarted = new UsageStore(_root);
        var call = Assert.Single(restarted.ReadAll());
        Assert.Equal(0, call.Usage?.InputTokens);
        Assert.Equal(0, restarted.Summarize().InputTokens);
        Assert.Equal(0, restarted.Summarize().CallsWithUnknownUsage);
    }

    [Fact]
    public async Task Stepper_RecordsACompletedSessionCall()
    {
        var sessionStore = new SessionStore(_root);
        var usage = new UsageStore(_root);
        var adapter = new FakeAdapter().ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.TextBlock("ok")],
            StopReason = "end_turn",
            Refused = false,
            Usage = Usage(12, 3),
        });
        var handle = SessionHandle.Create(sessionStore, @"C:\work", new ModelSelection
        {
            ProviderId = "provider",
            ModelId = "model",
            Protocol = Protocols.ClaudeMessages,
            Effort = "high",
            MaxTokens = 100,
        });

        await new Stepper(sessionStore, adapter, usage: usage)
            .StepAsync(handle, "hello", CancellationToken.None);

        var call = Assert.Single(usage.ForSession(handle.SessionId));
        Assert.Equal("session", call.Purpose);
        Assert.Equal("provider", call.ProviderId);
        Assert.Equal(12, call.Usage?.InputTokens);
        Assert.Equal(3, call.Usage?.OutputTokens);
        Assert.Equal("completed", call.Status);
    }
}

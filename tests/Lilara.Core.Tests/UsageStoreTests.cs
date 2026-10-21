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
        // 账本行自带写盘版本（只追加的文件，行与行可以来自不同版本；缺它读方无从判断口径）。
        Assert.Equal(Json.FormatVersion, call.FormatVersion);
    }

    // ---- 缓存命中率（2026-10-20）----
    //
    // 口径（`08` §3.1）：InputTokens 是**未命中输入**，CacheReadTokens 是**命中**，两者不相交；
    // 命中率 = 命中 / (命中 + 未命中)。没报缓存的调用**不进分母**——否则「未报告」会被显示成「命中变差」。

    private static UsageCall Call(string id, string protocol, long? input, long? read, long? output, string provider = "p") =>
        new()
        {
            CallId = id,
            ProviderId = provider,
            ModelId = "m",
            Protocol = protocol,
            StartedAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z"),
            Status = "completed",
            Usage = new TokenUsage { InputTokens = input, CacheReadTokens = read, OutputTokens = output },
        };

    [Fact]
    public void CacheHitRate_IsHitOverHitPlusMiss()
    {
        var call = Call("a", Protocols.OpenAiChat, input: 411, read: 3456, output: 78);

        Assert.Equal(3867, call.PromptTokens);
        Assert.Equal(3456d / 3867d, call.CacheHitRate!.Value, 12);
    }

    [Fact]
    public void CacheHitRate_IsUnknown_NotZero_WhenTheUpstreamDidNotReportCache()
    {
        // 没报缓存 ⇒ 命中率**算不出来**。这与「报了 0 命中」是两件事：
        // 前者该显示「未报告」，后者才该显示 0%。
        Assert.Null(Call("a", Protocols.OpenAiChat, input: 100, read: null, output: 5).CacheHitRate);

        var zero = Call("b", Protocols.OpenAiChat, input: 100, read: 0, output: 5);
        Assert.Equal(0d, zero.CacheHitRate);
    }

    [Fact]
    public void CacheHitRate_IsUnknown_WhenThereWasNoInputToHit()
    {
        // 0/0 不是 0%：空账本不能显示成「缓存全废」。
        Assert.Null(Call("a", Protocols.OpenAiChat, input: 0, read: 0, output: 5).CacheHitRate);
    }

    [Fact]
    public void Summary_Rate_ExcludesCallsThatDidNotReportCache_FromTheDenominator()
    {
        var store = new UsageStore(_root);
        // 一笔几乎全命中（3456/3867），一笔没报缓存。
        store.Append(Call("hit", Protocols.OpenAiChat, input: 411, read: 3456, output: 78));
        store.Append(Call("silent", Protocols.OpenAiChat, input: 9000, read: null, output: 10));

        var summary = store.Summarize();

        Assert.Equal(2, summary.Calls);
        Assert.Equal(1, summary.CallsWithKnownCache);
        Assert.Equal(3456, summary.CacheHitTokens);
        Assert.Equal(3867, summary.PromptTokens);                       // 分母不含 silent 的 9000
        Assert.Equal(3456d / 3867d, summary.CacheHitRate!.Value, 12);   // 不是 3456/12867
        // 但「未命中输入」的合计仍然是全量（两笔都报了输入）——两个数字回答的是两个问题。
        Assert.Equal(9411, summary.InputTokens);
    }

    [Fact]
    public void Summary_RateIsUnknown_WhenNoCallReportedCache()
    {
        var store = new UsageStore(_root);
        store.Append(Call("a", Protocols.OpenAiChat, input: 100, read: null, output: 5));

        var summary = store.Summarize();

        Assert.Null(summary.CacheHitRate);
        Assert.Null(summary.CacheHitTokens);
        Assert.Null(summary.PromptTokens);
        Assert.Equal(0, summary.CallsWithKnownCache);
    }

    [Fact]
    public void Summary_BucketsCarryTheirOwnRate()
    {
        var store = new UsageStore(_root);
        store.Append(Call("a", Protocols.OpenAiChat, input: 500, read: 500, output: 5, provider: "alpha"));
        store.Append(Call("b", Protocols.ClaudeMessages, input: 300, read: 1200, output: 5, provider: "beta"));

        var summary = store.Summarize();

        var alpha = summary.ByProvider.Single(b => b.Key == "alpha");
        var beta = summary.ByProvider.Single(b => b.Key == "beta");
        Assert.Equal(0.5, alpha.CacheHitRate);
        Assert.Equal(0.8, beta.CacheHitRate);
        Assert.Equal(0.68, summary.CacheHitRate);   // (500+1200) / (1000+1500)
    }

    // 旧行的口径归一：格式 8 之前 OpenAI 系的 inputTokens 是上游原值（**含**缓存）。
    // 不减的话「输入 + 缓存读取」会把同一批 token 数两遍；而 Anthropic 的行本来就是未命中，不能减。
    [Fact]
    public void LegacyNormalization_SubtractsCacheOnlyForOpenAiFamilies()
    {
        var legacyOpenAi = new TokenUsage { InputTokens = 3867, CacheReadTokens = 3456, OutputTokens = 78 };
        Assert.Equal(411, UsageJson.NormalizeLegacyInput(legacyOpenAi, Protocols.OpenAiChat).InputTokens);
        Assert.Equal(411, UsageJson.NormalizeLegacyInput(legacyOpenAi, Protocols.OpenAiResponses).InputTokens);

        var legacyClaude = new TokenUsage { InputTokens = 300, CacheReadTokens = 1200, OutputTokens = 78 };
        Assert.Equal(300, UsageJson.NormalizeLegacyInput(legacyClaude, Protocols.ClaudeMessages).InputTokens);

        // 缺缓存明细 ⇒ 没有可减的，原样返回（不是减成 0）。
        var unknown = new TokenUsage { InputTokens = 100, OutputTokens = 5 };
        Assert.Equal(100, UsageJson.NormalizeLegacyInput(unknown, Protocols.OpenAiChat).InputTokens);
    }

    [Fact]
    public void Append_StampsTheFormatVersion_SoReadersKnowWhichSemanticsTheRowHas()
    {
        var store = new UsageStore(_root);
        store.Append(Call("a", Protocols.OpenAiChat, input: 100, read: 0, output: 5));

        var line = File.ReadAllLines(store.Path).Single();
        Assert.Contains($"\"formatVersion\":{Json.FormatVersion}", line);

        var read = Assert.Single(new UsageStore(_root).ReadAll());
        Assert.Equal(Json.FormatVersion, read.FormatVersion);
    }
}

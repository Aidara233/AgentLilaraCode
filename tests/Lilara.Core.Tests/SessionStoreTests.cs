using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-store-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;

    public SessionStoreTests() => _store = new SessionStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SessionSpec Spec() => new()
    {
        SessionId = "s1",
        ProjectKey = "p1",
        WorkingDirectory = @"C:\work",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Model = new ModelSelection
        {
            ProviderId = "claude",
            ModelId = "claude-opus-5",
            Protocol = Protocols.ClaudeMessages,
            Effort = "high",
            MaxTokens = 16000,
        },
    };

    private static SessionState State(int turn) => new()
    {
        SessionId = "s1",
        Status = "Idle",
        Turn = turn,
        Pending = [],
    };

    private static TimelineEvent Msg(string text) => new TimelineEvent.MessageEvent
    {
        At = DateTimeOffset.UnixEpoch,
        From = Speaker.User,
        Blocks = [new Block.TextBlock(text)],
    };

    [Fact]
    public void Spec_RoundTrips()
    {
        _store.WriteSpec("p1", Spec());

        Assert.Equal(Spec(), _store.ReadSpec("p1", "s1"));
    }

    [Fact]
    public void State_RoundTripsAndOverwrites()
    {
        _store.WriteState("p1", State(1));
        _store.WriteState("p1", State(2));

        Assert.Equal(2, _store.ReadState("p1", "s1").Turn);
    }

    [Fact]
    public void State_RoundTripsAPopulatedPendingQueue()
    {
        // ⚠️ Pending 存在的全部理由就是「崩溃重启后待办不丢」——
        // 只测空队列等于没测这个字段。它序列化成 IReadOnlyList<string>，
        // 是有真实风险的接口类型，必须带值走一遍。
        var state = new SessionState
        {
            SessionId = "s1",
            Status = "WaitingForInput",
            Turn = 7,
            Pending = ["m1", "m2"],
        };

        _store.WriteState("p1", state);
        var back = _store.ReadState("p1", "s1");

        Assert.Equal("WaitingForInput", back.Status);
        Assert.Equal(7, back.Turn);
        // 逐元素比较——Pending 是集合，整记录 Assert.Equal 会退化成引用相等
        Assert.Equal(new[] { "m1", "m2" }, back.Pending);
    }

    [Fact]
    public void ListSessions_ReturnsMostRecentFirst()
    {
        // ⚠️ 调用方取 [0] 当"当前会话"——顺序错了会随机续到别的会话上
        void Make(string id, DateTimeOffset createdAt) =>
            _store.WriteSpec("p1", Spec() with { SessionId = id, CreatedAt = createdAt });

        Make("old", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Make("new", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Make("mid", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[] { "new", "mid", "old" }, _store.ListSessions("p1"));
    }

    [Fact]
    public void ListSessions_SkipsCorruptSpecRatherThanCrashingStartup()
    {
        // ⚠️ 一个损坏的会话目录不该让整个核心起不来。
        // 只判 File.Exists 挡不住这个——损坏的 JSON 会抛 JsonException 穿透出去。
        _store.WriteSpec("p1", Spec() with { SessionId = "good" });

        var badDir = _store.SessionDirectory("p1", "bad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "spec.json"), "{ this is not json");

        var ids = _store.ListSessions("p1");

        Assert.Equal(new[] { "good" }, ids);
        Assert.Null(_store.TryReadSpec("p1", "bad"));
    }

    [Fact]
    public void Timeline_AppendsInOrder()
    {
        _store.AppendEvent("p1", "s1", Msg("一"));
        _store.AppendEvent("p1", "s1", Msg("二"));

        var timeline = _store.ReadTimeline("p1", "s1");

        Assert.Equal(2, timeline.Count);
        Assert.Equal("一", ((Block.TextBlock)((TimelineEvent.MessageEvent)timeline[0]).Blocks[0]).Text);
        Assert.Equal("二", ((Block.TextBlock)((TimelineEvent.MessageEvent)timeline[1]).Blocks[0]).Text);
    }

    [Fact]
    public void Timeline_IsOneJsonObjectPerLine()
    {
        _store.AppendEvent("p1", "s1", Msg("a"));
        _store.AppendEvent("p1", "s1", Msg("b"));

        var lines = File.ReadAllLines(
            Path.Combine(_store.SessionDirectory("p1", "s1"), "history.jsonl"));

        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{", l));
    }

    [Fact]
    public void Timeline_ChineseSurvivesRoundTripByteForByte()
    {
        _store.AppendEvent("p1", "s1", Msg("看看这个报错"));

        var raw = File.ReadAllText(
            Path.Combine(_store.SessionDirectory("p1", "s1"), "history.jsonl"));

        Assert.Contains("看看这个报错", raw);
        var back = _store.ReadTimeline("p1", "s1");
        Assert.Equal("看看这个报错",
            ((Block.TextBlock)((TimelineEvent.MessageEvent)back[0]).Blocks[0]).Text);
    }

    [Fact]
    public void Timeline_EmptyFileReadsAsEmptyList()
    {
        Directory.CreateDirectory(_store.SessionDirectory("p1", "s1"));

        Assert.Empty(_store.ReadTimeline("p1", "s1"));
    }

    /// <summary>
    /// ⚠️ `Append` 用的是 <c>File.AppendAllText</c>，**不是原子**的：断电可能留下半行。
    /// 而 history.jsonl 只追加、永不重写（01 §12.4），那半行会一直在。
    /// 读的时候必须跳过它——完好前缀才是值钱的部分；
    /// 让一条半行把整个会话炸掉（重启后第一条 `history` 就直接吐栈）是本末倒置。
    /// </summary>
    [Fact]
    public void Timeline_SkipsTornLastLineRatherThanCrashing()
    {
        _store.AppendEvent("p1", "s1", Msg("一"));
        _store.AppendEvent("p1", "s1", Msg("二"));

        // 模拟断电截断：半个 JSON 对象，且**没有结尾换行**
        File.AppendAllText(
            Path.Combine(_store.SessionDirectory("p1", "s1"), "history.jsonl"),
            "{\"kind\":\"message\",\"at\":\"2026-09-30T00:00:00+00:00\",\"from\":\"u");

        var timeline = _store.ReadTimeline("p1", "s1");

        Assert.Equal(2, timeline.Count);
        Assert.Equal("二",
            ((Block.TextBlock)((TimelineEvent.MessageEvent)timeline[1]).Blocks[0]).Text);
    }
}

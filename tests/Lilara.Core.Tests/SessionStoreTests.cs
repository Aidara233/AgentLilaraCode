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
            Protocol = "claude-messages",
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
}

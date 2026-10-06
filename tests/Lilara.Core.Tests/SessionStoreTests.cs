using Lilara.Core.Protocol;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

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

    /// <summary>
    /// 项目键的顺序必须**确定**，而调用方会取 <c>[0]</c>（头在 `currentProject === null` 时
    /// 拿它当默认订阅的项目流）。
    ///
    /// <para>⚠️ <b>如实说明这条守卫的成色：它在本项目唯一支持的平台上「不修也是绿的」。</b>
    /// `Directory.GetDirectories` 的返回顺序由文件系统决定，而 Windows/NTFS <b>恰按文件名序</b>枚举，
    /// 项目键又是规范化后的小写串 ⇒ 默认返回的**碰巧**就是序数序。
    /// 所以它是一条**回归守卫**（防的是有人删掉 `OrderBy`、或换到别的文件系统），
    /// <b>不是</b>「先红后绿」的那种。声称它证明了「不修就红」是假话。</para>
    ///
    /// <para>⚠️ <b>它仍然有区分力，但区分的是「哪个比较器」</b>：下面故意造了含大写与 `_` 的目录名，
    /// 于是序数序与任何大小写不敏感的比较器会给出**不同的顺序**——
    /// 序数是 <c>A</c>(0x41) &lt; <c>_</c>(0x5F) &lt; <c>b</c>(0x62) &lt; <c>z</c>(0x7A)，
    /// 于是 <c>Alpha</c> 排在 <c>_under</c> **前面**；而忽略大小写时 <c>_</c> 会跑到最前。
    /// 换成 `OrdinalIgnoreCase` 或文化敏感比较器 ⇒ 这条红。
    /// <b>（期望值是跑出来核过的，不是手推的——我第一版就把它手推反了。）</b></para>
    /// </summary>
    [Fact]
    public void ListProjects_IsOrdinalSorted_NotWhateverTheFileSystemReturns()
    {
        // 造目录而不是造会话：`ListProjects` 只枚举目录名，它不校验键的形状。
        foreach (var pk in new[] { "zeta", "Alpha", "_under", "beta" })
            Directory.CreateDirectory(Path.Combine(_store.ProjectsRoot, pk));

        Assert.Equal(new[] { "Alpha", "_under", "beta", "zeta" }, _store.ListProjects());
    }

    // 没有项目目录时回空列表，不是抛。
    [Fact]
    public void ListProjects_WithNoProjectsRoot_ReturnsEmpty()
    {
        Assert.Empty(_store.ListProjects());
    }

    // ---- 格式版本闸 ----
    //
    // ⚠️ **这两条是「显式载入要响亮、扫盘要活下来」那对取舍的可执行形态。**
    // 折成一个 null 会让用户看到「会话不存在」——而那会把人引向删掉重来，
    // 正好是唯一一个会丢数据的做法。

    private void WriteRawState(string json)
    {
        var dir = _store.SessionDirectory("p1", "s1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), json);
    }

    [Fact]
    public void ReadState_WithANewerFormatVersion_IsRejectedWithTheReason()
    {
        WriteRawState("""{"version":99,"sessionId":"s1","status":"Idle","turn":0,"pending":[]}""");

        var ex = Assert.Throws<FileFormatException>(() => _store.ReadState("p1", "s1"));

        // 话里必须同时有：是哪一份、它多新、本程序认到哪、以及**别覆盖它**。
        Assert.Contains("version=99", ex.Message);
        Assert.Contains($"只认到 {Json.FormatVersion}", ex.Message);
        Assert.Contains("别覆盖它", ex.Message);
    }

    // ⚠️ **缺 `version` 键 = 版本 0**（这个字段引入之前写的那些），**不是**「未知 ⇒ 拒绝」。
    // 拒绝它们等于把用户在格式冻结期间攒下的全部会话作废——那比「静默当老格式读」坏得多。
    [Fact]
    public void ReadState_WithoutAVersionKey_IsTreatedAsTheOldestFormat()
    {
        WriteRawState("""{"sessionId":"s1","status":"Idle","turn":7,"pending":[]}""");

        Assert.Equal(7, _store.ReadState("p1", "s1").Turn);
    }

    // 扫盘那一侧：版本太新的会话**不参与列表**，但也不该让整个列表打不开。
    // 两种「读不出来」在返回值上仍然分不开（都是 null）——**区别只在 ReadSpec 那条路上**，
    // 它才是用户点名要打开某一个会话时会走到的。
    [Fact]
    public void TryReadSpec_WithANewerFormatVersion_ReturnsNullInsteadOfThrowing()
    {
        var dir = _store.SessionDirectory("p1", "s1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.json"), """{"version":99,"sessionId":"s1"}""");

        Assert.Null(_store.TryReadSpec("p1", "s1"));
    }

    [Fact]
    public void ReadSpec_WithANewerFormatVersion_IsRejectedWithTheReason()
    {
        var dir = _store.SessionDirectory("p1", "s1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.json"), """{"version":99,"sessionId":"s1"}""");

        Assert.Throws<FileFormatException>(() => _store.ReadSpec("p1", "s1"));
    }

    // ⚠️ **这条是手工 E2E 抓出来的**：`FindSession` 是按身份找（用户点了一个具体的会话），
    // `ListSessions` 是枚举——**两者的容错要求不同**。一开始两者共用 `TryReadSpec`，
    // 于是版本太新的会话在按身份找时也被吞成「不存在」，用户看到的理由是
    // 「会话 'xxx' 不存在」——而那句话会把人引向删掉重来。
    [Fact]
    public void FindSession_WithANewerFormatVersion_IsLoudInsteadOfPretendingItIsAbsent()
    {
        var dir = _store.SessionDirectory("p1", "s1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.json"), """{"version":99,"sessionId":"s1"}""");

        Assert.Throws<FileFormatException>(() => _store.FindSession("s1"));
    }

    // 反面：**损坏**的 spec.json 在按身份找时**照旧**被跳过（与 `TryReadSpec` 同一条路）。
    // 少了这条，「但也不该把损坏也一起抛出去」这条就没人守——而损坏的处置方式完全不同：
    // 重写一份就好，不该吓得用户以为数据是新版本写的。
    [Fact]
    public void FindSession_WithACorruptSpec_StillReportsAbsent()
    {
        var dir = _store.SessionDirectory("p1", "s1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.json"), "{ 这不是 JSON");

        Assert.Null(_store.FindSession("s1"));
    }

    private static TimelineEvent Msg(string text) => new TimelineEvent.MessageEvent
    {
        Id = "r1",
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
        // Pending 的理由是「崩溃重启后待办不丢」；它序列化成 IReadOnlyList<string>，必须带值走一遍。
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
        // Pending 是集合，整记录 Assert.Equal 会退化成引用相等，故逐元素比较。
        Assert.Equal(new[] { "m1", "m2" }, back.Pending);
    }

    [Fact]
    public void ListSessions_ReturnsMostRecentFirst()
    {
        // 调用方取 [0] 当「当前会话」，顺序错了会续到别的会话上。
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
        // 一个损坏的会话目录不该让整个核心起不来；只判 File.Exists 挡不住，损坏的 JSON 会抛 JsonException。
        _store.WriteSpec("p1", Spec() with { SessionId = "good" });

        var badDir = _store.SessionDirectory("p1", "bad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "spec.json"), "{ this is not json");

        var ids = _store.ListSessions("p1");

        Assert.Equal(new[] { "good" }, ids);
        Assert.Null(_store.TryReadSpec("p1", "bad"));
    }

    // ---- 销毁面要用的四条（提上来的项目枚举 + 存在性 + 删除 + 按 id 找） ----

    // 「存在性」≠「spec.json 读得出来」。损坏的 spec 让 TryReadSpec 返回 null，
    // 但那个目录是**真实存在**的 ⇒ 拿 TryReadSpec 当存在性判据，坏会话会**永远删不掉**，
    // 而用户看到的理由是「会话不存在」（一句假话）。
    [Fact]
    public void SessionExists_IsTrueForACorruptSpec_UnlikeTryReadSpec()
    {
        var badDir = _store.SessionDirectory("p1", "bad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "spec.json"), "{ this is not json");

        Assert.True(_store.SessionExists("p1", "bad"));
        Assert.Null(_store.TryReadSpec("p1", "bad"));   // 两个判据在这里**必须不一致**
        Assert.False(_store.SessionExists("p1", "nope"));
    }

    // 删掉整个目录：三个文件一起走。这是本仓唯一不可逆的动作。
    [Fact]
    public void DeleteSession_RemovesTheWholeDirectory()
    {
        _store.WriteSpec("p1", Spec());
        _store.WriteState("p1", State(1));
        _store.AppendEvent("p1", "s1", Msg("一"));

        Assert.True(_store.DeleteSession("p1", "s1"));
        Assert.False(Directory.Exists(_store.SessionDirectory("p1", "s1")));
        Assert.Empty(_store.ListSessions("p1"));
    }

    // 不存在 ⇒ false（调用方据此说「不存在」，而不是说删成了）。
    [Fact]
    public void DeleteSession_OnAMissingSession_ReturnsFalse()
    {
        Assert.False(_store.DeleteSession("p1", "nope"));
    }

    // 只剩 state.json 的**半删目录**也要删得掉——那正是该删的垃圾。
    [Fact]
    public void DeleteSession_RemovesAHalfWrittenDirectory()
    {
        var dir = _store.SessionDirectory("p1", "orphan");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), "{}");

        Assert.True(_store.DeleteSession("p1", "orphan"));
        Assert.False(Directory.Exists(dir));
    }

    // 按 id 找会话：**只扫盘、不载入**——跨项目也找得到（会话 id 全局唯一，但盘上没有全局索引）。
    [Fact]
    public void FindSession_ScansEveryProject()
    {
        _store.WriteSpec("p1", Spec() with { SessionId = "a" });
        _store.WriteSpec("p2", Spec() with { ProjectKey = "p2", SessionId = "b" });

        Assert.Equal("p1", _store.FindSession("a")!.Value.ProjectKey);
        Assert.Equal("p2", _store.FindSession("b")!.Value.ProjectKey);
        Assert.Null(_store.FindSession("nope"));
    }

    [Fact]
    public void ListProjects_ReturnsEveryDirectoryUnderProjectsRoot()
    {
        Assert.Empty(_store.ListProjects());   // 数据根还没建 ⇒ 空，不是抛

        _store.WriteSpec("p1", Spec() with { SessionId = "a" });
        _store.WriteSpec("p2", Spec() with { ProjectKey = "p2", SessionId = "b" });

        Assert.Equal(["p1", "p2"], _store.ListProjects().OrderBy(x => x, StringComparer.Ordinal));
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

    // Append 用的 File.AppendAllText 不是原子的，断电会留半行；history.jsonl 只追加不重写，读时须跳过半行。
    [Fact]
    public void Timeline_SkipsTornLastLineRatherThanCrashing()
    {
        _store.AppendEvent("p1", "s1", Msg("一"));
        _store.AppendEvent("p1", "s1", Msg("二"));

        // 模拟断电截断：半个 JSON 对象，没有结尾换行。
        File.AppendAllText(
            Path.Combine(_store.SessionDirectory("p1", "s1"), "history.jsonl"),
            "{\"kind\":\"message\",\"at\":\"2026-09-30T00:00:00+00:00\",\"from\":\"u");

        var timeline = _store.ReadTimeline("p1", "s1");

        Assert.Equal(2, timeline.Count);
        Assert.Equal("二",
            ((Block.TextBlock)((TimelineEvent.MessageEvent)timeline[1]).Blocks[0]).Text);
    }
}

using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// **记录 → 这一刻的形态**（`TimelineRender`）。
///
/// <para>⚠️ 这个类存在的理由是「只有一份」：`Stepper` 与 `SessionQueries.HistoryOf`
/// 都走它。而它做的三件事（解库引用 / 换回收渲染 / 解 `Ref`）**各有各的失败方式**——
/// 下面三条各钉一件。</para>
/// </summary>
public class TimelineRenderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-render-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly BlobStore _blobs;
    private readonly SessionHandle _handle;

    public TimelineRenderTests()
    {
        var data = Path.Combine(_root, "data");
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);

        _store = new SessionStore(data);
        _blobs = new BlobStore(data);

        _handle = SessionHandle.Create(_store, work, new ModelSelection
        {
            ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages,
            Effort = "high", MaxTokens = 1000,
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Append(params TimelineEvent[] events) =>
        _store.AppendEvents(_handle.ProjectKey, _handle.SessionId, events);

    private TimelineEvent.ToolResultEvent Result(string id, string callId, IReadOnlyList<Block> content) => new()
    {
        Id = id, At = CoreClock.Now(), CallId = callId, Content = content,
    };

    /// <summary>一条**过阈值**的结果 ⇒ 记录里降成一个库地址（大文件那条路）。</summary>
    private TimelineEvent.ToolResultEvent StoreResult(string id, string callId, params Block[] content) =>
        Result(id, callId, Reclaim.Materialize(content, _blobs));

    /// <summary>一条**小**结果 ⇒ 记录里内联（小文件那条路）。</summary>
    private TimelineEvent.ToolResultEvent InlineResult(string id, string callId, params Block[] content) =>
        Result(id, callId, Reclaim.Materialize(content, _blobs));

    private IReadOnlyList<TimelineEvent> Render() => TimelineRender.Render(_store, _handle);

    /// <summary>
    /// ⚠️ **库对象要解成内容**（`01` §8.4：模型压根不该意识到库存在）。
    ///
    /// <para>这条曾经漏过，后果是一次**单向门**：写入方过阈值就进库、而读取方没做
    /// ⇒ 适配器遇到 `StoreBlock` 直接抛 ⇒ **那个会话从此永久发不出消息**。</para>
    /// </summary>
    [Fact]
    public void DereferencesStoreBlocks_AndRestoresBlockLists()
    {
        // 一个正常的文本快照。
        var text = InlineResult("r1", "c1", new Block.TextBlock("文件内容"));
        // 一个**块数组**快照（混排 / 多块走的就是这条）。
        var mixed = new Block[] { new Block.TextBlock("这是那张图"), new Block.ImageBlock(new string('i', 64), "image/png") };
        var blocks = StoreResult("r2", "c2", mixed);

        Append(text, blocks);
        var rendered = Render();

        // ⚠️ 单文本块存的是**原始字节** ⇒ 解出来就该是那段原文。
        var r1 = (TimelineEvent.ToolResultEvent)rendered[0];
        Assert.Equal("文件内容", Assert.IsType<Block.TextBlock>(Assert.Single(r1.Content)).Text);

        // ⚠️ 块数组存的是 **JSON** ⇒ 必须还原成**那一串块**，不是「一个装着 JSON 的文本块」。
        // 混着读的后果：模型会看到一串转义过的 JSON，而它当时看到的是一段图文。
        var r2 = (TimelineEvent.ToolResultEvent)rendered[1];
        Assert.Equal(2, r2.Content.Count);
        Assert.Equal("这是那张图", Assert.IsType<Block.TextBlock>(r2.Content[0]).Text);
        Assert.IsType<Block.ImageBlock>(r2.Content[1]);
    }

    /// <summary>
    /// ⚠️ **`Ref` 要解成被引用那条的内容，而且是按<b>记录 id</b>找**（`01` §8.0.2 六 / 七）。
    ///
    /// <para>两个 id 在测试里刻意做成**不同**：用协议 `CallId` 找会找不到，
    /// 于是回一句「找不到了」——而那是**静默地少一段**（`fetch` 看起来成功，内容却是句错误提示）。
    /// ⇒ 这条测试是「用错 id」唯一能红的地方。</para>
    /// </summary>
    [Fact]
    public void DereferencesRef_ByRecordId_NotByProtocolCallId()
    {
        var original = InlineResult("r-original", "call-original", new Block.TextBlock("当时的内容"));

        // fetch 的结果：内容为空、引用指向原行。
        var fetched = new TimelineEvent.ToolResultEvent
        {
            Id = "r-fetch", At = CoreClock.Now(), CallId = "call-fetch",
            Content = [], Ref = "r-original",
        };

        Append(original, fetched);
        var rendered = (TimelineEvent.ToolResultEvent)Render()[1];

        Assert.Equal("当时的内容", Assert.IsType<Block.TextBlock>(Assert.Single(rendered.Content)).Text);
    }

    /// <summary>引用的目标不在 ⇒ 给一句人话，**不是空内容**（空会让模型以为「那次调用没有输出」）。</summary>
    [Fact]
    public void DereferencesRef_ToAMissingRow_IsAReadableMessage()
    {
        Append(new TimelineEvent.ToolResultEvent
        {
            Id = "r-fetch", At = CoreClock.Now(), CallId = "call-fetch",
            Content = [], Ref = "nope",
        });

        var rendered = (TimelineEvent.ToolResultEvent)Render()[0];
        Assert.Contains("nope", Assert.IsType<Block.TextBlock>(Assert.Single(rendered.Content)).Text);
    }

    /// <summary>
    /// ⚠️ **被引用的那条如果快照是 `StoreBlock`，也要解**——`fetch` 取回的是大文件时正是这个形状。
    /// 少这一步，取回的内容会以一个 `StoreBlock` 的形态交给适配器（而它不认识）。
    /// </summary>
    [Fact]
    public void DereferencesRef_WhoseTargetIsAStoreBlock()
    {
        var big = new string('x', BlobStore.InlineThresholdBytes + 1);
        var target = StoreResult("r-original", "call-original", new Block.TextBlock(big));
        Assert.IsType<Block.StoreBlock>(Assert.Single(target.Content));   // 确认它真的降成了地址

        Append(target, new TimelineEvent.ToolResultEvent
        {
            Id = "r-fetch", At = CoreClock.Now(), CallId = "call-fetch",
            Content = [], Ref = "r-original",
        });

        Assert.Equal(big, Assert.IsType<Block.TextBlock>(
            Assert.Single(((TimelineEvent.ToolResultEvent)Render()[1]).Content)).Text);
    }

    /// <summary>
    /// ⚠️ **回收与解引用互不干扰**：被回收的那条解出来的**不是**「一个装进库对象的块」，
    /// 而是注记（用户/头看到的是那句注记，不是原文，也不是库地址）。
    /// </summary>
    [Fact]
    public void ReclaimedResults_RenderAsTheNote_EvenWhenTheyAreStoreBlocks()
    {
        var big = new string('x', BlobStore.InlineThresholdBytes + 1);
        var target = StoreResult("r1", "c1", new Block.TextBlock(big));
        Append(target);

        _handle.SetReclaimed(new HashSet<string> { "r1" });

        var rendered = (TimelineEvent.ToolResultEvent)Render()[0];
        Assert.Contains("[已回收]", Assert.IsType<Block.TextBlock>(Assert.Single(rendered.Content)).Text);
    }
}

using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// **记录 → 这一刻的形态**（`TimelineRender`）。
///
/// <para>⚠️ <b>它现在做两件事</b>（曾经是三步，第三步「解 `Ref`」随那条机制一起删了）：
/// 解库引用 · 换回收渲染。下面各钉一件。</para>
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

    /// <summary>一条结果，内容**按写入侧的规则**落一遍（含进库）。</summary>
    private TimelineEvent.ToolResultEvent Result(string id, string callId, params Block[] content) => new()
    {
        Id = id, At = CoreClock.Now(), CallId = callId, Content = Reclaim.Materialize(content, _blobs),
    };

    private IReadOnlyList<TimelineEvent> Render() => TimelineRender.Render(_store, _handle);

    private static TimelineEvent.ToolResultEvent ResultAt(IReadOnlyList<TimelineEvent> rendered, int i) =>
        (TimelineEvent.ToolResultEvent)rendered[i];

    /// <summary>
    /// ⚠️ **库对象要解成内容**（`01` §8.4：模型压根不该意识到库存在）。
    ///
    /// <para>这条曾经漏过，后果是一次**单向门**：写入方过阈值就进库、而读取方没做
    /// ⇒ 适配器遇到 `StoreBlock` 直接抛 ⇒ **那个会话从此永久发不出消息**。</para>
    ///
    /// <para>⚠️ **两条路必须分得开**：单文本块存的是**原始字节**，混排 / 多块存的是
    /// **块数组 JSON**。混着读会让 `read` 的库对象吐出一段 JSON 来（而它本该是原文）。</para>
    /// </summary>
    [Fact]
    public void DereferencesStoreBlocks_AndRestoresBlockLists()
    {
        Append(
            // 小的：内联进记录（原文就在那儿）。
            Result("r1", "c1", new Block.TextBlock("小文件")),
            // 大的：降成一个库地址（**原始字节**）。
            Result("r2", "c2", new Block.TextBlock(new string('x', BlobStore.InlineThresholdBytes + 1))),
            // 混排：也降成一个库地址（**块数组 JSON**）。
            Result("r3", "c3",
                new Block.TextBlock("这是那张图"),
                new Block.ImageBlock(new string('i', 64), "image/png")));

        var rendered = Render();

        Assert.Equal("小文件", Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(rendered, 0).Content)).Text);

        var big = Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(rendered, 1).Content));
        Assert.Equal(BlobStore.InlineThresholdBytes + 1, big.Text.Length);

        // ⚠️ 块数组必须还原成**那一串块**，不是「一个装着 JSON 的文本块」。
        var mixed = ResultAt(rendered, 2).Content;
        Assert.Equal(2, mixed.Count);
        Assert.Equal("这是那张图", Assert.IsType<Block.TextBlock>(mixed[0]).Text);
        Assert.IsType<Block.ImageBlock>(mixed[1]);
    }

    /// <summary>
    /// ⚠️ **回收换的是渲染，不是记录**（`01` §8.0.2 一 / 八）：
    /// 被收的那条渲染成注记，**没被收的原样**——而盘上两份都还是原来的字节。
    /// </summary>
    [Fact]
    public void ReclaimedResults_RenderAsTheNote_OthersUntouched()
    {
        Append(
            Result("r1", "c1", new Block.TextBlock("被收的")),
            Result("r2", "c2", new Block.TextBlock("留着的")));

        _handle.SetReclaimed(new HashSet<string> { "r1" });
        var rendered = Render();

        var note = Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(rendered, 0).Content)).Text;
        Assert.Contains("[已回收]", note);
        Assert.Contains("r1", note);        // 注记给的是**记录 id**
        Assert.Contains("fetch(id=", note); // 而且是一条能直接抄走的调用

        Assert.Equal("留着的", Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(rendered, 1).Content)).Text);

        // ⚠️ **记录没动**：再读一遍盘，两条都还是原文（回收不写盘）。
        var raw = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId);
        Assert.Equal("被收的", Assert.IsType<Block.TextBlock>(Assert.Single(((TimelineEvent.ToolResultEvent)raw[0]).Content)).Text);
    }

    /// <summary>
    /// ⚠️ **大文件（记录里是库地址）被回收时，渲染出的是注记而不是「一个丢了的库对象」**。
    /// 次序错了（先回收后解引用）就会出现那个形状——因为地址是从**记录**算的，不是从渲染结果。
    /// </summary>
    [Fact]
    public void ReclaimedResults_ThatWereStoreBlocks_RenderAsTheNote_NotAsAMissingObject()
    {
        Append(Result("r1", "c1", new Block.TextBlock(new string('x', BlobStore.InlineThresholdBytes + 1))));

        _handle.SetReclaimed(new HashSet<string> { "r1" });
        var text = Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(Render(), 0).Content)).Text;

        Assert.Contains("[已回收]", text);
        Assert.DoesNotContain("找不到了", text);   // 「库对象没了」那句**不许**出现
    }
}

using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// **回收判定**（`01` §8.0.2 二 / 四）——本批最要紧的一组断言。
///
/// <para>⚠️ 最要紧的一条是 <see cref="Recoverable_CoversEveryShape_IncludingInlineAndMixed"/>：
/// 判据必须**不分大小、不分形状**。第一版按「字节在不在库里」判，于是把小文件整类判成
/// 「收不了」——而它们的内联内容**记录里一直就有**，`fetch` 从来取得回。</para>
/// </summary>
public class ReclaimTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-reclaim-" + Guid.NewGuid().ToString("N"));
    private readonly BlobStore _blobs;

    public ReclaimTests() => _blobs = new BlobStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private TimelineEvent.ToolResultEvent Result(string id, params Block[] content) => new()
    {
        Id = id,
        At = CoreClock.Now(),
        CallId = "call-" + id,
        Content = content,
    };

    /// <summary>把一条结果**按写入侧的规则**落一遍（含进库）。</summary>
    private TimelineEvent.ToolResultEvent Materialized(string id, params Block[] content) =>
        Result(id, [.. Reclaim.Materialize(content, _blobs)]);

    // ---- 判据 ----------------------------------------------------------------

    /// <summary>
    /// ⚠️ **判据不分大小、不分形状**（用户 2026-10-06 裁定）：
    /// 内联的、留地址的、混排的、纯图的——**一律可回收**。
    /// 区分只在「持久化到哪」，**模型视角不分大小**。
    /// </summary>
    [Fact]
    public void Recoverable_CoversEveryShape_IncludingInlineAndMixed()
    {
        // 小的：内联在记录里 ⇒ 内容自己就够。
        var small = Reclaim.Materialize([new Block.TextBlock("小")], _blobs);
        Assert.IsType<Block.TextBlock>(Assert.Single(small));
        Assert.True(Reclaim.Recoverable(small, _blobs));

        // 大的：降成了库地址，而那个对象在库里 ⇒ 够。
        var big = Reclaim.Materialize(
            [new Block.TextBlock(new string('x', BlobStore.InlineThresholdBytes + 1))], _blobs);
        Assert.IsType<Block.StoreBlock>(Assert.Single(big));
        Assert.True(Reclaim.Recoverable(big, _blobs));

        // 混排：块数组 JSON 进了库，记录里是原样那一串 ⇒ 够。
        var mixed = new Block[] { new Block.TextBlock("这是那张图"), new Block.ImageBlock(new string('i', 64), "image/png") };
        Assert.True(Reclaim.Recoverable(Reclaim.Materialize(mixed, _blobs), _blobs));

        // 纯图：一个字都没有，但内容就是那张图本身 ⇒ 够。
        Assert.True(Reclaim.Recoverable([new Block.ImageBlock(new string('j', 64), "image/png")], _blobs));
    }

    /// <summary>
    /// ⚠️ **唯一收不了的一档：内容指向一个不在库里的对象。**
    /// 那种情况下回收换出来的「已回收、可 fetch」是**假话**，所以不许收。
    /// </summary>
    [Fact]
    public void Recoverable_IsFalse_WhenTheContentPointsAtAMissingObject() =>
        Assert.False(Reclaim.Recoverable([new Block.StoreBlock(new string('a', 64), "text/plain")], _blobs));

    /// <summary>「什么都没返回」没有内容可回收——但它**不是**收不了，是没有东西可收。</summary>
    [Fact]
    public void Recoverable_IsFalse_ForEmptyContent() =>
        Assert.False(Reclaim.Recoverable([], _blobs));

    // ---- 判定 ----------------------------------------------------------------

    /// <summary>
    /// ⚠️ **内联的那一档也收得了**——这条是第一版写错的地方：按「在不在库里」判，
    /// 小文件整类被判成「收不了」，而回收器存在的意义正是收它。
    /// </summary>
    [Fact]
    public void Decide_IncludesSmallInlineResults()
    {
        var small = Materialized("small", new Block.TextBlock("小"));   // 内联
        var filler = Enumerable.Range(0, Reclaim.ProtectedRecentResults)
            .Select(i => Materialized($"f{i}", new Block.TextBlock("填充"))).ToArray();

        Assert.Contains(small.Id, Reclaim.Decide([small, .. filler], _blobs));
    }

    /// <summary>库对象真的没了 ⇒ 不收（收掉的后果是模型收到一句「可 fetch」而取不回）。</summary>
    [Fact]
    public void Decide_ExcludesResultsWhoseBytesAreGone()
    {
        var missing = Result("missing", new Block.StoreBlock(new string('a', 64), "text/plain"));
        var filler = Enumerable.Range(0, Reclaim.ProtectedRecentResults)
            .Select(i => Materialized($"f{i}", new Block.TextBlock("填充"))).ToArray();

        Assert.DoesNotContain(missing.Id, Reclaim.Decide([missing, .. filler], _blobs));
    }

    /// <summary>
    /// **保护带**：最近 <see cref="Reclaim.ProtectedRecentResults"/> 条**不动**
    /// ——哪怕它们完全可回收（`01` §11.3：保护的是**动作链**，不是内容预算）。
    /// </summary>
    [Fact]
    public void Decide_NeverTouchesTheProtectiveBand()
    {
        var all = Enumerable.Range(0, Reclaim.ProtectedRecentResults + 2)
            .Select(i => Materialized($"r{i}", new Block.TextBlock("都在记录里")))
            .ToList();

        var decided = Reclaim.Decide(all, _blobs);

        // 最前面两条在保护带之外 ⇒ 收；末尾 ProtectedRecentResults 条 ⇒ 一条都不动。
        Assert.Contains("r0", decided);
        Assert.Contains("r1", decided);
        foreach (var r in all.TakeLast(Reclaim.ProtectedRecentResults))
            Assert.DoesNotContain(r.Id, decided);
    }

    // 非工具结果的事件（用户/模型的消息）永远不在候选里。
    [Fact]
    public void Decide_IgnoresNonToolEvents()
    {
        var message = new TimelineEvent.MessageEvent
        {
            Id = "m1",
            At = CoreClock.Now(),
            From = Speaker.User,
            Blocks = [new Block.TextBlock("你说")],
        };

        var filler = Enumerable.Range(0, Reclaim.ProtectedRecentResults + 1)
            .Select(i => Materialized($"r{i}", new Block.TextBlock("x"))).ToList();

        Assert.DoesNotContain("m1", Reclaim.Decide([message, .. filler], _blobs));
    }

    // ---- 注记 ----------------------------------------------------------------

    /// <summary>
    /// 注记必含三样（`01` §8.0.2 四那张表），**而且给的是记录的稳定 id，不是协议 CallId**。
    /// 变异：把 <c>stableId</c> 换成 <c>CallId</c>，`fetch` 就再也取不回——这条测试要红。
    /// </summary>
    [Fact]
    public void NoteText_CarriesTheThreeThings_AndTheStableId()
    {
        var note = Reclaim.NoteText("7f3ab2c4d5e6");

        Assert.Contains("[已回收]", note);              // ① 标记本身
        Assert.Contains("\"7f3ab2c4d5e6\"", note);      // ② 取回地址（稳定 id，引号里是原样一个 id）
        Assert.Contains("重调", note);                  // ③ 「要现在的值就自己重调一次」
    }

    /// <summary>
    /// ⚠️ **渲染必须是纯的**：同一份状态给出**逐字节相同**的一句话。
    /// 变异：往注记里加一个时间戳 / 一个计数，这条要红——而那正是最容易顺手加上去的两样。
    /// </summary>
    [Fact]
    public void NoteText_IsByteForByteStable()
    {
        Assert.Equal(Reclaim.NoteText("abc123"), Reclaim.NoteText("abc123"));
    }

    // ---- 注入 ----------------------------------------------------------------

    /// <summary>
    /// ⚠️ **只降结果那一半**：<c>tool_use</c> 一律原样（它是模型自己的输出＝事实，而且是**锚**）。
    /// 同时断言**非候选与保护带里的结果一个字不动**。
    /// </summary>
    [Fact]
    public void Apply_DowngradesOnlyTheResult_AndOnlyTheReclaimedOnes()
    {
        var model = new TimelineEvent.MessageEvent
        {
            Id = "m1",
            At = CoreClock.Now(),
            From = Speaker.Model,
            Blocks = [new Block.ToolUseBlock("call-r1", "read", """{"path":"big.txt"}""")],
        };
        var reclaimedOne = Result("r1", new Block.TextBlock("原文很长很长"));
        var kept = Result("r2", new Block.TextBlock("还留着"));

        var applied = Reclaim.Apply([model, reclaimedOne, kept], new HashSet<string> { "r1" });

        // 模型消息（含 tool_use）逐字不动。
        var m = Assert.IsType<TimelineEvent.MessageEvent>(applied[0]);
        Assert.Equal(model.Blocks, m.Blocks);

        // 被回收的那条换成注记。
        var r1 = Assert.IsType<TimelineEvent.ToolResultEvent>(applied[1]);
        Assert.Equal(Reclaim.NoteText("r1"), Assert.IsType<Block.TextBlock>(Assert.Single(r1.Content)).Text);

        // 没被回收的原样。
        var r2 = Assert.IsType<TimelineEvent.ToolResultEvent>(applied[2]);
        Assert.Equal("还留着", Assert.IsType<Block.TextBlock>(Assert.Single(r2.Content)).Text);
    }

    // 空集合是**原样返回**（不复制、不改动）——回收器最常见的形态就是「这一步不收」。
    [Fact]
    public void Apply_WithNothingReclaimed_IsIdentity()
    {
        var one = Result("r1", new Block.TextBlock("x"));
        Assert.Same(one, Assert.Single(Reclaim.Apply([one], new HashSet<string>())));
    }

    // ---- 写入侧 --------------------------------------------------------------

    /// <summary>
    /// ⚠️ **内联与否只决定字节躺在哪**：小的**也**要进库。
    /// 不进库 ⇒ `fetch` 读不到 ⇒ 那条结果永远占着上下文，而回收器存在的意义正是收它。
    /// </summary>
    [Fact]
    public void Materialize_SmallSnapshot_IsBothInlineAndInTheStore()
    {
        var materialized = Reclaim.Materialize([new Block.TextBlock("小")], _blobs);

        // 记录里是原文。
        Assert.Equal("小", Assert.IsType<Block.TextBlock>(Assert.Single(materialized)).Text);

        // 库里也有，名字就是内容的哈希。
        Assert.True(_blobs.Has(BlobStore.Sha256Of(Encoding.UTF8.GetBytes("小"))));
    }

    /// <summary>
    /// 混排 / 多块**没有「原始字节」可言** ⇒ 存的是块数组规范 JSON，媒体类型与文本那条**不同**。
    /// ⚠️ 两条路的库对象语义不同，读的人必须分得开——合并之后 `read` 的库对象会吐出 JSON。
    /// </summary>
    [Fact]
    public void Materialize_MixedBlocks_StoresTheBlockList_WithItsOwnMediaType()
    {
        var big = new Block.TextBlock(new string('x', BlobStore.InlineThresholdBytes));
        var image = new Block.ImageBlock(new string('d', 64), "image/png");

        // ⚠️ 混排的 JSON 一定过线（那两个块的 JSON 比阈值长），所以它会降成地址。
        var materialized = Reclaim.Materialize([big, image], _blobs);

        var store = Assert.IsType<Block.StoreBlock>(Assert.Single(materialized));
        Assert.Equal(BlockList.MediaType, store.MediaType);
        Assert.Equal(
            BlockList.ToJson([big, image]),
            Encoding.UTF8.GetString(_blobs.TryGet(store.Sha256)!));
    }

    /// <summary>
    /// ⚠️ **降级之后照样可回收**（「过线就留地址」与形状无关）——
    /// 否则混排会莫名其妙地特例化：记录里只剩一个地址，而那个对象没人认得。
    /// </summary>
    [Fact]
    public void Materialize_ThenRecoverable_StillHolds()
    {
        var content = new Block[] { new Block.TextBlock(new string('y', BlobStore.InlineThresholdBytes + 1)) };

        var materialized = Reclaim.Materialize(content, _blobs);
        Assert.IsType<Block.StoreBlock>(Assert.Single(materialized));
        Assert.True(Reclaim.Recoverable(materialized, _blobs));
    }
}

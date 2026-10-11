using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// **回收之后，模型还够得到那条内容吗**——`01` §8.0.2 那条链的三环：
/// **内容进库（`06`）→ 记录里留地址 / 回收换渲染（0b + 回收批）→ `fetch` 取回（本批）**。
///
/// <para>⚠️ <b>这条链的中间一环从没验过</b>（2026-10-11）：`fetch` 的
/// 「记录里的地址 → 库里的字节」在生产里一直**不可达**——写侧与回收侧都写完了，
/// 而没有任何一条真实路径会去解那个地址。⇒ 三条断言各钉一环。</para>
///
/// <para>⚠️ <b>「用户手写的文件」与「工具写的文件」在库里是同一种字节</b>：
/// 这里连 `write` 的参数原文都测了一遍——它是**裸参数 JSON**，
/// 而库里另一种存法是**块数组 JSON**（`BlockList.MediaType`）。
/// 两条路读岔了的表现是「把一坨 JSON 交给模型」，而那不会报错。</para>
/// </summary>
public class ReclaimFetchTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-reclaim-fetch-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;

    private const string Original = "hello\nworld\n";

    public ReclaimFetchTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_work, "target.txt"), Original);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private BlobStore Blobs() => new(Path.Combine(_tmp, "data"));

    /// <summary>
    /// ⚠️ **这一次写不是「模拟」**：它走的正是 `UpdateTool` / `WriteTool` 写盘之前那条路
    /// （`context.EnsureStored(旧字节)` → `AtomicFile.Write` → 覆盖）。
    /// 自己拼一个 `StoreBlock` 塞进记录里会**跳过被测的那一环**——
    /// 而那一环（「记录里那个地址，库里真有东西吗」）恰恰是从没验过的那个。
    /// </summary>
    private void OverwriteWith(string content)
    {
        var context = new SessionToolContext(_work, Blobs());
        context.EnsureStored(Encoding.UTF8.GetBytes(Original));
        AtomicFile.Write(Path.Combine(_work, "target.txt"), content);
    }

    /// <summary>一条工具结果。⚠️ 带框架——渲染时它变成**独立的一块**（`01` §3.7），别让断言数错块。</summary>
    private static TimelineEvent.ToolResultEvent Result(string id, params Block[] content) => new()
    {
        Id = id,
        At = CoreClock.Now(),
        CallId = "call-" + id,
        Content = content,
        Framing = new ToolFraming("read", [new ToolFramingField("path", "target.txt")]),
    };

    private Task<ToolOutcome> Fetch(string id, params TimelineEvent[] timeline) =>
        new FetchTool().RunAsync(
            new ToolCall("c1", "fetch", JsonSerializer.Serialize(new { id }, Json.Options)),
            new SessionToolContext(_work, Blobs(), timeline), default);

    private static string TextOf(ToolOutcome outcome) =>
        string.Concat(outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));

    // ---- 第一环：写侧真的把旧字节放进了库 ----

    [Fact]
    public void TheWriteSide_ReallyPutsTheOldBytesInTheStore()
    {
        var sha = BlobStore.Sha256Of(Encoding.UTF8.GetBytes(Original));
        // 先确认前提：**改之前**它不在库里（否则下面的断言什么都证明不了）。
        Assert.False(Blobs().Has(sha));

        OverwriteWith("hello\nthere\n");

        Assert.Equal(Original, Encoding.UTF8.GetString(Blobs().TryGet(sha)!));
    }

    // ---- 第三环：`fetch` 取回的是**改之前**那串字节 ----

    /// <summary>
    /// ⚠️ <b>取回 ≠ 重读</b>（`01` §8.0.2 六）：「那时是什么样」与「现在是什么样」是两个问题。
    /// ⇒ 这条同时钉住另一半——文件**已经变了**，而取回的仍是旧的。
    /// </summary>
    [Fact]
    public async Task Fetch_ReturnsTheBytesFromBeforeTheOverwrite_NotTheFileAsItIsNow()
    {
        OverwriteWith("hello\nthere\n");   // 世界变了

        var text = TextOf(await Fetch("r1", Result("r1", new Block.TextBlock(Original))));

        Assert.Equal(Original, text);
        // 反证：文件本身已经不是那串了。
        Assert.Equal("hello\nthere\n", File.ReadAllText(Path.Combine(_work, "target.txt")));
    }

    /// <summary>
    /// ⚠️ <b>「存进去的是裸路径」那一版会在这里红</b>：
    /// `{"path":"target.txt","content":"…"}` 是**裸参数 JSON**，而库里另一种存法是
    /// **块数组 JSON**。按后者去读会解不出一串块 ⇒ 交回一句「读不出来」。
    /// </summary>
    [Fact]
    public async Task Fetch_OfAWriteToolsArgumentJson_ComesBackVerbatim()
    {
        var argsJson = JsonSerializer.Serialize(new { path = "target.txt", content = Original }, Json.Options);
        var sha = new SessionToolContext(_work, Blobs()).EnsureStored(Encoding.UTF8.GetBytes(argsJson));

        var text = TextOf(await Fetch("r1",
            Result("r1", new Block.StoreBlock(sha, "text/plain"))));

        Assert.Equal(argsJson, text);
    }

    /// <summary>大对象那条路：记录里留的是地址，字节在库（`01` §8.4）。取回要解得回来。</summary>
    [Fact]
    public async Task Fetch_OfAStoreBlock_ReadsThroughToTheBytes()
    {
        var big = new string('z', BlobStore.InlineThresholdBytes + 1);
        var sha = new BlobStore(Path.Combine(_tmp, "data")).Put(Encoding.UTF8.GetBytes(big));

        var text = TextOf(await Fetch("r1",
            Result("r1", new Block.StoreBlock(sha, "text/plain"))));

        Assert.Equal(big, text);
    }

    // ---- 那一环的两侧：回收换渲染 / 记录不动 ----

    /// <summary>
    /// ⚠️ **回收只动注入、不动持久化**（`01` §8.0.2 一 / 八）：`Apply` 换出来的是
    /// **这一次渲染**的形态，而记录里那条 `Content` 一个字没变 ⇒ 取回永远取得到。
    /// </summary>
    [Fact]
    public async Task Reclaim_ChangesOnlyTheRendering_TheRecordStillHoldsTheContent()
    {
        var result = Result("r1", new Block.TextBlock(Original));
        var applied = Reclaim.Apply([result], new HashSet<string> { "r1" });

        // 渲染那一份：换成注记了，而且给的是**记录 id**（不是协议 CallId）。
        var note = string.Concat(((TimelineEvent.ToolResultEvent)applied[0]).Content
            .OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("[已回收]", note, StringComparison.Ordinal);
        Assert.DoesNotContain(Original, note, StringComparison.Ordinal);

        // 而**记录**里那份原样——这正是 `fetch` 取得回的原因。
        Assert.Equal(Original, Assert.IsType<Block.TextBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(Original, TextOf(await Fetch("r1", result)));
    }

    /// <summary>
    /// ⚠️ <b>`fetch` 的结果是一条普通工具结果</b>（`01` §8.0.2 六末）：它照常进回收范围。
    /// 否则「回收 → 取回 → 永久占位」一次调用就把 GC 打败了——正是 §8.0.2 自己警告过的形状。
    /// </summary>
    [Fact]
    public async Task AFetchedResult_IsItselfReclaimable()
    {
        // 造一条「已经被取回过」的结果：它就是一条普通工具结果，内容与别的没有分别。
        var fetched = Result("r2", new Block.TextBlock(Original));
        var timeline = new List<TimelineEvent> { fetched };
        // 垫够保护带：`Decide` 只收最近 `ProtectedRecentResults` 条**之前**的。
        for (var i = 0; i < Reclaim.ProtectedRecentResults; i++)
            timeline.Add(Result($"p{i}", new Block.TextBlock("垫")));

        var reclaimed = Reclaim.Decide(timeline, Blobs());

        Assert.Contains("r2", reclaimed);
        // ⚠️ 而它能被收，靠的是那条**记录自己**——不是「它被取回过」这件事。
        Assert.True(Reclaim.Recoverable(fetched.Content, Blobs()));

        // 取回本身也不改盘：取完之后内容还在。
        Assert.Equal(Original, TextOf(await Fetch("r2", [.. timeline])));
    }
}

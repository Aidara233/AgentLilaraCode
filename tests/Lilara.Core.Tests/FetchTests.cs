using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// `fetch`（`01` §8.0.2 六）。⚠️ 最要紧的两条：**取回的是当时那份字节**
/// （不是现在的、不是重渲染的），以及**取回的结果里没有引用**（否则会串成链）。
/// </summary>
public class FetchTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-fetch-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly BlobStore _blobs;
    private readonly FetchTool _tool = new();

    public FetchTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
        _blobs = new BlobStore(_tmp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    /// <summary>造一个只含给定记录的会话句柄，并把句柄架在它上面（`fetch` 取的就是它）。</summary>
    private SessionToolContext ContextWith(params TimelineEvent[] timeline) =>
        new(_work, _blobs, timeline);

    /// <summary>一条**已经落过库**的工具结果（走写入侧那条路，与真实情形一致）。</summary>
    private TimelineEvent.ToolResultEvent StoredResult(string id, string text) => new()
    {
        Id = id,
        At = CoreClock.Now(),
        CallId = "call-" + id,
        Content = Reclaim.Materialize([new Block.TextBlock(text)], _blobs),
    };

    private Task<ToolOutcome> Fetch(string id) =>
        _tool.RunAsync(new ToolCall("c1", "fetch", $$"""{"id":"{{id}}"}"""), ContextWith(), default);

    // ---- 取回 ----------------------------------------------------------------

    /// <summary>
    /// ⚠️ **取回 = 读当时的字节，原样交回**。它**不是重渲染**——重渲染要过 `render(贡献, now)`，
    /// 而 `now` 一步一个 ⇒ 交回的就是另一个时刻的产物（`01` §8.0.2 二）。
    /// </summary>
    [Fact]
    public async Task Fetch_ReturnsTheBytesFromBackThen()
    {
        var result = StoredResult("r1", "当时的内容");
        var ctx = ContextWith(result);

        var outcome = await _tool.RunAsync(
            new ToolCall("c1", "fetch", """{"id":"r1"}"""), ctx, default);

        Assert.Equal("当时的内容", Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    // 大文件（过阈值 ⇒ 记录里是个地址）也要取得回来——那正是回收最常发生的一档。
    [Fact]
    public async Task Fetch_WorksForContentThatWentToTheStore()
    {
        var big = new string('x', BlobStore.InlineThresholdBytes + 1);
        var result = StoredResult("r1", big);
        Assert.IsType<Block.StoreBlock>(Assert.Single(result.Content));   // 确认它真的降成了地址

        var outcome = await _tool.RunAsync(
            new ToolCall("c1", "fetch", """{"id":"r1"}"""), ContextWith(result), default);

        Assert.Equal(big, Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    /// <summary>
    /// ⚠️ **取回的结果是「一串块」，不是一条引用**——那条引用机制已经作废
    /// （2026-10-06，理由见 <see cref="TimelineEvent.ToolResultEvent.Ref"/> 的注释：
    /// 零调用方，而它换来的三样全是负的）。
    ///
    /// <para>这条断言留着，钉的是**结果里真的有内容**——`fetch` 取的是当时的字节，
    /// 而模型下一轮就要看见它。空着交出去等于「取回了一个什么都取不回」。</para>
    /// </summary>
    [Fact]
    public async Task Fetch_ReturnsContent_NotAnEmptyShell()
    {
        var result = StoredResult("r1", "内容");
        var outcome = await _tool.RunAsync(
            new ToolCall("c1", "fetch", """{"id":"r1"}"""), ContextWith(result), default);

        Assert.NotEmpty(outcome.Content);
        Assert.Equal("内容", Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    /// <summary>
    /// `fetch` 一条 `fetch` 的结果**仍然取得回原内容**（不串成链）。
    /// 这是上一条的行为面：`fetch` 存的是**快照**，所以它能被再取一次。
    /// </summary>
    [Fact]
    public async Task Fetch_OfAFetchResult_StillYieldsTheOriginalBytes()
    {
        var original = StoredResult("r1", "原内容");

        // 模型先 fetch 了 r1：那条 fetch 的结果本身也是一条工具结果。
        var fetchResult = StoredResult("r2", "原内容");

        var outcome = await _tool.RunAsync(
            new ToolCall("c1", "fetch", """{"id":"r2"}"""), ContextWith(original, fetchResult), default);

        Assert.Equal("原内容", Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    // ---- 取不到 --------------------------------------------------------------

    /// <summary>
    /// ⚠️ **取不到要给一句人话，不是空结果**：空结果会让模型以为
    /// 「那次调用没有输出」，而它其实只是**找不到了**（`01` §8.0.2 六末）。
    /// </summary>
    [Fact]
    public async Task Fetch_UnknownId_IsAReadableMessage_NotEmptyContent()
    {
        var outcome = await Fetch("nope");

        Assert.NotEmpty(outcome.Content);
        Assert.Contains("nope", Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    /// <summary>
    /// ⚠️ **内联的快照也取得回**——内容**就在记录里**，`fetch` 读记录就够了。
    ///
    /// <para>这条钉的是「模型视角不分大小」：小结果内联进 jsonl、大结果留 `store/` 地址，
    /// 但两者对模型是**同一件事**。第一版的判据按「在不在库里」判，
    /// 于是把这一档整类判成「收不了」——而它一直取回得回。</para>
    /// </summary>
    [Fact]
    public async Task Fetch_WorksForInlineSnapshots_NotOnlyForTheStore()
    {
        // 小的 ⇒ 记录里内联（不是 StoreBlock），库里**没有**它的地址。
        var inline = StoredResult("r1", "很小的一段");
        Assert.IsType<Block.TextBlock>(Assert.Single(inline.Content));

        var outcome = await _tool.RunAsync(
            new ToolCall("c1", "fetch", """{"id":"r1"}"""), ContextWith(inline), default);

        Assert.Equal("很小的一段", Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    /// <summary>
    /// 那条结果**指向一个不在库里的对象** ⇒ 取不回，而且要说明白。
    ///
    /// <para>⚠️ 这是唯一真正收不了的一档（`Reclaim.Recoverable` 把它排除掉了），
    /// 所以这里的断言同时钉着「回收器不会造出取不回的结果」。</para>
    /// </summary>
    [Fact]
    public async Task Fetch_ContentWhoseBytesAreGone_IsAReadableMessage()
    {
        var missing = new TimelineEvent.ToolResultEvent
        {
            Id = "r1",
            At = CoreClock.Now(),
            CallId = "c1",
            Content = [new Block.StoreBlock(new string('a', 64), "text/plain")],
        };

        var outcome = await _tool.RunAsync(
            new ToolCall("c1", "fetch", """{"id":"r1"}"""), ContextWith(missing), default);

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text;
        Assert.Contains("a", text);          // 说得出是哪个对象没了
        Assert.NotEqual("", text);
    }

    // 缺参数也是一句人话，不是异常（工具失败是**内容**，`01` §3.6）。
    [Fact]
    public async Task Fetch_WithoutAnId_IsAReadableMessage()
    {
        var outcome = await _tool.RunAsync(new ToolCall("c1", "fetch", "{}"), ContextWith(), default);
        Assert.NotEmpty(outcome.Content);
    }

    // ---- 常驻 ----------------------------------------------------------------

    /// <summary>
    /// ⚠️ **`fetch` 必须常驻工具表**（`01` §8.0.2 五）：工具表在 Claude 协议下位于**前缀最前端**，
    /// 「有回收物时才出现」会让**第一次回收**全废一次前缀——正好抵消回收省下的东西。
    /// </summary>
    [Fact]
    public void Fetch_IsAlwaysInTheToolTable() =>
        Assert.Contains(BuiltInTools.All, t => t.Name == "fetch");
}

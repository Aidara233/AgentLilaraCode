using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// **记录 → 这一刻的形态**（`TimelineRender`）。
///
/// <para>⚠️ <b>它做三件事</b>：解库引用 · 按**已落盘的回收窗口**换渲染 · 加框架（外加投递截断）。
/// 下面各钉一件。</para>
///
/// <para>⚠️ <b>回收的<b>判据</b>不在这里</b>（2026-10-17 更正）：距离 / 点杀由
/// `session.gc` / 压缩在<b>开窗那一刻</b>用 `Reclaim.Resolve` 解析一次、落成 `ReclaimEvent`；
/// 渲染只读那条记录（`Reclaim.Decide`）。⚠️ 上一版让渲染每次现算，后果是
/// **每步都改历史中段 ⇒ 每步破一次缓存**，而且模型刚读到的内容在下一步就变成注记。
/// ⇒ 这里的测试**显式开窗**（`OpenWindow`），而不是「垫够保护带让渲染自己判」。</para>
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

    /// <summary>
    /// **开一次回收窗口**（2026-10-17）：回收只在窗口里发生——渲染发生在每一步，
    /// 而回收改的是前缀中段（`00` §3：热路径「绝不事后清理」）。
    ///
    /// <para>⚠️ 窗口里放哪些 id 由调用方给：真实路径是 `session.gc` 用
    /// <see cref="Reclaim.Resolve"/> 解析一次；这里直接把要收的 id 列出来，
    /// 因为这些测试钉的是<b>渲染</b>（换成注记 / 框架不动），不是判据本身。</para>
    /// </summary>
    private void OpenWindow(params string[] ids) =>
        Append(new ReclaimEvent
        {
            Id = Ids.New(), At = CoreClock.Now(), Trigger = "test", Ids = ids,
        });

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
        // ⚠️ **本测试钉的是「被收的换成注记、没被收的原样」这**两半****，
        // 不是「收了几条」——所以窗口只点名 r1 / r2，r3 原样，正好当「没被收」的那一半。
        Append(
            Result("r1", "c1", new Block.TextBlock("被收的")),
            Result("r2", "c2", new Block.TextBlock("也被收的")),
            Result("r3", "c3", new Block.TextBlock("没被点名的")));
        OpenWindow("r1", "r2");

        var rendered = Render();
        var reclaimed = Reclaim.Decide(
            _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId), _blobs);

        Assert.Contains("r1", reclaimed);
        Assert.Contains("r2", reclaimed);

        var note = Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(rendered, 0).Content)).Text;
        Assert.Contains("[已回收]", note);
        Assert.Contains("r1", note);        // 注记给的是**记录 id**
        Assert.Contains("fetch(id=", note); // 而且是一条能直接抄走的调用

        // ⚠️ **没被点名的那些原样**——那正是「只降一部分」的另一半。
        var untouched = rendered.OfType<TimelineEvent.ToolResultEvent>()
            .Where(r => !reclaimed.Contains(r.Id)).ToList();
        Assert.NotEmpty(untouched);
        Assert.All(untouched, r => Assert.Equal("没被点名的",
            Assert.IsType<Block.TextBlock>(Assert.Single(r.Content)).Text));

        // ⚠️ **记录没动**：再读一遍盘，那条还是原文（回收不写盘）。
        var raw = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId);
        Assert.Equal("被收的", Assert.IsType<Block.TextBlock>(Assert.Single(((TimelineEvent.ToolResultEvent)raw
            .First(e => e is TimelineEvent.ToolResultEvent r && r.Id == "r1")).Content)).Text);
    }

    /// <summary>
    /// ⚠️ **无窗口 ⇒ 渲染不回收**（2026-10-17 的核心性质，取代旧的
    /// 「判定住在渲染里、每次现算」那条）：同一份时间线反复渲染，
    /// 只要没人开窗（`session.gc` / 压缩 / release），结果始终是原文。
    ///
    /// <para>⚠️ 它钉的是「热路径绝不事后清理」（`00` §3）：若有人把距离 / 点杀搬回
    /// `Decide`，这条会红——这里的结果早就超过保护带条数，而<b>没有</b>任何窗口。</para>
    /// </summary>
    [Fact]
    public void NoWindow_MeansNoReclaim_EvenAfterManyResults()
    {
        Append(Result("r1", "c1", new Block.TextBlock("大")));
        Append([.. Enumerable.Range(0, Reclaim.ProtectedRecentResults + 2)
            .Select(i => Result($"pad{i}", $"padcall{i}", new Block.TextBlock("垫")))]);

        Assert.Contains("大", Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(Render(), 0).Content)).Text);

        // 再渲染一次——中间**没有任何人**触发过什么，结论必须逐字相同。
        Assert.Contains("大", Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(Render(), 0).Content)).Text);
    }

    /// <summary>
    /// ⚠️ **大文件（记录里是库地址）被回收时，渲染出的是注记而不是「一个丢了的库对象」**。
    /// 次序错了（先回收后解引用）就会出现那个形状——因为地址是从**记录**算的，不是从渲染结果。
    /// </summary>
    [Fact]
    public void ReclaimedResults_ThatWereStoreBlocks_RenderAsTheNote_NotAsAMissingObject()
    {
        Append(Result("r1", "c1", new Block.TextBlock(new string('x', BlobStore.InlineThresholdBytes + 1))));
        OpenWindow("r1");

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(Render(), 0).Content)).Text;

        Assert.Contains("[已回收]", text);
        Assert.DoesNotContain("找不到了", text);   // 「库对象没了」那句**不许**出现
    }

    // ---- 框架（`01` §3.7）------------------------------------------------------

    /// <summary>一条**带框架**的结果。框架是「那次调用返回了什么」= 事实。</summary>
    private TimelineEvent.ToolResultEvent Framed(string id, string callId, string tool, params Block[] content) => new()
    {
        Id = id, At = CoreClock.Now(), CallId = callId,
        Content = Reclaim.Materialize(content, _blobs),
        Framing = new ToolFraming(tool, [new ToolFramingField("路径", "a.log")]),
    };

    /// <summary>
    /// ⚠️ **框架渲染成它自己的一个块，恒定在内容之前**（`01` §3.7）。
    ///
    /// <para>「独立的块」是这条机制的全部要害，不是排版偏好——模型只能靠
    /// 「这是一个**块**」区分框架与内容。合并进同一个块之后，一个首行恰好长得像框架的
    /// **内容**就能冒充框架，而模型**没有任何办法分辨**。</para>
    /// </summary>
    [Fact]
    public void Frame_IsItsOwnBlock_BeforeTheContent()
    {
        Append(Framed("r1", "c1", "read", new Block.TextBlock("正文")));

        var content = ResultAt(Render(), 0).Content;

        Assert.Equal(2, content.Count);
        // ⚠️ 框架块末尾带一个 `\n`（2026-10-23）：文本化链路把相邻块直接接起来，
        // 不留分隔符就会得到 `[read 路径 a.log]正文`——「这是框架」在形状上就失效了。
        Assert.Equal("[read 路径 a.log]\n", Assert.IsType<Block.TextBlock>(content[0]).Text);
        Assert.Equal("正文", Assert.IsType<Block.TextBlock>(content[1]).Text);
    }

    /// <summary>
    /// ⚠️ **本批存在的理由**：回收换的是整个 `Content`——框架若混在里面会被**一起吃掉**。
    /// 分成两块之后，回收**只换内容**、框架**一个字不动**。
    ///
    /// <para>⚠️ 变异：让 `Reclaim.Apply` 连 `Framing` 一起换掉（或把框架拼进 `Content`）
    /// ⇒ 这条必须红。</para>
    /// </summary>
    [Fact]
    public void Reclaim_ReplacesTheContent_ButNotTheFrame()
    {
        Append(Framed("r1", "c1", "read", new Block.TextBlock("正文")));
        OpenWindow("r1");

        var content = ResultAt(Render(), 0).Content;

        // 框架仍在，而且**还是那一句**（回收状态不影响它——它是事实）。
        Assert.Equal("[read 路径 a.log]\n", Assert.IsType<Block.TextBlock>(content[0]).Text);
        // 内容换成了注记。
        Assert.Contains("[已回收]", Assert.IsType<Block.TextBlock>(content[1]).Text);
    }

    /// <summary>
    /// ⚠️ **没有框架的记录原样返回**（老记录、以及不需要框架的工具）——
    /// 这条钉的是「加字段不改老字节」那个结论在**渲染**这一侧也成立（本仓的 G20）。
    /// </summary>
    [Fact]
    public void AResultWithoutAFrame_RendersUnchanged()
    {
        Append(Result("r1", "c1", new Block.TextBlock("正文")));

        Assert.Equal("正文", Assert.IsType<Block.TextBlock>(Assert.Single(ResultAt(Render(), 0).Content)).Text);
    }

    /// <summary>
    /// ⚠️ <b>框架的渲染是**纯函数**</b>：同一条记录渲染两次必须**逐字节相同**。
    /// 一旦它带上时间/序号之类的东西，就成了「每步破一次前缀」——而那是**静默**的
    /// （`00` §5 铁律 1）。
    /// </summary>
    [Fact]
    public void Frame_RendersTheSameBytesEveryTime()
    {
        Append(Framed("r1", "c1", "read", new Block.TextBlock("正文")));

        var a = Assert.IsType<Block.TextBlock>(ResultAt(Render(), 0).Content[0]).Text;
        var b = Assert.IsType<Block.TextBlock>(ResultAt(Render(), 0).Content[0]).Text;

        Assert.Equal(a, b);
    }

    /// <summary>
    /// ⚠️ <b>本批唯一的致命不变量的守卫</b>（`01` §3.7 G18）：
    /// <b>内容的首行不许被当成框架。</b>
    ///
    /// <para>模型只能靠「这是<b>一个独立的块</b>」区分框架与内容。若框架被拼进同一个块
    /// （或内容恰好以同样的形状开头），<b>模型没有任何办法分辨</b>——
    /// 而那正是 §3.6 ①之二说的「最坏的失败不是少了信息，是<b>多了一条假信息</b>」。</para>
    ///
    /// <para>⚠️ 这条测试故意用一个**长得完全像框架的首行**：如果实现变成了"拼成一段文本"，
    /// 渲染出来的就是**两个** `[read …]`，而模型会以为有两次调用。</para>
    /// </summary>
    [Fact]
    public void ContentThatLooksLikeAFrame_IsNotTreatedAsOne()
    {
        var disguised = "[read a.log 1-8192/20971520]";
        Append(Framed("r1", "c1", "read", new Block.TextBlock(disguised + "\n真正的正文")));

        var content = ResultAt(Render(), 0).Content;

        // 两块，而不是被合并的一块。
        Assert.Equal(2, content.Count);
        // `[0]` 是**真的**框架（工具名 + 字段），`[1]` 是正文——而正文里那句伪装**原样还在**。
        Assert.Equal("[read 路径 a.log]\n", Assert.IsType<Block.TextBlock>(content[0]).Text);
        Assert.StartsWith(disguised, Assert.IsType<Block.TextBlock>(content[1]).Text);
    }

    // ---- 投递截断（2026-10-17）--------------------------------------------------

    /// <summary>一条工具结果 + 它的 `tool_use`（投递预算按工具名分，所以要有调用记录）。</summary>
    private void AppendCall(string callId, string tool) => Append(new TimelineEvent.MessageEvent
    {
        Id = "m-" + callId, At = CoreClock.Now(), From = Speaker.Model,
        Blocks = [new Block.ToolUseBlock(callId, tool, "{}")],
    });

    private IReadOnlyList<TimelineEvent> RenderForModel() =>
        TimelineRender.RenderForModel(_store, _handle);

    private static TimelineEvent.ToolResultEvent ToolResultById(IReadOnlyList<TimelineEvent> rendered, string id) =>
        rendered.OfType<TimelineEvent.ToolResultEvent>().Single(r => r.Id == id);

    private static string ContentOf(IReadOnlyList<TimelineEvent> rendered, string id) =>
        Assert.IsType<Block.TextBlock>(Assert.Single(ToolResultById(rendered, id).Content)).Text;

    /// <summary>
    /// ⚠️ **投递截断只保留开头**，尾部换成一条带稳定 id 与下一个偏移的注记
    /// （用户 2026-10-17 裁定）。
    ///
    /// <para>⚠️ 必须钉住的那条性质：`offset` 是**可兑付**的——它等于实际交付的字节数，
    /// 而不是预算数（注记本身不算、字符边界回退也不算）。否则模型照着它 `fetch`，
    /// 要么漏字节、要么把注记的一段再喂回来。</para>
    /// </summary>
    [Fact]
    public void ModelView_TruncatesOversizedResultsToTheHeadAndOffersAFetchOffset()
    {
        const string head = "HEAD-";
        const string tail = "-TAIL";
        var body = head + new string('x', ToolLimits.MaxResultBytes) + tail;
        AppendCall("c1", "read");
        Append(Result("r1", "c1", new Block.TextBlock(body)));

        var text = ContentOf(RenderForModel(), "r1");

        Assert.Contains("[已截断]", text);
        Assert.Contains("fetch(id=\"r1\"", text);
        Assert.Contains(head, text);
        Assert.DoesNotContain(tail, text);   // ⚠️ 只保留开头：尾部**不**出现

        // 注记里的偏移 = 实际交付量：取回从那里继续，拼起来恰好是原文。
        var match = System.Text.RegularExpressions.Regex.Match(text, @"offset=(\d+)");
        Assert.True(match.Success);
        var offset = int.Parse(match.Groups[1].Value);
        Assert.True(offset <= ToolLimits.MaxResultBytes);
        Assert.Equal(body[..offset], text[..offset]);   // 交付的恰好是原文前缀
    }

    /// <summary>
    /// ⚠️ **偏移必须是「实际交付的字节数」，不是预算数**：中文一字 3 字节，
    /// 预算切在某字中间时会回退 ⇒ 两者不相等。拿预算当偏移，`fetch` 会漏掉或重放字节。
    /// </summary>
    [Fact]
    public void ModelView_TruncationOffsetAccountsForUTF8BoundaryRollback()
    {
        // 每字 3 字节；总长不是 3 的整数倍时，预算切点必然落在字符中间。
        var body = string.Concat(Enumerable.Repeat("中", ToolLimits.MaxResultBytes / 3 + 1)) + "尾";
        AppendCall("c1", "read");
        Append(Result("r1", "c1", new Block.TextBlock(body)));

        var text = ContentOf(RenderForModel(), "r1");
        var offset = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"offset=(\d+)").Groups[1].Value);
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);

        Assert.True(offset < ToolLimits.MaxResultBytes, "切点落在字符中间时，偏移必须回退到字符边界。");
        Assert.Equal(0, offset % 3);                              // 都落在完整字符的起点上
        Assert.Equal(bodyBytes[..offset], System.Text.Encoding.UTF8.GetBytes(text)[..offset]);
    }

    /// <summary>
    /// ⚠️ **只有模型路径截断**：头 / 控制台看的是完整内容（那是查看存档，不是投递）。
    /// </summary>
    [Fact]
    public void HistoryView_DoesNotTruncate()
    {
        var body = new string('x', ToolLimits.MaxResultBytes + 100);
        AppendCall("c1", "read");
        Append(Result("r1", "c1", new Block.TextBlock(body)));

        var text = ContentOf(Render(), "r1");
        Assert.Equal(body.Length, text.Length);
        Assert.DoesNotContain("[已截断]", text);
    }

    /// <summary>
    /// ⚠️ **`fetch` 的结果不按普通预算再砍一次**（`ToolLimits.DeliveryBudgetOf`）：
    /// 否则「取回」等于取回了正好放不下的东西——功能自己抵消自己。
    /// </summary>
    [Fact]
    public void ModelView_DoesNotTruncateFetchResults()
    {
        var body = new string('x', ToolLimits.MaxResultBytes + 100);
        AppendCall("c1", "fetch");
        Append(Result("r1", "c1", new Block.TextBlock(body)));

        var text = ContentOf(RenderForModel(), "r1");
        Assert.Equal(body.Length, text.Length);
        Assert.DoesNotContain("[已截断]", text);
    }

    /// <summary>
    /// ⚠️ **已经是回收注记的不再截断**（两层注记叠在一起，模型分不清该照哪句做）。
    /// </summary>
    [Fact]
    public void ModelView_DoesNotTruncateReclaimNotes()
    {
        AppendCall("c1", "read");
        Append(Result("r1", "c1", new Block.TextBlock(new string('x', ToolLimits.MaxResultBytes + 100))));
        OpenWindow("r1");

        var text = ContentOf(RenderForModel(), "r1");
        Assert.Contains("[已回收]", text);
        Assert.DoesNotContain("[已截断]", text);
    }
}

using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **工具经由真实 <see cref="Stepper"/> 跑通**——从模型给出 <c>tool_use</c> 到结果落盘，
/// 中间走真的执行器、真的句柄、真的文件系统。
///
/// <para>⚠️ <b>为什么这组测试不能被手工 E2E 取代</b>：我试过用真模型诱导越界调用，
/// <b>三次都被模型在推理层拒了</b>（它认得 <c>..</c> 与绝对路径是越界，与文件名无关）。
/// ⇒ 那种 E2E 证的是「模型理解边界」，**不是「我的守卫拦住了」**——
/// 探针没碰到被测物。这里用**假适配器**直接脚本 `tool_use`，绕开模型的判断，
/// 才真的测到 <c>ResolvePath</c>。</para>
/// </summary>
public class StepperToolTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-step-tool-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();
    private readonly Stepper _stepper;

    public StepperToolTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(Path.Combine(_work, "src"));

        _store = new SessionStore(Path.Combine(_tmp, "data"));
        _stepper = new Stepper(_store, _adapter, BuiltInTools.All);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    };

    private SessionHandle NewSession() => SessionHandle.Create(_store, _work, Model());

    private static ModelReply Calls(params (string CallId, string Name, string Args)[] calls) => new()
    {
        Blocks = calls.Select(c => (Block)new Block.ToolUseBlock(c.CallId, c.Name, c.Args)).ToList(),
        StopReason = "tool_use",
        Refused = false,
    };

    private static string TextOf(TimelineEvent e) =>
        string.Concat(((TimelineEvent.ToolResultEvent)e).Content.OfType<Block.TextBlock>().Select(b => b.Text));

    /// <summary>
    /// 造一个 <c>read</c> 调用的参数。
    /// ⚠️ **走序列化器，不手拼字符串**：手拼时路径里的反斜杠会变成非法 JSON 转义
    /// （`{"path":"..\outside.txt"}` —— `\o` 不合法），而那个失败长得像「工具坏了」，
    /// 其实是我造测试数据的方式坏了（**实测踩过**）。
    /// </summary>
    private static string ReadArgs(string path) =>
        System.Text.Json.JsonSerializer.Serialize(new { path });

    // ---- 正常来回：`ls` → `read`（第二次调用依赖第一次的结果）----

    [Fact]
    public async Task Tools_RunThroughTheRealStepper_AndLandAsPairedEvents()
    {
        File.WriteAllText(Path.Combine(_work, "src", "auth.cs"), "class Auth {}");
        var handle = NewSession();

        _adapter.ReplyWith(_ => Calls(
            ("c1", "ls", """{"path":"src"}"""),
            ("c2", "read", ReadArgs("src/auth.cs"))));
        await _stepper.StepAsync(handle, "看看", default);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        // ⚠️ `StepAsync(handle, string)` 这个壳子**会先追加用户那条**（`01` §6.1 的次序），
        // 所以盘上是 [user][model][result][result] = 4 条。
        // 关键是**后三条挨着**：模型消息与它那批结果**一次落盘**（`01` §3.6 ④）。
        Assert.Equal(4, timeline.Count);
        Assert.IsType<TimelineEvent.MessageEvent>(timeline[0]);   // 用户
        var model = Assert.IsType<TimelineEvent.MessageEvent>(timeline[1]);
        Assert.Equal(2, model.Blocks.OfType<Block.ToolUseBlock>().Count());

        var results = timeline.OfType<TimelineEvent.ToolResultEvent>().ToList();
        Assert.Equal(2, results.Count);
        // ⚠️ **顺序 = 调用序**（`01` §4）——不是完成序。
        Assert.Equal(["c1", "c2"], results.Select(r => r.CallId));
        Assert.Contains("auth.cs", TextOf(results[0]));      // ls 的清单
        Assert.Equal("class Auth {}", TextOf(results[1]));   // read 的内容
    }

    // 同一步里落盘的事件是连续的——那是「一次 append」的意义（步原子性）。
    [Fact]
    public async Task Step_LandsModelMessageAndResults_InOneContiguousBatch()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "x");
        var handle = NewSession();

        _adapter.ReplyWith(_ => Calls(("c1", "read", """{"path":"a.txt"}""")));
        await _stepper.StepAsync(handle, "读", default);

        // 用户那条是**上一次**追加的（`StepAsync(handle, string)` 的壳子），
        // 所以盘上是 [user][model+result]——关键是后两条**挨着**。
        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Equal(3, timeline.Count);
        Assert.IsType<TimelineEvent.MessageEvent>(timeline[1]);
        Assert.IsType<TimelineEvent.ToolResultEvent>(timeline[2]);
    }

    // ---- 越界：本组真正要证的那条 ----

    /// <summary>
    /// ⚠️ **这是安全守卫的证据**：模型给出越界路径 ⇒ 结果是一条**拒绝**，
    /// 而工作目录之外的内容**从未进入记录**。
    /// </summary>
    [Theory]
    [InlineData("../../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    public async Task EscapingPath_IsRefused_AndTheOutsideContentNeverLands(string relative)
    {
        // 在**工作目录之外**放一个标记文件：守卫若失效，它会出现在记录里。
        File.WriteAllText(Path.Combine(_tmp, "outside.txt"), "LEAKED-CONTENT");

        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "read", ReadArgs(relative))));
        await _stepper.StepAsync(handle, "读", default);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var raw = string.Join("\n", timeline.Select(TimelineEvent.StableSerialize));

        Assert.DoesNotContain("LEAKED-CONTENT", raw);
        Assert.Contains("工作目录之外", TextOf(timeline.OfType<TimelineEvent.ToolResultEvent>().Single()));
    }

    /// <summary>
    /// 绝对路径那条路（我刚删掉一句**冗余**检查的那处）。
    /// ⚠️ 判据是**包含关系**，不是「以根开头」——见 <see cref="SessionToolContext.ResolvePath"/>。
    /// </summary>
    [Fact]
    public async Task AbsolutePath_IsRefused()
    {
        var outside = Path.Combine(_tmp, "abs-outside.txt");
        File.WriteAllText(outside, "LEAKED-ABS");

        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "read", $$"""{"path":"{{outside.Replace("\\", "\\\\")}}"}""")));
        await _stepper.StepAsync(handle, "读", default);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var raw = string.Join("\n", timeline.Select(TimelineEvent.StableSerialize));

        Assert.DoesNotContain("LEAKED-ABS", raw);
        Assert.Contains("工作目录之外", TextOf(timeline.OfType<TimelineEvent.ToolResultEvent>().Single()));
    }

    // 兄弟目录（前缀相同但不是子目录）——「以根开头」那个洞的具体形状。
    [Fact]
    public async Task SiblingDirectorySharingTheRootPrefix_IsRefused()
    {
        var sibling = _work + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "s.txt"), "LEAKED-SIBLING");

        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "read", """{"path":"../work-evil/s.txt"}""")));
        await _stepper.StepAsync(handle, "读", default);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.DoesNotContain(
            "LEAKED-SIBLING",
            string.Join("\n", timeline.Select(TimelineEvent.StableSerialize)));
    }

    // ---- 大快照进库 ----

    /// <summary>
    /// ⚠️ 快照过阈值 ⇒ **字节进库、记录里只留地址**（`01` §8.4）。
    /// 这条同时验证了「工具只交出内容，存哪儿由核心判」那条分工真的成立。
    /// </summary>
    [Fact]
    public async Task LargeSnapshot_GoesToTheBlobStore_AndTheRecordKeepsOnlyAnAddress()
    {
        var big = new string('x', BlobStore.InlineThresholdBytes + 100);
        File.WriteAllText(Path.Combine(_work, "big.txt"), big);

        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "read", """{"path":"big.txt"}""")));
        await _stepper.StepAsync(handle, "读", default);

        var result = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single();

        var store = Assert.IsType<Block.StoreBlock>(Assert.Single(result.Content));
        // 大对象真的在库里，而且**内容一致**。
        var blob = new BlobStore(_store.DataRoot).TryGet(store.Sha256);
        Assert.NotNull(blob);
        Assert.Equal(big, System.Text.Encoding.UTF8.GetString(blob!));
        // ⚠️ 引用与指纹照旧在（它们与快照落在哪无关）。
        Assert.Equal("big.txt", result.DependsOn);
        Assert.NotNull(result.Fingerprint);
    }

    /// <summary>
    /// ⚠️ **库引用（`StoreBlock`）必须在请求里被解成真内容**——这是实测事故的回归测试（2026-10-06）。
    ///
    /// <para>写入方（`ToolExecutor`：快照过阈值就进库、记录里留地址）先上线，而**读取方没有**
    /// ⇒ 适配器遇到 `StoreBlock` 直接抛「无法翻译的块」⇒ **那个会话从此永久发不出消息**
    /// （记录只追加，改不掉）。</para>
    ///
    /// <para>⚠️ 这条测的是**下一步的请求**（`_adapter.Calls`），不是落盘——落盘那一半本来就是对的；
    /// 缺的是「谁把地址读回来」。</para>
    /// </summary>
    [Fact]
    public async Task SecondStep_DereferencesBlobAddresses_InsteadOfChokingOnThem()
    {
        var big = new string('x', BlobStore.InlineThresholdBytes + 100);
        File.WriteAllText(Path.Combine(_work, "big.txt"), big);

        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "read", ReadArgs("big.txt"))));
        await _stepper.StepAsync(handle, "读", default);

        // 确认前提：盘上那条结果**确实**是个地址（不然这条测试测的是别的东西）。
        var stored = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single();
        Assert.IsType<Block.StoreBlock>(Assert.Single(stored.Content));

        // 第二步：请求里的那条结果必须是**解引用之后的文本**。
        _adapter.Reply("读到了");
        await _stepper.StepAsync(handle, [], default);

        var sent = Assert.Single(
            _adapter.Calls[1].Timeline.OfType<TimelineEvent.ToolResultEvent>(),
            r => r.CallId == "c1");
        // ⚠️ 请求侧是**两块**：框架 + 内容（`01` §3.7）。框架是渲染期加的独立一块。
        Assert.Equal(big, Assert.IsType<Block.TextBlock>(sent.Content[1]).Text);
        Assert.Contains("big.txt", Assert.IsType<Block.TextBlock>(sent.Content[0]).Text);
    }

    // 库对象**丢了**时给一段说得清的话，**不抛**——抛会让整个会话再也发不出消息。
    [Fact]
    public async Task SecondStep_WithAMissingBlob_SaysSo_InsteadOfThrowing()
    {
        var big = new string('x', BlobStore.InlineThresholdBytes + 100);
        File.WriteAllText(Path.Combine(_work, "big.txt"), big);
        var handle = NewSession();

        _adapter.ReplyWith(_ => Calls(("c1", "read", ReadArgs("big.txt"))));
        await _stepper.StepAsync(handle, "读", default);

        // 把库里那个对象删掉，模拟「库没了 / 被清了」。
        foreach (var f in Directory.GetFiles(new BlobStore(_store.DataRoot).Root)) File.Delete(f);

        _adapter.Reply("接着说");
        await _stepper.StepAsync(handle, [], default);   // 不抛

        var sent = Assert.Single(
            _adapter.Calls[1].Timeline.OfType<TimelineEvent.ToolResultEvent>(),
            r => r.CallId == "c1");
        // ⚠️ 内容在 `[1]`；`[0]` 是框架（它**不受**库对象丢失影响 —— 它是事实，不是内容）。
        Assert.Contains("找不到", Assert.IsType<Block.TextBlock>(sent.Content[1]).Text);
    }

    // 工具的调用在**请求**里也回传了（这一步的渲染看得见上一步的调用与结果）。
    [Fact]
    public async Task SecondStep_SeesThePreviousToolCallAndResult()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "内容");
        var handle = NewSession();

        _adapter.ReplyWith(_ => Calls(("c1", "read", """{"path":"a.txt"}""")));
        await _stepper.StepAsync(handle, "读", default);
        _adapter.Reply("读到了");
        await _stepper.StepAsync(handle, [], default);

        // 第二次请求的时间线里同时有 `tool_use` 与 `tool_result`——这正是「循环看得见上一步」。
        var second = _adapter.Calls[1];
        Assert.Contains(second.Timeline, e =>
            e is TimelineEvent.MessageEvent m && m.Blocks.OfType<Block.ToolUseBlock>().Any());
        Assert.Contains(second.Timeline, e => e is TimelineEvent.ToolResultEvent);
        // 工具表也发了（不发的话模型下一轮就再也调不了工具）。
        // ⚠️ **断言与工具表挂钩，不写死一个数**——那个数会随工具增加而腐
        // （本仓栽过：`2009d9f` 删掉的正是这一类计数）。这里要钉的是**「发的是同一份表」**。
        Assert.Equal(BuiltInTools.All.Count, second.Tools.Count);
    }

    // ---- 回收：整条链（`01` §8.0.2）----

    /// <summary>
    /// **回收 → 下一步的请求换成注记 → `fetch` 取回当时的字节**，全程走真推进器。
    ///
    /// <para>⚠️ 四种记录形状里**两种都要测**（内联的、留库地址的）——它们是两条不同的渲染路径。
    /// 这里用**大的**（过 `InlineThresholdBytes` ⇒ 记录里是 `StoreBlock`）。</para>
    ///
    /// <para>⚠️ **最硬的一条断言是「盘面一字不动」**：回收不是删除，是换个渲染
    /// （`01` §8.0.2 一 / 八）。它是可以直接测的，所以必须测。</para>
    /// </summary>
    [Fact]
    public async Task Reclaim_ReplacesTheResultWithANote_AndFetchBringsBackTheBytes()
    {
        var file = Path.Combine(_work, "src", "big.cs");
        var original = new string('x', BlobStore.InlineThresholdBytes + 1);
        File.WriteAllText(file, original);

        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "read", ReadArgs("src/big.cs"))));
        await _stepper.StepAsync(handle, "读", default);

        // ⚠️ **保护带会让「唯一的这条」落在窗口内** ⇒ 先垫四条结果，再**开一次窗**
        // （= `session.gc`：解析一次，落成窗口记录）。2026-10-17 起回收只发生在窗口里。
        for (var i = 0; i < Reclaim.ProtectedRecentResults; i++)
        {
            _adapter.ReplyWith(_ => Calls(($"f{i}", "read", ReadArgs("src/big.cs"))));
            await _stepper.StepAsync(handle, [], default);
        }

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var blobs = new BlobStore(_store.DataRoot);
        var ids = Reclaim.Resolve(timeline, blobs,
            new SessionToolContext(handle.Spec.WorkingDirectory, blobs, timeline));
        _store.AppendEvents(handle.ProjectKey, handle.SessionId, [Reclaim.Window("session.gc", ids, null)]);

        // ⚠️ **窗口记录是唯一新增的那条**：已有记录一字不改。渲染本身也不许再写盘
        // （回收只换注入）——所以先记下盘面，直接渲染一次后逐字节核一遍。
        var history = Path.Combine(_store.SessionDirectory(handle.ProjectKey, handle.SessionId), "history.jsonl");
        var before = File.ReadAllBytes(history);
        _ = TimelineRender.Render(_store, handle);
        Assert.Equal(before, File.ReadAllBytes(history));

        // 下一步的请求里：原文没了、注记在、`tool_use` 原样。
        _adapter.Reply("好");
        await _stepper.StepAsync(handle, [], default);
        var sent = _adapter.Calls[^1].Timeline;

        // 被收的那些：**从盘上现判一遍**（渲染用什么，这里就用什么——别各判一套）。
        var reclaimed = Reclaim.Decide(
            _store.ReadTimeline(handle.ProjectKey, handle.SessionId), new BlobStore(_store.DataRoot));
        Assert.NotEmpty(reclaimed);

        // `tool_use` 一个字不动（它是模型自己的输出，也是那个**锚**）。
        Assert.Contains(sent, e =>
            e is TimelineEvent.MessageEvent m && m.Blocks.OfType<Block.ToolUseBlock>()
                .Any(b => b.Name == "read" && b.Arguments.Contains("big.cs")));

        // ⚠️ **按 id 精确断言**：被收的那几条换成了注记，**没被收的原样**。
        // （写成「整条时间线里没有原文」会把保护带里那些同内容的结果一起算进去——
        // 而它们本来就该在。）
        var byId = sent.OfType<TimelineEvent.ToolResultEvent>().ToDictionary(r => r.Id, r => r);
        foreach (var rid in reclaimed)
        {
            var text = string.Concat(byId[rid].Content.OfType<Block.TextBlock>().Select(b => b.Text));
            Assert.Contains("[已回收]", text, StringComparison.Ordinal);
            Assert.Contains(rid, text, StringComparison.Ordinal);      // 注记给的是**记录 id**
            Assert.DoesNotContain(original, text, StringComparison.Ordinal);
        }

        // 没被回收的（保护带里的那些）内容原样——这是「只降一部分」的另一半。
        Assert.Contains(sent.OfType<TimelineEvent.ToolResultEvent>(),
            r => !reclaimed.Contains(r.Id) && r.Content.OfType<Block.TextBlock>().Any(b => b.Text == original));

        // 注记里给出的那个 id 真的取得回**当时那份字节**。
        var noteId = Assert.Single(reclaimed).ToString();
        var fetched = await new FetchTool().RunAsync(
            new ToolCall("f1", "fetch",
                System.Text.Json.JsonSerializer.Serialize(new { id = noteId })),
            new SessionToolContext(_work, new BlobStore(_store.DataRoot),
                _store.ReadTimeline(handle.ProjectKey, handle.SessionId)),
            default);

        Assert.Equal(original, Assert.IsType<Block.TextBlock>(Assert.Single(fetched.Content)).Text);
    }
}

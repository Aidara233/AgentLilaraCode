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
        Assert.Equal(2, second.Tools.Count);
    }
}

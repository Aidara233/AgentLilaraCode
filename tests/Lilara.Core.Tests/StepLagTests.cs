using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **回收 → 注记 → `fetch`** 这条链在**真实推进时序**下成立吗。
///
/// <para>⚠️ <b>为什么单这两条</b>（2026-10-11）：<see cref="StepperToolTests"/> 里那条
/// 「回收 → 注记 → `fetch` 取回」**是测试自己摆的时序**——它在落盘之后
/// 显式调 `Reclaim.Decide` + `SetReclaimed`。⇒ 它证的是「注记与 `fetch` 都工作」，
/// **不是**「会话在正常推进中自然走得到那一格」。
/// 而产品里若少了「下一步开始前重算回收」那一步，模型会在**收到注记的同一步**
/// 就被要求 `fetch(id)` ——而那个 id 在它那一趟渲染里还不存在。</para>
///
/// <para>⚠️ <b>本测试不许显式调 `Decide` / `SetReclaimed`</b>：那是被测物的内部动作，
/// 由推进器自己做。一经手，测的又变成「我自己摆的时序」了。</para>
///
/// <para>⚠️ <b>诱导手法</b>：不需要伪造模型。`read` 把文件**完整**读进来，
/// 而 `fetch` 只取回那么长一段——「是不是完整」这件事本来就摆在推进行为里
/// （它是观察的事实，不是意图），与「模型在这两轮之间发生了什么事」无关。
/// 参数里**出现过** `完整` 两个字不算读到：两轮传的都是 id，而 id 是随机 hex。</para>
/// </summary>
public class StepLagTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-step-lag-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();
    private readonly Stepper _stepper;
    private readonly SessionHandle _handle;

    public StepLagTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);

        _store = new SessionStore(Path.Combine(_tmp, "data"));
        _stepper = new Stepper(_store, _adapter, BuiltInTools.All);

        // ⚠️ **这一步是测试的脚手架，不是被测物**：`standard` 档下 `read` / `update` 是问不是放行
        // （`01` §3.5），而这里要的是「结果真的落下来」，不是再测一遍闸门。
        // ⚠️ 用的是**合法的档位名**（`Axes` 那张表里有 `authorized`）——
        // 闸门的判据在 `GateTests` 里，这里只要放行。
        // ⚠️ 走 `UpdateStateAndPersist` 而不是就地改字段：它**落盘**，于是后面重开的那份
        // 也是同一档（真实形态就是用户喊过 `session.setAxis permission authorized`）。
        _handle = SessionHandle.Create(_store, _work, Model());
        _handle.UpdateStateAndPersist(_store, s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal)
            {
                [Axes.Permission] = "authorized",
            },
        }, diskStatus: SessionStatus.Idle);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    };

    /// <summary>
    /// 每条结果里**所有**文本块拼起来——`fetch` 的结果可能不止一块
    /// （框架 + 内容，`01` §3.7）。
    /// </summary>
    private static string TextOf(TimelineEvent e) =>
        string.Concat(((TimelineEvent.ToolResultEvent)e).Content.OfType<Block.TextBlock>().Select(b => b.Text));

    /// <summary>
    /// ⚠️ **一次超大的 `read` 必须真的把内容交回给模型**——哪怕它过后会被回收。
    /// 「回收是正常发生的」不等于「把内容自动挡掉」：模型要**先看见**才谈得上「过后被收」。
    /// </summary>
    [Fact]
    public async Task ARead_HandsOverTheContent_EvenWhenItWillLaterBeReclaimed()
    {
        var original = new string('x', BlobStore.InlineThresholdBytes + 1);
        File.WriteAllText(Path.Combine(_work, "big.txt"), original);

        var handle = _handle;

        // 第 1 步：读一个大文件（它就是 §11.3 里「某步读了 20 个巨型文件」那个形状，
        // 也是被回收的头号对象——§8.0.2 说快照是头号可回收物）。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.ToolUseBlock("c1", "read", """{"path":"big.txt"}""")],
            StopReason = "tool_use",
            Refused = false,
        });
        await _stepper.StepAsync(handle, "读一下", default);

        // 第 2 步：纯文本（模型在收到那条结果之后再说话）。
        _adapter.Reply("读到了，很长。");
        await _stepper.StepAsync(handle, [], default);

        // ⚠️ 断言落在**第 2 步的请求**上：那是模型实际拿到的字节。
        // 拿盘面断言是不够的——盘面**永远不会**有回收的痕迹（回收只换渲染，§8.0.2 一/八）。
        var sent = _adapter.Calls[1].Timeline.OfType<TimelineEvent.ToolResultEvent>()
            .Single(r => r.CallId == "c1");
        Assert.Contains(original, TextOf(sent), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **模型收到一条「[已回收] … fetch(id) 取回当时的内容」之后就真的取得回**——
    /// 落盘、重开一个推进器（重启）、照常推一步，中间**没有任何人显式触发回收**。
    ///
    /// <para><b>这条在 2026-10-11 之前是红的</b>：`ResumeAsync` / `StepAsync` 落下工具结果之后
    /// 直接推下一步，中间没有重算回收 ⇒ 注记给的 id 在那个窗口里**还不存在**。
    /// 而保护带（`Reclaim.ProtectedRecentResults = 4`）把它藏住了——单条大结果的会话
    /// **一条都收不了**，于是「回收后取回」在真机上从没发生过。</para>
    ///
    /// <para>⚠️ **它不需要伪造模型**：第 4 步的 id 来自核心自己渲染出来的那行文字
    /// （模型看得到它、也照它发调用）。这是给真模型跑的同一件事的**忠实版**。</para>
    /// </summary>
    [Fact]
    public async Task Fetch_OfTheIdInTheNote_Works_AfterAWindow_AndAcrossRestart()
    {
        var original = new string('x', BlobStore.InlineThresholdBytes + 1);
        File.WriteAllText(Path.Combine(_work, "big.txt"), original);

        // ⚠️ 故意**不新造** handle：下面重开一个推进器接着推（= 重启的形状）。
        var handle = _handle;

        // ---- 第 1 步：读一个大文件（它是要被回收的那条） ----
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.ToolUseBlock("c1", "read", """{"path":"big.txt"}""")],
            StopReason = "tool_use",
            Refused = false,
        });
        await _stepper.StepAsync(handle, "读一下", default);

        // ⚠️ 这一条不是装饰：没有它，下面那条会被**内容也可能丢了**的失败混淆。
        var readId = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single().Id;

        // ⚠️ **再真读四次小文件**，而这几步是**这条测试成立的前提**：
        // 保护带保的是「最近的 N 条**工具结果**」（`Reclaim.ProtectedRecentResults`），
        // 而纯文本步**一条都不产生** ⇒ 光垫步数挪不动保护带。
        // 大结果要落到保护带之外，它后面至少得有 `ProtectedRecentResults` 条工具结果。
        // ⚠️ 用**常量**算，不写死 `4`——那个数是占位值，写死了它一改这条测试就静默失效。
        File.WriteAllText(Path.Combine(_work, "small.txt"), "小");
        for (var i = 0; i < Reclaim.ProtectedRecentResults; i++)
        {
            var callId = $"s{i}";
            _adapter.ReplyWith(_ => new ModelReply
            {
                Blocks = [new Block.ToolUseBlock(callId, "read", """{"path":"small.txt"}""")],
                StopReason = "tool_use",
                Refused = false,
            });
            await _stepper.StepAsync(handle, [], default);
        }

        // ---- 开一次窗（= `session.gc`）：解析一次，落成窗口记录 ----
        //
        // ⚠️ **2026-10-17 起必须显式开窗**：回收只发生在失效窗口里（`00` §3），
        // 渲染本身不再决定回收。这正是这条测试要覆盖的真实路径。
        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var blobs = new BlobStore(_store.DataRoot);
        var ids = Reclaim.Resolve(timeline, blobs,
            new SessionToolContext(handle.Spec.WorkingDirectory, blobs, timeline));
        _store.AppendEvents(handle.ProjectKey, handle.SessionId, [Reclaim.Window("session.gc", ids, null)]);

        // ---- 重开推进器（重启）：窗口是记录，重启之后照样读得到 ----
        var restarted = new Stepper(_store, _adapter, BuiltInTools.All);
        var reloaded = SessionHandle.Load(_store, handle.ProjectKey, handle.SessionId);

        // ---- 一条纯文本的模型回复 → 它下一趟就该看见注记 ----
        _adapter.Reply("好");
        await restarted.StepAsync(reloaded, [], default);

        // 前提确认（否则这条测试测的是别的东西）：模型**真的**看到了那行注记，
        // 而它给的 id 就是那条**记录**的 id。
        var withNote = _adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>()
            .Single(r => r.Id == readId);
        Assert.Contains("[已回收]", TextOf(withNote), StringComparison.Ordinal);
        Assert.Contains($"fetch(id=\"{readId}\")", TextOf(withNote), StringComparison.Ordinal);

        // ---- 第 7 步：模型照那行注记发出 `fetch(id)` ----
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.ToolUseBlock("c9", "fetch",
                System.Text.Json.JsonSerializer.Serialize(new { id = readId }))],
            StopReason = "tool_use",
            Refused = false,
        });
        await restarted.StepAsync(reloaded, [], default);

        // 第 8 步：一条纯文本。
        // ⚠️ **这一步不是多余的**：`fetch` 的**结果**在它自己那一步的渲染里当然还没有
        // （渲染发生在执行**之前**——那正是本测试存在的理由）。⇒ 要断言「模型真的收到了
        // 那串字节」，得看**下一步**的请求，而不是这一步的。
        _adapter.Reply("好，看到了。");
        await restarted.StepAsync(reloaded, [], default);

        // ⚠️ **断言落在请求上，不在工具的直接返回值上**：请求里那份是模型实际拿到的字节。
        // 直接调 `FetchTool` 会把被测的那条**时序**整个绕过去（那正是本测试存在的理由）。
        var fetched = _adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>()
            .Single(r => r.CallId == "c9");

        Assert.Equal(original, TextOf(fetched));
    }
}

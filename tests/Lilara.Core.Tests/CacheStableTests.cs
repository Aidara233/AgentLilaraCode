using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

/// <summary>
/// **`cache=stable` 形态**（2026-10-27 用户裁定，**默认**）：浮动读数不再每步插在位置 0，
/// 而是**作为真实内容、只在变化时**追加到历史末尾。
///
/// <para>⚠️ 它救的是什么：`dynamic` 那对合成调的 id 是 <c>floating-{turn}</c>、正文里还带
/// 「上次调用上下文总长度」——两者**每步都变** ⇒ 请求的第 1 个 token 起就与上一步不同 ⇒
/// 任何严格前缀缓存（OpenAI 自动缓存 / Anthropic <c>cache_control</c> / Gemini implicit）
/// **0 命中**，每步全量重算。`stable` 下没变化的那一步**一个字节都不写**，
/// 请求于是只剩「历史追加」这一种变化 ⇒ 共同前缀整段命中。</para>
///
/// <para>⚠️ 这批用例钉的四件事：① 位置 0 为空、记录里没有占位对；② 读数以**真内容**落进历史；
/// ③ 没有变化就**不再写**；④ 长度行按 **100k 取整**（否则它每步都变，等于没稳定）。</para>
/// </summary>
public sealed class CacheStableTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-stable-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();

    public CacheStableTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
        _store = new SessionStore(Path.Combine(_tmp, "data"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    // ⚠️ **不碰轴**：这里要的就是默认值（`stable`）。谁把默认改回 `dynamic`，这一整批会红。
    private SessionHandle NewSession() => SessionHandle.Create(_store, _work, new ModelSelection
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    });

    private Stepper NewStepper(UsageStore? usage = null) => new(_store, _adapter, BuiltInTools.All, usage: usage);

    private static ModelReply Say(string text) => new()
    {
        Blocks = [new Block.TextBlock(text)], StopReason = "end_turn", Refused = false,
    };

    private IReadOnlyList<TimelineEvent> Recorded(SessionHandle handle) =>
        _store.ReadTimeline(handle.ProjectKey, handle.SessionId);

    private static IEnumerable<TimelineEvent.ToolResultEvent> Readouts(IEnumerable<TimelineEvent> timeline) =>
        timeline.OfType<TimelineEvent.ToolResultEvent>()
            .Where(r => r.Id.EndsWith("-readout-result", StringComparison.Ordinal));

    /// <summary>
    /// ⚠️ **默认形态就是 `stable`**：没有把轴设成 `stable` 的会话也走这条路。
    /// 这条同时是「默认值被改回去」的报警器。
    /// </summary>
    [Fact]
    public void DefaultCacheMode_IsStable() =>
        Assert.True(SystemBlock.CacheStable(NewSession()));

    /// <summary>
    /// ⚠️ **位置 0 不再有合成对、记录里不再有占位对**——`dynamic` 那两条都消失了。
    /// 读数改以**真内容**出现在历史里：它不是「已回收」的占位，模型能直接读到轴表。
    /// </summary>
    [Fact]
    public async Task Step_PutsNoPairOnTop_AndTheReadoutLandsAsRealContent()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("好"));

        await NewStepper().StepAsync(handle, "你好", CancellationToken.None);

        var request = _adapter.Calls.Single();
        // ① 位置 0 是用户消息 —— `dynamic` 那对合成调（`floating-{turn}`）不再插在最前面。
        Assert.IsType<TimelineEvent.MessageEvent>(request.Timeline[0]);
        // ② 但读数**确实在这一次请求里**（它落在历史末尾，所以首步的模型也不会盲干）。
        var call = request.Timeline.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .Single(u => u.Name == FloatingContextTool.NameOf);
        Assert.StartsWith("floating-", call.CallId, StringComparison.Ordinal);

        // ② 记录里：**没有**占位对（`-reclaim-`），有**一条**真实读数（`-readout-`）。
        var recorded = Recorded(handle);
        Assert.DoesNotContain(recorded, e => e.Id.Contains("reclaim", StringComparison.Ordinal));
        var readout = Assert.Single(Readouts(recorded));
        // ③ 是真内容：正文里有轴表那条前缀，不是那句占位。
        var text = string.Concat(readout.Content.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains(CorePrompts.TailAxesPrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FloatingContextTool.ReclaimedBody, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **没有变化就一个字节都不写**：这是「前缀完全稳定」的机械判据。
    /// 两步的读数正文逐字相同 ⇒ 第二步不该再追加一条。
    /// </summary>
    [Fact]
    public async Task SecondStepWithoutChanges_WritesNoSecondReadout()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一"));
        await NewStepper().StepAsync(handle, "第一步", CancellationToken.None);
        var afterFirst = Readouts(Recorded(handle)).Count();

        _adapter.ReplyWith(_ => Say("二"));
        await NewStepper().StepAsync(handle, "第二步", CancellationToken.None);

        Assert.Equal(afterFirst, Readouts(Recorded(handle)).Count());
    }

    /// <summary>
    /// ⚠️ **变了就写一条新的**——而且写的是新值：改轴之后历史里会多一份读数。
    /// 旧的那份**留着**（追加，不改写），这是与 `dynamic` 的另一个区别：历史上看得见读数变过。
    /// </summary>
    [Fact]
    public async Task ChangingAnAxis_AppendsAFreshReadout()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一"));
        await NewStepper().StepAsync(handle, "第一步", CancellationToken.None);
        var before = Readouts(Recorded(handle)).Count();

        handle.WithAxis(Axes.Verify, "strict");
        _adapter.ReplyWith(_ => Say("二"));
        await NewStepper().StepAsync(handle, "第二步", CancellationToken.None);

        var after = Readouts(Recorded(handle)).ToList();
        Assert.Equal(before + 1, after.Count);
        Assert.Contains("strict", string.Concat(after[^1].Content.OfType<Block.TextBlock>().Select(b => b.Text)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **长度行按 100k 向下取整**（用户裁定「token 统计整 100k 时播报」）：
    /// 精确值每步都变，留着它等于把开关关掉。
    /// </summary>
    [Theory]
    [InlineData(950_000, "约 900k")]
    [InlineData(100_000, "约 100k")]
    // 不足一档 ⇒ **整行不给**（不是「约 0k」）。
    [InlineData(99_999, null)]
    public async Task UsageLine_IsBucketedToHundredK(long inputTokens, string? expected)
    {
        var handle = NewSession();
        var usage = new UsageStore(_store.DataRoot);
        usage.Append(new UsageCall
        {
            CallId = Ids.New(), SessionId = handle.SessionId, ProviderId = "p", ModelId = "m",
            Protocol = Protocols.ClaudeMessages, StartedAt = CoreClock.Now(), Status = "completed",
            Usage = new TokenUsage { InputTokens = inputTokens },
        });

        _adapter.ReplyWith(_ => Say("好"));
        await NewStepper(usage).StepAsync(handle, "你好", CancellationToken.None);

        var text = string.Concat(Readouts(Recorded(handle)).Single()
            .Content.OfType<Block.TextBlock>().Select(b => b.Text));

        if (expected is null)
        {
            Assert.DoesNotContain("上次调用上下文总长度", text, StringComparison.Ordinal);
            return;
        }

        Assert.Contains($"上次调用上下文总长度：{expected} tokens", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **两次「没变化」的请求，除历史追加外逐字节相同**——这是缓存能命中的定义。
    /// 具体钉：<c>Slots</c>（系统 / 首要 / 尾槽位）逐字相同，且时间线里**没有**任何东西
    /// 因为「步数变了」而变（`dynamic` 那对 id 带 turn，正是这一条的反面）。
    /// </summary>
    [Fact]
    public async Task TwoQuietSteps_ProduceTheSamePromptPrefix()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一"));
        await NewStepper().StepAsync(handle, "第一步", CancellationToken.None);
        var first = _adapter.Calls.Single();

        _adapter.ReplyWith(_ => Say("二"));
        await NewStepper().StepAsync(handle, "第二步", CancellationToken.None);
        var second = _adapter.Calls[1];

        // 三块提示词：逐字相同（轴没动、计划没动、插件没动、长度档位没跨）。
        Assert.Equal(string.Concat(first.Slots.System.OfType<Block.TextBlock>().Select(b => b.Text)),
            string.Concat(second.Slots.System.OfType<Block.TextBlock>().Select(b => b.Text)));
        Assert.Equal(string.Concat(first.Slots.Tail.OfType<Block.TextBlock>().Select(b => b.Text)),
            string.Concat(second.Slots.Tail.OfType<Block.TextBlock>().Select(b => b.Text)));
        Assert.Equal(first.Tools.Count, second.Tools.Count);

        // 时间线：第二步以第一步的全部事件为前缀（**位置 0 没有被插进任何东西**）。
        Assert.True(second.Timeline.Count > first.Timeline.Count);
        for (var i = 0; i < first.Timeline.Count; i++)
            Assert.Equal(first.Timeline[i].Id, second.Timeline[i].Id);
    }

    /// <summary>
    /// ⚠️ **逐字节**（不只是 id）：第二步请求的每一条事件（`id` + 全部块文本）必须与第一步
    /// 完全一致，且第二步以第一步的整条时间线为**前缀**。
    /// 这条比 `TwoQuietSteps_ProduceTheSamePromptPrefix` 更狠——那条只比了 id 与槽位，
    /// 不够：缓存命中的判据是**字节**，而「id 一样、内容被改写」正是回收/压缩会干的事。
    /// </summary>
    [Fact]
    public async Task TwoQuietSteps_SecondRequestStartsWithTheFirstByteForByte()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一"));
        await NewStepper().StepAsync(handle, "第一步", CancellationToken.None);
        var first = _adapter.Calls[0];

        _adapter.ReplyWith(_ => Say("二"));
        await NewStepper().StepAsync(handle, "第二步", CancellationToken.None);
        var second = _adapter.Calls[1];

        var firstShape = Shape(first).ToList();
        var secondShape = Shape(second).ToList();

        Assert.True(secondShape.Count > firstShape.Count,
            $"第二步的时间线没有变长：{firstShape.Count} → {secondShape.Count}");

        // ⚠️ 这一句就是「缓存能不能命中」：共同前缀逐项相等。
        for (var i = 0; i < firstShape.Count; i++)
            Assert.Equal(firstShape[i], secondShape[i]);

        static IEnumerable<string> Shape(ModelRequest request) =>
            request.Timeline.Select(e => e switch
            {
                TimelineEvent.MessageEvent m =>
                    $"{m.Id}|{m.From}|" + string.Join("\u0001", m.Blocks.Select(BlockText)),
                TimelineEvent.ToolResultEvent r =>
                    $"{r.Id}|{r.CallId}|" + string.Join("\u0001", r.Content.Select(BlockText)),
                _ => e.Id,
            });

        static string BlockText(Block block) => block switch
        {
            Block.TextBlock t => "t:" + t.Text,
            Block.ToolUseBlock u => $"u:{u.CallId}:{u.Name}:{u.Arguments}",
            Block.StoreBlock s => "s:" + s.Sha256,
            _ => block.GetType().Name,
        };
    }

    /// <summary>
    /// ⚠️ **两步「安静」的请求，头部指纹必须相同**——这正是缓存命中的前提：
    /// 提示词与工具表没变（轴、预设、插件字段、缓存形态都没动）。
    /// ⚠️ 它同时是「`stable` 把每步变化挡在头部之外」的证据。
    /// </summary>
    [Fact]
    public async Task TwoQuietSteps_HaveTheSameHeadFingerprint()
    {
        var handle = NewSession();
        var usage = new UsageStore(_store.DataRoot);

        _adapter.ReplyWith(_ => Say("一"));
        await NewStepper(usage).StepAsync(handle, "第一步", CancellationToken.None);
        _adapter.ReplyWith(_ => Say("二"));
        await NewStepper(usage).StepAsync(handle, "第二步", CancellationToken.None);

        var prints = usage.ForSession(handle.SessionId)
            .Where(c => c.Status == "completed")
            .Select(c => c.RequestFingerprint).ToList();

        Assert.Equal(2, prints.Count);
        Assert.NotNull(prints[0]);
        Assert.Equal(prints[0], prints[1]);
    }

    /// <summary>
    /// ⚠️ **改一条普通行为轴，头部指纹<b>不变</b>**——这条钉的是 `stable` 的一个真好处：
    /// 轴表与读数是**状态**，它们住在历史那条读数事件里（位置不在头部），
    /// 所以用户拨一下权限/验证强度，**不会把缓存整段作废**，只多一条末尾的读数。
    /// ⚠️ 在 `dynamic` 形态下这同样成立（轴表在位置 0 的合成对里，也在头部之外）。
    /// </summary>
    [Fact]
    public async Task ChangingABehaviourAxis_KeepsTheHeadFingerprint()
    {
        var handle = NewSession();
        var usage = new UsageStore(_store.DataRoot);

        _adapter.ReplyWith(_ => Say("一"));
        await NewStepper(usage).StepAsync(handle, "第一步", CancellationToken.None);

        handle.WithAxis(Axes.Verify, "strict");
        _adapter.ReplyWith(_ => Say("二"));
        await NewStepper(usage).StepAsync(handle, "第二步", CancellationToken.None);

        var prints = usage.ForSession(handle.SessionId)
            .Where(c => c.Status == "completed")
            .Select(c => c.RequestFingerprint).ToList();

        Assert.Equal(prints[0], prints[1]);
    }

    /// <summary>
    /// ⚠️ **换缓存形态会改头部指纹**（`floating_context` 的描述与框架头随形态变）
    /// ⇒ 切形态那一步的缓存要重建一次。这是**用户操作**的代价，可接受；但必须知道它会这样
    /// ——也正因为如此，这个字段才测得出东西（否则它恒等，什么也不回答）。
    /// </summary>
    [Fact]
    public async Task SwitchingCacheMode_ChangesTheHeadFingerprint()
    {
        var handle = NewSession();
        var usage = new UsageStore(_store.DataRoot);

        _adapter.ReplyWith(_ => Say("一"));
        await NewStepper(usage).StepAsync(handle, "第一步", CancellationToken.None);

        handle.WithAxis(Axes.Cache, Axes.CacheDynamic);
        _adapter.ReplyWith(_ => Say("二"));
        await NewStepper(usage).StepAsync(handle, "第二步", CancellationToken.None);

        var prints = usage.ForSession(handle.SessionId)
            .Where(c => c.Status == "completed")
            .Select(c => c.RequestFingerprint).ToList();

        Assert.NotEqual(prints[0], prints[1]);
    }

    /// 但占位判据现在问的是**内容**（那句「读数不在这里」），所以真内容的结果照常进候选。
    ///
    /// <para>判据走 **`Decide`**（生产路径）：两条结果都在一个显式窗口里，内容都可回收
    /// ⇒ 只有**真读数**被收，占位被排除。</para>
    /// </summary>
    [Fact]
    public void StableReadout_IsReclaimable_WhilePlaceholdersAreNot()
    {
        var blobs = new BlobStore(_store.DataRoot);
        TimelineEvent.ToolResultEvent Result(string id, string body) => new()
        {
            Id = id, At = CoreClock.Now(), CallId = id, Content = [new Block.TextBlock(body)],
        };

        var readout = Result("floating-1-readout-result", "正文");
        var placeholder = Result("floating-2-reclaim-result", FloatingContextTool.ReclaimedBody);

        var decided = Reclaim.Decide(
            [readout, placeholder, Reclaim.Window("session.gc", [readout.Id, placeholder.Id], null)], blobs);

        Assert.Contains(readout.Id, decided);
        Assert.DoesNotContain(placeholder.Id, decided);
    }
}

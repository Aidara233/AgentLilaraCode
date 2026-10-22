using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Control;
using Lilara.Core.Storage;
using System.Text.Json;

namespace Lilara.Core.Tests;

public class WireFormatTests
{
    // wire 与磁盘的 options 必须是两个不同实例——Converters 是可变 List，
    // 共用会让 wire 的改动同时改掉落盘格式（而 history.jsonl 只追加、永不重写）。
    [Fact]
    public void WireOptions_AreNotTheSameInstanceAsDiskOptions()
    {
        Assert.NotSame(Json.Options, WireJson.Options);

        // Converters 也必须是各自独立的 List，否则「两个 options 对象」只是表象。
        Assert.NotSame(Json.Options.Converters, WireJson.Options.Converters);
    }

    // 两边策略值目前一致；钉住它是为了「哪天有人只改了一边」立刻显形。
    [Fact]
    public void WireOptions_CurrentlyMirrorTheDiskPolicies()
    {
        Assert.Equal(Json.Options.PropertyNamingPolicy, WireJson.Options.PropertyNamingPolicy);
        Assert.Equal(Json.Options.DefaultIgnoreCondition, WireJson.Options.DefaultIgnoreCondition);
        Assert.Equal(Json.Options.Converters.Count, WireJson.Options.Converters.Count);
    }

    // 事件必须带判别式——头靠它分派。
    [Fact]
    public void WireEvent_CarriesItsDiscriminator()
    {
        var json = WireEvent.StableSerialize(new WireEvent.StepBegin("s1", 3, DateTimeOffset.UnixEpoch));

        Assert.Contains("\"kind\":\"session.step.begin\"", json);
    }

    // axis.changed 的判别式；漏了注册不会报错，只会序列化成 {}（基类没有属性）。
    [Fact]
    public void WireEvent_AxisChangedCarriesItsDiscriminatorAndValues()
    {
        var json = WireEvent.StableSerialize(new WireEvent.AxisChanged("s1", "effort", "max"));

        Assert.Contains("\"kind\":\"axis.changed\"", json);
        Assert.Contains("\"axis\":\"effort\"", json);
        Assert.Contains("\"value\":\"max\"", json);
    }

    // ⚠️ 询问那两条事件的**载荷形状**（2026-10-11 加）。
    //
    // `askKind` 曾经是 `string`，而它是在调用点手写 `asked.Kind.ToString()` 造出来的
    // ⇒ 线上 `"Permission"`（PascalCase），与**同一份数据**在 `/sessions` · `/state` 上的
    // 拼法不一致（那两处走 `JsonStringEnumConverter(camelCase)`）。头拿 PascalCase 去比
    // ⇒ 「？授权」徽标静默画成「？提问」。
    // ⇒ 现在类型是枚举、由序列化器出值，判据是「线上只有一种拼法」。
    [Fact]
    public void WireEvent_ApprovalRequested_SpellsAskKindInCamelCase()
    {
        var json = WireEvent.StableSerialize(new WireEvent.ApprovalRequested(
            "s1", "a1", Kernel.AskKind.Permission,
            [Kernel.AskItem.Choice("q1", "跑吗？", ["是", "否"])],
            [], null));

        Assert.Contains("\"kind\":\"approval.requested\"", json);
        // ⚠️ **字段名是 `askKind`，不是 `askKindName`**——后者是「它曾经是个 string」的化石，
        // 而头是按真字段名取值的（`app.js` 的 `p.askKind`）⇒ 名字一漂，头拿到的就是 `undefined`。
        Assert.Contains("\"askKind\":\"permission\"", json);
        Assert.DoesNotContain("Permission", json);
    }

    [Fact]
    public void WireEvent_UsageUpdatedHasStableDiscriminator()
    {
        var call = new Lilara.Core.Storage.UsageCall
        {
            CallId = "c1",
            ProviderId = "p",
            ModelId = "m",
            Protocol = "chat",
            StartedAt = DateTimeOffset.UnixEpoch,
            Status = "completed",
            Usage = new Lilara.Core.Kernel.TokenUsage { InputTokens = 3, OutputTokens = 2 },
        };

        var json = WireEvent.StableSerialize(new WireEvent.UsageUpdated(
            "s1", call, new Lilara.Core.Storage.UsageSummary
            {
                Calls = 1,
                CallsWithKnownInput = 1,
                CallsWithKnownOutput = 1,
                CallsWithUnknownUsage = 0,
                InputTokens = 3,
                OutputTokens = 2,
            }));

        Assert.Contains("\"kind\":\"usage.updated\"", json);
        Assert.Contains("\"inputTokens\":3", json);
    }

    // 嵌套 Block 的判别式取自静态声明类型，少了它头收到一串没有类型的块。
    [Fact]
    public void WireEvent_NestedBlocksKeepTheirDiscriminator()
    {
        var json = WireEvent.StableSerialize(new WireEvent.MessageAppended(
            "s1", "user", [new Block.TextBlock("你好")], DateTimeOffset.UnixEpoch)
            { HistoryId = "h1" });

        Assert.Contains("\"kind\":\"message.appended\"", json);
        Assert.Contains("\"historyId\"", json);
        Assert.Contains("\"kind\":\"text\"", json);          // 嵌套块的判别式
        Assert.Contains("\"text\":\"你好\"", json);
    }

    // 无签名推理块在 wire 上必须与磁盘同形（signature 键整个不出现），否则头读到 null 而不是「没有这个键」。
    [Fact]
    public void WireEvent_ReasoningBlockWithoutSignature_OmitsTheKey()
    {
        var json = WireEvent.StableSerialize(new WireEvent.MessageAppended(
            "s1", "model", [new Block.ReasoningBlock(null, "想")], DateTimeOffset.UnixEpoch)
            { HistoryId = "h1" });

        Assert.Contains("\"reasoningKind\":\"text\"", json);
        Assert.DoesNotContain("signature", json);
    }

    // 信封：seq 与 topic 是流的属性，与载荷分开。
    [Fact]
    public void Frame_IsATransportEnvelopeAroundAPayload()
    {
        var frame = new WireFrame.Event("session:s1", 42, new WireEvent.StateChanged("s1", "Idle", 1));

        var json = JsonSerializer.Serialize<WireFrame>(frame, WireJson.Options);

        Assert.Contains("\"topic\":\"session:s1\"", json);
        Assert.Contains("\"seq\":42", json);
        Assert.Contains("\"kind\":\"session.state\"", json);   // 载荷自己的判别式仍在
    }

    // 回执只有两类；Accepted=false 的 Reason 是「命令本身不合法」，不是「这次调用失败」（后者走 WireEvent.Error）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ack_CarriesOnlyTwoOutcomes(bool accepted)
    {
        var ack = new WireFrame.Ack("c1", accepted, accepted ? "s1" : null, accepted ? null : "会话不存在。", null);

        var json = JsonSerializer.Serialize<WireFrame>(ack, WireJson.Options);

        Assert.Contains($"\"accepted\":{(accepted ? "true" : "false")}", json);
        Assert.Contains("\"id\":\"c1\"", json);
    }

    /// <summary>
    /// **每个上线的事件类型都有发射点。**
    ///
    /// <para>⚠️ **为什么值得一条守卫**：`03` §4 有一句「**判据是数 `WireEvent` 自己那张表**」——
    /// 那就是说，**类型进了表、却没人 <c>Publish</c> 它**，是这张表最容易出的错。
    /// 那个形状已经在别处出现过一次（`03` §4 记着 `session.created` 发两份、
    /// 其中会话流那份**没有任何消费者**——「死投递」）。</para>
    ///
    /// <para>⚠️ **它只管「有没有发射点」，不管「发得对不对」**（发给哪个 topic、什么时候发、
    /// 载荷对不对——那要靠真 WS 测试）。**这是有用的近似，不是证明。**</para>
    /// </summary>
    [Fact]
    public void EveryWireEventType_HasAProducer()
    {
        // 从线上类型自己那张表派生（`WireEvent` 的嵌套类型），不手抄名单。
        var declared = typeof(WireEvent).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(WireEvent)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // ⚠️ 探针：类型搬走 / 反射换了写法时，下面那个空断言会**假绿**。
        // ⚠️ **加事件类型时按实测改这个数，别按感觉改**（被机械改过的探针等于没有）。
        // 13 → 14：`session.reclaimed`（2026-10-06，回收批）。
        // 14 → 16：`approval.requested` / `.resolved`（2026-10-08，询问批）。
        // 16 → 17：`usage.updated`（2026-10-15，统计批）与 `release.recorded`（2026-10-17，线的头出口）
        // 是两条分支各自加的 ⇒ 合并后是 16 + 2。
        // 17 → 18：`session.compaction.completed`（2026-10-16，压缩批）。
        // 18 → 19：`session.compaction.completed` 之外，`tool.result.appended`（2026-10-17，
        //          工具结果的实时出口——此前结果只有重拉 `/history` 才看得见）。
        // 19 → 20：`release.recorded` 与 `usage.updated` 之外，`session.compaction.completed`… ——
        //          这条链一直记少了一格（表里是 20 个，注释只列到 19）。⚠️ 下一次改这个数时，
        //          先数 `WireEvent.cs` 的 `JsonDerivedType`，别信这行注释。
        // 20 → 21：`retry.attempt`（2026-10-21，重试上报——头不能只看到一直等待）。
        Assert.Equal(21, declared.Length);

        var dispatcher = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Lilara.Core", "Hosting", "CommandDispatcher.cs"));

        var silent = declared.Where(name => !dispatcher.Contains($"new WireEvent.{name}(", StringComparison.Ordinal)).ToList();

        Assert.Empty(silent.Select(n => $"`{n}` 在 WireEvent 的表里，但 CommandDispatcher 里没有发射点（死投递）。"));
    }

    // 同族的探针守卫：`03` §4 的数条数那句的自动化版。
    // ⚠️ 它钉的是「表本身」，不是「文档里写的那个数」——文档那个数靠这条测试红来维护。
    // 13 → 14：`session.reclaimed`（2026-10-06，回收批）……20 → 21：`retry.attempt`（2026-10-21）。
    [Fact]
    public void WireEventTable_HasExactlyTheTypesWeThinkItDoes()
    {
        Assert.Equal(21, typeof(WireEvent).GetNestedTypes().Count(t => t.IsSubclassOf(typeof(WireEvent))));
    }

    // ⚠️ 重试上报的**载荷形状**（2026-10-21）：头按字段名取值（`p.attempt` / `p.maxAttempts` /
    // `p.delayMs`），名字一漂头拿到的就是 `undefined`——而那个圆括号里写的仍是「重试 undefined/undefined」。
    [Fact]
    public void WireEvent_RetryAttempt_CarriesTheCountsAndTheWait()
    {
        var json = WireEvent.StableSerialize(new WireEvent.RetryAttempt("s1", 3, 10, "被限流了（429）。", 1621));

        Assert.Contains("\"kind\":\"retry.attempt\"", json);
        Assert.Contains("\"attempt\":3", json);
        Assert.Contains("\"maxAttempts\":10", json);
        Assert.Contains("\"delayMs\":1621", json);
    }

    /// <summary>
    /// **没有任何消费者用「强转成某一个 `TimelineEvent` 子类」来处理时间线。**
    ///
    /// <para>⚠️ <b>这条守卫是从三次真实事故里长出来的</b>（2026-10-06）：加了
    /// <c>ToolResultEvent</c> 之后，有四处代码写着 <c>(TimelineEvent.MessageEvent)evt</c>：
    /// 三个协议适配器（我修了）· <c>ControlServer</c> 的线上渲染（**漏了 ⇒ `/history` 对含工具的
    /// 会话直接 HTTP 500**）· 控制台的 `history` 命令（**也漏了 ⇒ InvalidCastException**）。
    /// ⇒ 形状是「**加一个封闭层级的新变体，得找出所有消费者**」，而人记住的总是前几个。</para>
    ///
    /// <para>⚠️ <b>判据是源码文本，不是运行时</b>——同 <c>Clock_IsOnlyReadThroughCoreClock</c>
    /// 那条：它不碰被测物，所以不存在「探针 ≠ 被测物」的问题。但它是**有用的近似，不是证明**：
    /// 拦不住「先转成 <c>object</c> 再转回来」这类绕法。它管的是**最常见的那个写法**。</para>
    /// </summary>
    [Fact]
    public void NoConsumer_CastsTheTimelineToASingleEventSubtype()
    {
        var types = typeof(TimelineEvent).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(TimelineEvent)))
            .Select(t => t.Name)
            .ToArray();

        // 探针：反射若换写法、类型搬走，下面那个空断言会假绿。
        // 2 → 3：`ReleaseEvent`（2026-10-11，线）。⚠️ **这个数不是装饰**——
        // 它正是「加一个变体要找出所有消费者」那条纪律**第一道**闸：
        // 数不对，下面的空断言就是**在数错的那一组类型上**跑的。
        Assert.Equal(3, types.Length);

        var root = Path.Combine(RepoRoot(), "src");
        var offenders = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            // ⚠️ **必须切掉 `//` 之后的部分**（同 `Clock_IsOnlyReadThroughCoreClock`）：
            // 不切的话，**这条守卫自己的注释**（里面拿强转当反面例子）就会**误报**，
            // 而误报的守卫最后一定会被改成永远通过。实测踩过。
            .SelectMany(f => File.ReadLines(f).Select((raw, n) =>
            {
                var cut = raw.IndexOf("//", StringComparison.Ordinal);
                return (f, Line: n + 1, Text: cut < 0 ? raw : raw[..cut]);
            }))
            // ⚠️ **判据是「<b>强转</b>」，所以模式必须写成 <c>(TimelineEvent.X)</c>——那个左括号是判据的一部分。**
            // （2026-10-11 收紧）原先写的是 `TimelineEvent.X)`，它把**模式匹配**也一起抓了：
            // `e is not Kernel.TimelineEvent.ReleaseEvent)`（`ControlServer`）与
            // `timeline[i] is TimelineEvent.ReleaseEvent`（`Reclaim`）都被判成「强转」。
            // ⚠️ 而那两处**恰好是这条守卫要护的行为**（switch / 模式分派，不是强转）
            // ⇒ 那条消息（「改用 switch 分派」）会指着**已经用了 switch 分派的代码**骂。
            // ⚠️ 这不是放宽：真正的强转写法**一定**带那个左括号。
            .Where(x => types.Any(t => x.Text.Contains($"(TimelineEvent.{t})", StringComparison.Ordinal)))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders.Select(o =>
            $"{o}   ← 强转成单一事件类型：加一个新事件类型时这里会 InvalidCastException。改用 switch 分派。"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Lilara.Core", "Kernel")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

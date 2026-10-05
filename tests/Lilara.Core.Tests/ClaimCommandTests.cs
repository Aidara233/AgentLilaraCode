using Lilara.Core.Kernel;
using Lilara.Core.Adapters;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

// 冲突判定的对外行为：占用冲突回 rejected 并点名占它的会话——那是「别人的工作集会被作废」唯一的可见时刻。
public class ClaimCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-claim-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-claimwork-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private (CoreHost Host, CommandDispatcher Dispatcher, EventBus Bus) Harness()
    {
        Directory.CreateDirectory(_work);
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var bus = new EventBus();
        var adapter = new Fakes.FakeAdapter();
        return (host, new CommandDispatcher(host, bus, h => new Stepper(host.Store, adapter)), bus);
    }

    private static string NewSession(CommandDispatcher d)
    {
        var r = d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None)
            .GetAwaiter().GetResult();
        return ((Ack.Accepted)r.Ack).SessionId!;
    }

    // 声明写在工作目录：可分享的在工作目录，会话状态在数据根。
    [Fact]
    public async Task Acquire_WritesIntoTheWorkingDirectorysLilaraFolder()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);

        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.True(File.Exists(Path.Combine(_work, ".lilara", "claims.json")),
            "声明是**可分享的项目信息**（01 §8），必须落在工作目录而不是数据根");
    }

    [Fact]
    public async Task Acquire_PublishesClaimAcquiredOnTheProjectTopic()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        var pk = Lilara.Core.Kernel.ProjectKey.FromPath(_work);
        using var sub = bus.Subscribe(EventBus.ProjectTopic(pk));

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        // 项目流，不是会话流：声明是「谁在动哪些文件」，跨会话才有意义。
        Assert.True(sub.Reader.TryRead(out var p), "声明必须发到项目流——会话流上别的人看不见");
        var e = Assert.IsType<WireEvent.ClaimAcquired>(p.Payload);
        Assert.Equal(sid, e.SessionId);
        Assert.EndsWith("src", e.Path);
    }

    // 冲突 ⇒ rejected 且点名占它的会话；走 rejected 而不是 error：请求本身不合法（那块地方已有主）。
    [Fact]
    public async Task Acquire_OfAnOccupiedPath_IsRejected_AndNamesTheHolder()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src"]), "c1", false, CancellationToken.None);
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(b, ["src\\a.cs"]), "c2", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains(a, rejected.Reason);   // 点名，不是一句「已被占用」
    }

    [Fact]
    public async Task Acquire_TwiceByTheSameSession_IsIdempotent()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        // 第二次不该多出一条声明——否则右栏会列出两遍同一个区域。
        Assert.Single(ClaimsOf(_work));
    }

    [Fact]
    public async Task Release_RemovesIt_AndPublishes()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        using var sub = bus.Subscribe(EventBus.ProjectTopic(Lilara.Core.Kernel.ProjectKey.FromPath(_work)));
        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, ["src"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Empty(ClaimsOf(_work));
        Assert.True(sub.Reader.TryRead(out var p));
        Assert.IsType<WireEvent.ClaimReleased>(p.Payload);
    }

    // 释放一个没占过的路径：不算错，但不该发事件（没有迁移）。
    [Fact]
    public async Task Release_OfSomethingNotHeld_IsANoOp()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        using var sub = bus.Subscribe(EventBus.ProjectTopic(Lilara.Core.Kernel.ProjectKey.FromPath(_work)));

        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, ["never"]), "c1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.False(sub.Reader.TryRead(out _));
    }

    // claims.json 是用户可写、可分享的输入，坏它不能打死核心：`"path":null` 与 `[null]` 都会让
    // Claims.Conflicts 抛 NRE，且都不进 catch (JsonException)；判据必须是 `c is not null && c.Path is not null`。
    [Theory]
    [InlineData("""[{"sessionId":"s1","projectKey":"pk","path":null,"at":"2026-01-01T00:00:00+00:00"}]""")]
    [InlineData("""[null]""")]
    public async Task Acquire_WithAHandEditedBadClaimsFile_SurvivesInsteadOfKillingTheCore(string badJson)
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);

        // 手写一份坏 claims.json（模拟「可分享、人手可编辑」的来路）。
        Directory.CreateDirectory(Path.Combine(_work, ".lilara"));
        File.WriteAllText(Path.Combine(_work, ".lilara", "claims.json"), badJson);

        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        // 坏条目被丢掉 ⇒ 这条命令**照常成功**，而不是把核心抛死。
        Assert.IsType<Ack.Accepted>(r.Ack);
    }

    // 核心重启后会话在盘上、不在内存激活表里；claim 走 TryGetActivated 会回一句假话「没有会话」。
    [Fact]
    public async Task Acquire_OnASessionThatIsOnlyOnDisk_IsFoundNotReportedMissing()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);                // 用一个宿主建会话……

        // 换一个新宿主指向同一个数据根：它的激活表是空的，
        // 于是这个会话此刻只存在于磁盘上——核心重启后的真实形态。
        var restarted = new CoreHost(_root, _work);
        var d2 = new CommandDispatcher(restarted, new EventBus(),
            h => new Stepper(restarted.Store, new Fakes.FakeAdapter()));

        var r = await d2.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        // 缺陷下这里是 Rejected（「没有会话 '…'」）——而会话明明在盘上。
        Assert.IsType<Ack.Accepted>(r.Ack);
    }

    // Release 也必须走 TryFind：把 Release 的 TryFind 变异回 TryGetActivated 会全绿
    // （Acquire 有 disk-only 那条网）；同一纪律两个入口，只测一个等于没测另一个。
    [Fact]
    public async Task Release_OnASessionThatIsOnlyOnDisk_IsFoundNotReportedMissing()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);
        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c0", false, CancellationToken.None);

        var restarted = new CoreHost(_root, _work);      // 新宿主 ⇒ 空激活表 ⇒ 会话只在盘上
        var d2 = new CommandDispatcher(restarted, new EventBus(),
            h => new Stepper(restarted.Store, new Fakes.FakeAdapter()));

        var r = await d2.DispatchAsync(new CoreCommand.ClaimRelease(sid, ["src"]), "c1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Empty(ClaimsOf(_work));                   // 真的删掉了，不只是回了个 accepted
    }

    // Release 的「跨会话强删」是刻意的（模型失控时的逃生口）：A 占的声明，B 能把它删掉；
    // 给 RemoveAll 加 SessionId 过滤会堵死逃生口——那个改动看起来很安全，实际更坏。
    [Fact]
    public async Task Release_ByADifferentSession_StillRemovesIt()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src"]), "c1", false, CancellationToken.None);
        Assert.Single(ClaimsOf(_work));

        // B 从没占过 src——它仍然能删。
        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(b, ["src"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Empty(ClaimsOf(_work));
    }

    // 两个会话并发 acquire 互不重叠的路径 ⇒ 两条都必须留下（钉读-改-写的原子性）。
    // 用 Barrier 对齐、不用 Task.Delay 错开——后者是在测试里编造时序而不是产生并发；这类测试天然间歇，必须跑多轮。
    [Fact]
    public async Task Acquire_FromTwoSessionsConcurrently_KeepsBothClaims()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        // 跑多轮：单轮可能因调度恰好错开而"碰巧绿"。
        // 每轮用不同路径（不是先删文件）：每轮都真的产生两次写入，丢更新会体现为累计数少了。
        for (var round = 0; round < 20; round++)
        {
            var pa = $"a-dir-{round}";
            var pb = $"b-dir-{round}";

            using var barrier = new Barrier(2);
            var t1 = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, [pa]), "r1", false, CancellationToken.None);
            });
            var t2 = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await d.DispatchAsync(new CoreCommand.ClaimAcquire(b, [pb]), "r2", false, CancellationToken.None);
            });

            var both = await Task.WhenAll(t1, t2);

            Assert.IsType<Ack.Accepted>(both[0].Ack);
            Assert.IsType<Ack.Accepted>(both[1].Ack);
            // 断言是「累计两条都在」——缺陷下某轮会丢一条 ⇒ 累计数少。
            Assert.Equal(2 * (round + 1), ClaimsOf(_work).Count);
        }
    }

    // 多元素 scope 里有一条冲突 ⇒ 整条命令一个都不写。
    // 必须用多元素 scope（单元素下初版也恰好对），且冲突那条要排在后面才触发早退分支。
    [Fact]
    public async Task Acquire_WithAMultiElementScope_WhereOneConflicts_WritesNothing()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src"]), "c1", false, CancellationToken.None);

        // B 要占两条，第二条与 A 的 src 冲突（第一条本身合法、不与任何东西冲突）
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(b, ["other", "src/a.cs"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        // 要害：other 不能被写进去——盘上应当只有 A 那一条。
        var all = ClaimsOf(_work);
        Assert.Single(all);
        Assert.Equal(a, all[0].SessionId);
    }

    // 同一 scope 里两条相互重叠的路径 ⇒ 命令内部自洽（不自己和自己冲突）。
    [Fact]
    public async Task Acquire_WithAScopeThatOverlapsItself_IsAccepted()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);

        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src", "src/a.cs"]), "c1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Equal(2, ClaimsOf(_work).Count);
    }

    // 幂等重占不得发事件（无迁移则无事件）：Acquire 曾丢弃 Update 的返回值、无条件为每条路径发事件。
    [Fact]
    public async Task Acquire_TwicePublishesOnlyOnce()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        var pk = Lilara.Core.Kernel.ProjectKey.FromPath(_work);
        using var sub = bus.Subscribe(EventBus.ProjectTopic(pk));

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);
        Assert.True(sub.Reader.TryRead(out _), "第一次必须发（那是一次真迁移）");

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c2", false, CancellationToken.None);

        // 第二次什么都没变（幂等）⇒ 不该有事件。
        Assert.False(sub.Reader.TryRead(out _), "幂等重占不许发事件——无迁移则无事件");
    }

    // Release 只为真正被删掉的路径发事件：初版为 scope 里每条发，而 never-held 根本没被删。
    [Fact]
    public async Task Release_PublishesOnlyForPathsActuallyRemoved()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        var pk = Lilara.Core.Kernel.ProjectKey.FromPath(_work);
        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c0", false, CancellationToken.None);
        using var sub = bus.Subscribe(EventBus.ProjectTopic(pk));

        // 一条真在、一条从没占过
        await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, ["src", "never-held"]), "c1", false, CancellationToken.None);

        Assert.True(sub.Reader.TryRead(out var p), "真删掉的那条必须发");
        Assert.EndsWith("src", Assert.IsType<WireEvent.ClaimReleased>(p.Payload).Path);
        Assert.False(sub.Reader.TryRead(out _), "从没占过的那条不许发——它没有被删掉");
    }

    // 锁的键必须规范化（含大小写），否则并发保护在真实路径上失效；
    // 必须真并发——串联两次 Update 与锁无关，「去掉 ToLowerInvariant」的变异照样绿。
    [Fact]
    public async Task Update_OnPathsDifferingOnlyByCase_StillSerializes()
    {
        // 只差大小写的两个工作目录；ClaimStore 的 Path_ 只 Combine，故这里正是「键没规范化」会露出来的地方。
        var upper = new ClaimStore(Path.Combine(_work, "CaseDir"));
        var lower = new ClaimStore(Path.Combine(_work, "casedir"));

        for (var round = 0; round < 30; round++)
        {
            var cu = new Claim("su", "pk", $"c:\\x\\u{round}", DateTimeOffset.UnixEpoch);
            var cl = new Claim("sl", "pk", $"c:\\x\\l{round}", DateTimeOffset.UnixEpoch);

            using var barrier = new Barrier(2);
            var t1 = Task.Run(() => { barrier.SignalAndWait(); return upper.Update(cur => cur.Append(cu)); });
            var t2 = Task.Run(() => { barrier.SignalAndWait(); return lower.Update(cur => cur.Append(cl)); });

            var both = await Task.WhenAll(t1, t2);

            // 同一把锁 ⇒ 第二次看得见第一次写的 ⇒ 两条都在；两把锁 ⇒ 各写一条 ⇒ 丢一条。
            Assert.Single(both[0].Added);
            Assert.Single(both[1].Added);
            // 按值的累计断言：30 轮下来应当有 60 条。缺陷下会少一大截。
            Assert.Equal(2 * (round + 1), upper.All().Count);
        }
    }

    private static List<Claim> ClaimsOf(string work) =>
        new ClaimStore(work).All().ToList();
}

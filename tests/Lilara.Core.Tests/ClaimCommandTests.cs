using Lilara.Core.Adapters;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 声明的命令路径与持久化（`01` §12）。
/// ⚠️ 冲突判定的**对外行为**在这里钉：占用冲突回 `rejected` 并**点名占它的会话**——
/// 那是「别人的工作集会被作废」唯一的可见时刻。
/// </summary>
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

    /// <summary>⚠️ 声明写在**工作目录**（`01` §8：可分享的在工作目录，会话状态在数据根）。</summary>
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
        var pk = Lilara.Core.Projects.ProjectKey.FromPath(_work);
        using var sub = bus.Subscribe(EventBus.ProjectTopic(pk));

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        // ⚠️ **项目流，不是会话流**：声明是「谁在动哪些文件」，**跨会话**才有意义（03 §5）
        Assert.True(sub.Reader.TryRead(out var p), "声明必须发到项目流——会话流上别的人看不见");
        var e = Assert.IsType<WireEvent.ClaimAcquired>(p.Payload);
        Assert.Equal(sid, e.SessionId);
        Assert.EndsWith("src", e.Path);
    }

    /// <summary>
    /// ⚠️ **冲突 ⇒ `rejected` 且点名占它的会话**。
    /// 这条消息是「你的工作集会被作废」唯一能被看见的地方（`01` §12.2）。
    /// ⚠️ 它走 `rejected` 而不是 `error`：请求**本身**不合法（那块地方已经有主了），
    /// 不是「命令没错但这次没成」（G6）。
    /// </summary>
    [Fact]
    public async Task Acquire_OfAnOccupiedPath_IsRejected_AndNamesTheHolder()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src"]), "c1", false, CancellationToken.None);
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(b, ["src\\a.cs"]), "c2", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains(a, rejected.Reason);   // ⚠️ 点名，不是一句「已被占用」
    }

    [Fact]
    public async Task Acquire_TwiceByTheSameSession_IsIdempotent()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        // ⚠️ 第二次不该多出一条声明——否则右栏会列出两遍同一个区域。
        Assert.Single(ClaimsOf(_work));
    }

    [Fact]
    public async Task Release_RemovesIt_AndPublishes()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        using var sub = bus.Subscribe(EventBus.ProjectTopic(Lilara.Core.Projects.ProjectKey.FromPath(_work)));
        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, ["src"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Empty(ClaimsOf(_work));
        Assert.True(sub.Reader.TryRead(out var p));
        Assert.IsType<WireEvent.ClaimReleased>(p.Payload);
    }

    /// <summary>⚠️ 释放一个**没占过**的路径：不算错，但也**不该发事件**（没有迁移）。</summary>
    [Fact]
    public async Task Release_OfSomethingNotHeld_IsANoOp()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        using var sub = bus.Subscribe(EventBus.ProjectTopic(Lilara.Core.Projects.ProjectKey.FromPath(_work)));

        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, ["never"]), "c1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.False(sub.Reader.TryRead(out _));
    }

    /// <summary>
    /// ⚠️ **`claims.json` 是用户可写、可分享的输入，坏它不能打死核心。**
    /// 两种**合法 JSON** 的坏形状，两种都会让 `Claims.Conflicts` 抛 `NullReferenceException`
    /// （`child.Length` 对 null），而它们**都不进** `catch (JsonException)`：
    /// <list type="bullet">
    ///   <item>`"path": null` —— 反序列化成 `Claim.Path = null`；</item>
    ///   <item>`[null]`（**数组元素**为 null）—— 列表里出现 null 项。</item>
    /// </list>
    /// ⚠️ 这两种是**同族**，不是同一种：只过滤 `Path is null` 关掉的是前者的**形状**，
    /// 后者会在**过滤那一行本身**抛（`c.Path` 对 `c == null`）。⇒ 判据必须是
    /// `c is not null && c.Path is not null`。
    /// </summary>
    [Theory]
    [InlineData("""[{"sessionId":"s1","projectKey":"pk","path":null,"at":"2026-01-01T00:00:00+00:00"}]""")]
    [InlineData("""[null]""")]
    public async Task Acquire_WithAHandEditedBadClaimsFile_SurvivesInsteadOfKillingTheCore(string badJson)
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);

        // 手写一份坏 claims.json（模拟「可分享、人手可编辑」那个真实来路）
        Directory.CreateDirectory(Path.Combine(_work, ".lilara"));
        File.WriteAllText(Path.Combine(_work, ".lilara", "claims.json"), badJson);

        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        // 坏条目被丢掉 ⇒ 这条命令**照常成功**，而不是把核心抛死。
        Assert.IsType<Ack.Accepted>(r.Ack);
    }

    /// <summary>
    /// ⚠️ **核心重启后，会话在盘上、不在内存激活表里。**
    /// `claim` 走 `TryGetActivated` 时会回一句**假话**——「没有会话 '…'」，而它存在。
    /// ⚠️ **这是 T3 那个缺陷的原样重演**（`9c3bd0d` 修过 `session.setAxis` 同一处），
    /// 所以测试的形状照抄那条：**让会话只存在于磁盘上**，再调。
    ///
    /// <para>⚠️ **"重启"用一个新 `CoreHost` 表达**（指向同一个数据根），
    /// 因为新宿主天然有一张空的激活表。**别**用别的办法模拟——
    /// 激活表是私有的，而"新宿主"正是真实的重启语义。</para>
    /// </summary>
    [Fact]
    public async Task Acquire_OnASessionThatIsOnlyOnDisk_IsFoundNotReportedMissing()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);                // 用一个宿主建会话……

        // ……然后换一个**新宿主**指向同一个数据根：它的激活表是空的，
        // 于是这个会话此刻**只存在于磁盘上**——核心重启后的真实形态。
        var restarted = new CoreHost(_root, _work);
        var d2 = new CommandDispatcher(restarted, new EventBus(),
            h => new Stepper(restarted.Store, new Fakes.FakeAdapter()));

        var r = await d2.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        // ⚠️ 缺陷下这里是 `Rejected`（「没有会话 '…'」）——而会话明明在盘上。
        Assert.IsType<Ack.Accepted>(r.Ack);
    }

    /// <summary>
    /// ⚠️ **`Release` 也必须走 `TryFind`——而审阅者证明这条以前没有网。**
    /// 它把 `Release` 的 `TryFind` 变异回 `TryGetActivated` ⇒ **29/29 全绿**
    /// （对照：`Acquire` 有 disk-only 那条网，所以它变异会红）。
    /// ⇒ 「同一纪律、两个入口」只测一个等于没测另一个。形状与 `Acquire` 那条对称。
    /// </summary>
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

    /// <summary>
    /// ⚠️ **`Release` 的「跨会话强删」是**刻意**的（`03` §3：模型失控时的逃生口），
    /// 但审阅者证明它**零回归网**——没有任何测试会在它被改成「只删自己的」时变红。**
    ///
    /// <para>⚠️ 这条钉的正是那个**方向**：A 占的声明，**B** 能把它删掉。
    /// 若有人「顺手」给 `RemoveAll` 加上 `x.SessionId == c.SessionId`，这条**必须红**——
    /// 而那个改动看起来很安全（"只删自己的更保守"），实际**堵死了逃生口**。</para>
    /// </summary>
    [Fact]
    public async Task Release_ByADifferentSession_StillRemovesIt()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src"]), "c1", false, CancellationToken.None);
        Assert.Single(ClaimsOf(_work));

        // ⚠️ **B 从没占过 `src`**——它仍然能删。
        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(b, ["src"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Empty(ClaimsOf(_work));
    }

    /// <summary>
    /// ⚠️ **两个会话并发 acquire 两条互不重叠的路径 ⇒ 两条都必须留下。**
    ///
    /// <para>⚠️ **这条钉的是读-改-写的原子性。** 缺陷下（`All()` → 改 → `Save()` 分开调用）
    /// 两个任务各自读到**同一份旧列表**、各自写回 ⇒ **后写的盖掉前写的**，盘上只剩一条，
    /// 而**两边都收到 `Accepted`**（静默）。实际计数由 `ClaimStore.Update` 的按路径锁保证。</para>
    ///
    /// <para>⚠️ **实测过的严重度**（审阅者，8 会话）：带 `Barrier` **40/40 轮只剩 1 条**；
    /// 无 `Barrier` **30/30 轮丢更新 + 19/30 轮抛 `UnauthorizedAccessException`**
    /// （`AtomicFile` 重试耗尽后 rethrow ⇒ 逃出 `DispatchAsync`）。
    /// ⇒ 本测试顺带钉住第二种症状。</para>
    ///
    /// <para>⚠️ **用 `Barrier` 对齐，不用 `Task.Delay` 错开**：后者是在测试里**编造**
    /// 时序，而不是**产生**并发。⚠️ 这类测试天然间歇——跑**多轮**（见下面的循环），
    /// 单跑一次绿说明不了什么。</para>
    /// </summary>
    [Fact]
    public async Task Acquire_FromTwoSessionsConcurrently_KeepsBothClaims()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        // ⚠️ 跑多轮：单轮可能因调度恰好错开而"碰巧绿"。
        // ⚠️ **每轮用不同路径**（不是先删文件）——这样每轮都**真的**产生两次写入，
        // 丢更新会直接体现为「累计数少了」。早先版本用 `File.Delete` 清场，
        // 在第一轮就抛 `DirectoryNotFoundException`（`.lilara/` 还没建）——
        // **那是测试的 bug，不是实现的**。
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
            // ⚠️ 断言是「**累计**两条都在」——缺陷下某轮会丢一条 ⇒ 累计数少。
            Assert.Equal(2 * (round + 1), ClaimsOf(_work).Count);
        }
    }

    /// <summary>
    /// ⚠️ **多元素 `scope` 里有一条冲突 ⇒ 整条命令一个都不写。**
    ///
    /// <para>⚠️ 这是控制器在**修复并发时自己引入**的缺陷：初版是「边判边 `list.Add`」，
    /// 于是 `["other", "src/a.cs"]` 会把 `other` **先写进去**再拒绝——
    /// 那时「一个都不写」只对**单元素** scope 成立，而注释声称的是无条件成立。</para>
    ///
    /// <para>⚠️ **这条测试必须用多元素 scope**：单元素下初版也"恰好"是对的，
    /// 没有区分力。冲突的那条要排在**后面**——排前面就不会触发那个早退分支。</para>
    /// </summary>
    [Fact]
    public async Task Acquire_WithAMultiElementScope_WhereOneConflicts_WritesNothing()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src"]), "c1", false, CancellationToken.None);

        // B 要占两条，**第二条**与 A 的 `src` 冲突（第一条本身合法、不与任何东西冲突）
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(b, ["other", "src/a.cs"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        // ⚠️ 要害：`other` **不能**被写进去——盘上应当**只有 A 那一条**。
        var all = ClaimsOf(_work);
        Assert.Single(all);
        Assert.Equal(a, all[0].SessionId);
    }

    /// <summary>
    /// ⚠️ **同一 scope 里两条相互重叠的路径 ⇒ 命令内部自洽**（不自己和自己冲突）。
    /// `["src", "src/a.cs"]` 是合法的一次"占这片区域"，不该回 rejected。
    /// </summary>
    [Fact]
    public async Task Acquire_WithAScopeThatOverlapsItself_IsAccepted()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);

        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src", "src/a.cs"]), "c1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Equal(2, ClaimsOf(_work).Count);
    }

    private static List<Claim> ClaimsOf(string work) =>
        new ClaimStore(work).All().ToList();
}

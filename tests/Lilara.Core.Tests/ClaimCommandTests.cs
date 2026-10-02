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

        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, "src"), "c1", false, CancellationToken.None);

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

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, "src"), "c1", false, CancellationToken.None);

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

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, "src"), "c1", false, CancellationToken.None);
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(b, "src\\a.cs"), "c2", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains(a, rejected.Reason);   // ⚠️ 点名，不是一句「已被占用」
    }

    [Fact]
    public async Task Acquire_TwiceByTheSameSession_IsIdempotent()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, "src"), "c1", false, CancellationToken.None);
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, "src"), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        // ⚠️ 第二次不该多出一条声明——否则右栏会列出两遍同一个区域。
        Assert.Single(ClaimsOf(_work));
    }

    [Fact]
    public async Task Release_RemovesIt_AndPublishes()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, "src"), "c1", false, CancellationToken.None);

        using var sub = bus.Subscribe(EventBus.ProjectTopic(Lilara.Core.Projects.ProjectKey.FromPath(_work)));
        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, "src"), "c2", false, CancellationToken.None);

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

        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, "never"), "c1", false, CancellationToken.None);

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

        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, "src"), "c1", false, CancellationToken.None);

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

        var r = await d2.DispatchAsync(new CoreCommand.ClaimAcquire(sid, "src"), "c1", false, CancellationToken.None);

        // ⚠️ 缺陷下这里是 `Rejected`（「没有会话 '…'」）——而会话明明在盘上。
        Assert.IsType<Ack.Accepted>(r.Ack);
    }

    private static List<Claim> ClaimsOf(string work) =>
        new ClaimStore(work).All().ToList();
}

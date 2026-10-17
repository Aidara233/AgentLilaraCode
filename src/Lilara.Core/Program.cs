using System.Text;
using Lilara.Core.Protocol;
using Lilara.Core.Control;
using Lilara.Core.Console;
using Lilara.Core.Hosting;
using Lilara.Core.Storage;
using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Microsoft.AspNetCore.Builder;

// ⚠️ **控制台的 stdin/stdout 要显式设成 UTF-8**，否则非 ASCII 的 provider id 在控制台上根本打不进来：
// `provider set COM¹ --key k` 会写成 `COM鹿.json`（读进来时已被代码页编坏），
// 而计划里明确要求放行的 `本地` 会变成 `鏈`。⇒「`本地` 必须放行」在那之前是**空头支票**。
// ⚠️ **既有测试测不出来**：`ProviderIdTests` 喂的是字符串常量、绕过了控制台 ⇒ 全绿。
// ⚠️ 这行**无法用单元测试钉**（编码是进程级的；改完再读回来只证明「设上了」，不证明「往返正确」）
// ⇒ 验收只能靠手工 E2E（见本批的验证清单）。
// 两个编码器都接受**被重定向的标准流**（实测：`sleep 4000 | ... lilara` 那种起法不抛异常）。
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var dataRoot = DataRoot.Resolve();
Directory.CreateDirectory(dataRoot);

// ⚠️ **「一设备一核心」的真正守卫在数据根上，不在端口上。**
// 换个端口号（`--port 7789`）就绕过端口守卫了，而**写路径依旧无锁**——
// 两个核心共用一个数据根会互相破坏。端口那条检查是**独立的另一条**（Task 8）。
using var dataRootLock = DataRootLock.Acquire(dataRoot);

var workingDirectory = Directory.GetCurrentDirectory();
var host = new CoreHost(dataRoot, workingDirectory);

Console.WriteLine($"LILARA — 数据根 {dataRoot}");

// ⚠️ 核心启动时**不建任何会话**（01 §8.1）。
// 这里只做一件事：把当前项目下最近的会话选出来当"当前会话"。
// 「配置完提供商自动建会话」是头的行为——控制台要建，得显式敲 `session create`。
var projectKey = ProjectKey.FromPath(workingDirectory);
var existing = host.Store.ListSessions(projectKey);

SessionHandle? resume = null;

// ⚠️ 「会话绑定的提供商没了」是一条**独立**的落点，不能和「全新跑」混为一谈：
// 两者都让 preferredProviderId 变成 null，但只有前者刚刚才向用户报告过一个坏消息。
var boundProviderMissing = false;

if (existing.Count > 0)
{
    var handle = SessionHandle.Load(host.Store, projectKey, existing[0]);
    var provider = host.Providers.Load(handle.Spec.Model.ProviderId);

    if (provider is null)
    {
        boundProviderMissing = true;
        // ⚠️ 要点名缺的是哪个提供商——只说「已不存在」，用户不知道该重配哪一条。
        Console.WriteLine(
            $"  会话引用的提供商 '{handle.Spec.Model.ProviderId}' 已不存在。用 `provider set` 重新配一个。");
    }
    else
    {
        resume = handle;
        host.Register(handle);   // 登记进激活表——快照端点据此避免读到盘上过期的 Idle
        Console.WriteLine($"  当前会话 {handle.SessionId}（第 {handle.State.Turn} 步）");
    }
}
else
{
    // ⚠️ **「还没有会话」是一句可能不成立的话**（手工 E2E 抓到的）。
    // `ListSessions` 走 `TryReadSpec`，而它连**版本太新**也一起吞了（那是刻意的：
    // 一个太新的会话不该让整张列表打不开）。于是这里若只说「还没有会话」，
    // 用户会以为会话没了——而它其实在，只是这个核心读不了。
    // ⇒ 分红字说清楚：这是同一类「静默」缺陷（横幅说一句假话，真话只在别处）。
    // ⚠️ 原因分两类，**处置方式正相反**（损坏 ⇒ 重写就好；太新 ⇒ 重写会丢数据）
    // ⇒ 分类走 `UnreadableReason`，别在这里自己猜（第一版就猜错过：把损坏也说成了「太新」）。
    var sessionsDir = Path.Combine(host.Store.ProjectsRoot, projectKey, "sessions");
    var unreadable = Directory.Exists(sessionsDir)
        ? Directory.GetDirectories(sessionsDir)
            .Select(Path.GetFileName).OfType<string>()
            .Select(id => (Id: id, Reason: host.Store.UnreadableReason(projectKey, id)))
            .Where(x => x.Reason is not null)
            .ToList()
        : [];

    if (unreadable.Count == 0)
    {
        Console.WriteLine("  还没有会话。");
    }
    else
    {
        foreach (var (id, reason) in unreadable)
            Console.WriteLine($"  ⚠️ 会话 {id} 读不出来：{reason}。");

        // 只在**真的**可能是「太新」的时候才说那句「别删」——说给损坏的文件听是吓人。
        if (unreadable.Any(x => x.Reason == "按更新的格式写的"))
            Console.WriteLine("     这本机数据可能是更新的核心写的。用那个核心打开，**别删**：删了里面的记录就没了。");
    }
}

// ⚠️ 适配器**不再在构造时定死**。`CoreHost.StepperFor` 按**该会话自己的** provider
// 现解析并缓存，失效点在 dispatcher 的 `provider.upsert` 里。
// ⇒ 这里不再需要在 `provider set` 之后整个重建 Repl（旧形状就是为了绕开「Repl 持一把固定
//    stepper」才那么写的），那条记忆化 hack 随之消失。
var bus = new EventBus();
var dispatcher = new CommandDispatcher(host, bus, host.StepperFor);

// ⚠️ **心跳**（`01` §12.8 的第三条时机）：每 20 秒扫一遍**已知会话**，把到期没人答的询问收掉。
//
// <para>⚠️ <b>它是「超时自动」唯一的落点，因为超时**没有**定时器</b>
//（§5「超时是盘面自证」）：一个挂着等回答的会话**没有任何东西在动它**
//（那正是它挂着的定义）⇒ 没有这一趟，它会**永久**停在那里。
// 实测过：过截止 75 秒之后 `state.json` 还是 `WaitingForInput`。</para>
//
// <para>⚠️ <b>它绝不碰回收</b>（`01` §11.7 栽过的那一跤）：只做「询问到期」一件事，
// 而没有断口的会话**一个字节都不写**。</para>
//
// <para>⚠️ **心跳文件**：正常退出时删它，强杀删不掉 ⇒ 下一次启动据此知道
// 「上一次是被弄死的」，于是**对当前会话先收一次尾**。</para>
var heartbeatFile = CoreClock.HeartbeatPath(dataRoot);

if (host.RecoveredFromACrash)
{
    Console.WriteLine("  ⚠️ 上一次核心没有正常退出（被强杀或断电）。正在收尾……");
}
File.WriteAllText(heartbeatFile, "");   // ⚠️ 必须在**读**过之后才建

using var heartbeat = new CancellationTokenSource();
var heartbeatTask = Task.Run(async () =>
{
    while (!heartbeat.Token.IsCancellationRequested)
    {
        try { await Task.Delay(CoreHost.HeartbeatInterval, heartbeat.Token); }
        catch (OperationCanceledException) { return; }

        // ⚠️ **失败绝不许掀翻这一趟循环**：一条会话收尾失败不该让心跳死掉
        //（死了之后所有挂着的询问就永远没人收了，而**没有任何东西会告诉你**）。
        try { dispatcher.SweepExpiredAsks(); }
        catch (Exception ex) { Console.Error.WriteLine($"（心跳那一趟出错了：{ex.Message}）"); }
    }
}, heartbeat.Token);

// ⚠️ 端口**可注入**（环境变量），只在**默认值**上不许静默换（05 §2.1）。
// 可注入是硬需求：不然测试里两个 host 并行跑必然互撞，集成测试根本写不出来。
// ⚠️ 但**用户没法靠它绕开「一设备一核心」**——那条守卫在**数据根**上（见上面 `DataRootLock`），
// 不在端口上。换端口只是让第二个核心起得来，它一样会被数据根锁挡住。
var port = int.TryParse(Environment.GetEnvironmentVariable("LILARA_PORT"), out var p)
    ? p
    : ControlServer.DefaultPort;

// WS 的 Origin 白名单跟随头自己的端口配置。
var allowedOrigin = AllowedOriginResolver.Resolve();
var controlServer = new ControlServer(host, dispatcher, bus, port, allowedOrigin.Origin);
WebApplication? api = null;
try
{
    api = await controlServer.StartAsync(CancellationToken.None);
    Console.WriteLine($"  控制面 {controlServer.Url}（头连这个；只监听本机）");
    Console.WriteLine($"  WS 只接受来自 {controlServer.AllowedOrigin} 的页面（{AllowedOriginResolver.Describe(allowedOrigin.Source)}）。");
}
catch (Exception ex)
{
    // ⚠️ **显式失败并报告**，不许静默换端口（05 §2.1）——头连不上且不知道为什么，
    // 比直接起不来更难查。端口那条检查是**独立的另一条**（数据根锁是另一条）。
    // ⚠️ Windows 上这里可能是两种原因，**别把话说死成「端口被占」**：
    //   · 端口真被占        → 去看是谁占的
    //   · 落在 excluded range（Hyper-V/WSL 保留段）→ 报的是**拒绝访问**，
    //     而**根本没有另一个核心**。说成「端口被占」会把用户引向错的方向。
    Console.Error.WriteLine($"✗ 控制面起不来（{controlServer.Url}）：{ex.Message}");
    Console.Error.WriteLine("  端口被占**或**落在 Windows 保留段（`netsh int ipv4 show excludedportrange protocol=tcp`）。");
    Console.Error.WriteLine("  换个端口：设环境变量 LILARA_PORT。");
    return 1;
}

var repl = new Repl(host, dispatcher, host.StepperFor, Console.Out)
{
    Current = resume,
};

// ⚠️ 端点是**谁定的**必须看得见（01 §7）。
// 环境变量缺省时接管不是错——本机就是靠它把流量导向本地代理的；
// **悄悄**接管才是错。所以每次解析出适配器都印一行，并且点名来源。
// ⚠️ 但「会话绑定的提供商已不存在」这条落点**不印**：那里 preferredProviderId 是 null，
// `StepperResolver` 会挑**任意另一个**提供商，印出来的就是那个陌生提供商的端点——
// 紧跟在「你绑的那个已经没了」后面，纯属误导。这条落点上没有「本会话的端点」可报。
if (!boundProviderMissing) PrintEndpoint(host, resume);

Console.WriteLine("  `provider set <id> --key <key>` 配置；`session create` 建会话；`axis set <会话> <轴> <值>` 调行为；`claim <路径>` 占区域；`history` 回放；`quit` 退出。");

while (true)
{
    Console.Write("lilara> ");
    var line = Console.ReadLine();
    if (line is null) break;

    if (!await repl.ExecuteAsync(line, CancellationToken.None)) break;

    // ⚠️ `provider set` 之后要**再印一次端点**：那是「端点可能刚刚变了」的时刻，
    // 不印的话环境变量正是在此刻悄悄接管的。
    // ⚠️ 这里的判据是**纯显示用途**——核心侧的 stepper 缓存失效已经由 dispatcher 负责了
    // （挂在字符串判据上会让线上 `provider.upsert` 走不到 ⇒ 缓存静默失效）。
    if (Repl.IsProviderSetCommand(line)) PrintEndpoint(host, repl.Current);
}

// ⚠️ 正常退出也要**显式**返回 0：上面「控制面起不来」那条路 `return 1`，
// 而顶层语句里只要有一条 `return <值>`，**所有**路径都得有返回值（否则 CS0161）。
// ⚠️ 退出前停掉控制面：不 `StopAsync` 的话监听 socket 会挂到进程被强杀为止，
// 紧接着重启会撞上「端口仍被占」——而那个报错**看起来**像另一个核心在跑。
if (api is not null) await api.StopAsync(CancellationToken.None);

// ⚠️ **心跳要停、心跳文件要删**（顺序：先停循环再删文件）。
// - 不删文件 ⇒ 下一次启动会**误报**「上一次崩了」；
// - 不停循环 ⇒ 那个 `Task` 还攥着 `dispatcher`，而进程正要退出。
// ⚠️ **这两句只覆盖「正常退出」**：强杀 / 断电走不到这里，而**那正是判据本身**
//（心跳文件还在 ⇒ 下一次知道要收尾）。
await heartbeat.CancelAsync();
await heartbeatTask;
try { File.Delete(heartbeatFile); } catch (IOException) { /* 删不掉最多误报一次崩溃 */ }

return 0;

/// <summary>
/// 端点一行。⚠️ 没有端点（占位适配器）时**不印**——那是「还没配提供商」那条路径，
/// 印一个地址只会让人以为已经有去处了。
/// ⚠️ 用接口上的 <see cref="IModelAdapter.Endpoint"/>，**不是**某个具体适配器的成员——
/// 否则加了第二种协议就等于「端点没人印得出来」，那正是端点裁定要防的静默接管。
///
/// <para>⚠️ <b>本函数没有测试覆盖，这是已知且接受的缺口（实测，不是推测）。</b>
/// 它是顶层语句里的静态局部函数，**测试够不着，也没有注入口**。
/// 实测：把它改回「只认 <c>ClaudeAdapter</c>」那一版（亦即引入第二种协议时端点就印不出来的那个回归），
/// 全量测试**仍然全绿**。⇒ 钉住它的只有下面两件事，**都不是这条 <c>Console.WriteLine</c> 本身</b>：
/// ① 被它依赖的前提（三种真适配器都交得出非空 <c>Endpoint</c>）有测试；
/// ② 真联调会肉眼核对横幅那一行。
/// <b>所以：改这一个函数时不要指望测试会红。</b></para>
/// </summary>
static void PrintEndpoint(CoreHost host, SessionHandle? current)
{
    // ⚠️ 这里**刻意不复用 `host.StepperFor`**：那是按会话缓存的，而这条只为了印一行字。
    // 用 `StepperResolver` 现解析一次，与旧行为一致（且不会往缓存里塞一个可能过期的条目）。
    var stepper = StepperResolver.Resolve(host.Store, host.Providers, current?.Spec.Model.ProviderId);

    if (stepper.Adapter.Endpoint is not { } endpoint) return;

    Console.WriteLine($"  端点 {endpoint.Url}（{DescribeEndpointSource(endpoint.Source)}）");
}

static string DescribeEndpointSource(EndpointSource source) => source switch
{
    EndpointSource.Configured => "来自 provider 配置",
    EndpointSource.Environment => $"来自环境变量 {EndpointResolver.EnvVar}",
    _ => "官方地址",
};

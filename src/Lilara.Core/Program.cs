using Lilara.Core.Adapters;
using Lilara.Core.Console;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Projects;
using Lilara.Core.Session;

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
    Console.WriteLine("  还没有会话。");
}

// ⚠️ 适配器**不再在构造时定死**。`CoreHost.StepperFor` 按**该会话自己的** provider
// 现解析并缓存，失效点在 dispatcher 的 `provider.upsert` 里。
// ⇒ 这里不再需要在 `provider set` 之后整个重建 Repl（旧形状就是为了绕开「Repl 持一把固定
//    stepper」才那么写的），那条记忆化 hack 随之消失。
var bus = new EventBus();
var dispatcher = new CommandDispatcher(host, bus, host.StepperFor);

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

Console.WriteLine("  `provider set <id> --key <key>` 配置；`session create` 建会话；`history` 回放；`quit` 退出。");

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

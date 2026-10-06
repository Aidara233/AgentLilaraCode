using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 头是独立进程，只托管静态资源：页面 JS 直连核心，头不代理、不缓存、不理解协议（头是可替换件）。

// ⚠️ **这两个默认值与核心侧是同一事实的两份副本**，改一个要跟着改另一个：
//   · 头端口 17990 ←→ `ControlServer.DefaultAllowedOrigin`
//   · 核心地址 17988 ←→ `ControlServer.DefaultPort`
// ⚠️ 从 7790 / 7788 改过来是**实测**的结果（2026-10-06）：那两个数落在了 Windows 的
// **保留端口段**里（本机实测 7681-7780 / 7781-7880 / 7881-7980，通常由 Hyper-V/WSL/Docker 的
// NAT 预留）⇒ 绑定直接 `SocketException 10013`。⚠️ **保留段会在重启后变**，
// 所以这不是永久解——真正管用的是下面那句人话报错与那两个环境变量。
var port = int.TryParse(Environment.GetEnvironmentVariable("LILARA_HEAD_PORT"), out var p) ? p : 17990;
var core = Environment.GetEnvironmentVariable("LILARA_CORE") ?? "http://127.0.0.1:17988";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // content root 必须在这里传：builder.WebHost.UseContentRoot 会抛。
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot",
});
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// /config.json 把核心地址递给页面——唯一需要从环境传进去的东西。头不做反向代理。
app.MapGet("/config.json", () => Results.Json(new { core }));

// ⚠️ **绑定失败要说人话，不能砸栈**（2026-10-06 修）。
// 原先这里是裸的 `await app.StartAsync()`：端口落在保留段时，
// Kestrel 抛 `SocketException 10013` 一路冒到顶层，屏幕上只有一大段栈——
// 而**核心那边对同一件事印的是一句能照着做的话**（`Program.cs` 里那段）。
// 两个进程同一种失败，说法不该差这么多。
try
{
    await app.StartAsync();
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
{
    Console.Error.WriteLine($"✗ 头起不来（http://127.0.0.1:{port}）：{ex.Message}");
    Console.Error.WriteLine("  端口被占**或**落在 Windows 保留段（`netsh int ipv4 show excludedportrange protocol=tcp`）。");
    Console.Error.WriteLine($"  换个端口：设环境变量 LILARA_HEAD_PORT（并让核心跟着走——同名变量，核心读它推 WS 白名单）。");
    Console.Error.WriteLine("  ⚠️ 内核换了端口而**核心没跟着换**时，WS 握手会被**静默拒绝**（浏览器不把 403 交给页面 JS）。");
    return 1;
}

// ⚠️ 端口**必须印**，而且要说清「这就是核心要放行的那个 origin」：
// 核心的 WS 白名单要跟着这个端口走（`LILARA_HEAD_PORT`），对不上时**握手会被静默拒绝**
//（浏览器不把 403 响应体交给页面 JS）⇒ 用户只能靠对这两行横幅来排查。
// 核心那边会印它实际接受的那个 origin，两行放在一起看即可。
Console.WriteLine($"LILARA 头 — http://127.0.0.1:{port}");
Console.WriteLine($"  页面 origin http://127.0.0.1:{port}（核心的 WS 白名单必须包含它）");
Console.WriteLine($"  核心 {core}（页面直连它，头不代理）");
await app.WaitForShutdownAsync();
return 0;

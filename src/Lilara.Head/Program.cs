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

// ⚠️ **静态文件不许被浏览器缓存**（2026-10-06 加，原因是同一类事故栽了三次）。
//
// 页面是 `index.html` + `app.js`，而 `app.js` 从来不换名字 ⇒ 浏览器完全有理由一直用旧的。
// 更要命的是**同一次改动有多份副本**：`src/Lilara.Head/wwwroot/` 是源，`bin/Debug/.../wwwroot` 与
// `bin/Release/.../wwwroot` 是**构建时复制**过去的，而**跑哪个构建就看哪一份**。
// 于是「我改了、你却说没效果」可以有四种成因（跑了另一份构建 / 没重建 / 浏览器缓存 /
// 改错了副本），而它们**在页面上长得一模一样**。
//
// ⇒ 两条一起上：① 浏览器不许留副本；② 页面把**它自己是哪一份**显示在底栏
// （见下面的 `/build.json`）——「我手上是哪一版」从此不用猜。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-store",
});

// /config.json 把核心地址递给页面——唯一需要从环境传进去的东西。头不做反向代理。
app.MapGet("/config.json", () => Results.Json(new { core }));

// **页面自述**：它是由哪个构建的 `wwwroot` 提供的、那份 `app.js` 有多大。
// ⚠️ 这一条是为了终结「我改了、你说没效果」那类对话：底栏显示的那两个数就是
// **你现在看的这一份**，与 `src/Lilara.Head/wwwroot/app.js` 的属性一比对，答案立刻出来。
// ⚠️ 读的是 `AppContext.BaseDirectory`（= 静态文件的真实来源），不是源码目录。
app.MapGet("/build.json", () =>
{
    var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var js = Path.Combine(webRoot, "app.js");
    var stamp = File.Exists(js) ? File.GetLastWriteTime(js) : (DateTime?)null;
    return Results.Json(new
    {
        servedFrom = webRoot,
        appJsBytes = File.Exists(js) ? new FileInfo(js).Length : 0,
        appJsWritten = stamp?.ToString("yyyy-MM-dd HH:mm:ss"),
    });
});

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

// ⚠️ **启动时就把「连不上」说出来**（2026-10-06）。
// 原先这里什么都不问：核心没起 / origin 对不上时，页面只会「已断开 · 3 秒后重连」，
// 而**头这一侧本来就能当场发现**——它刚拿到核心的 `/info`，那份东西里就有白名单。
// `05` §1 那条「核心越克制，头越要喧哗」在这里适用：核心对「谁被拒了」是**静默**的
// （握手 403 时浏览器不把响应体交给页面 JS），所以这句话只能由头来说。
await CheckCoreAsync(core);
await app.WaitForShutdownAsync();
return 0;

static async Task CheckCoreAsync(string core)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    string allowed;
    try
    {
        var body = await http.GetStringAsync($"{core.TrimEnd('/')}/info");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        allowed = doc.RootElement.GetProperty("allowedOrigin").GetString() ?? "?";
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  ⚠️ 核心没应答（{core}）：{ex.Message}");
        Console.Error.WriteLine("     页面打开后会一直「已断开·重连中」。先把核心起起来。");
        return;
    }

    // ⚠️ 判据与核心**逐字相同**：`AllowedOriginResolver` 解析出来的那个 origin。
    // 头只知道**自己**的 origin（`127.0.0.1:{port}`），而用户可能用 `localhost` 打开——
    // 那**不是**同一个 origin（浏览器这么判）⇒ 握手照样会被静默拒绝。
    // ⇒ 这里只说头自己那个，并点名 localhost 那条路。
    Console.WriteLine($"  ✓ 核心在，WS 白名单 = {allowed}");
    Console.WriteLine("     ⚠️ 请用上面的 `127.0.0.1` 地址打开页面——用 `localhost` 打开时");
    Console.WriteLine("        origin 会变成 http://localhost:…，与白名单**不是同一个**，握手会被静默拒绝。");
}

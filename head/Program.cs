using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ⚠️ **头是独立进程**（05 §2）：它只做一件事——托管那份静态资源。
// 页面里的 JS **直连核心**（03 §1 的 WS + HTTP），头进程**不代理**。
// 那样省掉一整层（不必在头里再实现一遍协议、也不必维护两条连接），
// 而代价只是核心要放开 CORS（已经在做了）。
// ⚠️ 它**不缓存、不理解协议**——头是**可替换件**（`05` §1.1：换头是允许的，
// 不履行义务是不允许的），不该背协议的语义。
// ⚠️ 所以这个文件**不该长大**。要加东西时先问：那是头的事，还是核心的事？
// （`05` §3：渲染归核心。头只显示。）

var port = int.TryParse(Environment.GetEnvironmentVariable("LILARA_HEAD_PORT"), out var p) ? p : 7790;
var core = Environment.GetEnvironmentVariable("LILARA_CORE") ?? "http://127.0.0.1:7788";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // ⚠️ content root 必须在这里传（`builder.WebHost.UseContentRoot` 会抛，
    // 实测过——见 `ControlServer` 里那段注释）。
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot",
});
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// 把核心地址递给页面——**唯一**需要从环境传进去的东西。
// ⚠️ 不做反向代理：那样头就要理解协议了，而 `05` §1.1 说得很清楚——
// 头是可以整个换掉的，它不该背协议的语义。
app.MapGet("/config.json", () => Results.Json(new { core }));

await app.StartAsync();
Console.WriteLine($"LILARA 头 — http://127.0.0.1:{port}");
Console.WriteLine($"  核心 {core}（页面直连它，头不代理）");
await app.WaitForShutdownAsync();
return 0;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 头是独立进程，只托管静态资源：页面 JS 直连核心，头不代理、不缓存、不理解协议（头是可替换件）。

var port = int.TryParse(Environment.GetEnvironmentVariable("LILARA_HEAD_PORT"), out var p) ? p : 7790;
var core = Environment.GetEnvironmentVariable("LILARA_CORE") ?? "http://127.0.0.1:7788";

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

await app.StartAsync();
Console.WriteLine($"LILARA 头 — http://127.0.0.1:{port}");
Console.WriteLine($"  核心 {core}（页面直连它，头不代理）");
await app.WaitForShutdownAsync();
return 0;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 头是独立进程，只托管静态资源：页面 JS 直连核心，头不代理、不缓存、不理解协议（头是可替换件）。

// ⚠️ **这几个默认值与核心侧是同一事实的多份副本**，改一个要跟着改另一个：
//   · 头端口 17990 ←→ `ControlServer.DefaultAllowedOrigin`
//   · 核心地址 17988 ←→ `ControlServer.DefaultPort`
//   · 头监听地址 0.0.0.0 ←→ `ControlServer.DefaultBindAddress`（两者默认为同一取舍：对局域网开放）
// ⚠️ 从 7790 / 7788 改过来是**实测**的结果（2026-10-06）：那两个数落在了 Windows 的
// **保留端口段**里（本机实测 7681-7780 / 7781-7880 / 7881-7980，通常由 Hyper-V/WSL/Docker 的
// NAT 预留）⇒ 绑定直接 `SocketException 10013`。⚠️ **保留段会在重启后变**，
// 所以这不是永久解——真正管用的是下面那句人话报错与那两个环境变量。
var port = int.TryParse(Environment.GetEnvironmentVariable("LILARA_HEAD_PORT"), out var p) ? p : 17990;

// ⚠️ **监听地址默认对局域网开放（2026-11-05 用户裁定；原先是 127.0.0.1）**：
// 页面要能从手机/平板上打开，头就必须绑 0.0.0.0。代价写在下面那句横幅里——头只发静态文件，
// **真正能被驱动的是核心**，所以「要不要拦人」的开关在核心那边（`LILARA_AUTH`）。
// 要收回本机：设 `LILARA_HEAD_BIND=127.0.0.1`（记得核心那边也要跟着 `LILARA_BIND`）。
var bind = Environment.GetEnvironmentVariable("LILARA_HEAD_BIND") is { Length: > 0 } configuredBind
    ? configuredBind.Trim()
    : "0.0.0.0";

// 核心端口 / 地址：页面直连核心，头只负责把地址递过去。
var corePort = int.TryParse(Environment.GetEnvironmentVariable("LILARA_PORT"), out var cp)
    ? cp
    : 17988;
var coreEnv = Environment.GetEnvironmentVariable("LILARA_CORE");
var core = string.IsNullOrWhiteSpace(coreEnv) ? $"http://127.0.0.1:{corePort}" : coreEnv.Trim();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // content root 必须在这里传：builder.WebHost.UseContentRoot 会抛。
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot",
});
builder.WebHost.UseUrls($"http://{HostForUrl(bind)}:{port}");
builder.Logging.ClearProviders();

// **响应压缩**（2026-11 性能批）。
//
// ⚠️ 头只发三个文件，而 `app.js` 是 155 KB、`index.html` 是 44 KB（实测）——
// 它们的内容高度重复（几乎全是缩进 + 中文注释 + 中文界面串）。实测（curl 实测，
// `CompressionLevel.Optimal`）：`app.js` gzip 57 KB、`index.html` br 15 KB
// ⇒ 局域网 / 手机上打开页面时，这一句就是首屏字节数的七成。
//
// ⚠️ **它不破「不许缓存」那条**：压缩与 `Cache-Control: no-store` 是两件事，
// 那份 no-store 的注释（见下面 `UseStaticFiles`）一个字都不用改。
// ⚠️ MIME 表用框架默认值（含 `text/html` / `application/javascript` / `text/css`）；
// 手抄一份就多一个会漂的地方。
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

// ⚠️ **默认档是 `Fastest`**（实测：`app.js` gzip 77 KB / br 75 KB），而 `Optimal`
// 是 54 KB / 46 KB —— 同一个文件，**多省三成**。这台机器上是单用户、每次启动只压几次，
// 那几毫秒 CPU 换掉的正是「手机上打开页面多等的半秒」。
builder.Services.Configure<BrotliCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Optimal);

var app = builder.Build();

// ⚠️ **必须在 `UseStaticFiles` 与端点之前**：中间件按注册顺序跑。
app.UseResponseCompression();
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
//
// ⚠️ **核心地址必须跟着「页面是从哪台机器打开的」走**（2026-11-05，与 0.0.0.0 一起改）：
// 头绑 0.0.0.0 之后，局域网里的手机打开 `http://192.168.1.5:17990`，页面若仍去连
// `http://127.0.0.1:17988`，那个 `127.0.0.1` 指的是**手机自己** ⇒ 永远连不上，
// 而页面上只会显示「已断开·3 秒后重连」（症状与核心没起来**完全一样**）。
// ⇒ 没显式设 `LILARA_CORE` 时，用**这次请求的 Host** 拼核心地址（同一个地址，核心端口）。
// ⚠️ 设了 `LILARA_CORE` 就照抄——那是「核心在别的机器上 / 走了隧道」那条路，头不许改写它。
app.MapGet("/config.json", (HttpRequest request) =>
{
    var url = coreEnv is not null && !string.IsNullOrWhiteSpace(coreEnv)
        ? core
        : $"http://{HostForUrl(string.IsNullOrWhiteSpace(request.Host.Host) ? "127.0.0.1" : request.Host.Host)}:{corePort}";
    return Results.Json(new { core = url });
});

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
    Console.Error.WriteLine($"✗ 头起不来（http://{HostForUrl(bind)}:{port}）：{ex.Message}");
    Console.Error.WriteLine("  端口被占**或**落在 Windows 保留段（`netsh int ipv4 show excludedportrange protocol=tcp`）。");
    Console.Error.WriteLine($"  换个端口：设环境变量 LILARA_HEAD_PORT（并让核心跟着走——同名变量，核心读它推 WS 白名单）。");
    Console.Error.WriteLine("  ⚠️ 内核换了端口而**核心没跟着换**时，WS 握手会被**静默拒绝**（浏览器不把 403 交给页面 JS）。");
    return 1;
}

// ⚠️ 端口**必须印**，而且要说清「这就是核心要放行的那个端口」：
// 核心的 WS 白名单按**头端口**匹配（`http://*:<头端口>`），端口对不上时**握手会被静默拒绝**
//（浏览器不把 403 响应体交给页面 JS）⇒ 用户只能靠对这两行横幅来排查。
// 核心那边会印它实际接受的那个模式，两行放在一起看即可。
Console.WriteLine($"LILARA 头 — http://{HostForUrl(bind)}:{port}（{(bind == "0.0.0.0" ? "对局域网开放，默认" : "由 LILARA_HEAD_BIND 指定")}）");
Console.WriteLine($"  页面 origin 形如 http://<这台机器的地址>:{port}（核心的 WS 白名单必须覆盖它）");
Console.WriteLine($"  核心 {core}（页面直连它，头不代理）");
if (bind is "0.0.0.0" or "*" or "::" or "[::]")
{
    // 默认就是对外 ⇒ 每次都说一句代价，并列出这台机器上可用的地址（别的设备照着输）。
    Console.Error.WriteLine("  ⚠️ 头对局域网开放：同一网络里的设备都能打开这个页面。");
    Console.Error.WriteLine($"     要收回本机：设 LILARA_HEAD_BIND=127.0.0.1（核心那边同样设 LILARA_BIND=127.0.0.1）。");
    foreach (var address in LanAddresses(port)) Console.WriteLine($"     可达地址 {address}");
}

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
    bool authRequired;
    try
    {
        var body = await http.GetStringAsync($"{core.TrimEnd('/')}/info");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        allowed = doc.RootElement.GetProperty("allowedOrigin").GetString() ?? "?";
        // ⚠️ 未授权时 `/info` 只交最小一格（含这个字段），所以 `TryGetProperty` 而不是索引器。
        authRequired = doc.RootElement.TryGetProperty("authRequired", out var flag)
                       && flag.ValueKind == System.Text.Json.JsonValueKind.True;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  ⚠️ 核心没应答（{core}）：{ex.Message}");
        Console.Error.WriteLine("     页面打开后会一直「已断开·重连中」。先把核心起起来。");
        return;
    }

    // ⚠️ 判据与核心**逐字相同**：`AllowedOriginResolver` 解析出来的那个模式。
    // 默认是 `http://*:<头端口>`（任意主机 + 头端口）⇒ 用局域网 IP 或 localhost 打开都能握手成功；
    // 用户若显式设成了某个具体 origin，头也没法在启动时替他判断（他不知道别的设备用什么地址打开）。
    Console.WriteLine($"  ✓ 核心在，WS 白名单 = {allowed}");
    Console.WriteLine("     页面用哪个地址打开都行，但**端口必须是上面那个**——端口对不上时握手会被静默拒绝。");

    // ⚠️ 开了鉴权而页面不知道 = 「页面一片空白，没有任何人能说出为什么」。
    // token 只在核心的控制台里（头是独立进程，拿不到也不该去读），所以这句话只指路，不复述。
    if (authRequired)
    {
        Console.WriteLine("  ⚠️ 核心开了鉴权（LILARA_AUTH=1）：页面第一次连会要求填 token。");
        Console.WriteLine("     token 在**核心**的控制台启动横幅里（`访问 token …` 那一行）。");
    }
}

static IEnumerable<string> LanAddresses(int port)
{
    try
    {
        return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(a))
            .Select(a => $"http://{a}:{port}")
            .Distinct()
            .ToList();
    }
    catch (System.Net.Sockets.SocketException)
    {
        // 解析不出来就不列：地址列不出来不该影响启动。
        return [];
    }
}

// IPv6 字面量在 URL 里必须带方括号，否则 `http://::1:17990` 解析不出来。
// 同理，`Request.Host.Host` 交出来的 IPv6 是**不带**方括号的。
static string HostForUrl(string address)
{
    var a = address.Trim();
    if (a.Length > 2 && a[0] == '[' && a[^1] == ']') return a;
    return a.Contains(':') ? $"[{a}]" : a;
}

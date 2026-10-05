using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Adapters;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lilara.Core.Control;

/// <summary>
/// 控制面：一条 WS（双向、持久、有序）+ 一组 HTTP（一次性读 / 大对象）（`03` §1）。
///
/// <para>⚠️ **三条判据**（`03` §1）：核心不关心头在不在 · 命令投递即返回、结果走事件 ·
/// 头是无状态的。⇒ WS 收到命令**立刻回 ack**，步在后台跑，结果经事件流出。</para>
/// </summary>
public sealed class ControlServer(
    CoreHost host, CommandDispatcher dispatcher, EventBus bus, int port,
    string allowedOrigin = ControlServer.DefaultAllowedOrigin)
{
    public const int DefaultPort = 7788;

    /// <summary>
    /// ⚠️ 默认只放行**头自己的 origin**（`head/Program.cs:17` 默认端口 **7790**）。
    ///
    /// <para>⚠️ **`localhost:7790` 不在内，这是刻意的**：它与 `127.0.0.1:7790`
    /// **不是同一个 origin**（浏览器就是这么判的），且放行 `localhost` 等于放行一个
    /// **任何本机程序都能抢占的名字**（hosts 文件 / DNS）——端口也一样，所以
    /// 这里**不做** `127.0.0.1:*` 那种通配。</para>
    ///
    /// <para>⚠️ **只有参数默认值读它**。启动横幅读的是**实例值** <see cref="AllowedOrigin"/>——
    /// 否则将来有人传非默认值时，横幅会印一个闸其实**拒绝**的 origin，而用户恰好靠横幅自解释失败原因。</para>
    /// </summary>
    internal const string DefaultAllowedOrigin = "http://127.0.0.1:7790";

    /// <summary>⚠️ 只监听本机（`03` §10「鉴权最小」：核心是本设备唯一的，本机进程即可信）。</summary>
    public const string BindAddress = "127.0.0.1";

    public string Url => $"http://{BindAddress}:{port}";

    /// <summary>
    /// ⚠️ 给**启动横幅**读：横幅必须印**闸真正在接受的那个值**（实例值），不是默认常量——
    /// 否则将来有人传非默认值时，横幅会印一个闸其实**拒绝**的 origin，而用户恰好靠横幅自解释失败原因。
    ///
    /// <para>⚠️ `internal`（不是 `public`）：公开面里没有它，`Program.cs` 同程序集读得到。</para>
    /// </summary>
    internal string AllowedOrigin => allowedOrigin;

    /// <summary>
    /// 启动。
    /// ⚠️ **`StartAsync` 而不是 `RunAsync`**：后者会**阻塞**，而控制台提示符在那之前就打出来了——
    /// 端口起不来时用户会看见提示符、然后进程才死，原因还埋在日志里。
    /// 先 `StartAsync` 并 catch，才能做到 `05` §2.1 要的「**显式失败并报告**」。
    /// </summary>
    public async Task<WebApplication> StartAsync(CancellationToken ct)
    {
        // ⚠️ **content root 必须在这里传，不能在 `builder.WebHost` 上设。**
        // 实测：`builder.WebHost.UseContentRoot(...)` 会抛
        // 「The content root changed from … Changing the host configuration using
        //   WebApplicationBuilder.WebHost is not supported. Use WebApplication.CreateBuilder(
        //   WebApplicationOptions) instead.」——**起不来**。
        // 背景：`Microsoft.NET.Sdk`（不是 `.Sdk.Web`）下默认 content root = **当前工作目录**
        // （与 `Program.cs` 的 `workingDirectory` 是两回事）。我们**不托管静态资源**
        // （头是独立进程），所以本可以不管；但显式钉住 `AppContext.BaseDirectory`
        // 免得将来有人加静态文件时踩坑。
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls(Url);

        // ⚠️ **必须清掉日志 provider**：`Console.Write("lilara> ")` 是**不带换行**的，
        // Kestrel 默认把日志写 stdout，两者交错会把用户正在敲的那一行**搅烂**。
        builder.Logging.ClearProviders();

        var app = builder.Build();

        // ⚠️ 头是**独立进程、独立 origin**（`05` §2.1 的本机 Web 应用）⇒ 必须放开 CORS，
        // 否则浏览器里那个页面连不上。范围限本机。
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "content-type";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            if (ctx.Request.Method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }
            await next();
        });

        app.UseWebSockets();

        MapSnapshots(app);
        MapWebSocket(app);

        await app.StartAsync(ct);
        return app;
    }

    // ---- 快照 / 查询（HTTP，一次性）（03 §6） ----

    private void MapSnapshots(WebApplication app)
    {
        // ⚠️ **每个快照响应都带该 topic 的 `seq` 高水位**（`03` §7 的原子性）。
        // 头拿它去 `subscribe(since:)`，「拉快照 → 订阅」之间落的事件才**不漏不重**。
        // 少了它，头要么永远看不到那一条，要么重复渲染——而**头是无状态的**，
        // 每次刷新页面都重来一次。
        app.MapGet("/projects", () => Results.Json(ProjectKeys(), WireJson.Options));

        app.MapGet("/projects/{pk}/sessions", (string pk) =>
            Results.Json(host.Store.ListSessions(pk), WireJson.Options));

        app.MapGet("/sessions/{id}/state", (string id) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();

            // ⚠️ **先取高水位，再取内容。** 反过来的话，落在「取内容 → 取高水位」
            // 那段时间里的事件**既不在快照里、也不会被补发**（因为 seq ≤ highWater）⇒
            // 头**永久少一条**，而且它没办法发现。
            // ⚠️ **方向**是从「漏」改成「重」——**不是**「窗口关上了」。
            // 落在 `(高水位, 内容]` 的事件**既在快照里、又会被补发** ⇒ 头会**重复渲染一次**。
            // ⚠️ 而「重可以被头去重」这个说法**不成立**：`/history` 的 `rendered` 行**不带 `seq`**，
            // 头**没有任何东西**能把补发事件和已渲染的行对上（`app.js` 也没实现去重）。
            // 重比漏轻（下次 `openSession` 会从快照重渲染而自愈），但**别说成关上了**——
            // 那与本轮刚改掉的那句过度声称**同形**。
            var highWater = bus.HighWaterMark(EventBus.SessionTopic(id));

            // ⚠️ **先查内存激活表**（`SessionQueries.StateOf`）——读磁盘会在**步进行中报 Idle**，
            // 于是头渲染「空闲」⇒ 用户以为没在跑 ⇒ **再点一次发送** ⇒ 队列两条 ⇒ 烧钱。
            return Results.Json(
                new
                {
                    handle.SessionId,
                    State = SessionQueries.StateOf(host, handle),

                    // ⚠️ **高水位不能在这轮改动里掉队**：`History_And_State_CarryTheSeqHighWaterMark`
                    // 断言 `state.highWater` 存在且等于流高水位——brief 里的替换块漏了它，
                    // 只跑 Step 4 那三条筛选的话看不到（本条不在筛选里）。
                    HighWater = highWater,

                    // ⚠️ **轴进快照必须走顶层这个 `axes`**（`05` §11 #4）：`axis.changed` 只在订阅**之后**才有，
                    // 头刚打开页面时无事件可等。少了它，五个下拉全是空的——
                    // 而「空」与「默认值」在视觉上分不出来。
                    // ⚠️ **`state.axes` 是同一份**（`SessionState` 整个序列化出去时带的）；头读**顶层**这个。
                    // 两个键承载同一份数据是刻意的——顶层是为头的便利而放，但**它们是同一个引用**，不会分叉。
                    // ⚠️ 轴只有一份，住在 `State` 里（`SessionHandle` 上不该有第二份——两份会分叉）。
                    // `State.Axes` 的类型仍带 `?`，那是为**磁盘迁移**留的（见 `SessionState` 的注释）；
                    // 而这把 `handle` 必经 `Load` / `Create`：`TryFind` 要么从激活表拿
                    //（`CoreHost.Register` 的调用点只有 `Create` 与 `Load` 出来的那份），
                    // 要么 `host.Activate` 重新 `Load`——两条路都过 `CompleteAxes`。
                    // ⇒ `!` 不是赌，是那条不变量的落点。
                    Axes = handle.State.Axes!,

                    // ⚠️ **整个 spec 交出去**（`01` §7 的配置分层本就把它当可分享信息）。
                    // 控制台 `status` 印这几样（会话 id · `Status`/`Turn` · **模型** · **目录** · 策略），
                    // 而头此前只拿得到前两样与（轴/槽）——`modelId` / `providerId` /
                    // `workingDirectory` / `createdAt` **一个都没有**。后果实测过：
                    // 用户问「怎么切换提供商」，头**答不出来**。
                    // ⇒ **不做子集裁剪**：「头要什么」是头的事（R-A），整份 `SessionSpec` 序列化即可。
                    //
                    // ⚠️ **不含 key**：`SessionSpec` 里没有 `ApiKey`（key 住在 `providers/*.json` 的
                    // `ProviderDefinition` 里）⇒ 「整份交出去」**天生不泄 key**，不是靠这里挑字段挑出来的。
                    //
                    // ⚠️ **`WorkingDirectory` 是**本机绝对路径——交出去意味着它在开放的 CORS（`*`）
                    // 下可读（终审 §Declined #2 记过）。**本计划接受**这个代价（R-A 的代价），
                    // 但它必须**点名在此**，不能装作没有：这是「整个 spec」的已知代价，不是免费的。
                    Spec = handle.Spec,

                    // ⚠️ **三个槽**：`01` §3.1 的头两个（系统 / 首要）+ §3.2 的**尾槽位**（浮动）。
                    // 第一版只有两个——它把 `01` 的「头槽位」当成全部，漏了尾槽位；
                    // 而尾槽位正是五轴显示的地方（§3.2「策略：主动性 · 权限 · 安全 · 验证强度」）。
                    // ⇒ 漏了它，义务 4 就没有落点。
                    //
                    // ⚠️ `slots` 是**渲染产物**（`05` §3.1：核心渲染、头显示）。
                    // 核心现在还没有装配器（`04` §1.3：裸核心连系统提示词都没有），
                    // 所以三个都恒为空——但 `05` §7.1 要的正是「**空的也要显示**」：
                    // 用户要能看见「它真的是空的」，而不是以为头坏了。
                    // ⇒ 如实交出空数组，**不省略字段**。
                    //
                    // ⚠️ **浮动槽的内容由头拼**：它的字段（态势 / 策略 / 其他）核心已经
                    // 用 `Axes` 如实交出来了，而「怎么排」是**显示**（`05` §3 归头）。
                    // 换个头可以排成别样——那是 §1.1 允许的。
                    Slots = new
                    {
                        System = Array.Empty<object>(),
                        Premier = Array.Empty<object>(),
                        Floating = Array.Empty<object>(),
                    },
                },
                WireJson.Options);
        });

        app.MapGet("/sessions/{id}/history", (string id) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();

            // ⚠️ 同 `/state`：**先高水位、后内容**，理由见那里。
            var highWater = bus.HighWaterMark(EventBus.SessionTopic(id));

            // ⚠️ 返回的是**当前可渲染的形态**（`05` §3「核心渲染，头只显示」）。
            // 倒查语义（`01` §12.4：≥R 原文 / <R 简化）在**核心侧**完成，头不需要知道 R 在哪。
            // ⚠️ 现在还没有简化机制，所以这一步是恒等的——但**接口形状已经对了**，
            // 将来加简化时**头的代码不用改**。
            var events = SessionQueries.HistoryOf(host, handle);

            return Results.Json(new
            {
                highWater,
                rendered = events.Select(Render).ToList(),
            }, WireJson.Options);
        });

        app.MapGet("/sessions/{id}/queue", (string id) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();
            return Results.Json(new { handle.IsRunning, handle.QueueDepth }, WireJson.Options);
        });

        // ⚠️ **头唯一的轴来源**（G8：头不许手抄轴表——`Axes.cs` 的类头明说「别处都从这张表读」）。
        // ⚠️ **标签也交出去**（`05` §3：渲染归核心）——头拿到英文档名画不出中文名。
        // ⚠️ **告警的两端也交出去**：头里写字面量的话，轴里改一个档名就让告警**静默失效**——
        // 用户以为它在守，而它再也不响。
        // ⚠️ **`alertFrom` 是「起始档」，不是「两个合法值之一」**：权限是**单调阶梯**
        //（0 禁用 … 5 信任），所以头的判据要按**档位序**——`values.indexOf(当前) >= values.indexOf(alertFrom)`。
        // 枚举 `{authorized, trusted}` 恰好等于今天的结果，但权限轴加第 6 档时枚举会静默漏掉它，
        // 序数写法自动覆盖。⇒ `values` 的顺序（= 表的顺序 = 档位序）也是契约的一部分，别排序。
        app.MapGet("/axes", () => Results.Json(new
        {
            // ⚠️ **不许排序**：`Axes.Names` 的顺序**就是**渲染顺序（T1 定的约定）。
            Axes = Session.Axes.Names.Select(n => new
            {
                Name = n,
                Label = Session.Axes.LabelOf(n),
                Values = Session.Axes.ValuesOf(n),
                InContext = Session.Axes.InContext(n),
            }),
            AlertSafety = Session.Axes.AlertSafety,
            AlertFrom = Session.Axes.AlertFrom,
        }, WireJson.Options));

        // ⚠️ **没有声明时返回 `[]`**（`03` §6）。「这个项目没人声明任何东西」
        // 是合法且最常见的形态——回 404 会让头以为端点写错了。
        //
        // ⚠️ **声明的落点是工作目录**（`01` §8：可分享的项目信息），而数据根里
        // 只存了会话——所以得先找一个会话问它的工作目录。没有会话时无从问起，如实回空数组。
        //
        // ⚠️ **这里能用 `SelectMany`**：`TryFind` 是 `ControlServer` 的私有方法，
        // 而 `ProjectDirs()` 给的是 `projects/` 下的**目录名**——那正是 `projectKey`。
        // ⚠️ **读 spec，不用 `TryFind`——因为本类的 `TryFind` 会 `Activate` 会话！**
        // （见它的回落分支 `host.Activate(pk, sessionId)`。）
        // 那意味着**每拉一次 `/claims` 就把数据根下所有会话激活进内存**：
        // 一个 **GET 端点产生了状态副作用**，且**随会话数无限增长**。
        // 与 `01` §8.3 的懒激活取向（「激活即缓存」，按需才有）相悖。
        // ⇒ 直接 `TryReadSpec` 取 `WorkingDirectory`：**不激活、更简单、无副作用**。
        // ⚠️ 且**没有陈旧问题**：`SessionSpec.WorkingDirectory` 是 `init`、建会话时定下、之后不变，
        // 所以读盘那份与内存那份**必然一致**。
        app.MapGet("/claims", () =>
        {
            var claims = ProjectKeys()
                .SelectMany(pk => host.Store.ListSessions(pk)
                    .Select(id => host.Store.TryReadSpec(pk, id)))
                .Where(spec => spec is not null)
                .SelectMany(spec => new ClaimStore(spec!.WorkingDirectory).All())
                .Distinct()
                .ToList();

            return Results.Json(claims, WireJson.Options);
        });

        // ⚠️ 头的引导（`05` §2.2）要判断「有没有提供商」——没有就得挡住其余操作。
        // ⚠️ **`apiKey` 绝不进响应**：它是本机秘密，而头的页面在浏览器里、
        // 还开着 CORS（`*`）。只回「有没有」，不回「是什么」。
        app.MapGet("/providers", () =>
            Results.Json(host.Providers.List().Select(p => new
            {
                p.Id, p.Protocol, p.DefaultModel,
                // ⚠️ **端点与认证方式要交**——端点决定数据发去哪（核心自己每次 `provider set`
                //    都印一行端点），头看不到就无从核对。`modality` 同理是配置的一部分。
                p.Endpoint, p.AuthScheme, p.Modality,
                // ⚠️ **key 只回「有没有」，不回「是什么」**（用户裁定 R-B：能改不能看）。
                //    有了它，表单能显示「已配置」而不是一个空框（空框会让人以为没配、然后重填一遍）。
                // ⚠️ 判据走 `ProviderDefinition.HasApiKey`——**一处定义、三处调用**（见 R2）。
                HasApiKey = p.HasApiKey,
            }), WireJson.Options));

        // ⚠️ **头渲染配置表单的真相来源**（「不许手抄」的执行面）：合法协议 / 合法 auth
        // 的真相都在核心（`Protocols.All` / `AuthSchemes`），头手抄过一次而且**抄错了**——
        // `head/wwwroot/index.html` 的引导文案写死 `--protocol claude-messages`，
        // 而它只是**默认值**，不是「唯一能用的」。
        //
        // ⚠️ **不交 `defaultModel`——因为它今天没有可信来源。**
        // 核到的事实：`claude-opus-5` 这个字面量**只出现在 `CommandDispatcher.cs` 一处**
        //（`ProviderUpsert` 的兜底），而 `IModelAdapter` **不交**默认模型。
        // ⇒ 它不是「第二份副本」（G10 那种），它是**唯一那份**，住在命令的兜底里。
        // ⇒ 在 schema 里交它 = 要么复制一份（G10），要么**发明**一个来源。
        // **两条都不做**：如实不交。头要预填模型，从 `/providers` 拿**已有那个 provider 的**
        // `defaultModel`——那是**用户实际在用的值**，比任何全局默认都准。
        app.MapGet("/provider-schema", () => Results.Json(new
        {
            Protocols = Protocols.All,
            AuthSchemes = new[] { AuthSchemes.ApiKey, AuthSchemes.Bearer },
            ProviderIdRule = "单个文件名：不含路径分隔符，不能是 . 或 ..，也不是 CON/NUL/COM1/CONIN$ 这类 Windows 设备名",
        }, WireJson.Options));
    }

    /// <summary>
    /// 一条事件的**可渲染形态**。
    /// ⚠️ 头显示的是**这个**，不是原始记录——「核心渲染，头只显示」（`05` §3）。
    /// </summary>
    private static object Render(Records.TimelineEvent evt)
    {
        var m = (Records.TimelineEvent.MessageEvent)evt;
        return new
        {
            From = m.From == Records.Speaker.User ? "user" : "model",
            At = m.At,
            Blocks = m.Blocks,
        };
    }

    // ---- WS（03 §3/§4/§5/§7） ----

    private void MapWebSocket(WebApplication app) => app.Map("/ws", async (HttpContext ctx) =>
    {
        // ⚠️ **Origin 白名单是 `apiKey` 可选的硬前置**：网页能改 `endpoint` 就能把流量
        // （连同 key 与整段对话）转到攻击者主机——**且不需要知道 key**。
        //
        // ⚠️ **判据是「放行无 Origin，拒绝别的 origin」，不是「要求 Origin」**：
        // 浏览器发的握手**永远带 `Origin`**（挡住坏 origin 就达到了目的），
        // 而 curl / 测试驱动 / 未来的 CLI 头**不带**——要求它会把它们**全部**误伤，
        // 而那批既有 WS 测试正是这条改动的**回归网**。
        //
        // ⚠️ **这是防网页，不是鉴权**：非浏览器客户端可以随意伪造 `Origin`
        // （本文件对应的测试就是靠 `SetRequestHeader` 伪造的）。
        // 它挡住的是「用户在浏览器里开着某个页面」这一条路，不是「有人想连就能连」。
        // ⇒ 别把这道闸当成鉴权边界（`apiKey` 可选之后尤其别）。
        //
        // ⚠️ 用**索引器**取头（`ctx.Request.Headers["Origin"]`，大小写不敏感），
        // 不用 .NET 10 的强类型属性——索引器两种写法都吃，且**能被 grep 到**。
        // ⚠️ **`localhost:7790` 与 `127.0.0.1:7790` 不是同一个 origin**（浏览器就是这么判的）
        // ⇒ 用户改用 `localhost:7790` 打开页面**会被拒**，**这是刻意的**：放行 `localhost`
        // 等于放行一个**任何本机程序都能抢占的名字**（hosts 文件 / DNS）。
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (!string.IsNullOrEmpty(origin)
            && !string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            // ⚠️ **失败要能自解释**：浏览器里用户只会看到「连不上」，得让响应体说清楚
            // 被接受的是哪个页面——否则他无从改出正确的访问方式（启动横幅里也印了同一个值）。
            ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await ctx.Response.WriteAsync($"本核心只接受来自 {allowedOrigin} 的页面。");
            return;
        }

        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        await PumpAsync(socket, ctx.RequestAborted);
    });

    /// <summary>
    /// 一条 WS 连接的一生：**收命令、发事件**。
    /// ⚠️ 两条方向各自一个循环——写不能在读里做（读在等服务端的帧时写不出去）。
    /// </summary>
    private async Task PumpAsync(WebSocket socket, CancellationToken ct)
    {
        var subs = new Dictionary<string, EventSubscription>(StringComparer.Ordinal);
        var writeLock = new SemaphoreSlim(1, 1);

        async Task SendAsync(WireFrame frame)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes<WireFrame>(frame, WireJson.Options);
            // ⚠️ 串行化写：两个方向（回执 / 事件）可能同时想写，而
            // `WebSocket.SendAsync` **不允许并发调用**（会抛）。
            await writeLock.WaitAsync(ct);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally { writeLock.Release(); }
        }

        // ⚠️ **事件转发循环**：订阅的每条流各自一个，读到就原样发出去。
        // `seq` 由 `EventBus` 在**锁内**分配并与投递同临界区 ⇒ 发出去的顺序**就是** seq 的顺序。
        var pumps = new List<Task>();
        void Subscribe(string topic, long? since)
        {
            // ⚠️ **重复订阅必须换掉那条旧的，不能直接 return。**
            // 第一版写的是 `if (subs.ContainsKey(topic)) return;`——于是一条**已经死掉的**订阅
            // 会永远占着位置：resync 时 `EventBus` 已经 `TryComplete()` 了那条 channel，
            // 泵随之结束，头按设计重新 subscribe ⇒ 被这里挡回去 ⇒ **该 topic 从此收不到任何事件**。
            // 而 `03` §7 说得很清楚：resync 是**唯一**的降级路径——那条路径因此是死的。
            // 症状更坏的地方在于头**不会发现自己停了**（socket 是健康的，它只是安静）。
            if (subs.TryGetValue(topic, out var stale))
            {
                stale.Dispose();
                subs.Remove(topic);
            }

            var sub = bus.Subscribe(topic, since);
            subs[topic] = sub;

            pumps.Add(Task.Run(async () =>
            {
                // ⚠️ 跟不上 ⇒ **拉快照重建**，这是 `03` §7 **唯一**的降级路径。
                if (sub.NeedsResync) { await SendAsync(new WireFrame.ResyncRequired(topic)); return; }

                await foreach (var p in sub.Reader.ReadAllAsync(ct))
                    await SendAsync(new WireFrame.Event(p.Topic, p.Seq, p.Payload));

                if (sub.NeedsResync) await SendAsync(new WireFrame.ResyncRequired(topic));
            }, ct));
        }

        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(socket, buffer, ct);
                if (json is null) break;

                await HandleClientFrameAsync(json, Subscribe, SendAsync, ct);
            }
        }
        catch (OperationCanceledException) { /* 头断开了——核心不关心头在不在（03 §1） */ }
        catch (WebSocketException) { /* 同上：连接层面的断开不是核心的错误 */ }
        finally
        {
            foreach (var sub in subs.Values) sub.Dispose();
            await Task.WhenAll(pumps.Select(p => p.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // ⚠️ **必须回一个 Close 才能完成关闭握手。**
                // 实测：直接靠 `Dispose` 关掉时，对端报
                // `WebSocketException: The remote party closed the WebSocket connection
                //  without completing the close handshake.`——那是**头的正常断连**
                // 被我们的实现变成了一次异常，而头那边只会看到一个「出错了」。
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return null;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return sb.ToString();
        }
    }

    private async Task HandleClientFrameAsync(
        string json, Action<string, long?> subscribe, Func<WireFrame, Task> send, CancellationToken ct)
    {
        // ⚠️ **整帧解析都要在兜底里。**
        // 第一版只把 `payload` 的**内容**解析包了 try，而 `JsonDocument.Parse`、
        // `GetProperty("topic")`、`GetProperty("id")`、`GetProperty("payload")` 都在外面——
        // 实测三种畸形帧（无 `id`、无 `topic`、根本不是 JSON）都会让 socket 变成 `Aborted`。
        // 而 `05` §1.1 明说**头是可以整个换掉的**：一个第三方头发错一帧，
        // 不该让整条连接（以及它上面所有订阅）一起死掉。
        // ⚠️ 也与既有测试的不变量一致：`WebSocket_RejectsAMalformedCommandWithoutClosingTheSocket`
        // 断言「socket 还活着」——但它只喂了一条**形状合法、会话不存在**的命令，
        // 判据比测试覆盖的边界窄得多（这是那次的教训）。
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement.Clone();
            if (!root.TryGetProperty("kind", out var kind)) return;
            var k = kind.GetString();

            switch (k)
            {
                case "subscribe":
                    subscribe(root.GetProperty("topic").GetString()!, OptSince(root));
                    break;

                case "command":
                    await HandleCommandAsync(root, send, ct);
                    break;

                default:
                    await send(new WireFrame.Ack("?", false, null, $"认不出的帧 kind='{k}'", null));
                    break;
            }
        }
        catch (Exception ex)
        {
            // 畸形帧：**回一句人话，连接留着**。
            // ⚠️ **不按异常类型白名单赌**：第一版写的是
            //   `ex is JsonException or KeyNotFoundException or InvalidOperationException`，
            //   而 `{"kind":"subscribe","topic":null}` 走的是另一条路——`GetString()` 对 JSON `null`
            //   **返回 null 而不抛** ⇒ `subscribe(null!)` ⇒ 字典查 null ⇒ **ArgumentNullException**，
            //   不在白名单里 ⇒ 逃出接收循环 ⇒ **连接 Aborted**（复核者实测 9 帧矩阵，只有这一帧会踢）。
            // ⇒ 既然「一个第三方头发错一帧，不该让整条连接一起死掉」，那就**不该按类型赌**。
            await send(new WireFrame.Ack("?", false, null, $"帧形状不对：{ex.Message}", null));
        }

        static long? OptSince(JsonElement e) =>
            e.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
    }

    /// <summary>
    /// 一条命令。
    /// ⚠️ **回执立刻发**（`03` §1 判据 2），步在后台跑（见 `CommandDispatcher` 的后台路）。
    /// ⚠️ 命令**不合法** ⇒ `rejected`；**业务失败** ⇒ `error` **事件**（`03` §3 的分界线）。
    /// </summary>
    private async Task HandleCommandAsync(JsonElement root, Func<WireFrame, Task> send, CancellationToken ct)
    {
        var id = root.GetProperty("id").GetString()!;
        var payload = root.GetProperty("payload");

        CoreCommand command;
        try { command = ParseCommand(payload); }
        catch (Exception ex)
        {
            await send(new WireFrame.Ack(id, false, null, $"命令形状不对：{ex.Message}", null));
            return;
        }

        try
        {
            var result = await dispatcher.DispatchAsync(command, id, awaitCompletion: false, ct);

            await send(result.Ack switch
            {
                Ack.Rejected r => new WireFrame.Ack(id, false, null, r.Reason, null),
                Ack.Accepted a => new WireFrame.Ack(id, true, a.SessionId, null, a.Note),
                _ => new WireFrame.Ack(id, false, null, "认不出的回执", null),
            });
        }
        catch (DispatchException)
        {
            // ⚠️ **业务失败在这里是罕见路径**：后台模式（`awaitCompletion: false`）下
            // dispatcher **不抛**——它在后台任务里捕获并**已经发过 `error` 事件**了
            // （见 `CommandDispatcher.RunStepAsync`：后台路必须吞异常，否则未观测异常会终结进程）。
            // ⇒ 这里只回执，**不重复发 error**。
            // ⚠️ 别在这里补一条 `ResyncRequired`——那是**流对不上**的信号，
            // 与「这次调用失败」是两回事，混用会让头以为要重建视图。
            await send(new WireFrame.Ack(id, true, null, null, null));
        }
    }

    private static CoreCommand ParseCommand(JsonElement payload)
    {
        var kind = payload.GetProperty("kind").GetString()!;
        return kind switch
        {
            "session.create" => new CoreCommand.SessionCreate(),
            "session.activate" => new CoreCommand.SessionActivate(payload.GetProperty("sessionId").GetString()!),
            "session.interrupt" => new CoreCommand.SessionInterrupt(payload.GetProperty("sessionId").GetString()!),
            "session.start" => new CoreCommand.SessionStart(payload.GetProperty("sessionId").GetString()!),
            "session.send" => new CoreCommand.SessionSend(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("blocks").Deserialize<List<Canonical.Block>>(WireJson.Options)!),
            // ⚠️ **线上解析是命令的第三条线**（核心语义在 dispatcher、控制台解析在 `CoreCommandParser`）——
            // 少一条这条命令就只在别的两条线上存在，而头发的帧会静默落到 `_ => throw`。
            // T3 加命令时漏了这里，而**计划里没有任何任务认领它**（全计划 grep `ParseCommand` 零命中）。
            "session.setAxis" => new CoreCommand.SessionSetAxis(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("axis").GetString()!,
                payload.GetProperty("value").GetString()!),
            // ⚠️ 与 `session.setAxis` 同一条纪律：线上解析是命令的**第三条线**。
            // ⚠️ **只有 release，没有 acquire**——`03` §3 的命令表里资源那节只有 release，
            // 且 §8.2 把它归入「止血」（头在核心/控制台不响应时的逃生口）。
            // 头的认领动作走控制台（T8），头自己**从不发** `claim.acquire`。
            // ⚠️ **字段名是 `scope`（数组），不是 `path`**——`03` §3 写 `claim.release(sessionId, scope)`，
            // `01` §12.4 写 `scope: [...]`。头的将来客户端按 `scope` 发帧。
            "claim.release" => new CoreCommand.ClaimRelease(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("scope").Deserialize<List<string>>(WireJson.Options)!),
            "provider.upsert" => new CoreCommand.ProviderUpsert(
                payload.GetProperty("id").GetString()!,
                // ⚠️ **`apiKey` 必须走 `Opt(...)`，不能用 `GetProperty`。**
                // `JsonElement.GetProperty` 在**键缺失**时抛 `KeyNotFoundException`（实测），
                // 而那会被 `HandleCommandAsync` 的 `catch` 接住、回一句「命令形状不对：…」——
                // 但线上的「省略」就是**键不存在**（不是 `"apiKey": null`），
                // 正确语义是「保留旧 key」（R-B）。用 `GetProperty` 会让头**永远改不了已有 provider**。
                // ⚠️ **刻意的取舍（不是疏漏）**：`Opt` 只认字符串，所以 `{"apiKey": 12345}` 这类
                // **非字符串**值会被当成「没给」、**静默忽略**（改前那臂是
                // `GetProperty("apiKey").GetString()!`——非字符串时 `GetProperty` 不抛，
                // 是 `GetString()` 抛，被 `HandleCommandAsync` 那个 catch-all 接住 ⇒「命令形状不对」）。
                // 之所以接受这次严格性的回退：**与 `model` / `protocol` / `endpoint` / `auth`
                // 四个兄弟字段的行为一致**（它们一直是这么处理的）。
                // ⚠️ **实测过**（真 WS + 真核心，帧 `"apiKey":12345`）：`accepted=True`、无 `reason`、
                // 盘上 key 仍是 `k`、协议照改 ⇒ **忽略，不是抹掉、也不是泄露**。
                Opt(payload, "apiKey"),
                Opt(payload, "model"), Opt(payload, "protocol"), Opt(payload, "endpoint"), Opt(payload, "auth")),
            _ => throw new ArgumentException($"认不出的命令 '{kind}'"),
        };

        static string? Opt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private bool TryFind(string sessionId, out SessionHandle handle)
    {
        if (host.TryGetActivated(sessionId, out handle!)) return true;

        foreach (var dir in ProjectDirs())
        {
            var pk = Path.GetFileName(dir);
            if (host.Store.TryReadSpec(pk, sessionId) is null) continue;
            handle = host.Activate(pk, sessionId);
            return true;
        }
        return false;
    }

    private IReadOnlyList<string> ProjectKeys() =>
        ProjectDirs().Select(d => Path.GetFileName(d)!).ToList();

    private IEnumerable<string> ProjectDirs()
    {
        var root = host.Store.ProjectsRoot;
        return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
    }
}

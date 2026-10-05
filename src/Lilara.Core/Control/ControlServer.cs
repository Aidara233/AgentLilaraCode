using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;
using Lilara.Core.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lilara.Core.Control;

// 控制面：一条 WS（双向、持久、有序）+ 一组 HTTP（一次性读 / 大对象）。
// 命令投递即返回、结果走事件；WS 收到命令立刻回 ack，步在后台跑。
public sealed class ControlServer(
    CoreHost host, CommandDispatcher dispatcher, EventBus bus, int port,
    string allowedOrigin = ControlServer.DefaultAllowedOrigin)
{
    public const int DefaultPort = 7788;

    // 默认只放行头自己的 origin（默认端口 7790）。localhost:7790 与 127.0.0.1:7790
    // 不是同一个 origin（浏览器这么判），且 localhost 是任何本机程序都能抢占的名字（hosts / DNS）。
    internal const string DefaultAllowedOrigin = "http://127.0.0.1:7790";

    // 只监听本机（核心是本设备唯一的，本机进程即可信）。
    public const string BindAddress = "127.0.0.1";

    public string Url => $"http://{BindAddress}:{port}";

    // 给启动横幅读闸真正在接受的那个值（实例值，不是默认常量）；internal 只给同程序集读。
    internal string AllowedOrigin => allowedOrigin;

    // 用 StartAsync 而不是 RunAsync：后者阻塞，端口起不来时用户先看到提示符、进程才死。
    public async Task<WebApplication> StartAsync(CancellationToken ct)
    {
        // content root 必须在这里传：builder.WebHost.UseContentRoot(...) 会抛「Changing the host
        // configuration ... is not supported」而起不来。显式钉住 AppContext.BaseDirectory。
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls(Url);

        // 必须清掉日志 provider：提示符不带换行，而 Kestrel 默认把日志写 stdout，会搅烂用户正在敲的那行。
        builder.Logging.ClearProviders();

        var app = builder.Build();

        // 头是独立进程、独立 origin ⇒ 必须放开 CORS，否则浏览器里那个页面连不上。
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

    // ---- 快照 / 查询（HTTP，一次性） ----

    private void MapSnapshots(WebApplication app)
    {
        // 自检**最先**列：核心跑在哪个数据根、哪个工作目录、几个会话在内存里。
        // 「我配了 provider 怎么看不到」这类问题，九成是数据根不对——这是它唯一的答案来源。
        // ⚠️ dataRoot / workingDirectory 是本机绝对路径，在 CORS(*) 下可读——沿用 `/state` 早就
        // 接受的同一个取舍（它一直在交出 WorkingDirectory），且只绑 127.0.0.1。
        app.MapGet("/health", () => Results.Json(new
        {
            Ok = true,
            DataRoot = host.DataRoot,
            WorkingDirectory = host.WorkingDirectory,
            Sessions = host.Activated.Count(),
        }, WireJson.Options));

        // 核心自述：版本 + 真正在接受的那个 origin + 两个端点的 URL。
        // origin 读**实例值**（不是默认常量）——构造参数换过之后，这里必须跟着变。
        // ⚠️ 构造参数今天由 `AllowedOriginResolver` 从环境变量解析（`Program.cs` 的调用点）。
        // **核心不解析任何命令行参数**——所以这里没有 `--allowed-origin` 那样的旗标，
        // 配置一律走环境变量。别在这一行写一个不存在的旗标名：那会让读注释的人以为有这么个入口。
        app.MapGet("/info", () => Results.Json(new
        {
            DataRoot = host.DataRoot,
            WorkingDirectory = host.WorkingDirectory,
            CoreVersion = typeof(ControlServer).Assembly.GetName().Version?.ToString() ?? "?",
            AllowedOrigin = allowedOrigin,
            Endpoints = new { Http = Url, Ws = $"ws://{BindAddress}:{port}/ws" },
        }, WireJson.Options));

        // 每个快照都带该 topic 的 seq 高水位：头拿它去 subscribe(since:)，
        // 「拉快照 → 订阅」之间落的事件才不漏不重。
        app.MapGet("/projects", () => Results.Json(ProjectKeys(), WireJson.Options));

        app.MapGet("/projects/{pk}/sessions", (string pk) =>
            Results.Json(host.Store.ListSessions(pk), WireJson.Options));

        app.MapGet("/sessions/{id}/state", (string id) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();

            // 先取高水位，再取内容：反过来，落在中间的事件既不在快照里、也不会被补发，头永久少一条。
            // 代价是落在 (高水位, 内容] 的事件会重复渲染一次（头去重不了），比漏轻。
            var highWater = bus.HighWaterMark(EventBus.SessionTopic(id));

            // 走 SessionQueries（先查内存激活表）：读磁盘会在步进行中报 Idle，用户会以为没在跑、再发一次。
            return Results.Json(
                new
                {
                    handle.SessionId,
                    State = SessionQueries.StateOf(host, handle),

                    HighWater = highWater,

                    // 轴必须走顶层这个 axes：axis.changed 只在订阅之后才有，头刚开页面时无事件可等。
                    // 轴只有一份、住在 State 里；handle 必经 Load / Create（都过 CompleteAxes）⇒ 下面的 ! 不是赌。
                    Axes = handle.State.Axes!,

                    // 整个 spec 交出去、不做子集裁剪：SessionSpec 里没有 ApiKey，所以天生不泄 key。
                    // 代价：WorkingDirectory 是本机绝对路径，在开放 CORS(*) 下可读。
                    Spec = handle.Spec,

                    // 三个槽（系统 / 首要 / 浮动）：渲染归核心、显示归头。
                    // 核心还没有装配器，所以恒为空——但如实交出空数组、不省略字段（空的也要显示）。
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

            // 同 /state：先高水位、后内容。
            var highWater = bus.HighWaterMark(EventBus.SessionTopic(id));

            // 返回的是当前可渲染的形态（核心渲染，头只显示）；简化机制还没有，这一步是恒等的。
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

        // 头唯一的轴来源（不许手抄轴表）：标签与告警的两端都从核心交出去，头里写字面量会静默失效。
        // alertFrom 是「起始档」，头的判据要按 values.indexOf 的档位序比（权限是单调阶梯）。
        // ⇒ values 的顺序（= 档位序）也是契约的一部分，别排序。
        // ⚠️ `range` 非空 ⇒ 这是一条**数值范围**轴：头应当画数字输入而不是下拉。
        // 但**即使有 range，`values` 也照样是那张网格**（见 `Axes.Grid` 的注释）——
        // 所以一个不认识 range 的头**画出来仍然是对的**（21 个选项的下拉），不会崩。
        // 这条是刻意的：让核心能先上线，头后跟。
        app.MapGet("/axes", () => Results.Json(new
        {
            Axes = Kernel.Axes.Names.Select(n => new
            {
                Name = n,
                Label = Kernel.Axes.LabelOf(n),
                Values = Kernel.Axes.ValuesOf(n),
                InContext = Kernel.Axes.InContext(n),
                Range = Kernel.Axes.RangeOf(n),
            }),
            AlertSafety = Kernel.Axes.AlertSafety,
            AlertFrom = Kernel.Axes.AlertFrom,
        }, WireJson.Options));

        // 没有声明时回 []（「没人声明」是合法且最常见的形态）。
        // 声明落在工作目录，所以要先找一个会话问它的工作目录。
        // 读 spec 不用 TryFind：本类的 TryFind 会 Activate 会话，一个 GET 端点不该产生状态副作用。
        app.MapGet("/claims", () =>
        {
            var projectKeys = ProjectKeys();

            // ⚠️ **`stale`**：`session.delete` 是两步（先摘声明、再删目录），崩在中间会留下
            // 「sessionId 指向一个已经不存在的会话」的声明；手删目录、或另一个核心删掉也会。
            // 它不是半删的会话目录（那个更糟，见 `01` §8），是**过期的项目级数据**——
            // 今天**不自动清理**（在别人正活跃地写 claims.json 时从别处删条目会打架，
            // 那属于 GC 一整块的事），但必须**看得见**，否则它会静静地骗人（「这个文件有人占着」）。
            //
            // ⚠️ 判据用 `SessionExists`（看**目录**）而不是 `FindSession`（要读得出 spec）：
            // 后者对**损坏的 spec.json** 返回 null ⇒ 一个真实存在、只是配置坏了的会话
            // 会被判成「已消失」，它的声明被误报成 stale。
            var pks = projectKeys.ToList();
            bool SessionAlive(string sessionId) =>
                pks.Any(pk => host.Store.SessionExists(pk, sessionId));

            var claims = projectKeys
                .SelectMany(pk => host.Store.ListSessions(pk)
                    .Select(id => host.Store.TryReadSpec(pk, id)))
                .Where(spec => spec is not null)
                .SelectMany(spec => new ClaimStore(spec!.WorkingDirectory).All())
                .Distinct()
                .Select(c => new
                {
                    c.SessionId, c.ProjectKey, c.Path, c.At,
                    Stale = !SessionAlive(c.SessionId),
                })
                .ToList();

            return Results.Json(claims, WireJson.Options);
        });

        // apiKey 绝不进响应（本机秘密 + CORS *）：只回「有没有」，不回「是什么」。
        app.MapGet("/providers", () =>
            Results.Json(host.Providers.List().Select(p => new
            {
                p.Id, p.Protocol, p.DefaultModel,
                // 端点 / 认证方式 / modality 是配置的一部分，要交出去（头看不到就无从核对）。
                p.Endpoint, p.AuthScheme, p.Modality,
                // 模型清单：会话视图把模型画成下拉，选项就从这里来。null = 没声明（头退回文本框）。
                p.Models,
                // key 只回 HasApiKey（能改不能看）：表单能显示「已配置」而不是空框。
                HasApiKey = p.HasApiKey,
            }), WireJson.Options));

        // 头渲染配置表单的真相来源：合法协议 / auth 都在核心（头不许手抄，曾抄错）。
        // 不交 defaultModel——它没有可信来源；头要预填模型，从 /providers 拿已有 provider 的那个值。
        app.MapGet("/provider-schema", () => Results.Json(new
        {
            Protocols = Protocols.All,
            // ⚠️ 从 `AuthSchemes.All` 读，**不是**手写的 `new[] { … }`——那份加常量时不会跟上。
            AuthSchemes = AuthSchemes.All,
            // ⚠️ 缺省认证方式也要交：不交的话头要么手抄（会漂），要么把下拉留空。
            DefaultAuthScheme = AuthSchemes.Default,
            // ⚠️ 文案与谓词同处一地（`CommandDispatcher.ProviderIdRule`）——别在这里内联，
            // 那份曾经内联在此，改谓词的人看不见它 ⇒ 人话会静默变成假话。
            ProviderIdRule = CommandDispatcher.ProviderIdRule,
        }, WireJson.Options));
    }

    // 一条事件的可渲染形态（核心渲染，头只显示）。
    private static object Render(Kernel.TimelineEvent evt)
    {
        var m = (Kernel.TimelineEvent.MessageEvent)evt;
        return new
        {
            From = m.From == Kernel.Speaker.User ? "user" : "model",
            At = m.At,
            Blocks = m.Blocks,
        };
    }

    // ---- WS ----

    private void MapWebSocket(WebApplication app) => app.Map("/ws", async (HttpContext ctx) =>
    {
        // Origin 白名单是 apiKey 可选的硬前置：网页能改 endpoint 就能把流量（含 key）转到攻击者主机。
        // 放行「无 Origin」、拒绝别的 origin——浏览器握手必带 Origin，curl / 测试 / CLI 不带。
        // 这是防网页、不是鉴权（非浏览器客户端可伪造 Origin）。
        // localhost:7790 与 127.0.0.1:7790 不是同一个 origin（浏览器这么判），拒 localhost 是刻意的。
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (!string.IsNullOrEmpty(origin)
            && !string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            // 失败要能自解释：响应体说明被接受的是哪个页面（启动横幅里也印了同一个值）。
            // ⚠️ **但这句只有非浏览器客户端看得到**：WS 握手被拒时浏览器**不把响应体交给页面 JS**，
            // 页面只会走 `onclose`。所以横幅才是浏览器用户的自解释渠道——
            // 头那边会去拉 `/info` 比对 `location.origin`，把原因显示在页面上。
            ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await ctx.Response.WriteAsync(
                $"本核心只接受来自 {allowedOrigin} 的页面。若是浏览器页面，"
                + "请对照核心启动横幅里印的那一行（页面本身看不到这句响应体）。");
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

    // 一条 WS 连接：收命令、发事件。读写各一个循环——写不能在读里做（读在等服务端帧时写不出去）。
    private async Task PumpAsync(WebSocket socket, CancellationToken ct)
    {
        var subs = new Dictionary<string, EventSubscription>(StringComparer.Ordinal);
        var writeLock = new SemaphoreSlim(1, 1);

        async Task SendAsync(WireFrame frame)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes<WireFrame>(frame, WireJson.Options);
            // 串行化写：回执与事件可能同时想写，而 WebSocket.SendAsync 不允许并发调用（会抛）。
            await writeLock.WaitAsync(ct);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally { writeLock.Release(); }
        }

        // 订阅的每条流一个转发循环；seq 由 EventBus 在锁内分配 ⇒ 发出的顺序就是 seq 的顺序。
        var pumps = new List<Task>();
        void Subscribe(string topic, long? since)
        {
            // 重复订阅必须换掉旧的那条，不能直接 return：死掉的订阅占着位置，
            // 该 topic 会从此收不到任何事件（resync 后重新 subscribe 也被挡回去）。
            if (subs.TryGetValue(topic, out var stale))
            {
                stale.Dispose();
                subs.Remove(topic);
            }

            var sub = bus.Subscribe(topic, since);
            subs[topic] = sub;

            pumps.Add(Task.Run(async () =>
            {
                // 跟不上 ⇒ 拉快照重建（唯一的降级路径）。
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
        catch (OperationCanceledException) { }   // 头断开了；核心不关心头在不在
        catch (WebSocketException) { }           // 连接层面的断开，不是核心的错误
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
                // 必须回一个 Close 才能完成关闭握手：靠 Dispose 关掉时对端报
                // 「without completing the close handshake」，头的正常断连变成一次异常。
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
        // 整帧解析都要在兜底里：头可以整个换掉，一个第三方头发错一帧不该让整条连接一起死掉。
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
            // 畸形帧：回一句人话，连接留着。
            // 不按异常类型白名单赌：种类数不完（如 topic:null 走 ArgumentNullException），
            // 漏一种就连整条连接一起死。
            await send(new WireFrame.Ack("?", false, null, $"帧形状不对：{ex.Message}", null));
        }

        static long? OptSince(JsonElement e) =>
            e.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
    }

    // 回执立刻发，步在后台跑。命令不合法 ⇒ rejected；业务失败 ⇒ error 事件。
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
            // 后台模式下 dispatcher 不抛（已自己发过 error 事件）⇒ 这里只回执，不重复发 error。
            // 别在这里补 ResyncRequired——那是流对不上的信号，与「这次调用失败」是两回事。
            await send(new WireFrame.Ack(id, true, null, null, null));
        }
    }

    // ⚠️ `internal` 而不是 `private`：`CommandSurfaceTests` 直接调它来钉「线上每条 kind 都
    // 造出它该造的那个类型」——**调真函数，不在测试里复刻一层判据**（复刻品总比真货少几行，
    // 而少的那几行恰恰是没人在看的）。仅可见性放开，行为不动。
    internal static CoreCommand ParseCommand(JsonElement payload)
    {
        var kind = payload.GetProperty("kind").GetString()!;
        return kind switch
        {
            // 两个字段全走 Opt：**键缺失 = 「没给」**（回落规则在 dispatcher）。
            // 用 GetProperty 会让「只起名、不选目录」这种常见形态抛 KeyNotFoundException。
            "session.create" => new CoreCommand.SessionCreate
            {
                Cwd = Opt(payload, "cwd"),
                Title = Opt(payload, "title"),
            },
            "session.activate" => new CoreCommand.SessionActivate(payload.GetProperty("sessionId").GetString()!),
            "session.interrupt" => new CoreCommand.SessionInterrupt(payload.GetProperty("sessionId").GetString()!),
            "session.start" => new CoreCommand.SessionStart(payload.GetProperty("sessionId").GetString()!),
            "session.send" => new CoreCommand.SessionSend(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("blocks").Deserialize<List<Kernel.Block>>(WireJson.Options)!),
            // 线上解析是命令的第三条线（dispatcher / CoreCommandParser / 这里）——
            // 少一条，头发的帧会静默落到 `_ => throw`。
            "session.setAxis" => new CoreCommand.SessionSetAxis(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("axis").GetString()!,
                payload.GetProperty("value").GetString()!),
            // 同 session.setAxis：线上解析是第三条线。
            // providerId / model 走 Opt：**键缺失 = 「保留旧值」**（与 provider.upsert 同一条语义）。
            // 用 GetProperty 会在头只想换模型、不提 provider 时抛 KeyNotFoundException。
            "session.configure" => new CoreCommand.SessionConfigure(
                payload.GetProperty("sessionId").GetString()!,
                Opt(payload, "providerId"), Opt(payload, "model")),
            // 同 session.setAxis：线上解析是第三条线。
            // 只有 release、没有 acquire（头的认领走控制台）；字段名是 scope（数组），不是 path。
            "claim.release" => new CoreCommand.ClaimRelease(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("scope").Deserialize<List<string>>(WireJson.Options)!),
            // ---- 销毁面 ----
            "session.deactivate" => new CoreCommand.SessionDeactivate(
                payload.GetProperty("sessionId").GetString()!),
            // ⚠️ `archived` 走 `OptBool` 而不是 GetProperty：**键缺失 = 归档**（三态的中性那一档）。
            // 用 `GetProperty(...).GetBoolean()` 会逼着头每次都显式发一个 true，
            // 而「没给」在这条命令里是**有意义的**（= 归档），不是「用户没填」。
            "session.archive" => new CoreCommand.SessionArchive(
                payload.GetProperty("sessionId").GetString()!, OptBool(payload, "archived")),
            "session.delete" => new CoreCommand.SessionDelete(
                payload.GetProperty("sessionId").GetString()!),
            "provider.upsert" => new CoreCommand.ProviderUpsert(
                payload.GetProperty("id").GetString()!,
                // apiKey 必须走 Opt：GetProperty 在键缺失时抛 KeyNotFoundException，
                // 而线上的「省略」就是键不存在，语义是「保留旧 key」（用 GetProperty 会让头永远改不了已有 provider）。
                // Opt 只认字符串 ⇒ 非字符串值被静默当成没给；与 model/protocol/endpoint/auth 四个兄弟字段一致。
                Opt(payload, "apiKey"),
                Opt(payload, "model"), Opt(payload, "protocol"), Opt(payload, "endpoint"), Opt(payload, "auth"))
            {
                // 模型清单是**数组**，Opt 只认字符串 ⇒ 单独一个助手。
                // 键缺失 / 不是数组 / 数组里有非字符串 ⇒ null（= 保留旧清单），与兄弟字段同一条语义。
                Models = OptStringList(payload, "models"),
            },
            // 同 provider.upsert / claim.release：线上解析是第三条线。
            "provider.delete" => new CoreCommand.ProviderDelete(
                payload.GetProperty("id").GetString()!),
            // 连通性自检。与 provider.delete 同形（只带 id）。
            "provider.test" => new CoreCommand.ProviderTest(
                payload.GetProperty("id").GetString()!),
            _ => throw new ArgumentException($"认不出的命令 '{kind}'"),
        };

        static string? Opt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // 布尔版。⚠️ **只认 `true` / `false` 两个字面量**，其余（缺失 / null / 字符串 `"false"` /
        // 数字）一律当「没给」⇒ 在 `session.archive` 上就是**归档**。
        // 把 `"false"` 也认成 false 会开一个「前端传了个字符串也能改语义」的口子，
        // 而 JSON 里布尔本来就有它自己的形状。与 `Opt` / `OptStringList` 同一个粒度取舍。
        static bool? OptBool(JsonElement e, string name) => e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

        // 字符串数组版。⚠️ 与 `Opt` 一样「只认形状对的」：键缺失 / 不是数组 / 元素不是字符串 ⇒ null。
        // 元素不是字符串这一条**不**报错而是整份当没给——与 `Opt` 的取舍一致（`provider.upsert`
        // 的兄弟字段都是这个粒度），且「头传了半份清单」这种形状不会比「没传」更可信。
        static IReadOnlyList<string>? OptStringList(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;

            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                list.Add(item.GetString()!);
            }
            return list;
        }
    }

    private bool TryFind(string sessionId, out SessionHandle handle)
    {
        if (host.TryGetActivated(sessionId, out handle!)) return true;

        // ⚠️ **miss 时必须回落 `Activate`，不许在这里交磁盘那份。**
        // `SessionQueries.StateOf` / `CoreHost.TryGetActivated` / `/state` 三处都写着同一条：
        // 步进行中**磁盘上是 `Idle`**（`Running` 只写内存、到步末才落盘）。
        // 少了这条回落，一个刚被 `session.deactivate` 摘掉的会话——而头那一侧看着它还在跑——
        // 会被渲染成「空闲」，用户以为没在跑、**再点一次发送** ⇒ 队列两条 ⇒ 烧钱。
        if (host.Store.FindSession(sessionId) is not { } found) return false;

        handle = host.Activate(found.ProjectKey, sessionId);
        return true;
    }

    private IReadOnlyList<string> ProjectKeys() => host.Store.ListProjects();
}

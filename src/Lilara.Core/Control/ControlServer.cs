using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Protocol;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;
using Lilara.Core.Providers;
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
        app.MapGet("/axes", () => Results.Json(new
        {
            Axes = Kernel.Axes.Names.Select(n => new
            {
                Name = n,
                Label = Kernel.Axes.LabelOf(n),
                Values = Kernel.Axes.ValuesOf(n),
                InContext = Kernel.Axes.InContext(n),
            }),
            AlertSafety = Kernel.Axes.AlertSafety,
            AlertFrom = Kernel.Axes.AlertFrom,
        }, WireJson.Options));

        // 没有声明时回 []（「没人声明」是合法且最常见的形态）。
        // 声明落在工作目录，所以要先找一个会话问它的工作目录。
        // 读 spec 不用 TryFind：本类的 TryFind 会 Activate 会话，一个 GET 端点不该产生状态副作用。
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

        // apiKey 绝不进响应（本机秘密 + CORS *）：只回「有没有」，不回「是什么」。
        app.MapGet("/providers", () =>
            Results.Json(host.Providers.List().Select(p => new
            {
                p.Id, p.Protocol, p.DefaultModel,
                // 端点 / 认证方式 / modality 是配置的一部分，要交出去（头看不到就无从核对）。
                p.Endpoint, p.AuthScheme, p.Modality,
                // key 只回 HasApiKey（能改不能看）：表单能显示「已配置」而不是空框。
                HasApiKey = p.HasApiKey,
            }), WireJson.Options));

        // 头渲染配置表单的真相来源：合法协议 / auth 都在核心（头不许手抄，曾抄错）。
        // 不交 defaultModel——它没有可信来源；头要预填模型，从 /providers 拿已有 provider 的那个值。
        app.MapGet("/provider-schema", () => Results.Json(new
        {
            Protocols = Protocols.All,
            AuthSchemes = new[] { AuthSchemes.ApiKey, AuthSchemes.Bearer },
            ProviderIdRule = "单个文件名：不含路径分隔符，不能是 . 或 ..，也不是 CON/NUL/COM1/CONIN$ 这类 Windows 设备名",
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
                payload.GetProperty("blocks").Deserialize<List<Kernel.Block>>(WireJson.Options)!),
            // 线上解析是命令的第三条线（dispatcher / CoreCommandParser / 这里）——
            // 少一条，头发的帧会静默落到 `_ => throw`。
            "session.setAxis" => new CoreCommand.SessionSetAxis(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("axis").GetString()!,
                payload.GetProperty("value").GetString()!),
            // 同 session.setAxis：线上解析是第三条线。
            // 只有 release、没有 acquire（头的认领走控制台）；字段名是 scope（数组），不是 path。
            "claim.release" => new CoreCommand.ClaimRelease(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("scope").Deserialize<List<string>>(WireJson.Options)!),
            "provider.upsert" => new CoreCommand.ProviderUpsert(
                payload.GetProperty("id").GetString()!,
                // apiKey 必须走 Opt：GetProperty 在键缺失时抛 KeyNotFoundException，
                // 而线上的「省略」就是键不存在，语义是「保留旧 key」（用 GetProperty 会让头永远改不了已有 provider）。
                // Opt 只认字符串 ⇒ 非字符串值被静默当成没给；与 model/protocol/endpoint/auth 四个兄弟字段一致。
                Opt(payload, "apiKey"),
                Opt(payload, "model"), Opt(payload, "protocol"), Opt(payload, "endpoint"), Opt(payload, "auth")),
            // 同 provider.upsert / claim.release：线上解析是第三条线。
            "provider.delete" => new CoreCommand.ProviderDelete(
                payload.GetProperty("id").GetString()!),
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

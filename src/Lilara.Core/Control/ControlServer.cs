using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
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
    CoreHost host, CommandDispatcher dispatcher, EventBus bus, int port)
{
    public const int DefaultPort = 7788;

    /// <summary>⚠️ 只监听本机（`03` §10「鉴权最小」：核心是本设备唯一的，本机进程即可信）。</summary>
    public const string BindAddress = "127.0.0.1";

    public string Url => $"http://{BindAddress}:{port}";

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

            // ⚠️ **先查内存激活表**（`SessionQueries.StateOf`）——读磁盘会在**步进行中报 Idle**，
            // 于是头渲染「空闲」⇒ 用户以为没在跑 ⇒ **再点一次发送** ⇒ 队列两条 ⇒ 烧钱。
            return Results.Json(
                new { handle.SessionId, State = SessionQueries.StateOf(host, handle), HighWater = bus.HighWaterMark(EventBus.SessionTopic(id)) },
                WireJson.Options);
        });

        app.MapGet("/sessions/{id}/history", (string id) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();

            // ⚠️ 返回的是**当前可渲染的形态**（`05` §3「核心渲染，头只显示」）。
            // 倒查语义（`01` §12.4：≥R 原文 / <R 简化）在**核心侧**完成，头不需要知道 R 在哪。
            // ⚠️ 现在还没有简化机制，所以这一步是恒等的——但**接口形状已经对了**，
            // 将来加简化时**头的代码不用改**。
            var events = SessionQueries.HistoryOf(host, handle);

            return Results.Json(new
            {
                highWater = bus.HighWaterMark(EventBus.SessionTopic(id)),
                rendered = events.Select(Render).ToList(),
            }, WireJson.Options);
        });

        app.MapGet("/sessions/{id}/queue", (string id) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();
            return Results.Json(new { handle.IsRunning, handle.QueueDepth }, WireJson.Options);
        });
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
            if (subs.ContainsKey(topic)) return;

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
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind", out var kind)) return;

        switch (kind.GetString())
        {
            case "subscribe":
                subscribe(
                    root.GetProperty("topic").GetString()!,
                    root.TryGetProperty("since", out var since) && since.ValueKind == JsonValueKind.Number
                        ? since.GetInt64() : null);
                break;

            case "command":
                await HandleCommandAsync(root, send, ct);
                break;
        }
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
            "provider.upsert" => new CoreCommand.ProviderUpsert(
                payload.GetProperty("id").GetString()!,
                payload.GetProperty("apiKey").GetString()!,
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

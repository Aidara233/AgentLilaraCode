using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Adapters;
using Lilara.Core.Kernel;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

// 控制面（HTTP 快照 + WS）：真的起 Kestrel 并真的连（短连接、端口由系统分配）——控制面的错法大多是
// 「形状对但接不上」（CORS 头、WS 握手、帧序列化、并发写 socket），桩测不到。
// 端口绑 0 让 OS 分配（不是 Random.Next）：随机段会撞上 Windows 的 TCP 排除段（Hyper-V/WSL 保留），绑到它会抛 WSAEACCES。
public class ControlServerTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-cs-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-cswork-" + Guid.NewGuid().ToString("N"));

    private CoreHost _host = null!;
    private EventBus _bus = null!;
    private CommandDispatcher _dispatcher = null!;
    private ControlServer _server = null!;
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _host = new CoreHost(_root, _work);
        _host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        _bus = new EventBus();
        // 预置回复：让步真的走通，事件里才有模型那条 message.appended 与 step.end，否则测的只是「失败路径也发事件」。
        _adapter = new Fakes.FakeAdapter();
        _adapter.Reply("好的").Reply("好的").Reply("好的");
        _dispatcher = new CommandDispatcher(_host, _bus, h => new Stepper(_host.Store, _adapter));

        // 绑 0 让 OS 分配：随机段会撞上 Windows 的 TCP 排除段（WSAEACCES）；端口从启动后的应用读回来。
        _server = new ControlServer(_host, _dispatcher, _bus, 0);
        _app = await _server.StartAsync(CancellationToken.None);
        _port = new Uri(_app.Urls.First()).Port;
    }

    private Fakes.FakeAdapter _adapter = null!;

    public async Task DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private string Http(string path) => $"http://127.0.0.1:{_port}{path}";
    private string Ws() => $"ws://127.0.0.1:{_port}/ws";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    // 只监听本机（鉴权最小）。
    [Fact]
    public void BindAddress_IsLoopbackOnly() =>
        Assert.Equal("127.0.0.1", ControlServer.BindAddress);

    // 快照必须带 seq 高水位：头拿它去 subscribe(since:)，「拉快照 → 订阅」之间落的事件才不漏不重。
    [Fact]
    public async Task History_And_State_CarryTheSeqHighWaterMark()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // 建会话本身会发事件（session.created，项目流 + 会话流各一条），故高水位不是从 0 起；
        // 断言改成「与 HighWaterMark 读数一致」——那才是真正的不变量。
        var before = _bus.HighWaterMark(EventBus.SessionTopic(sid));
        _bus.Publish(EventBus.SessionTopic(sid), new WireEvent.Error(sid, "x"));
        var expected = _bus.HighWaterMark(EventBus.SessionTopic(sid));
        Assert.Equal(before + 1, expected);

        var history = await Client.GetStringAsync(Http($"/sessions/{sid}/history"));
        var state = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));

        using var h = JsonDocument.Parse(history);
        using var s = JsonDocument.Parse(state);

        // 不是写死 1：是「快照带回来的就是流的高水位」。
        Assert.Equal(expected, h.RootElement.GetProperty("highWater").GetInt64());
        Assert.Equal(expected, s.RootElement.GetProperty("highWater").GetInt64());
    }

    // 未知会话 ⇒ 404，不是 500、不是空对象。
    [Fact]
    public async Task UnknownSession_Is404()
    {
        var r = await Client.GetAsync(Http("/sessions/nope/state"));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, r.StatusCode);
    }

    // 头是独立 origin，不放开 CORS 浏览器就连不上；预检（OPTIONS）必须回 204 且带允许头。
    [Fact]
    public async Task Cors_AllowsTheHeadsOrigin()
    {
        using var req = new HttpRequestMessage(HttpMethod.Options, Http("/projects"));
        var r = await Client.SendAsync(req);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, r.StatusCode);
        Assert.True(r.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // 控制面的核心验收：连上 → 订阅 → 发命令 → 拿到 ack → 事件到达。
    // 一次验掉 WS 握手、帧的序列化形状、命令投递即返回、事件带 seq 且单调。
    [Fact]
    public async Task WebSocket_AcksCommandsAndStreamsEvents()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);

        // 建会话——拿 sessionId（分配型命令**必须**回传身份）
        await SendAsync(ws, """{"kind":"command","id":"c1","payload":{"kind":"session.create"}}""");
        var ack = await ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("kind").GetString());
        Assert.Equal("c1", ack.GetProperty("id").GetString());
        Assert.True(ack.GetProperty("accepted").GetBoolean());
        var sid = ack.GetProperty("sessionId").GetString()!;

        // 订阅之后再发——订阅点之前的我们不关心
        await SendAsync(ws, "{\"kind\":\"subscribe\",\"topic\":\"session:" + sid + "\"}");

        // 用拼接而不是 $$"""：内插原始字符串里，载荷末尾的 }}} 会被当成内插洞的收尾（CS9007）。
        await SendAsync(ws,
            "{\"kind\":\"command\",\"id\":\"c2\",\"payload\":{\"kind\":\"session.send\","
            + "\"sessionId\":\"" + sid + "\",\"blocks\":[{\"kind\":\"text\",\"text\":\"你好\"}]}}");

        // 不能写死帧数：ack 与 event 是两条独立的写路径，条数与时序都不定；收到真正在等的东西就停。
        var frames = new List<JsonElement>();
        for (var i = 0; i < 12; i++)
        {
            frames.Add(await ReceiveAsync(ws));
            var haveAck = frames.Any(f => f.GetProperty("kind").GetString() == "ack"
                                       && f.GetProperty("id").GetString() == "c2");
            var haveEnd = frames.Any(f => f.GetProperty("kind").GetString() == "event"
                                       && f.GetProperty("payload").GetProperty("kind").GetString()
                                          == "session.step.end");
            if (haveAck && haveEnd) break;
        }

        Assert.Contains(frames, f => f.GetProperty("kind").GetString() == "ack"
                                  && f.GetProperty("id").GetString() == "c2");

        var events = frames.Where(f => f.GetProperty("kind").GetString() == "event").ToList();
        Assert.NotEmpty(events);

        // 事件带 seq，且单调增
        var seqs = events.Select(e => e.GetProperty("seq").GetInt64()).ToList();
        for (var i = 1; i < seqs.Count; i++)
            Assert.True(seqs[i] > seqs[i - 1], "事件 seq 必须严格递增");

        // 载荷自己的判别式也在（WireEvent 独立成根，不是继承 TimelineEvent）
        Assert.Contains(events, e => e.GetProperty("payload").GetProperty("kind").GetString()
                                     == "message.appended");
    }

    // 命令不合法 ⇒ accepted: false + 理由；业务失败走 error 事件——两者混了头会把网络抖动当成「你命令写错了」。
    [Fact]
    public async Task WebSocket_RejectsAMalformedCommandWithoutClosingTheSocket()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);

        await SendAsync(ws, """{"kind":"command","id":"c1","payload":{"kind":"session.interrupt","sessionId":"nope"}}""");
        var ack = await ReceiveAsync(ws);

        Assert.False(ack.GetProperty("accepted").GetBoolean());
        Assert.Contains("nope", ack.GetProperty("reason").GetString());

        // 而且 socket 还活着——一条坏命令不该踢掉头。
        Assert.Equal(WebSocketState.Open, ws.State);
        await SendAsync(ws, """{"kind":"command","id":"c2","payload":{"kind":"provider.upsert","id":"p","apiKey":"k2"}}""");
        Assert.True((await ReceiveAsync(ws)).GetProperty("accepted").GetBoolean());
    }

    // 线上的 session.setAxis 必须真的能到 dispatcher；走真的 WS，不是直接调 dispatcher——
    // 后者会在实现缺失时照样绿。
    [Fact]
    public async Task WebSocket_SetAxis_ReachesTheDispatcher()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        // 用拼接而不是 $$"""：载荷末尾的 }}} 会被当成内插洞的收尾（CS9007）。
        await SendAsync(ws, "{\"kind\":\"command\",\"id\":\"a1\",\"payload\":{\"kind\":\"session.setAxis\","
            + "\"sessionId\":\"" + sid + "\",\"axis\":\"safety\",\"value\":\"prod\"}}");

        var ack = await ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("kind").GetString());
        // 理由必须惰性取：WhenWritingNull 把成功回执的 reason 整个省略，ack.GetProperty("reason") 会抛
        // KeyNotFoundException，而 Assert.True 的消息串是无条件先求值的；用 TryGetProperty。
        Assert.True(ack.GetProperty("accepted").GetBoolean(),
            "认不出这条命令——`ParseCommand` 的 switch 漏了它："
            + (ack.TryGetProperty("reason", out var why) ? why.GetString() : "(无 reason 字段)"));

        // 判据是「命令真的生效了」，不只是「回执说 accepted」——回执可以说 accepted 而语义根本没跑。
        var state = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));
        using var doc = JsonDocument.Parse(state);
        Assert.Equal("prod", doc.RootElement.GetProperty("axes").GetProperty("safety").GetString());
    }

    // 线上的 provider.upsert：省略 apiKey = 保留旧 key。
    // 判据是「帧里根本没有 apiKey 键」，不是 "apiKey": null——线上是 JSON，省略就是这个形状；走真的 WS。
    [Fact]
    public async Task WebSocket_ProviderUpsert_WithoutTheApiKeyKey_KeepsTheStoredOne()
    {
        // 前置：InitializeAsync 已存下 provider "p"（key="k"、协议 claude-messages）。
        Assert.Equal("k", _host.Providers.Load("p")!.ApiKey);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        // 帧里没有 apiKey 键——「省略」是键不存在，不是值为 null。
        await SendAsync(ws, "{\"kind\":\"command\",\"id\":\"u1\",\"payload\":{\"kind\":\"provider.upsert\","
            + "\"id\":\"p\",\"protocol\":\"openai-chat\"}}");

        var ack = await ReceiveAsync(ws);
        // 理由必须惰性取：WhenWritingNull 把成功回执的 reason 省略，直接 GetProperty 会抛 KeyNotFoundException。
        Assert.True(ack.GetProperty("accepted").GetBoolean(),
            "省略 apiKey 的帧被回了「命令形状不对」——`ParseCommand` 那臂还在用 `GetProperty(\"apiKey\")`："
            + (ack.TryGetProperty("reason", out var why) ? why.GetString() : "(无 reason 字段)"));

        // 判据是盘上的后果，不只是回执说 accepted：协议真的改了，而 key 仍是旧的。
        var after = _host.Providers.Load("p")!;
        Assert.Equal(Protocols.OpenAiChat, after.Protocol);
        Assert.Equal("k", after.ApiKey);
    }

    // 线上那条：帧里 apiKey 是空串（「键完全缺失」是上面另一条测试）——只测 dispatcher 会漏掉「线上那条把 "" 变成 null、或反过来」。
    [Fact]
    public async Task WebSocket_ProviderUpsert_WithAnEmptyApiKey_KeepsTheStoredOne()
    {
        // 前置：InitializeAsync 已存下 provider "p"（key="k"、协议 claude-messages）。
        Assert.Equal("k", _host.Providers.Load("p")!.ApiKey);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        // "apiKey":""——是空串，不是缺失。
        await SendAsync(ws, "{\"kind\":\"command\",\"id\":\"u2\",\"payload\":{\"kind\":\"provider.upsert\","
            + "\"id\":\"p\",\"protocol\":\"openai-chat\",\"apiKey\":\"\"}}");

        var ack = await ReceiveAsync(ws);
        Assert.True(ack.GetProperty("accepted").GetBoolean(),
            "空串 apiKey 的帧没被接受——"
            + (ack.TryGetProperty("reason", out var why) ? why.GetString() : "(无 reason 字段)"));

        var after = _host.Providers.Load("p")!;
        Assert.Equal("k", after.ApiKey);                          // 空串不该抹掉旧 key
        Assert.Equal(Protocols.OpenAiChat, after.Protocol);
    }

    // 线上的 provider.delete：判据是两端（ack.accepted 与盘上的后果）；
    // 必须走真的 WS，直接调 dispatcher 的测试会绕过解析照样绿。
    [Fact]
    public async Task WebSocket_ProviderDelete_RemovesTheStoredDefinition()
    {
        // 前置：InitializeAsync 已存下 provider "p"。
        Assert.NotNull(_host.Providers.Load("p"));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, """{"kind":"command","id":"d1","payload":{"kind":"provider.delete","id":"p"}}""");

        var ack = await ReceiveAsync(ws);
        // 理由必须惰性取：WhenWritingNull 把成功回执的 reason 省略，直接 GetProperty 会抛 KeyNotFoundException。
        Assert.True(ack.GetProperty("accepted").GetBoolean(),
            "provider.delete 的帧没被接受——`ParseCommand` 缺那一臂时它会落进 `_ => throw`："
            + (ack.TryGetProperty("reason", out var why) ? why.GetString() : "(无 reason 字段)"));

        // 盘上真没了——只断言 ack 是半条判据。
        Assert.Null(_host.Providers.Load("p"));
    }

    // 线上那条的「不存在 ⇒ 拒绝」（不是幂等成功），且理由要点名那个 id；
    // 与 dispatcher 那条同源，但这条走真 WS——帧的字段名接不上时只有这条会红。
    [Fact]
    public async Task WebSocket_ProviderDelete_OfAMissingProvider_IsRejectedNamingTheId()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, """{"kind":"command","id":"d2","payload":{"kind":"provider.delete","id":"nope"}}""");

        var ack = await ReceiveAsync(ws);
        Assert.False(ack.GetProperty("accepted").GetBoolean());
        Assert.Contains("nope", ack.GetProperty("reason").GetString());
    }

    // 头断连不影响任何东西：断开之后核心照常收命令。
    [Fact]
    public async Task HeadDisconnecting_DoesNotAffectTheCore()    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        ws.Dispose();

        // 核心照常工作
        var r = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(r.Ack);
    }

    // WS 的 Origin 白名单：放行无 Origin，拒绝别的 origin（不是「要求 Origin」）——
    // 浏览器发的握手永远带 Origin，而 curl / 测试驱动 / 未来的 CLI 头不带，要求它等于把它们全部误伤。
    [Theory]
    [InlineData(null)]                     // 无 Origin（curl / 测试驱动）⇒ 放行
    [InlineData("http://127.0.0.1:7790")]  // 头自己的 origin ⇒ 放行
    [InlineData("http://evil.example")]    // ⇒ 拒绝
    [InlineData("http://localhost:7790")]  // ⚠️ 同机不同写法：**拒绝**——见下
    [InlineData("null")]                   // `file://` 页面与沙箱 iframe 的 Origin 字面量是 "null"
    public async Task WebSocket_OriginPolicy(string? origin)
    {
        using var socket = new ClientWebSocket();
        // 只能手设头（设备端没有带 Origin 的握手 API）；不设则该头根本不存在（读出来是空串，不是 null）。
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);

        var connect = socket.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        if (origin is null or "http://127.0.0.1:7790")
        {
            await connect;                                    // 放行 ⇒ 握上手
            Assert.Equal(WebSocketState.Open, socket.State);
        }
        else
        {
            // 握手被拒时 ConnectAsync 抛 WebSocketException（服务端回了 403）。
            // localhost:7790 在拒绝之列是刻意的：它与 127.0.0.1:7790 不是同一个 origin，
            // 且放行 localhost 等于放行一个任何本机程序都能抢占的名字（hosts 文件 / DNS）。
            await Assert.ThrowsAnyAsync<WebSocketException>(() => connect);
        }
    }

    // 钉住「闸读的是参数，不是写死的字面量」：上面那条理论用的是默认值，把参数忽略掉也照样全绿；
    // 本条在显式传非默认值的服务器上断言默认值那个 origin 必须被拒。
    [Fact]
    public async Task WebSocket_HonoursANonDefaultAllowedOrigin()
    {
        // 显式传一个非默认的 allowedOrigin；另起一个 ControlServer（绑 0 再读回实际端口）。
        const string mine = "http://127.0.0.1:19999";
        var server = new ControlServer(_host, _dispatcher, _bus, 0, allowedOrigin: mine);
        var app = await server.StartAsync(CancellationToken.None);
        var port = new Uri(app.Urls.First()).Port;
        try
        {
            // 放行：非默认那个 origin 自己
            using (var ok = new ClientWebSocket())
            {
                ok.Options.SetRequestHeader("Origin", mine);
                await ok.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
                Assert.Equal(WebSocketState.Open, ok.State);
            }
            // 反向：默认值那个 origin 在这里必须被拒——否则说明参数没被用上
            using var bad = new ClientWebSocket();
            bad.Options.SetRequestHeader("Origin", "http://127.0.0.1:7790");
            await Assert.ThrowsAnyAsync<WebSocketException>(
                () => bad.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None));
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private static async Task SendAsync(WebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    // 收一整帧文本消息并解析：ReceiveAsync 在有数据可用时就返回，不保证收完整帧
    // （负载下 Kestrel 会把一帧拆成两次 TCP 写出），JsonDocument.Parse 会抛 JsonException。
    // 8 个调用点全都期望完整帧，收满再接是严格更正确。
    private static async Task<JsonElement> ReceiveAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;   // 这一句是修的全部要害
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone();
    }

    // resync_required 之后该流的订阅会永久死亡：EventBus 已 TryComplete 那条 channel，
    // 而服务端把死订阅留在字典里 ⇒ 直接挡回去 ⇒ 该 topic 从此收不到任何事件，且头不会发现自己停了。
    [Fact]
    public async Task ResubscribingAfterResync_ActuallyRevivesTheStream()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);

        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        var topic = $"session:{sid}";

        // 灌爆缓冲，让「要补发的起点」够不着
        for (var i = 0; i < 1200; i++) _bus.Publish(topic, new WireEvent.Error(sid, $"{i}"));

        // 用太旧的起点订阅 ⇒ 应当立刻 resync_required
        await SendAsync(ws, "{\"kind\":\"subscribe\",\"topic\":\"" + topic + "\",\"since\":1}");
        var first = await ReceiveAsync(ws);
        Assert.Equal("resync_required", first.GetProperty("kind").GetString());

        // 头按设计重建：重新订阅（这次从那之后开始）
        await SendAsync(ws, "{\"kind\":\"subscribe\",\"topic\":\"" + topic + "\",\"since\":"
            + _bus.HighWaterMark(topic) + "}");

        // 再来一条活的事件——它必须到
        _bus.Publish(topic, new WireEvent.Error(sid, "LIVE"));

        var got = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var f = await ReceiveAsync(ws);
            if (f.GetProperty("kind").GetString() == "event") got.Add(f.GetProperty("payload").GetProperty("message").GetString()!);
            if (got.Contains("LIVE")) break;
        }

        Assert.Contains("LIVE", got);
    }

    // session.created 声明并注册了却从没发布：在控制台敲 session create，已经打开的页面看不见这个新会话，且不会自愈。
    [Fact]
    public async Task SessionCreate_PublishesSessionCreatedOnTheProjectTopic()
    {
        var pk = Kernel.ProjectKey.FromPath(_work);
        using var sub = _bus.Subscribe(EventBus.ProjectTopic(pk));

        await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);

        Assert.True(sub.Reader.TryRead(out var p), "项目流上必须能收到 session.created");
        Assert.IsType<WireEvent.SessionCreated>(p.Payload);
    }

    // 畸形帧 {"kind":"subscribe","topic":null}：GetString() 对 JSON null 返回 null 而不抛 ⇒
    // subscribe(null!) ⇒ ArgumentNullException 逃出接收循环 ⇒ 连接 Aborted。修法是不按类型赌，一律兜住。
    [Theory]
    [InlineData("{\"kind\":\"subscribe\",\"topic\":null}")]
    [InlineData("{\"kind\":\"subscribe\"}")]
    [InlineData("{\"kind\":\"subscribe\",\"topic\":123}")]
    [InlineData("{\"kind\":\"command\",\"payload\":{\"kind\":\"session.create\"}}")]
    [InlineData("not json at all")]
    public async Task MalformedFrames_NeverKillTheConnection(string frame)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);

        await SendAsync(ws, frame);

        // 连接必须还活着：再发一条合法命令，它必须能回执。
        await SendAsync(ws, """{"kind":"command","id":"ok","payload":{"kind":"provider.upsert","id":"p","apiKey":"k2"}}""");

        JsonElement ack = default;
        for (var i = 0; i < 4; i++)
        {
            var f = await ReceiveAsync(ws);
            if (f.GetProperty("kind").GetString() == "ack"
                && f.TryGetProperty("id", out var id) && id.GetString() == "ok")
            { ack = f; break; }
        }

        Assert.Equal(WebSocketState.Open, ws.State);
        Assert.True(ack.ValueKind != JsonValueKind.Undefined, "连接被畸形帧踢掉了——它必须留着");
        Assert.True(ack.GetProperty("accepted").GetBoolean());
    }

    // 初始轴值必须来自快照：axis.changed 只在订阅之后才有，头刚打开页面时没有事件可等；
    // 少了它五个下拉全空，用户看到的「空」与「默认值」无法区分。
    [Fact]
    public async Task State_CarriesTheFiveAxes()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        var state = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));
        using var doc = JsonDocument.Parse(state);
        var axes = doc.RootElement.GetProperty("axes");

        foreach (var axis in Axes.Names)
            Assert.True(axes.TryGetProperty(axis, out _), $"快照少了轴 '{axis}'");
        Assert.Equal("dev", axes.GetProperty("safety").GetString());
    }

    // 三个槽位（头两个 + 尾槽位「浮动」）：空的也要显示 ⇒ 恒在且是数组，省略字段会让头分不清「空」与「没有这个概念」。
    [Fact]
    public async Task State_CarriesThreeSlots_FloatingIncluded()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http($"/sessions/{sid}/state")));
        var slots = doc.RootElement.GetProperty("slots");

        Assert.Equal(JsonValueKind.Array, slots.GetProperty("system").ValueKind);
        Assert.Equal(JsonValueKind.Array, slots.GetProperty("premier").ValueKind);
        Assert.Equal(JsonValueKind.Array, slots.GetProperty("floating").ValueKind);
        Assert.Empty(slots.GetProperty("floating").EnumerateArray());
    }

    [Fact]
    public async Task State_CarriesTheWholeSpec()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;      // 分派型命令只在这个回执里给身份

        using var s = JsonDocument.Parse(await Client.GetStringAsync(Http($"/sessions/{sid}/state")));
        var spec = s.RootElement.GetProperty("spec");

        // 整份 SessionSpec 交出去（核心不做子集裁剪）；线上是 camelCase，键名按它写。
        Assert.Equal(sid, spec.GetProperty("sessionId").GetString());
        Assert.False(string.IsNullOrEmpty(spec.GetProperty("projectKey").GetString()));
        Assert.False(string.IsNullOrEmpty(spec.GetProperty("workingDirectory").GetString()));
        Assert.NotEqual(default, spec.GetProperty("createdAt").GetDateTimeOffset());

        var model = spec.GetProperty("model");
        // 值来自测试预置的那个 provider——写死它们同时钉住「spec 里的模型就是该会话实际绑的那份」。
        Assert.Equal("p", model.GetProperty("providerId").GetString());
        Assert.Equal("m", model.GetProperty("modelId").GetString());
        Assert.Equal(Protocols.ClaudeMessages, model.GetProperty("protocol").GetString());
        Assert.False(string.IsNullOrEmpty(model.GetProperty("effort").GetString()));
        Assert.True(model.GetProperty("maxTokens").GetInt32() > 0);

        // key 不在 SessionSpec 里（它住 providers/*.json）⇒「整份交出去」天生不泄 key。
        Assert.False(spec.TryGetProperty("apiKey", out _));
    }

    // SessionSpec 的属性集就是这几样——将来谁往里加一个凭据字段，这条会红。
    // 只靠 Assert.False(spec.TryGetProperty("apiKey")) 不够：WhenWritingNull 会省掉值为 null 的键；
    // 且反射而不是枚举 JSON，两层一起钉，与序列化选项无关。
    // 钉的是「名字 + 类型」：只比名字的话，把 ProviderId: string 改成持有 ApiKey 的类型会名字集一模一样、守卫照样绿。
    [Fact]
    public void SessionSpec_HasExactlyTheseProperties_SoNoCredentialCanHide()
    {
        // 名字 + 类型，并按名字定序（反射的属性顺序不保证 ⇒ 不定序会偶发红）。
        static string[] Shape(Type t) =>
            t.GetProperties()
                .Select(p => $"{p.Name}:{p.PropertyType.Name}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(
            ["CreatedAt:DateTimeOffset", "Model:ModelSelection", "ProjectKey:String",
             "SessionId:String", "WorkingDirectory:String"],
            Shape(typeof(SessionSpec)));

        // 嵌套那层也要钉——key 加在这里会序列化成 spec.model.apiKey，顶层看不见。
        Assert.Equal(
            ["Effort:String", "MaxTokens:Int32", "ModelId:String",
             "Protocol:String", "ProviderId:String"],
            Shape(typeof(ModelSelection)));
    }

    // GET /axes 是头唯一的轴来源（头不许手抄）；同时钉住「中文标签归核心」——下拉的中文名头自己画不出来。
    [Fact]
    public async Task Axes_Endpoint_HandsOutEverythingTheHeadNeeds()
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http("/axes")));
        var axes = doc.RootElement.GetProperty("axes").EnumerateArray().ToList();

        Assert.Equal(Axes.Names.Count, axes.Count);
        // 顺序就是 Axes.Names 的顺序——它决定头的渲染顺序。
        Assert.Equal(Axes.Names, axes.Select(a => a.GetProperty("name").GetString()!).ToList());

        foreach (var a in axes)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("label").GetString()),
                $"轴 {a.GetProperty("name").GetString()} 没有标签——头画不出中文名");
            Assert.NotEmpty(a.GetProperty("values").EnumerateArray());
        }

        // inContext 必须是真值而不是漏字段：effort 是 false，头据此把它画灰；少了这个字段头分不清「不进上下文」与「核心没这个信息」。
        var effort = axes.Single(a => a.GetProperty("name").GetString() == "effort");
        Assert.False(effort.GetProperty("inContext").GetBoolean());

        // 告警的两端也归核心：头里写字面量的话，轴里改一个档名就会让告警静默失效。
        Assert.Equal("prod", doc.RootElement.GetProperty("alertSafety").GetString());
        Assert.Equal("authorized", doc.RootElement.GetProperty("alertFrom").GetString());
    }

    // 它不是「可选的美化」，是「不许手抄」的执行面：头要渲染配置表单就必须知道合法协议 / 合法 auth，真相来源在核心。
    [Fact]
    public async Task ProviderSchema_CarriesTheTruthItOwns()
    {
        using var s = JsonDocument.Parse(await Client.GetStringAsync(Http("/provider-schema")));

        // 与真相来源对拍，不写死字面量；! 是必须的（GetString() 返回 string?，不加会报 CS8620，-warnaserror 下即错误）。
        var protocols = s.RootElement.GetProperty("protocols").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        Assert.Equal(Protocols.All, protocols);

        var auth = s.RootElement.GetProperty("authSchemes").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal([AuthSchemes.ApiKey, AuthSchemes.Bearer], auth);

        // 人话规则要含设备名那一档，头要靠它预先挡掉用户手打的 NUL（不然用户撞的是「核心崩了」）。
        Assert.Contains("设备名", s.RootElement.GetProperty("providerIdRule").GetString());
    }

    // defaultModel 有意不交——它今天没有可信来源（claude-opus-5 只出现在 CommandDispatcher 的兜底那一处）；
    // 交它 = 要么复制一份，要么发明一个来源。这条断言是反向的，因为「下一个人顺手补上它」是最可能发生的事。
    [Fact]
    public async Task ProviderSchema_DeliberatelyOmitsTheDefaultModel()
    {
        using var s = JsonDocument.Parse(await Client.GetStringAsync(Http("/provider-schema")));

        Assert.False(s.RootElement.TryGetProperty("defaultModel", out _));
        Assert.False(s.RootElement.TryGetProperty("model", out _));
    }

    // 端点交的键就这三个，一个不多——任何会被序列化出来的新增键都会让这条红。
    // 限定词「会被序列化出来的」不是修辞：值为 null 的新增字段不会被序列化，这条看不见它。
    // 它与 SessionSpec_HasExactlyTheseProperties 不是同一套理：那里反射 CLR 属性集（null 也看得见），这里数的是序列化出来的键。
    [Fact]
    public async Task ProviderSchema_CarriesExactlyTheseKeys()
    {
        using var s = JsonDocument.Parse(await Client.GetStringAsync(Http("/provider-schema")));

        Assert.Equal(
            ["authSchemes", "protocols", "providerIdRule"],
            s.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // /providers 要把「端点决定数据发去哪」交出来，hasApiKey 让表单显示「已配置」而不是一个空框。
    // 端点与认证方式显式设上——它们是可空的，WhenWritingNull 会把 null 的键整个省掉。
    [Fact]
    public async Task Providers_CarriesEverythingButTheKey()
    {
        // 端点与认证方式显式设上——它们是可空的，而 WhenWritingNull 会把 null 的键整个省掉。
        _host.Providers.Save(new ProviderDefinition
        {
            Id = "full", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
            Endpoint = "http://127.0.0.1:1/v1", AuthScheme = AuthSchemes.Bearer,
        });

        using var a = JsonDocument.Parse(await Client.GetStringAsync(Http("/providers")));
        var p = a.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "full");

        Assert.Equal(Protocols.ClaudeMessages, p.GetProperty("protocol").GetString());
        Assert.Equal("m", p.GetProperty("defaultModel").GetString());
        Assert.Equal("http://127.0.0.1:1/v1", p.GetProperty("endpoint").GetString());
        Assert.Equal(AuthSchemes.Bearer, p.GetProperty("authScheme").GetString());
        Assert.Equal("vision", p.GetProperty("modality").GetString());
        Assert.True(p.GetProperty("hasApiKey").GetBoolean());
    }

    // key 能改不能看。
    [Fact]
    public async Task Providers_NeverLeaksTheKey()
    {
        // 哨兵串：一个不可能偶然出现的值；别用「不含 apiKey 字样」那种弱判据（字段一改名就失效）。
        const string sentinel = "SENTINEL-8f3a2b1c-DO-NOT-LEAK";
        _host.Providers.Save(new ProviderDefinition
        {
            Id = "leaky", Protocol = Protocols.ClaudeMessages, ApiKey = sentinel,
            DefaultModel = "m", Modality = "vision",
        });

        var body = await Client.GetStringAsync(Http("/providers"));

        // 断言字节里不含它——这是「可写不可读」唯一可执行的判据。
        Assert.DoesNotContain(sentinel, body);
        Assert.DoesNotContain(sentinel[..16], body);

        // 反向那半不能省：只断言「不含哨兵」的话，「这个端点什么都没交」也能过。
        using var a = JsonDocument.Parse(body);
        var p = a.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "leaky");
        Assert.True(p.GetProperty("hasApiKey").GetBoolean());   // 「有 key」这件事要看得见
        Assert.False(p.TryGetProperty("apiKey", out _));        // 而「是什么」不行
    }

    // 没有 claims.json 时返回 []，不是 404 也不是 500——「这个项目没人声明任何东西」是合法且最常见的形态。
    [Fact]
    public async Task Claims_WithNoClaimsFile_IsAnEmptyArray()
    {
        var r = await Client.GetAsync(Http("/claims"));

        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("[]", (await r.Content.ReadAsStringAsync()).Trim());
    }

    // 走真 WS，不是直接调 dispatcher——后者在接线缺失时会照样绿，它绕过的正是本条要钉的那一层。
    [Fact]
    public async Task WebSocket_ClaimRelease_ReachesTheDispatcher()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // 先占上。claim.acquire 不在线上命令表里，故直接走 dispatcher——认领是控制台/模型的动作，头从不发它。
        await _dispatcher.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c2", false, CancellationToken.None);
        Assert.NotEmpty(new ClaimStore(_work).All());

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        // 用拼接而不是 $$"""：载荷末尾的 }}} 会被当成内插洞的收尾（CS9007）。
        await SendAsync(ws, "{\"kind\":\"command\",\"id\":\"a1\",\"payload\":{\"kind\":\"claim.release\","
            + "\"sessionId\":\"" + sid + "\",\"scope\":[\"src\"]}}");

        var ack = await ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("kind").GetString());
        // 理由必须惰性取：WhenWritingNull 把成功回执的 reason 省略，直接 GetProperty("reason") 会抛 KeyNotFoundException；用 TryGetProperty。
        Assert.True(ack.GetProperty("accepted").GetBoolean(),
            "认不出这条命令——`ParseCommand` 的 switch 漏了它："
            + (ack.TryGetProperty("reason", out var why) ? why.GetString() : "(无 reason 字段)"));

        // 判据是「命令真的生效了」，不只是「回执说 accepted」。
        Assert.Empty(new ClaimStore(_work).All());
    }
}

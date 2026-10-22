using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Protocol;
using Lilara.Core.Kernel;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Storage;
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

    [Fact]
    public async Task Directories_StartAtCoreDeviceRootAndBrowseAnyExistingDirectory()
    {
        var outside = Path.Combine(Path.GetTempPath(), "lilara-cs-directory-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(outside, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            var rootResponse = await Client.GetAsync(Http("/directories"));
            Assert.Equal(System.Net.HttpStatusCode.OK, rootResponse.StatusCode);
            using var root = JsonDocument.Parse(await rootResponse.Content.ReadAsStringAsync());
            Assert.True(root.RootElement.GetProperty("directories").GetArrayLength() > 0);
            if (OperatingSystem.IsWindows())
                Assert.False(root.RootElement.TryGetProperty("current", out _));
            else
                Assert.Equal(Path.GetPathRoot(_work), root.RootElement.GetProperty("current").GetString());
            Assert.Empty(_host.Activated);

            var response = await Client.GetAsync(
                Http("/directories?path=" + Uri.EscapeDataString(outside)));
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            using var directory = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(Path.GetFullPath(outside), directory.RootElement.GetProperty("current").GetString());
            Assert.Equal("nested", directory.RootElement.GetProperty("directories")[0].GetProperty("name").GetString());
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task Directories_RejectMissingPaths()
    {
        var missing = Path.Combine(Path.GetTempPath(), "lilara-missing-" + Guid.NewGuid().ToString("N"));
        var response = await Client.GetAsync(Http("/directories?path=" + Uri.EscapeDataString(missing)));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }


    // 头端点只列直接子目录；它不应改变项目/会话状态。
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

    /// <summary>
    /// ⚠️ **退订之后，那条流真的不再发东西了**（2026-10-06 加）。
    ///
    /// <para>在加退订之前，核心**没有**这条路：`subs` 是**按 topic** 建的，头切到别的会话时
    /// 旧会话那条订阅留着、泵一直发 ⇒ 白占一条泵与整条连接，切得越多积得越多
    /// （而头会丢弃那些事件，所以**看不出来**）。</para>
    ///
    /// <para>⚠️ **判据是「不再收到」，不是「收到一个 ack」**——退订本身不回执
    /// （它不是命令），所以只能靠「之后的发送的结果不再出现在这条连接上」来证。</para>
    /// </summary>
    [Fact]
    public async Task WebSocket_Unsubscribe_StopsTheStream()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);

        await SendAsync(ws, """{"kind":"command","id":"c1","payload":{"kind":"session.create"}}""");
        var sid = (await ReceiveAsync(ws)).GetProperty("sessionId").GetString()!;

        await SendAsync(ws, "{\"kind\":\"subscribe\",\"topic\":\"session:" + sid + "\"}");

        // 先证「订上了」——不然下面那条「没收到」可能只是因为压根没订上（假绿）。
        await SendAsync(ws,
            "{\"kind\":\"command\",\"id\":\"c2\",\"payload\":{\"kind\":\"session.send\","
            + "\"sessionId\":\"" + sid + "\",\"blocks\":[{\"kind\":\"text\",\"text\":\"你好\"}]}}");
        var sawEvent = false;
        for (var i = 0; i < 12 && !sawEvent; i++)
            sawEvent = await TryReceiveAsync(ws, 5000) is { } f
                       && f.GetProperty("kind").GetString() == "event"
                       && f.GetProperty("topic").GetString() == "session:" + sid;
        Assert.True(sawEvent, "订上了就该收到事件——收不到说明这条测试的前提不成立");

        // 退订，然后再发一句。
        await SendAsync(ws, "{\"kind\":\"unsubscribe\",\"topic\":\"session:" + sid + "\"}");
        await SendAsync(ws,
            "{\"kind\":\"command\",\"id\":\"c3\",\"payload\":{\"kind\":\"session.send\","
            + "\"sessionId\":\"" + sid + "\",\"blocks\":[{\"kind\":\"text\",\"text\":\"再来\"}]}}");

        // 收到 c3 的回执（**回执与订阅无关，一定会来**）⇒ 说明 c3 真的被处理了；
        // 它之后**不该再有**这条 topic 的事件（退订前积压的帧有界，这里给足余量）。
        var ackC3 = false;
        for (var i = 0; i < 12 && !ackC3; i++)
            ackC3 = await TryReceiveAsync(ws, 5000) is { } f
                    && f.GetProperty("kind").GetString() == "ack"
                    && f.GetProperty("id").GetString() == "c3";
        Assert.True(ackC3, "c3 的回执必须到");

        for (var i = 0; i < 5; i++)
        {
            var after = await TryReceiveAsync(ws, 500);
            if (after is null) break;   // 静默 = 这就是要的结论
            Assert.False(
                after.Value.GetProperty("kind").GetString() == "event"
                && after.Value.GetProperty("topic").GetString() == "session:" + sid,
                "退订之后不该再收到那条流的事件");
        }
    }

    // 退订一个**没订过的** topic 是合法 no-op：抛的话会回一句「帧形状不对」，那是在说谎。
    [Fact]
    public async Task WebSocket_UnsubscribeUnknownTopic_IsANoOp()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);

        await SendAsync(ws, """{"kind":"unsubscribe","topic":"session:nope"}""");

        // 连接还活着、还能正常收帧（发一条命令拿回执）。
        await SendAsync(ws, """{"kind":"command","id":"c1","payload":{"kind":"session.create"}}""");
        var ack = await ReceiveAsync(ws);

        Assert.Equal("ack", ack.GetProperty("kind").GetString());
        Assert.True(ack.GetProperty("accepted").GetBoolean());
    }

    // 命令不合法 ⇒ accepted: false + 理由；业务失败走 error 事件——两者混了头会把网络抖动当成「你命令写错了」。
    [Fact]    public async Task WebSocket_RejectsAMalformedCommandWithoutClosingTheSocket()
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
    [InlineData("http://127.0.0.1:17990")]  // 头自己的 origin ⇒ 放行
    [InlineData("http://evil.example")]    // ⇒ 拒绝
    [InlineData("http://localhost:17990")]  // ⚠️ 同机不同写法：**拒绝**——见下
    [InlineData("null")]                   // `file://` 页面与沙箱 iframe 的 Origin 字面量是 "null"
    public async Task WebSocket_OriginPolicy(string? origin)
    {
        using var socket = new ClientWebSocket();
        // 只能手设头（设备端没有带 Origin 的握手 API）；不设则该头根本不存在（读出来是空串，不是 null）。
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);

        var connect = socket.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        if (origin is null or "http://127.0.0.1:17990")
        {
            await connect;                                    // 放行 ⇒ 握上手
            Assert.Equal(WebSocketState.Open, socket.State);
        }
        else
        {
            // 握手被拒时 ConnectAsync 抛 WebSocketException（服务端回了 403）。
            // localhost 在拒绝之列是刻意的：它与 127.0.0.1 不是同一个 origin，
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
            bad.Options.SetRequestHeader("Origin", "http://127.0.0.1:17990");
            await Assert.ThrowsAnyAsync<WebSocketException>(
                () => bad.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None));
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// 收一帧，**超时就返回 <c>null</c> 而不是抛**。
    /// ⚠️ 存在的理由：有几条测试要证的正是「**它不再发了**」，而静默的表现就是超时——
    /// 用会抛的 <see cref="ReceiveAsync"/> 只能把它写成「等 10 秒然后失败」（实测踩过：
    /// 那条退订测试因此整条超时，而它其实**已经**证明了要证的东西）。
    /// </summary>
    private static async Task<JsonElement?> TryReceiveAsync(WebSocket ws, int milliseconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(milliseconds));
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType != WebSocketMessageType.Text) return null;
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
        }
        catch (OperationCanceledException)
        {
            return null;   // 这就是「静默」
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone();
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

    // ---- 销毁面的线上解析（第三条线）----

    // ⚠️ 这一段单列的理由：`ParseCommand` 是命令的**第三条线**，缺了它头发的帧会落到
    // `_ => throw`（回一句「认不出的命令」）。dispatcher 与解析器的测试**都测不到这里**——
    // 那正是「四条线」这条规则存在的原因。
    [Fact]
    public async Task Wire_Deactivate_ReachesTheDispatcher()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, Command("d", "session.deactivate", "\"sessionId\":\"" + sid + "\""));
        var ack = await AckFor(ws, "d");

        Assert.True(ack.GetProperty("accepted").GetBoolean(), "线上必须认得出 session.deactivate");
        Assert.False(_host.TryGetActivated(sid, out _));
    }

    // `archived` 键**缺失** ⇒ 归档（三态的中性档）。写成 GetProperty(...).GetBoolean()
    // 会让这条帧直接抛 KeyNotFoundException ⇒ 头永远归档不了。
    [Fact]
    public async Task Wire_Archive_WithoutTheArchivedKey_MeansArchive()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        var pk = Kernel.ProjectKey.FromPath(_work);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, Command("a", "session.archive", "\"sessionId\":\"" + sid + "\""));
        var ack = await AckFor(ws, "a");

        Assert.True(ack.GetProperty("accepted").GetBoolean());
        Assert.NotNull(_host.Store.ReadSpec(pk, sid).ArchivedAt);
    }

    [Fact]
    public async Task Wire_Archive_WithArchivedFalse_MeansUnarchive()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        var pk = Kernel.ProjectKey.FromPath(_work);
        await _dispatcher.DispatchAsync(new CoreCommand.SessionArchive(sid), "seed", false, CancellationToken.None);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, Command("u", "session.archive", "\"sessionId\":\"" + sid + "\"", "\"archived\":false"));
        var ack = await AckFor(ws, "u");

        Assert.True(ack.GetProperty("accepted").GetBoolean());
        Assert.Null(_host.Store.ReadSpec(pk, sid).ArchivedAt);
    }

    // ⚠️ `"false"`（**字符串**）不能被当成 false：只认 JSON 的布尔字面量。
    // 认了的话前端传个字符串就能悄悄改语义——而这条命令的 false 是「取消归档」，
    // 与「归档」是两个相反的动作。
    [Fact]
    public async Task Wire_Archive_WithAStringFalse_IsTreatedAsMissing_SoItArchives()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        var pk = Kernel.ProjectKey.FromPath(_work);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, Command("s", "session.archive", "\"sessionId\":\"" + sid + "\"", "\"archived\":\"false\""));
        var ack = await AckFor(ws, "s");

        Assert.True(ack.GetProperty("accepted").GetBoolean());
        Assert.NotNull(_host.Store.ReadSpec(pk, sid).ArchivedAt);   // 归档了，不是取消
    }

    [Fact]
    public async Task Wire_Delete_ReachesTheDispatcher()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        var pk = Kernel.ProjectKey.FromPath(_work);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, Command("x", "session.delete", "\"sessionId\":\"" + sid + "\""));
        var ack = await AckFor(ws, "x");

        Assert.True(ack.GetProperty("accepted").GetBoolean());
        Assert.False(_host.Store.SessionExists(pk, sid));
    }

    // `session.create` 的两个可空字段：**键缺失 = 回落**，用 GetProperty 会抛。
    [Fact]
    public async Task Wire_Create_WithCwdAndTitle_ReachesTheDispatcher()
    {
        var other = Path.Combine(Path.GetTempPath(), "lilara-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        var pk = Kernel.ProjectKey.FromPath(other);

        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
            // JSON 里的路径要转义反斜杠——手拼的 `"E:\Temp"` 是**非法 JSON**（\T 不是合法转义）。
            var cwd = other.Replace("\\", "\\\\");
            await SendAsync(ws, Command("n", "session.create", "\"cwd\":\"" + cwd + "\"", "\"title\":\"wire-made\""));
            var ack = await AckFor(ws, "n");

            Assert.True(ack.GetProperty("accepted").GetBoolean());
            var sid = ack.GetProperty("sessionId").GetString()!;
            var spec = _host.Store.ReadSpec(pk, sid);
            Assert.Equal("wire-made", spec.Title);
            Assert.Equal(pk, spec.ProjectKey);
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    // 光一个 `{"kind":"session.create"}`（一个字段都不给）必须仍然成立——
    // 这是头今天发的形状，也是「不给就回落」那条契约的最小形态。
    [Fact]
    public async Task Wire_Create_WithNoFields_StillWorks()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, """{"kind":"command","id":"bare","payload":{"kind":"session.create"}}""");
        var ack = await AckFor(ws, "bare");

        Assert.True(ack.GetProperty("accepted").GetBoolean());
    }

    // ⚠️ `/claims` 必须交出 `stale`：`session.delete` 是两步（先摘声明、再删目录），
    // 崩在中间会留下「主人已经不在了」的声明。核心**不自动清理**它（那是 GC 的事），
    // 但**必须看得见**——否则它会静静骗人：「这个文件有人占着」，而那个「人」没了。
    [Fact]
    public async Task Claims_FlagsOnesWhoseSessionIsGone()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        var pk = Kernel.ProjectKey.FromPath(_work);

        // 一条主人的会话还在，一条的主人是个不存在的 id。
        new ClaimStore(_work).Save([
            new Claim(sid, pk, Path.Combine(_work, "a.cs"), DateTimeOffset.UnixEpoch),
            new Claim("ghost", pk, Path.Combine(_work, "b.cs"), DateTimeOffset.UnixEpoch),
        ]);

        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http("/claims")));
        var rows = doc.RootElement.EnumerateArray().ToList();

        Assert.False(rows.Single(r => r.GetProperty("path").GetString()!.EndsWith("a.cs"))
            .GetProperty("stale").GetBoolean());
        Assert.True(rows.Single(r => r.GetProperty("path").GetString()!.EndsWith("b.cs"))
            .GetProperty("stale").GetBoolean());
    }

    // ⚠️ 判据走**目录存在**而不是「spec 读得出来」：损坏的 `spec.json` 让后者返回 null
    // ⇒ 一个真实存在、只是配置坏了的会话，它**活着的**声明会被误报成 stale。
    //
    // ⚠️ 这条测试必须**两个会话共用同一个工作目录**才成立，而这不是凑数：
    // `/claims` 是**按工作目录**去读 `claims.json` 的，而工作目录只有 `spec.json` 里有。
    // spec 一坏，那个会话就进不了 `ListSessions` ⇒ **它的工作目录根本不会被访问到**，
    // 它自己的声明连出现都不会出现（那是 `/claims` 枚举方式的既有性质，不是本批引入的）。
    // ⇒ 损坏的会话只有在**别人替它把工作目录带进来**时才会出现在列表里，而那时
    //   `stale` 的判据才有机会判错。**没有这一条，两种判据的行为完全一样、测不出差别。**
    [Fact]
    public async Task Claims_DoesNotFlagASessionWhoseSpecIsMerelyCorrupt()
    {
        var a = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c1", false, CancellationToken.None);
        var b = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c2", false, CancellationToken.None);
        var sidA = ((Ack.Accepted)a.Ack).SessionId!;
        var sidB = ((Ack.Accepted)b.Ack).SessionId!;
        var pk = Kernel.ProjectKey.FromPath(_work);

        new ClaimStore(_work).Save([new Claim(sidA, pk, Path.Combine(_work, "a.cs"), DateTimeOffset.UnixEpoch)]);

        // 把 A 的 spec 弄坏：A 的**目录还在**（会话没消失），只是读不出来。
        File.WriteAllText(Path.Combine(_host.Store.SessionDirectory(pk, sidA), "spec.json"), "{ broken");

        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http("/claims")));
        var row = doc.RootElement.EnumerateArray().Single();   // 靠 B 把工作目录带进来

        Assert.Equal(sidA, row.GetProperty("sessionId").GetString());
        Assert.False(row.GetProperty("stale").GetBoolean(),
            "损坏 ≠ 消失；误报 stale 会让用户去删一条活着的声明");
    }

    // 收帧直到找到指定 id 的回执（中间会有事件帧）。
    private static async Task<JsonElement> AckFor(ClientWebSocket ws, string id)
    {
        for (var i = 0; i < 10; i++)
        {
            var f = await ReceiveAsync(ws);
            if (f.GetProperty("kind").GetString() == "ack"
                && f.TryGetProperty("id", out var got) && got.GetString() == id)
                return f;
        }
        throw new Xunit.Sdk.XunitException($"没有收到 id='{id}' 的回执");
    }

    // 一条命令帧。⚠️ **用拼接而不是 `$$"""`**：内插原始字符串里，载荷末尾的 `}}` 会被
    // 当成内插洞的收尾（CS9007）——本文件那几处老测试也踩过同一个坑。
    // `fields` 是**已经转义好的** JSON 片段（形如 `"sessionId":"abc"`）。
    private static string Command(string id, string kind, params string[] fields) =>
        "{\"kind\":\"command\",\"id\":\"" + id + "\",\"payload\":{\"kind\":\"" + kind + "\""
        + (fields.Length == 0 ? "" : "," + string.Join(",", fields))
        + "}}";

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

    // 三个槽位（系统 / 首要 / 浮动）：空的也要显示 ⇒ 恒在且是数组，省略字段会让头分不清「空」与「没有这个概念」。
    [Fact]
    public async Task State_CarriesThreeSlots_FloatingIncluded()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http($"/sessions/{sid}/state")));
        var slots = doc.RootElement.GetProperty("slots");

        // 三格**恒在**、恒是数组——这一条与内容无关，它是「头分不清空与没有」那个歧义的守卫。
        Assert.Equal(JsonValueKind.Array, slots.GetProperty("system").ValueKind);
        Assert.Equal(JsonValueKind.Array, slots.GetProperty("premier").ValueKind);
        Assert.Equal(JsonValueKind.Array, slots.GetProperty("floating").ValueKind);

        // ⚠️ **2026-10-11 反过来了**：这三格原先**恒空**（装配器只做了系统块那一半），
        // 所以这里曾断言 `Assert.Empty(floating)`——那句钉的是「还没实现」，
        // 而不是任何设计性质。本批之后三格都是**真的**：
        //   系统：没配过预设 ⇒ 内置兜底那句（**非空**）
        //   首要：没配过 `<数据根>/premiers/default.md` ⇒ **空**（核心不替用户编人设）
        //   浮动：核心每步现算的轴表 ⇒ **非空**
        // ⇒ 现在钉的是**各自的真相**，而不是「都空」。
        Assert.NotEmpty(slots.GetProperty("system").EnumerateArray());
        Assert.Empty(slots.GetProperty("premier").EnumerateArray());
        Assert.NotEmpty(slots.GetProperty("floating").EnumerateArray());

        // ⚠️ 尾块的内容**从轴来**——所以它里面必须看得见轴值与那个自我声明。
        // 只断言「非空」的话，一个恒定输出「x」的实现照样绿。
        var floating = slots.GetProperty("floating").EnumerateArray().Single().GetProperty("text").GetString()!;
        Assert.Contains(CorePrompts.TailDeclaration, floating);
        Assert.Contains("权限", floating);
        Assert.Contains(Axes.Defaults()[Axes.Permission], floating);
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
        // ⚠️ 但这条**只是「没有 apiKey 这个键名」**——字段一改名它就失效。
        // 真正钉住「不泄 key」的是下面那条哨兵式的 `State_NeverLeaksTheKey`（同 `/providers` 的做法）。
        Assert.False(spec.TryGetProperty("apiKey", out _));
    }

    // 「整份 spec 交出去也不泄 key」这条，用**哨兵串**钉——不是「没有 apiKey 这个键名」
    //（那种判据一改名就失效，`/providers` 那边早就换掉了）。
    // ⚠️ 哨兵必须**真的设进 provider**，否则 spec 根本不提它、这条就是空真。
    [Fact]
    public async Task State_NeverLeaksTheKey()
    {
        const string sentinel = "SENTINEL-8f3a2b1c-DO-NOT-LEAK";
        _host.Providers.Save(new ProviderDefinition
        {
            Id = "leaky", Protocol = Protocols.ClaudeMessages, ApiKey = sentinel,
            DefaultModel = "m", Modality = "vision",
        });

        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // 把会话改绑到那个带哨兵的提供商——否则 spec 里根本不提它。
        var cfg = await _dispatcher.DispatchAsync(
            new CoreCommand.SessionConfigure(sid, "leaky", null), "cfg", false, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(cfg.Ack);

        var body = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));

        Assert.DoesNotContain(sentinel, body);

        // 反向那半不能省：只断言「不含哨兵」的话，「这个端点其实什么都没交」也能过。
        // 提供商 **id** 是看得见的（它要被显示），**key** 看不见——这两件事要分开钉。
        Assert.Contains("leaky", body);
    }

    // ════════════════ 守卫 B：11 个快照端点的键集 ════════════════
    //
    // 为什么要有这一组：`ControlServer` 的 11 个 `MapGet` 交的都是**内联匿名类型**，
    // 往 `new { … }` 里加一个字段**不会被任何测试发现** ⇒ `03 §6` 那份 schema 必腐。
    // 这组把「交出去的键恰好是这些」钉成可执行的，于是文档腐了会红。
    //
    // ⚠️ 与 `SessionSpec_HasExactlyTheseProperties` 不是同一套理：那里反射 **CLR 属性集**
    //（null 也看得见），这里数的是**序列化出来的键**——`WhenWritingNull` 会省掉 null ⇒
    // **可空字段必须先设上**，否则这条测试测的是「这个数据碰巧长什么样」。

    // 一个「每个可空字段都设上」的 provider ⇒ 键集最大、最稳定。
    private void SeedFullProvider() => _host.Providers.Save(new ProviderDefinition
    {
        Id = "full", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
        DefaultModel = "m", Modality = "vision",
        Endpoint = "http://127.0.0.1:1/v1", AuthScheme = AuthSchemes.Bearer,
        Models = ["m1", "m2"],
    });

    private static string[] KeysOf(JsonElement obj) =>
        obj.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static void AssertKeysOf(JsonElement obj, params string[] expected)
    {
        Assert.Equal(JsonValueKind.Object, obj.ValueKind);
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), KeysOf(obj));
    }

    // 数组：钉**每一项**的键集。
    // ⚠️ `NotEmpty` 不能省：空数组上「每项都满足」是**空真**，而空恰恰是最常见的状态
    //（`/claims` 没声明文件时就是 []）⇒ 少了这一句这条测试零区分力。
    private static void AssertEachItemKeysOf(JsonElement arr, params string[] expected)
    {
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        var items = arr.EnumerateArray().ToList();
        Assert.NotEmpty(items);
        foreach (var item in items) AssertKeysOf(item, expected);
    }

    // 字符串数组（`/projects` 这类）：正确契约是「每项都是字符串」，不是「每项都有某些键」。
    private static void AssertEachItemIsString(JsonElement arr)
    {
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.NotEmpty(arr.EnumerateArray());
        Assert.All(arr.EnumerateArray(), e => Assert.Equal(JsonValueKind.String, e.ValueKind));
    }

    private async Task<JsonElement> GetJson(string path) =>
        (await JsonDocument.ParseAsync(await Client.GetStreamAsync(Http(path)))).RootElement.Clone();

    // ---- 统计端点的口径（2026-10-20）----
    //
    // 这一组钉两件事：① 命中率与分家后的输入**上得了线**；② 账本里【格式 8 之前】的 OpenAI 行
    // 必须按旧口径解释（inputTokens 含缓存 ⇒ 读的时候减掉），否则旧账本的「输入 + 缓存读取」会把同一批 token 数两遍。

    private void Seed(string line) => File.AppendAllText(
        Path.Combine(_root, "usage.jsonl"), line + "\n", Encoding.UTF8);

    // 结构化行用匿名对象拼：`$$"""` 里连续的 `}}` 会撞上内插原始字面量的收尾歧义（CS9007）。
    private void SeedRow(object row) => Seed(JsonSerializer.Serialize(row, Lilara.Core.Kernel.Json.Options));

    [Fact]
    public async Task UsageSummary_ExposesCacheHitRate_AndLegacyRowsAreNormalized()
    {
        // 没有 formatVersion 的键 ⇒ 这是格式 8 之前的行；上游原值 3867 **含**缓存 3456。
        Seed("""{"callId":"old","providerId":"apiplan","modelId":"deepseek-flash","protocol":"openai-chat","startedAt":"2026-10-18T00:00:00+00:00","status":"completed","usage":{"inputTokens":3867,"outputTokens":78,"totalTokens":3945,"cacheReadTokens":3456}}""");
        // 新行（有版本）已经归一过：411 是未命中输入，**不能再减**。
        SeedRow(new
        {
            CallId = "new", FormatVersion = Json.FormatVersion, ProviderId = "apiplan",
            ModelId = "deepseek-flash", Protocol = Protocols.OpenAiChat,
            StartedAt = DateTimeOffset.Parse("2026-10-18T01:00:00Z"), Status = "completed",
            Usage = new TokenUsage { InputTokens = 411, OutputTokens = 78, TotalTokens = 3945, CacheReadTokens = 3456 },
        });

        var summary = await GetJson("/usage/summary");

        Assert.Equal(822, summary.GetProperty("inputTokens").GetInt64());       // 411 + 411
        Assert.Equal(6912, summary.GetProperty("cacheHitTokens").GetInt64());   // 3456 + 3456
        Assert.Equal(7734, summary.GetProperty("promptTokens").GetInt64());
        Assert.Equal(2, summary.GetProperty("callsWithKnownCache").GetInt32());
        Assert.Equal(6912d / 7734d, summary.GetProperty("cacheHitRate").GetDouble(), 12);
    }

    [Fact]
    public async Task UsageSummary_RateIsNull_NotZero_WhenNoCallReportedCache()
    {
        SeedRow(new
        {
            CallId = "a", ProviderId = "p", ModelId = "m", Protocol = Protocols.OpenAiChat,
            StartedAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z"), Status = "completed",
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 5 },
        });

        var summary = await GetJson("/usage/summary");

        // 键被省略（WhenWritingNull）——与 tokenText 的「未报告」同一个判据：未报告不是 0。
        Assert.False(summary.TryGetProperty("cacheHitRate", out _));
        Assert.False(summary.TryGetProperty("promptTokens", out _));
        Assert.Equal(0, summary.GetProperty("callsWithKnownCache").GetInt32());
    }

    // ⚠️ 旧实现挑的是「最后一条带 usage 的调用」：一条只有输出、没报输入的失败行会把
    // 「上下文长度」变成「未报告」——比一条旧值更糟，它看起来像上下文没了。
    [Fact]
    public async Task Context_SkipsCallsThatDidNotReportInput()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        SeedRow(new
        {
            CallId = "good", FormatVersion = Json.FormatVersion, SessionId = sid,
            ProviderId = "p", ModelId = "m", Protocol = Protocols.OpenAiChat,
            StartedAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z"), Status = "completed",
            Usage = new TokenUsage { InputTokens = 1200, OutputTokens = 5 },
        });
        SeedRow(new
        {
            CallId = "noinput", FormatVersion = Json.FormatVersion, SessionId = sid,
            ProviderId = "p", ModelId = "m", Protocol = Protocols.OpenAiChat,
            StartedAt = DateTimeOffset.Parse("2026-10-18T01:00:00Z"), Status = "failed",
            Usage = new TokenUsage { OutputTokens = 5 },
        });

        var context = await GetJson($"/sessions/{sid}/context");

        var latest = context.GetProperty("latest");
        Assert.Equal("good", latest.GetProperty("callId").GetString());
        Assert.Equal(1200, latest.GetProperty("inputTokens").GetInt64());
        Assert.Equal("reported", latest.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Snapshot_Health_CarriesExactlyTheseKeys() =>
        AssertKeysOf(await GetJson("/health"), "ok", "dataRoot", "workingDirectory", "sessions");

    [Fact]
    public async Task Snapshot_Info_CarriesExactlyTheseKeys()
    {
        var root = await GetJson("/info");
        AssertKeysOf(root, "dataRoot", "workingDirectory", "coreVersion", "allowedOrigin", "endpoints");

        // 嵌套那层也要钉：`endpoints` 里加个字段，顶层键集**看不见**。
        AssertKeysOf(root.GetProperty("endpoints"), "http", "ws");

        // ⚠️ `/info` 交的 origin 必须来自 AllowedOriginResolver，供 head 解释 WS 拒绝原因。
        Assert.Equal(AllowedOriginResolver.Resolve().Origin, root.GetProperty("allowedOrigin").GetString());
    }

    // 数组型端点的「每项是字符串」——不伪造键集断言。
    [Fact]
    public async Task Snapshot_Projects_IsAnArrayOfStrings()
    {
        // ⚠️ **得先有项目**，否则 `/projects` 是空数组、`NotEmpty` 会红。
        //（这正是那条守卫的用途：空数组上「每项都是字符串」是空真。）
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(created.Ack);

        AssertEachItemIsString(await GetJson("/projects"));
    }

    [Fact]
    public async Task Snapshot_ProjectSessions_IsAnArrayOfStrings()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(created.Ack);

        AssertEachItemIsString(await GetJson($"/projects/{ProjectKey.FromPath(_work)}/sessions"));
    }

    /// <summary>
    /// ⚠️ **`/sessions` 的键集守卫**（`01` §3.8）——与「`/projects/{pk}/sessions` 是字符串数组」
    /// 那条**刻意并存**：两个端点的形状**不一样**，而它们只差一个路径段。
    /// 不钉这条的话，「顺手把它也改成数组」是一次静默的线上 schema 变更。
    /// </summary>
    [Fact]
    public async Task Snapshot_Sessions_CarriesExactlyTheseKeys()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        var rows = await GetJson("/sessions");
        var row = rows.EnumerateArray().Single(e => e.GetProperty("sessionId").GetString() == sid);

        // ⚠️ **实测是六格，不是七格**：`title` 可空，而 `WhenWritingNull` 会把
        // 它**整个省掉** ⇒ 没起名的会话那一行**根本没有** `title` 这个键。
        // 这条不是我推出来的，是这条守卫头一次跑就抓出来的（我原先按七个字段写）。
        // ⇒ 头的判据必须是 `r.title` 而不是「`title` 一定在」。
        Assert.Equal(
            ["active", "ask", "goal", "projectKey", "sessionId", "status", "turn"],
            row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Snapshot_State_CarriesExactlyTheseKeys()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        var root = await GetJson($"/sessions/{sid}/state");
        AssertKeysOf(root, "sessionId", "state", "highWater", "axes", "spec", "slots", "goal", "axisDefinitions", "pluginProblems");

        // goal 是「永远在的对象」（照 `ask` 的 `{}` 纪律），内部只有 active 一格。
        AssertKeysOf(root.GetProperty("goal"), "active");

        // ⚠️ 嵌套那层也钉：往 `slots` / `model` 里加字段，顶层看不见。
        //（`spec` 自己那层**不在这里钉**——它归 `SessionSpec_HasExactlyTheseProperties` +
        //  `State_CarriesTheWholeSpec` 管；在这里再来一份是重复的守位，只会多一处要改。）
        AssertKeysOf(root.GetProperty("slots"), "system", "premier", "floating");
        AssertKeysOf(root.GetProperty("spec").GetProperty("model"),
            "providerId", "modelId", "protocol", "effort", "maxTokens");
    }

    [Fact]
    public async Task Snapshot_History_CarriesExactlyTheseKeys()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        // 让步真的走完 ⇒ rendered 非空（空数组上钉「每项」是空真）。
        await _dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sid, [new Block.TextBlock("hi")]), "s", awaitCompletion: true, CancellationToken.None);

        var root = await GetJson($"/sessions/{sid}/history");
        AssertKeysOf(root, "highWater", "rendered", "hasMore", "nextCursor");

        // `rendered` 的每一项来自 `ControlServer.Render`（核心渲染、头只显示）——
        // 消息和工具结果都带稳定的记录 id；工具结果另有 callId 用于协议配对。
        AssertEachItemKeysOf(root.GetProperty("rendered"), "kind", "id", "from", "at", "blocks");
    }

    [Fact]
    public async Task Snapshot_History_PaginatesNewestThenOlder()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        for (var i = 0; i < 3; i++)
            _host.Store.AppendEvent(Kernel.ProjectKey.FromPath(_work), sid, new TimelineEvent.MessageEvent
            {
                Id = $"h{i}", At = DateTimeOffset.UnixEpoch.AddSeconds(i), From = Speaker.User,
                Blocks = [new Block.TextBlock($"m{i}")],
            });

        var first = await GetJson($"/sessions/{sid}/history?limit=2");
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(new[] { "m1", "m2" }, first.GetProperty("rendered").EnumerateArray()
            .Select(x => x.GetProperty("blocks")[0].GetProperty("text").GetString()).ToArray());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var older = await GetJson($"/sessions/{sid}/history?limit=2&before={Uri.EscapeDataString(cursor!)}");
        Assert.False(older.GetProperty("hasMore").GetBoolean());
        Assert.Equal("m0", older.GetProperty("rendered")[0].GetProperty("blocks")[0].GetProperty("text").GetString());

        var invalid = await Client.GetAsync(Http($"/sessions/{sid}/history?limit=2&before=bad"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
    }


    ///
    /// <para>原先 `ControlServer.Render` 把每个事件**强转成 `MessageEvent`** ⇒ 遇到
    /// `ToolResultEvent` 直接 `InvalidCastException` ⇒ **端点 HTTP 500**
    /// ⇒ 头那边表现为「加载不出会话内容」（实测：盘上 17 行、端点 500）。</para>
    /// </summary>
    [Fact]
    public async Task Snapshot_History_WithToolResults_StillReturns200()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // 直接落一条工具结果进记录（本测试在 HTTP 层，不必真跑一次工具）。
        // ⚠️ 走**生产路径**的 append（`SessionStore.AppendEvents`），不是手写文件——
        // 手写会绕过格式校验，那样这条测试就测不到「记录里真有这种事件」。
        _host.Store.AppendEvents(
            Kernel.ProjectKey.FromPath(_work), sid,
            [
                new TimelineEvent.ToolResultEvent
                {
                    Id = "r1", At = DateTimeOffset.UnixEpoch, CallId = "c1",
                    Content = [new Block.TextBlock("结果")],
                },
            ]);

        var root = await GetJson($"/sessions/{sid}/history");

        // 工具结果那一项：有 kind / callId，**没有 from**。
        var tool = root.GetProperty("rendered").EnumerateArray()
            .Single(i => i.GetProperty("kind").GetString() == "toolResult");
        Assert.Equal("c1", tool.GetProperty("callId").GetString());
        Assert.False(tool.TryGetProperty("from", out _));
    }

    [Fact]
    public async Task Snapshot_History_KeepsEachToolResultBesideItsModelMessage()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // ⚠️ 造**记录里真实的次序**：模型消息（调用）→ 它的结果 → 紧接着下一条用户消息。
        // 2026-10-17：结果必须**紧跟**在它的模型消息之后，否则头只能画成「调用都在上、
        // 结果都在下」——而那是用户点名的那个形状。
        _host.Store.AppendEvents(
            Kernel.ProjectKey.FromPath(_work), sid,
            [
                new TimelineEvent.MessageEvent
                {
                    Id = "m1", At = DateTimeOffset.UnixEpoch, From = Speaker.Model,
                    Blocks =
                    [
                        new Block.TextBlock("我查一下"),
                        new Block.ToolUseBlock("c1", "demo", "{}"),
                        new Block.ToolUseBlock("c2", "demo", "{}"),
                    ],
                },
                new TimelineEvent.ToolResultEvent
                {
                    Id = "r1", At = DateTimeOffset.UnixEpoch, CallId = "c1",
                    Content = [new Block.TextBlock("结果一")],
                },
                new TimelineEvent.ToolResultEvent
                {
                    Id = "r2", At = DateTimeOffset.UnixEpoch, CallId = "c2",
                    Content = [new Block.TextBlock("结果二")],
                },
                new TimelineEvent.MessageEvent
                {
                    Id = "m2", At = DateTimeOffset.UnixEpoch, From = Speaker.User,
                    Blocks = [new Block.TextBlock("下一句")],
                },
            ]);

        var root = await GetJson($"/sessions/{sid}/history");
        var ids = root.GetProperty("rendered").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!).ToArray();

        // 组内次序：模型消息 → 两条结果（按记录序）→ 下一条消息。
        Assert.Equal(["m1", "r1", "r2", "m2"], ids);

        // ⚠️ 探针：这两个结果**确实是** `callId` 配得上 m1 的两条调用——
        // 否则「紧跟」只是碰巧，而不是配对判据在起作用。
        var calls = root.GetProperty("rendered")[0].GetProperty("blocks").EnumerateArray()
            .Where(b => b.GetProperty("kind").GetString() == "toolUse")
            .Select(b => b.GetProperty("callId").GetString()!).ToArray();
        Assert.Equal(["c1", "c2"], calls);
    }

    [Fact]
    public async Task Snapshot_Queue_CarriesExactlyTheseKeys()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        AssertKeysOf(await GetJson($"/sessions/{sid}/queue"), "isRunning", "queueDepth");
    }

    // ⚠️ `/axes` 的每项**随轴而变**：数值范围轴（温度）多一个 `range`，枚举轴没有。
    // ⇒ 期望值从 `Axes.RangeOf` 现算，并且**两支都要真的走到**——
    // 否则 `else` 那支会静默变成死代码，而它恰恰是五条轴共用的那条。
    [Fact]
    public async Task Snapshot_Axes_CarriesExactlyTheseKeys()
    {
        var root = await GetJson("/axes");
        AssertKeysOf(root, "axes", "alertSafety", "alertFrom");

        var items = root.GetProperty("axes").EnumerateArray().ToList();
        Assert.NotEmpty(items);

        foreach (var a in items)
        {
            var name = a.GetProperty("name").GetString()!;
            var expected = Axes.RangeOf(name) is null
                ? new[] { "label", "name", "values", "inContext" }
                : ["label", "name", "values", "inContext", "range"];
            Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), KeysOf(a));
        }

        // 两支都走到（少了这句，一个只有枚举轴的实现也能让上面全绿）。
        Assert.Contains(items, a => Axes.RangeOf(a.GetProperty("name").GetString()!) is not null);
        Assert.Contains(items, a => Axes.RangeOf(a.GetProperty("name").GetString()!) is null);
    }

    [Fact]
    public async Task Snapshot_Claims_CarriesExactlyTheseKeys()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        await _dispatcher.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src/x.cs"]), "c2", false, CancellationToken.None);

        AssertEachItemKeysOf(await GetJson("/claims"), "sessionId", "projectKey", "path", "at", "stale");
    }

    // ⚠️ `/providers` 的键集**随数据而变**（可空字段不设会被 `WhenWritingNull` 省掉）⇒
    // 必须先播一个「字段全设上」的 provider，否则钉的是「碰巧只有 5 个键」。
    [Fact]
    public async Task Snapshot_Providers_CarriesExactlyTheseKeys()
    {
        SeedFullProvider();

        var item = (await GetJson("/providers")).EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "full");

        AssertKeysOf(item, "id", "protocol", "defaultModel", "endpoint", "authScheme", "modality", "models", "hasApiKey");
    }

    // 缺省供应商（可空字段全不设）的键集**更小**——这条把「省略」也钉住：
    // 若哪天有人给 `Endpoint` 加个非空默认值，上面那条仍绿而这条会红。
    [Fact]
    public async Task Snapshot_Providers_OmitsNullFields()
    {
        var item = (await GetJson("/providers")).EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "p");   // InitializeAsync 播的那个

        AssertKeysOf(item, "id", "protocol", "defaultModel", "modality", "hasApiKey");
    }

    [Fact]
    public async Task Snapshot_ProviderSchema_CarriesExactlyTheseKeys() =>
        AssertKeysOf(await GetJson("/provider-schema"), "protocols", "authSchemes", "defaultAuthScheme", "providerIdRule");

    [Fact]
    public async Task Snapshot_NetworkConfig_CarriesExactlyTheseKeys()
    {
        var root = await GetJson("/network-config");

        // ⚠️ key 只回 hasApiKey（与 `/providers` 同一条纪律）。这条同时钉住「没有泄漏 apiKey 这个键」。
        AssertKeysOf(root, "enabled", "hasApiKey", "search", "fetch");
        Assert.False(root.TryGetProperty("apiKey", out _));

        AssertKeysOf(root.GetProperty("search"), "provider", "endpoint", "maxResults", "timeoutSeconds");
        AssertKeysOf(root.GetProperty("fetch"), "timeoutSeconds", "maxBytes", "allowDomains");
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
            ["ArchivedAt:Nullable`1", "AskTimeout:Nullable`1", "CreatedAt:DateTimeOffset",
             "Model:ModelSelection", "Plugins:IReadOnlyList`1", "PremierPrompt:String", "ProjectKey:String", "SessionId:String",
             "SystemPromptPreset:String", "Title:String",
             "Version:Int32", "WorkingDirectory:String"],
            Shape(typeof(SessionSpec)));

        // 嵌套那层也要钉——key 加在这里会序列化成 spec.model.apiKey，顶层看不见。
        Assert.Equal(
            ["Effort:String", "MaxTokens:Int32", "ModelId:String",
             "Protocol:String", "ProviderId:String"],
            Shape(typeof(ModelSelection)));
        Assert.Equal(["Defaults:IReadOnlyDictionary`2", "Id:String", "Version:String"], Shape(typeof(SessionPluginBinding)));
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

    // 端点交的键就这四个，一个不多——任何会被序列化出来的新增键都会让这条红。
    // 限定词「会被序列化出来的」不是修辞：值为 null 的新增字段不会被序列化，这条看不见它。
    // 它与 SessionSpec_HasExactlyTheseProperties 不是同一套理：那里反射 CLR 属性集（null 也看得见），这里数的是序列化出来的键。
    [Fact]
    public async Task ProviderSchema_CarriesExactlyTheseKeys()
    {
        using var s = JsonDocument.Parse(await Client.GetStringAsync(Http("/provider-schema")));

        Assert.Equal(
            ["authSchemes", "defaultAuthScheme", "protocols", "providerIdRule"],
            s.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        // ⚠️ 判据文案与缺省认证方式都要**来自定义处**，不能是在端点里内联的第二份。
        //（内联那份会腐：改谓词的人看不见它，而头的表单文案是照它写的。）
        Assert.Equal(CommandDispatcher.ProviderIdRule, s.RootElement.GetProperty("providerIdRule").GetString());
        Assert.Equal(AuthSchemes.Default, s.RootElement.GetProperty("defaultAuthScheme").GetString());
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

    // ---- 全量会话状态（`01` §3.8）：一次拿全，供头顶「哪个会话在等我」----

    /// <summary>
    /// ⚠️ <b>同一条已知数据经两个端点交出去时，<b>线上拼法必须相同</b></b>（2026-10-11 加）。
    ///
    /// <para>这条抓的是一个**真出过的**缺陷：`/sessions` 上那一格是手写的
    /// <c>a.Kind.ToString()</c>（⇒ <c>"Permission"</c>），而 <c>/state</c> 上那一格直接交枚举、
    /// 走 <c>JsonStringEnumConverter(camelCase)</c>（⇒ <c>"permission"</c>）。
    /// ⇒ <b>同一条 <c>/state</c> 响应里两种拼法并存</b>（顶层 `ask.kind` 与内层 `state.ask.kind`），
    /// 而任何一处都不报错——头只是把「？授权」徽标静默画成「？提问」。</para>
    ///
    /// <para>⚠️ <b>为什么必须钉「两个端点一致」而不是各钉各的字面量</b>：各钉各的能抓住
    /// 「改了一处」，但抓不住「本来就分叉」——分叉正是这次的形状。一致性判据没有第二个副本。</para>
    /// </summary>
    [Fact]
    public async Task AskKind_IsSpelledTheSameWayOnEveryEndpoint()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        var projectKey = _host.Store.FindSession(sid)!.Value.ProjectKey;
        var handle = _host.Activate(projectKey, sid);
        handle.UpdateStateAndPersist(_host.Store, s => s with
        {
            Status = SessionStatus.WaitingForInput,
            Ask = new PausedAsk("ask-1", AskKind.Permission, "call-1", "bash", "hash", null),
        }, diskStatus: SessionStatus.WaitingForInput);

        using var list = JsonDocument.Parse(await Client.GetStringAsync(Http("/sessions")));
        var listKind = list.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("sessionId").GetString() == sid)
            .GetProperty("ask").GetProperty("kind").GetString();

        using var state = JsonDocument.Parse(await Client.GetStringAsync(Http($"/sessions/{sid}/state")));
        var outerKind = state.RootElement.GetProperty("ask").GetProperty("kind").GetString();
        var innerKind = state.RootElement.GetProperty("state").GetProperty("ask").GetProperty("kind").GetString();

        Assert.Equal(listKind, outerKind);
        Assert.Equal(outerKind, innerKind);

        // 顺带钉死它**是哪一个**拼法——只钉「三处一致」的话，三处一起变成 PascalCase 也照样绿，
        // 而那会让头（按 camelCase 写）再一次静默失配。
        Assert.Equal("permission", listKind);
    }

    /// <summary>
    /// ⚠️ <b>这条钉的是「读不能有副作用」</b>——而这个端点最容易写错的地方正是它：
    /// `/sessions/{id}/state` 在激活表 miss 时会**载入并登记**那个会话，
    /// 照着它写 ⇒ **打开一次列表 = 全库载入**（`01` §11.7 那次事故的形状）。
    /// ⇒ 判据：**读完之后那个会话仍然不在激活表里**。
    /// </summary>
    [Fact]
    public async Task SessionsList_DoesNotActivateTheSessionsItLists()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // 先摘掉（`session.create` 会登记）——这样「它没被重新载入」才是可观测的。
        _host.Deactivate(sid);

        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http("/sessions")));
        Assert.Contains(doc.RootElement.EnumerateArray(),
            e => e.GetProperty("sessionId").GetString() == sid);

        Assert.False(_host.TryGetActivated(sid, out _));
    }

    /// <summary>
    /// ⚠️ <b>四种状态里有两种是内存事实</b>（`Running` 只写内存；「不活跃」= 不在激活表里），
    /// 而盘上只存 `Idle` / `WaitingForInput` ⇒ 这个端点必须两个来源都看。
    /// </summary>
    [Fact]
    public async Task SessionsList_ReportsInactiveAndDiskStatus()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        _host.Deactivate(sid);

        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http("/sessions")));
        var row = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("sessionId").GetString() == sid);

        Assert.False(row.GetProperty("active").GetBoolean());
        Assert.Equal("Idle", row.GetProperty("status").GetString());

        // ⚠️ **「没在等」是 `{}`**——键在，而里面什么都没有（`WhenWritingNull` 把可空字段
        // 也省掉了）。⇒ 判据必须是 `askId` **为空**，**不能**拿 `ask` 本身当判据
        // （`{}` 在 JS 里是 truthy ⇒ 头会以为在等）。见 `ControlServer` 那段注释。
        var ask = row.GetProperty("ask");
        Assert.False(ask.TryGetProperty("askId", out _));
    }

    /// <summary>
    /// **待在等待里的会话，列表上看得见**——这是头标出「哪个会话在等我」的唯一来源
    /// （重启之后 `approval.requested` 那条事件**没有生产者**：那次挂起发生在进程死之前）。
    /// </summary>
    [Fact]
    public async Task SessionsList_ShowsAPendingAsk()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // 直接写盘 + 摘内存：模拟「进程重启后读回来的那次挂起」——
        // 那正是这条路径的真实来源（内存里那份 `AwaitingInput` 已经不存在了）。
        var projectKey = _host.Store.FindSession(sid)!.Value.ProjectKey;
        var handle = _host.Activate(projectKey, sid);
        handle.UpdateStateAndPersist(_host.Store, s => s with
        {
            Status = SessionStatus.WaitingForInput,
            Ask = new PausedAsk("ask-1", AskKind.Permission, "call-1", "bash", "hash", null),
        }, diskStatus: SessionStatus.WaitingForInput);
        _host.Deactivate(sid);

        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http("/sessions")));
        var row = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("sessionId").GetString() == sid);

        Assert.Equal("WaitingForInput", row.GetProperty("status").GetString());
        var ask = row.GetProperty("ask");
        Assert.Equal("ask-1", ask.GetProperty("askId").GetString());

        // ⚠️ **camelCase，不是 `"Permission"`**（2026-10-11 改）：核心这里曾经手写 `.ToString()`
        // ⇒ PascalCase，而同一份数据在 `/state` 里是 camelCase（`JsonStringEnumConverter`）
        // ⇒ **同一条响应两种拼法**。头拿 PascalCase 去比 ⇒ 「？授权」徽标静默画成「？提问」。
        // ⇒ 判据是「线上只有一种拼法」，而它与**每一个**线上枚举值一致（`"event"` / `"toolUse"` …）。
        Assert.Equal("permission", ask.GetProperty("kind").GetString());
        Assert.Equal("bash", ask.GetProperty("toolName").GetString());

        // ⚠️ 内部配对用的那两格**不进线上 schema**（交出去等于把实现细节写进协议）。
        Assert.False(ask.TryGetProperty("argsHash", out _));
        Assert.False(ask.TryGetProperty("callId", out _));
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

    // ---- 自检端点：跑在哪个数据根 / 哪个 workdir / 哪个 origin ----

    [Fact]
    public async Task Health_ReportsTheDataRootAndWorkingDirectory()
    {
        var json = await Client.GetStringAsync(Http("/health"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        // 「我配了 provider 怎么看不到」的答案来源就是这两个字段。
        Assert.Equal(_root, root.GetProperty("dataRoot").GetString());
        Assert.Equal(_work, root.GetProperty("workingDirectory").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("sessions").ValueKind);
    }

    // origin 读的必须是**实例值**，不是默认常量——换过 allowedOrigin 的服务器上要跟着变。
    [Fact]
    public async Task Info_ReportsTheOriginThisServerActuallyAccepts()
    {
        var json = await Client.GetStringAsync(Http("/info"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(_root, root.GetProperty("dataRoot").GetString());
        Assert.Equal(ControlServer.DefaultAllowedOrigin, root.GetProperty("allowedOrigin").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("coreVersion").GetString()));
        Assert.Contains("/ws", root.GetProperty("endpoints").GetProperty("ws").GetString());
    }

    // ---- provider.test 的线上那一线 ----

    [Fact]
    public async Task WebSocket_ProviderTest_ForAMissingProvider_IsRejectedNamingTheId()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await SendAsync(ws, """{"kind":"command","id":"t1","payload":{"kind":"provider.test","id":"nope"}}""");

        var ack = await ReceiveAsync(ws);
        Assert.False(ack.GetProperty("accepted").GetBoolean());
        Assert.Contains("nope", ack.GetProperty("reason").GetString());
    }

    // ---- 回收的线上那一线（回收批）----

    /// <summary>
    /// ⚠️ **回收只在开窗时发生（`session.gc`），不在每一步**（2026-10-17 的形状）：
    /// 这是「未期望的时机」那次事故在**线上端点**上的守卫——`/history` 是头与控制台
    /// 读的那一份，若渲染自己回收，模型刚读到的结果会在**下一次重拉**时变成注记
    /// （而下一步的请求里也已经是注记）。这里用真实的 `session.gc` 命令开窗，
    /// 前后各拉一次 `/history`：开窗前必须是原文，开窗后必须变成注记。
    /// </summary>
    [Fact]
    public async Task History_ReclaimsOnlyAfterSessionGc_NotOnEveryRender()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // 一条内联结果 + 垫够保护带（与 `session.gc` 的解析判据一致：末尾 N 条不动）。
        var events = new List<TimelineEvent>
        {
            new TimelineEvent.ToolResultEvent
            {
                Id = "r1", At = DateTimeOffset.UnixEpoch, CallId = "c1",
                Content = [new Block.TextBlock("原文")],
            },
        };
        events.AddRange(Enumerable.Range(0, Session.Reclaim.ProtectedRecentResults).Select(i =>
            (TimelineEvent)new TimelineEvent.ToolResultEvent
            {
                Id = $"pad{i}", At = DateTimeOffset.UnixEpoch, CallId = $"padcall{i}",
                Content = [new Block.TextBlock("垫")],
            }));
        _host.Store.AppendEvents(Kernel.ProjectKey.FromPath(_work), sid, events);

        // 开窗前：渲染（两次）都是原文——反复渲染不改变结论。
        for (var i = 0; i < 2; i++)
        {
            var root = await GetJson($"/sessions/{sid}/history");
            var r1 = root.GetProperty("rendered").EnumerateArray()
                .Single(e => e.GetProperty("id").GetString() == "r1");
            var text = string.Concat(r1.GetProperty("blocks").EnumerateArray()
                .Select(b => b.GetProperty("text").GetString()));
            Assert.Contains("原文", text);
            Assert.DoesNotContain("[已回收]", text);
        }

        // 开窗（真实命令路径：解析 → 落窗口记录）。
        var gc = await _dispatcher.DispatchAsync(new CoreCommand.SessionGc(sid), "g", false, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(gc.Ack);

        // 开窗后：同一条变成注记，而注记给的 id 就是记录 id。
        var after = await GetJson($"/sessions/{sid}/history");
        var reclaimed = after.GetProperty("rendered").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "r1");
        var note = string.Concat(reclaimed.GetProperty("blocks").EnumerateArray()
            .Select(b => b.GetProperty("text").GetString()));
        Assert.Contains("[已回收]", note);
        Assert.Contains("fetch(id=\"r1\")", note);

        // ⚠️ 窗口记录本身**不进渲染**（它是给我们自己的锚，不是对话）。
        Assert.DoesNotContain("reclaim", after.GetProperty("rendered").EnumerateArray()
            .Select(e => e.GetProperty("kind").GetString()));
    }

    /// <summary>
    /// ⚠️ **`session.gc` 是同步跑完才回执的，而头在等回执的整个窗口里 `current` 还是旧会话。**
    /// 连续点两次（或切完会话立刻点）时，第二条命令会带着**上一个会话的 id** 发出去。
    ///
    /// <para>这条钉的是**核心侧那一半**：它必须<b>当场、在这个会话上</b>把回收算完并发出事件
    /// ——这样头「等回执 → 用它带回的 sessionId 去重拉」那条路才成立。</para>
    ///
    /// <para>⚠️ <b>为什么这条必须有</b>：`session.reclaimed` 的整个存在理由就是
    /// 「回收不改盘，头不重拉就永远看不见」⇒ 事件没发出来 = 这个功能在线上是**静默失效**的
    /// （核心那边一切正常、控制台也看得见，只有头什么都看不到）。</para>
    /// </summary>
    [Fact]
    public async Task WebSocket_SessionGc_ReclaimsInThatSession_AndAnnouncesIt()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);

        await SendAsync(ws, """{"kind":"command","id":"c1","payload":{"kind":"session.create"}}""");
        var sid = (await ReceiveAsync(ws)).GetProperty("sessionId").GetString()!;

        await SendAsync(ws, "{\"kind\":\"subscribe\",\"topic\":\"session:" + sid + "\"}");

        // 一条**过阈值**的工具结果 ⇒ 可回收（保护带之后才会被收，所以多造几条）。
        var big = new string('x', BlobStore.InlineThresholdBytes + 1);
        for (var i = 0; i < Session.Reclaim.ProtectedRecentResults + 1; i++)
        {
            _adapter.ReplyBlocks(new Block.ToolUseBlock($"k{i}", "read", """{"path":"a.txt"}"""));
            _adapter.Reply("好");
        }

        // 让那个工作目录里有那个文件，`read` 才产得出快照。
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_work, "a.txt"), big);

        for (var i = 0; i <= Session.Reclaim.ProtectedRecentResults; i++)
        {
            await SendAsync(ws,
                "{\"kind\":\"command\",\"id\":\"s" + i + "\",\"payload\":{\"kind\":\"session.send\","
                + "\"sessionId\":\"" + sid + "\",\"blocks\":[{\"kind\":\"text\",\"text\":\"读一下\"}]}}");

            // ⚠️ **收到这条的回执为止**——不要用「超时就当收完」那种排空：
            // `ManagedWebSocket` 在**取消**挂起的接收时会**中止整个连接**
            // （实测：下一次 `SendAsync` 直接抛 `WebSocketState.Aborted`）。
            // 而每条命令**一定**有回执（成败都有）⇒ 按回执收是确定性的、不靠超时。
            for (var k = 0; ; k++)
            {
                Assert.True(k < 200, "排空事件时收不到 s" + i + " 的回执");
                if (await ReceiveAsync(ws) is { } f
                    && f.GetProperty("kind").GetString() == "ack"
                    && f.GetProperty("id").GetString() == "s" + i) break;
            }
        }

        await SendAsync(ws,
            "{\"kind\":\"command\",\"id\":\"g1\",\"payload\":{\"kind\":\"session.gc\",\"sessionId\":\"" + sid + "\"}}");

        // ⚠️ 回执必须**当场**回来（同步算完），且在这条流上发了 `session.reclaimed`。
        string? ackNote = null, eventReason = null;
        for (var i = 0; i < 200 && (ackNote is null || eventReason is null); i++)
        {
            var f = await ReceiveAsync(ws);
            var kind = f.GetProperty("kind").GetString();
            if (kind == "ack" && f.GetProperty("id").GetString() == "g1")
                ackNote = f.GetProperty("note").GetString();
            if (kind == "event"
                && f.GetProperty("payload").GetProperty("kind").GetString() == "session.reclaimed")
                eventReason = f.GetProperty("payload").GetProperty("reason").GetString();
        }

        Assert.NotNull(ackNote);
        Assert.NotNull(eventReason);

        // ⚠️ **这里原先是 `Assert.Contains("回收了", ackNote)`**，而它在本批红了——
        // 因为预设一上线，工具结果的**内容**变了：`read` 读到的那个文件更小/更大了，
        // 而「收不收得了」本来就取决于内容在不在库里。**这条断言在钉一个会随别的东西变的事实**，
        // 而本测试要钉的是「回执与事件说同一句话」，不是「这一次到底收了几条」。
        // ⇒ 改成断言那句人话的**形状**（两种结果都认），把「具体收了几条」留给 ReclaimTests。
        Assert.True(
            ackNote.Contains("回收了") || ackNote.Contains("没有可回收"),
            $"回执得说清结果，实际是：{ackNote}");

        // ⚠️ **同一个事实，两处必须逐字相同**（2026-10-06 加）。
        //
        // 这句话的两个去处是**不同用途**、不是冗余：回执给「发命令的那个客户端」
        // （控制台 / 脚本），事件给「盯着这条流的每个头」。但**它只有一个出处**（核心这里）——
        // 各写一份的话，改了一句忘了另一句，两个客户端就会对同一件事说不同的话。
        //
        // ⚠️ **为什么单靠「事件到了」不够**（这条断言就是那次事故留下的）：
        // 原先事件只带**条数**，而条数**到不了用户眼前**——回执先到、事件后到，
        // 头处理事件时要重拉历史，那一步清空时间线，把回执刚画上的那行一起抹掉。
        // 于是核心侧全绿、控制台也看得见，**只有头一个字都不说**。
        // ⇒ 现在头把这句话画在**重拉之后**，前提正是它随事件一起来。
        Assert.Equal(ackNote, eventReason);
    }
}

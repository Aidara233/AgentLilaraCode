using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Adapters;
using Lilara.Core.Projects;
using Lilara.Core.Canonical;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 控制面（`03`）——HTTP 快照 + WS。
///
/// <para>⚠️ 这些测试**真的起一个 Kestrel 并真的连**（短连接、随机端口）。
/// 理由：控制面的错法大多是「形状对但接不上」——CORS 头、WS 握手、
/// 帧的序列化、并发写 socket 抛异常。**桩测不到那些**。</para>
///
/// <para>⚠️ 端口用<b>随机</b>的：写死端口会让并行测试互撞，而那条约束是硬的
/// （见 `ControlServer` 的注释：端口可注入是「集成测试写得出来」的前提）。</para>
/// </summary>
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
        // ⚠️ 预置回复：让步**真的走通**（而不是每次都撞「没有预置回复了」）。
        // 这样事件里才有模型那条 `message.appended` 与 `step.end`，
        // 否则测的只是「失败路径也发事件」。
        _adapter = new Fakes.FakeAdapter();
        _adapter.Reply("好的").Reply("好的").Reply("好的");
        _dispatcher = new CommandDispatcher(_host, _bus, h => new Stepper(_host.Store, _adapter));

        // ⚠️ 随机端口：写死会让并行测试互撞。
        _port = Random.Shared.Next(20_000, 60_000);
        _server = new ControlServer(_host, _dispatcher, _bus, _port);
        _app = await _server.StartAsync(CancellationToken.None);
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

    /// <summary>⚠️ 只监听本机（`03` §10「鉴权最小」）。</summary>
    [Fact]
    public void BindAddress_IsLoopbackOnly() =>
        Assert.Equal("127.0.0.1", ControlServer.BindAddress);

    /// <summary>
    /// ⚠️ 快照必须带 **`seq` 高水位**（`03` §7 的原子性）：头拿它去 `subscribe(since:)`，
    /// 「拉快照 → 订阅」之间落的事件才**不漏不重**。少了它头要么永远缺一条、要么重复渲染，
    /// 而**头是无状态的**——每次刷新都重来。
    /// </summary>
    [Fact]
    public async Task History_And_State_CarryTheSeqHighWaterMark()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // ⚠️ **建会话本身会发事件**（`session.created`，项目流 + 会话流各一条）——
        // 所以高水位**不是从 0 起**。这条测试原本假设「建会话不产生事件」，
        // 那个前提在复审补上 `session.created` 之后就不成立了。
        // ⇒ 断言改成「与 `HighWaterMark` 读数一致」——**那才是真正的不变量**
        //（快照带的高水位必须等于当时的流高水位），而不是写死一个数。
        var before = _bus.HighWaterMark(EventBus.SessionTopic(sid));
        _bus.Publish(EventBus.SessionTopic(sid), new WireEvent.Error(sid, "x"));
        var expected = _bus.HighWaterMark(EventBus.SessionTopic(sid));
        Assert.Equal(before + 1, expected);

        var history = await Client.GetStringAsync(Http($"/sessions/{sid}/history"));
        var state = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));

        using var h = JsonDocument.Parse(history);
        using var s = JsonDocument.Parse(state);

        // ⚠️ **不是写死 1**：是「快照带回来的**就是**流的高水位」。
        Assert.Equal(expected, h.RootElement.GetProperty("highWater").GetInt64());
        Assert.Equal(expected, s.RootElement.GetProperty("highWater").GetInt64());
    }

    /// <summary>⚠️ 未知会话 ⇒ **404**，不是 500、不是空对象。</summary>
    [Fact]
    public async Task UnknownSession_Is404()
    {
        var r = await Client.GetAsync(Http("/sessions/nope/state"));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, r.StatusCode);
    }

    /// <summary>
    /// ⚠️ 头是**独立 origin**，不放开 CORS 浏览器就连不上。
    /// 预检（OPTIONS）必须回 204 且带允许头。
    /// </summary>
    [Fact]
    public async Task Cors_AllowsTheHeadsOrigin()
    {
        using var req = new HttpRequestMessage(HttpMethod.Options, Http("/projects"));
        var r = await Client.SendAsync(req);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, r.StatusCode);
        Assert.True(r.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// ⚠️ **这条是控制面的核心验收**：连上 → 订阅 → 发命令 → 拿到 `ack` → 事件到达。
    ///
    /// <para>它一次验掉四件事，桩测都覆盖不到：
    /// ① WS 握手通；② 帧的序列化形状对（头要按 `kind` 分派）；
    /// ③ 命令**投递即返回**（`03` §1 判据 2）——`ack` 先到，事件随后来；
    /// ④ 事件**带 `seq` 且单调**。</para>
    /// </summary>
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

        // ⚠️ **用拼接而不是 `$$"""`**：内插原始字符串里，载荷末尾的 `}}}` 会被当成内插洞的收尾
        // （`CS9007`）——这个坑本项目已经踩过两次（T6/T8 的 brief）。拼接没有花括号歧义。
        await SendAsync(ws,
            "{\"kind\":\"command\",\"id\":\"c2\",\"payload\":{\"kind\":\"session.send\","
            + "\"sessionId\":\"" + sid + "\",\"blocks\":[{\"kind\":\"text\",\"text\":\"你好\"}]}}");

        // ⚠️ **不能写死帧数**：ack 与 event 是两条独立的写路径，条数与时序都不定。
        // 第一版写死「收 6 帧」，于是等第 6 帧等到超时——**那是测试的问题，不是实现的**。
        // ⇒ 收到「我们真正在等的东西」就停。
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

        // ⚠️ 事件带 seq，且**单调增**
        var seqs = events.Select(e => e.GetProperty("seq").GetInt64()).ToList();
        for (var i = 1; i < seqs.Count; i++)
            Assert.True(seqs[i] > seqs[i - 1], "事件 seq 必须严格递增");

        // ⚠️ 载荷自己的判别式也在（`WireEvent` 独立成根，不是继承 `TimelineEvent`）
        Assert.Contains(events, e => e.GetProperty("payload").GetProperty("kind").GetString()
                                     == "message.appended");
    }

    /// <summary>
    /// ⚠️ 命令**不合法** ⇒ `accepted: false` + 理由（`rejected` 的正当用法）。
    /// 而**业务失败**走 `error` 事件——两者混了，头会把网络抖动当成「你命令写错了」。
    /// </summary>
    [Fact]
    public async Task WebSocket_RejectsAMalformedCommandWithoutClosingTheSocket()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);

        await SendAsync(ws, """{"kind":"command","id":"c1","payload":{"kind":"session.interrupt","sessionId":"nope"}}""");
        var ack = await ReceiveAsync(ws);

        Assert.False(ack.GetProperty("accepted").GetBoolean());
        Assert.Contains("nope", ack.GetProperty("reason").GetString());

        // ⚠️ 而且 **socket 还活着**——一条坏命令不该踢掉头。
        Assert.Equal(WebSocketState.Open, ws.State);
        await SendAsync(ws, """{"kind":"command","id":"c2","payload":{"kind":"provider.upsert","id":"p","apiKey":"k2"}}""");
        Assert.True((await ReceiveAsync(ws)).GetProperty("accepted").GetBoolean());
    }

    /// <summary>
    /// ⚠️ **线上的 `session.setAxis` 必须真的能到 dispatcher。**
    /// T3 只在**类型**层加了命令，`ParseCommand` 的 switch 没跟上 ⇒ 帧落到 `_ => throw`
    /// ⇒ 头（`05` §7.4 的轴下拉）发出来的东西全被回「命令形状不对」。
    /// ⚠️ 这条测试走**真的 WS**，不是直接调 dispatcher——后者会在实现缺失时照样绿。
    /// </summary>
    [Fact]
    public async Task WebSocket_SetAxis_ReachesTheDispatcher()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        // ⚠️ **用拼接而不是 `$$"""`**：内插原始字符串里，载荷末尾的 `}}}` 会被当成内插洞的收尾
        // （`CS9007`）——见 `WebSocket_AcksCommandsAndStreamsEvents` 的同款注释。
        await SendAsync(ws, "{\"kind\":\"command\",\"id\":\"a1\",\"payload\":{\"kind\":\"session.setAxis\","
            + "\"sessionId\":\"" + sid + "\",\"axis\":\"safety\",\"value\":\"prod\"}}");

        var ack = await ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("kind").GetString());
        // ⚠️ **理由必须惰性取**：`WireJson.Options` 的 `WhenWritingNull` 把成功回执的 `reason`
        // 整个省略（camelCase），`ack.GetProperty("reason")` 会**抛 KeyNotFoundException**。
        // 而 `Assert.True` 的消息串是**无条件先求值**的——直接拼进去，成功那条路也照样炸
        // （这是本测试第一版真踩到的：从「认不出命令」修好之后，失败点挪到了这行）。
        // ⇒ 用 `TryGetProperty`，它在两种形态下都不抛。
        Assert.True(ack.GetProperty("accepted").GetBoolean(),
            "认不出这条命令——`ParseCommand` 的 switch 漏了它："
            + (ack.TryGetProperty("reason", out var why) ? why.GetString() : "(无 reason 字段)"));

        // ⚠️ 判据是「**命令真的生效了**」，不只是「回执说 accepted」——
        // 回执可以说 accepted 而语义根本没跑（那正是「形状对但接不上」的形态）。
        var state = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));
        using var doc = JsonDocument.Parse(state);
        Assert.Equal("prod", doc.RootElement.GetProperty("axes").GetProperty("safety").GetString());
    }

    /// <summary>
    /// ⚠️ **头断连不影响任何东西**（`03` §1 判据 1）：断开之后核心照常收命令。
    /// </summary>
    [Fact]
    public async Task HeadDisconnecting_DoesNotAffectTheCore()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        ws.Dispose();

        // 核心照常работает
        var r = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(r.Ack);
    }

    /// <summary>
    /// ⚠️ **WS 的 Origin 白名单**——它是**`apiKey` 可选的硬前置**：网页能改 `endpoint`
    /// 就能把流量（连同 key）转走，**且不需要知道 key**。
    ///
    /// <para>⚠️ **判据是「放行无 Origin，拒绝别的 origin」，不是「要求 Origin」**：
    /// 浏览器发的握手**永远带 `Origin`**（挡住坏 origin 就达到了目的），
    /// 而 curl / 测试驱动 / 未来的 CLI 头**不带**——要求它等于把它们**全部**误伤，
    /// 而下面那批既有 WS 测试正是这条改动的**回归网**（误伤它们 = 自己拆掉安全网）。</para>
    /// </summary>
    [Theory]
    [InlineData(null)]                     // 无 Origin（curl / 测试驱动）⇒ 放行
    [InlineData("http://127.0.0.1:7790")]  // 头自己的 origin ⇒ 放行
    [InlineData("http://evil.example")]    // ⇒ 拒绝
    [InlineData("http://localhost:7790")]  // ⚠️ 同机不同写法：**拒绝**——见下
    [InlineData("null")]                   // `file://` 页面与沙箱 iframe 的 Origin 字面量是 "null"
    public async Task WebSocket_OriginPolicy(string? origin)
    {
        using var socket = new ClientWebSocket();
        // ⚠️ **设备端没有「带 Origin 的握手」这个 API，只能手设头**——
        // `SetRequestHeader` 这条路**实测过**（.NET 10）：设了服务端 `ctx.Request.Headers["Origin"]`
        // 收得到；**不设则该头根本不存在**（读出来是空串，不是 null）⇒ 判据里「放行无 Origin」就是这条。
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);

        var connect = socket.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        if (origin is null or "http://127.0.0.1:7790")
        {
            await connect;                                    // 放行 ⇒ 握上手
            Assert.Equal(WebSocketState.Open, socket.State);
        }
        else
        {
            // ⚠️ 握手被拒时 `ConnectAsync` 抛 `WebSocketException`（服务端回了 403）。
            // ⚠️ **`localhost:7790` 在拒绝之列是刻意的**：它与 `127.0.0.1:7790` **不是同一个 origin**
            // （浏览器就是这么判的），且放行 `localhost` 等于放行一个**任何本机程序都能抢占的名字**
            // （hosts 文件 / DNS）。
            await Assert.ThrowsAnyAsync<WebSocketException>(() => connect);
        }
    }

    /// <summary>
    /// ⚠️ **钉住「闸读的是参数，不是写死的字面量」**。
    ///
    /// <para>上面那条理论用的是**默认值** ⇒ 把参数忽略掉、写死 `http://127.0.0.1:7790`
    /// 它**照样全绿**。本条的全部价值在**反向**那半：在一个显式传了**非默认**值的服务器上，
    /// **默认值那个 origin 必须被拒**——只有这一条能杀死「参数被忽略」。</para>
    /// </summary>
    [Fact]
    public async Task WebSocket_HonoursANonDefaultAllowedOrigin()
    {
        // ⚠️ 显式传一个**非默认**的 allowedOrigin ⇒ 钉住「闸读的是参数，不是写死的字面量」。
        // ⚠️ 建一个**第二个** ControlServer（另起随机端口），别去动既有那个 `_server`。
        const string mine = "http://127.0.0.1:19999";
        var port = Random.Shared.Next(20_000, 60_000);
        var server = new ControlServer(_host, _dispatcher, _bus, port, allowedOrigin: mine);
        var app = await server.StartAsync(CancellationToken.None);
        try
        {
            // 放行：非默认那个 origin 自己
            using (var ok = new ClientWebSocket())
            {
                ok.Options.SetRequestHeader("Origin", mine);
                await ok.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
                Assert.Equal(WebSocketState.Open, ok.State);
            }
            // ⚠️ 反向：**默认值**那个 origin 在这里必须**被拒**——否则说明参数没被用上
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

    /// <summary>
    /// 收**一整帧**文本消息并解析。
    ///
    /// <para>⚠️ **不能只收一次就解析。** `ClientWebSocket.ReceiveAsync` 在
    /// 「**有数据可用**」时就返回，**不保证**这一帧收完整了——语义如此，不是猜测。
    /// 负载下 Kestrel 把一帧拆成两次 TCP 写出，`result.Count` 就是**半个帧**，
    /// `JsonDocument.Parse` 会抛 `JsonException`。</para>
    ///
    /// <para>⚠️ **观测到的事实**（不是从语义推的）：`WebSocket_SetAxis_ReachesTheDispatcher`
    /// 在全量跑时**偶发失败一次**，而**错开单跑 5/5 全绿** ⇒ 负载相关，与逻辑无关。
    /// ⚠️ 分片本身**没有**被独立复现过——修的是**由 `ReceiveAsync` 语义推得**的隐患；
    /// 若将来又见偶发，先怀疑别处。</para>
    ///
    /// <para>⚠️ **8 个调用点全都期望完整帧**（`grep -c EndOfMessage` = 0，无人依赖部分帧语义）
    /// ⇒ 收满再接是**严格更正确**，不是行为变更。</para>
    /// </summary>
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
            if (result.EndOfMessage) break;   // ⚠️ 这一句是修的全部要害
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone();
    }

    /// <summary>
    /// ⚠️ **复审抓到的真缺陷（Critical）**：`resync_required` 之后该流的订阅**永久死亡**。
    ///
    /// <para>resync 时 `EventBus` 已经 `TryComplete()` 了那条 channel，泵随之结束；
    /// 头按设计**重新订阅**（`03` §7 说这是**唯一**的降级路径），
    /// 而第一版服务端 `if (subs.ContainsKey(topic)) return;` 把那条**死订阅**留在字典里
    /// ⇒ 直接挡回去 ⇒ **该 topic 从此收不到任何事件**。</para>
    ///
    /// <para>⚠️ 更坏的地方：头**不会发现自己停了**（socket 是健康的，它只是安静）。</para>
    /// </summary>
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

        // 用**太旧**的起点订阅 ⇒ 应当立刻 resync_required
        await SendAsync(ws, "{\"kind\":\"subscribe\",\"topic\":\"" + topic + "\",\"since\":1}");
        var first = await ReceiveAsync(ws);
        Assert.Equal("resync_required", first.GetProperty("kind").GetString());

        // ⚠️ 头按设计重建：重新订阅（这次从那之后开始）
        await SendAsync(ws, "{\"kind\":\"subscribe\",\"topic\":\"" + topic + "\",\"since\":"
            + _bus.HighWaterMark(topic) + "}");

        // 再来一条**活的**事件——它必须到
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

    /// <summary>
    /// ⚠️ **复审抓到的真缺陷**：`session.created` **声明并注册了却从没发布**。
    /// `05` §6.1 要求头的「全局会话列表」随事件更新，而那份列表是**派生**的。
    /// 少了它：在**控制台**敲 `session create`，已经打开的页面**看不见**这个新会话，且不会自愈。
    /// </summary>
    [Fact]
    public async Task SessionCreate_PublishesSessionCreatedOnTheProjectTopic()
    {
        var pk = Projects.ProjectKey.FromPath(_work);
        using var sub = _bus.Subscribe(EventBus.ProjectTopic(pk));

        await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);

        Assert.True(sub.Reader.TryRead(out var p), "项目流上必须能收到 session.created");
        Assert.IsType<WireEvent.SessionCreated>(p.Payload);
    }

    /// <summary>
    /// ⚠️ **复核抓到的漏网畸形帧**：`{"kind":"subscribe","topic":null}`。
    ///
    /// <para>它走的是另一条路：`GetString()` 对 JSON `null` **返回 null 而不抛** ⇒
    /// `subscribe(null!)` ⇒ 字典查 null ⇒ **`ArgumentNullException`**，
    /// 而不在原来那份异常类型白名单里 ⇒ 逃出接收循环 ⇒ **连接 Aborted**。
    /// 复核者实测 9 帧矩阵：只有这一帧会踢掉连接，其余 8 种（`topic:123`、无 `topic`、
    /// 无 `id`、`payload:null`、`id:null`、`kind:123`、非 JSON、未知 kind）都安然无恙。</para>
    ///
    /// <para>⇒ 修法不是往白名单里加一类，而是**不按类型赌**：既然「一个第三方头发错一帧
    /// 不该让整条连接一起死掉」，那就该一律兜住。</para>
    /// </summary>
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

        // ⚠️ 连接必须**还活着**：再发一条合法命令，它必须能回执。
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

    /// <summary>
    /// ⚠️ **初始轴值必须来自快照**（`05` §11 #4）：`axis.changed` 只在订阅**之后**才有，
    /// 头刚打开页面时没有任何事件可等。少了这一项，五个下拉全是空的，
    /// 而用户看到的「空」与「默认值」在视觉上无法区分。
    /// </summary>
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

    /// <summary>
    /// ⚠️ **三个槽位**（`01` §3.1 头两个 + §3.2 尾槽位「浮动」）。
    /// `05` §7.1 的「空的也要显示」要求它**恒在**且是**数组**——
    /// 省略字段会让头分不清「空」与「没有这个概念」（既有注释已写明这条）。
    /// </summary>
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
        var sid = ((Ack.Accepted)created.Ack).SessionId!;      // ⚠️ 分派型命令只在这个回执里给身份

        using var s = JsonDocument.Parse(await Client.GetStringAsync(Http($"/sessions/{sid}/state")));
        var spec = s.RootElement.GetProperty("spec");

        // ⚠️ **整份 `SessionSpec` 交出去**（R-A：头要什么由头决定，核心不做子集裁剪）。
        // ⚠️ 线上是 **camelCase**（`WireJson.Options`）——键名按它写，别按 C# 属性名。
        Assert.Equal(sid, spec.GetProperty("sessionId").GetString());
        Assert.False(string.IsNullOrEmpty(spec.GetProperty("projectKey").GetString()));
        Assert.False(string.IsNullOrEmpty(spec.GetProperty("workingDirectory").GetString()));
        Assert.NotEqual(default, spec.GetProperty("createdAt").GetDateTimeOffset());

        var model = spec.GetProperty("model");
        // ⚠️ 值来自测试**预置的那个 provider**（`"p"` / `DefaultModel = "m"`）——
        // 写死它们同时钉住了「spec 里的模型就是该会话实际绑的那份」。
        Assert.Equal("p", model.GetProperty("providerId").GetString());
        Assert.Equal("m", model.GetProperty("modelId").GetString());
        Assert.Equal(Protocols.ClaudeMessages, model.GetProperty("protocol").GetString());
        Assert.False(string.IsNullOrEmpty(model.GetProperty("effort").GetString()));
        Assert.True(model.GetProperty("maxTokens").GetInt32() > 0);

        // ⚠️ **G12**：key 不在 `SessionSpec` 里（它住 `providers/*.json`）
        // ⇒ 「整份交出去」这件事**天生不泄 key**——这条断言把那句话钉成可执行的。
        Assert.False(spec.TryGetProperty("apiKey", out _));
    }

    /// <summary>
    /// ⚠️ **`SessionSpec` 的属性集就是这几样——将来谁往里加一个凭据字段，这条会红。**
    ///
    /// <para>为什么不能只靠 `Assert.False(spec.TryGetProperty("apiKey"))`：那条是**缺字段型**断言，
    /// 而 `WireJson.Options` 带 `WhenWritingNull` ⇒ 加一个**为 null** 的 `string? ApiKey`
    /// 时键会被整个省掉、那条照样绿。（加了**非 null** 的值它才抓得住。）</para>
    ///
    /// <para>⚠️ **反射而不是枚举 JSON**：key 若加在**嵌套**类型上，会变成 `spec.model.apiKey`——
    /// 顶层的 `TryGetProperty` 看不见它。属性集这条**两层一起钉**，
    /// 且**与序列化选项无关**（改 `JsonSerializerOptions` 也动不了它）。</para>
    ///
    /// <para>⚠️ **这条是**白名单**：加字段就得来改它。那正是要的——`SessionSpec` 是
    /// **写进每个会话目录**的东西，加字段该是有意识的动作，不是顺手。</para>
    /// </summary>
    [Fact]
    public void SessionSpec_HasExactlyTheseProperties_SoNoCredentialCanHide()
    {
        static string[] Names(Type t) =>
            t.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["CreatedAt", "Model", "ProjectKey", "SessionId", "WorkingDirectory"],
            Names(typeof(SessionSpec)));

        // ⚠️ **嵌套那层也要钉**——key 加在这里会序列化成 `spec.model.apiKey`，顶层看不见。
        Assert.Equal(
            ["Effort", "MaxTokens", "ModelId", "Protocol", "ProviderId"],
            Names(typeof(ModelSelection)));
    }

    /// <summary>
    /// ⚠️ **`GET /axes` 是头唯一的轴来源**（G8：头不许手抄）。
    /// 少了它，头里那份手抄的 `AXES` 常量就是第二份契约——而 `Axes.cs:8` 明说「不许手抄」。
    ///
    /// <para>⚠️ 这条测试**同时钉住「中文标签归核心」**：`05` §3「渲染归核心，头只显示」——
    /// 而下拉的 `主动性` / `安全` 就是渲染。头拿到英文档名自己是画不出中文的。</para>
    /// </summary>
    [Fact]
    public async Task Axes_Endpoint_HandsOutEverythingTheHeadNeeds()
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http("/axes")));
        var axes = doc.RootElement.GetProperty("axes").EnumerateArray().ToList();

        Assert.Equal(Axes.Names.Count, axes.Count);
        // ⚠️ 顺序**就是** `Axes.Names` 的顺序——它决定头的渲染顺序（与 T1 那条同名约定一致）。
        Assert.Equal(Axes.Names, axes.Select(a => a.GetProperty("name").GetString()!).ToList());

        foreach (var a in axes)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("label").GetString()),
                $"轴 {a.GetProperty("name").GetString()} 没有标签——头画不出中文名");
            Assert.NotEmpty(a.GetProperty("values").EnumerateArray());
        }

        // ⚠️ **`inContext` 必须是真值而不是漏字段**：`effort` 是 false（`01` §3.5：不进上下文），
        // 头据此把它画灰。少了这个字段，头分不清「不进上下文」与「核心没这个信息」。
        var effort = axes.Single(a => a.GetProperty("name").GetString() == "effort");
        Assert.False(effort.GetProperty("inContext").GetBoolean());

        // ⚠️ **告警的两端也归核心**：头里写字面量的话，轴里改一个档名就会让告警**静默失效**。
        Assert.Equal("prod", doc.RootElement.GetProperty("alertSafety").GetString());
        Assert.Equal("authorized", doc.RootElement.GetProperty("alertFrom").GetString());
    }

    /// <summary>
    /// ⚠️ **没有 `claims.json` 时返回 `[]`，不是 404 也不是 500**——
    /// 「这个项目没人声明任何东西」是合法且**最常见**的形态。
    /// </summary>
    [Fact]
    public async Task Claims_WithNoClaimsFile_IsAnEmptyArray()
    {
        var r = await Client.GetAsync(Http("/claims"));

        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("[]", (await r.Content.ReadAsStringAsync()).Trim());
    }

    /// <summary>
    /// ⚠️ **走真 WS，不是直接调 dispatcher。** 后者在接线缺失时会**照样绿**——
    /// 它绕过的正是本条要钉的那一层。实现前这条必须红，且红的方式应当是
    /// 「认不出的命令 'claim.release'」。
    /// </summary>
    [Fact]
    public async Task WebSocket_ClaimRelease_ReachesTheDispatcher()
    {
        var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        // ⚠️ 先占上。`claim.acquire` **不在**线上命令表里（`03` §3 资源那节只有 release），
        // 所以这里直接走 dispatcher——认领是控制台/模型的动作，头**从不发**它。
        await _dispatcher.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c2", false, CancellationToken.None);
        Assert.NotEmpty(new ClaimStore(_work).All());

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
        // ⚠️ **用拼接而不是 `$$"""`**：内插原始字符串里，载荷末尾的 `}}}` 会被当成内插洞的收尾
        // （`CS9007`）——见 `WebSocket_AcksCommandsAndStreamsEvents` 的同款注释。
        await SendAsync(ws, "{\"kind\":\"command\",\"id\":\"a1\",\"payload\":{\"kind\":\"claim.release\","
            + "\"sessionId\":\"" + sid + "\",\"scope\":[\"src\"]}}");

        var ack = await ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("kind").GetString());
        // ⚠️ **理由必须惰性取**：`WireJson.Options` 的 `WhenWritingNull` 把成功回执的 `reason`
        // 整个省略（camelCase），`ack.GetProperty("reason")` 会**抛 KeyNotFoundException**。
        // 而 `Assert.True` 的消息串是**无条件先求值**的——直接拼进去，成功那条路也照样炸。
        // ⇒ 用 `TryGetProperty`，它在两种形态下都不抛。
        Assert.True(ack.GetProperty("accepted").GetBoolean(),
            "认不出这条命令——`ParseCommand` 的 switch 漏了它："
            + (ack.TryGetProperty("reason", out var why) ? why.GetString() : "(无 reason 字段)"));

        // ⚠️ 判据是「**命令真的生效了**」，不只是「回执说 accepted」——
        // 回执可以说 accepted 而语义根本没跑（那正是「形状对但接不上」的形态）。
        Assert.Empty(new ClaimStore(_work).All());
    }
}

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Adapters;
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
        _bus.Publish(EventBus.SessionTopic(sid), new WireEvent.Error(sid, "x"));

        var history = await Client.GetStringAsync(Http($"/sessions/{sid}/history"));
        var state = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));

        using var h = JsonDocument.Parse(history);
        using var s = JsonDocument.Parse(state);

        Assert.Equal(1, h.RootElement.GetProperty("highWater").GetInt64());
        Assert.Equal(1, s.RootElement.GetProperty("highWater").GetInt64());
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

    private static async Task SendAsync(WebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> ReceiveAsync(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ws.ReceiveAsync(buffer, cts.Token);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
    }
}

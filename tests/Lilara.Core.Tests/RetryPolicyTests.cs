using System.Net;
using System.Reflection;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.Claude;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

// RetryPolicy：判据 / 退避 / 循环。全部离线——「等多久」走可注入的 delay，
// 「什么值得重试」走两条协议各自的打标函数（不手拼异常对象）。
public class RetryPolicyTests
{
    // ⚠️ 时间线**非空**：三个构造器都会在「空时间线 + 空槽位」时早抛（那是它们的合法用例），
    // 而这里要的是**真的发出一次请求**、让状态码分支跑到。空时间线会让 `PostAsync` 一次都没被调用，
    // 于是 `LastStatus` 是 null、断言变成一条永远说不清「到底走到哪一层」的错误。
    private static ModelRequest Request() => new()
    {
        ModelId = "m", MaxTokens = 16, Protocol = Protocols.ClaudeMessages,
        Effort = "high", Temperature = null, Slots = Lilara.Core.Session.PromptSlots.Empty,
        Tools = [],
        Timeline =
        [
            new TimelineEvent.MessageEvent
            {
                Id = "m1", At = CoreClock.Now(), From = Speaker.User,
                Blocks = [new Block.TextBlock("ping")],
            },
        ],
    };

    private static ModelReply Pong() => new() { Blocks = [], StopReason = "end_turn", Refused = false };

    private static ModelCallFailedException Transient(string why = "上游 503") =>
        new(why) { Transient = true };

    private static ProviderDefinition Def(string protocol) => new()
    {
        Id = "p", Protocol = protocol, ApiKey = "k", DefaultModel = "m", Modality = "vision",
    };

    // ---- 判据：只有打了瞬态标的才重试 ----

    [Fact]
    public void ShouldRetry_OnlyForTheTransientFlag()
    {
        Assert.True(RetryPolicy.ShouldRetry(Transient()));
        Assert.False(RetryPolicy.ShouldRetry(new ModelCallFailedException("上游 503")));   // 默认不重试
        Assert.False(RetryPolicy.ShouldRetry(new InvalidOperationException("boom")));
    }

    // ---- 状态码真值表：两条协议共用这一张表 ----

    [Theory]
    [InlineData(408, true)]     // 这一次没赶上
    [InlineData(425, true)]     // 同上（没有具名成员，按整型）
    [InlineData(429, true)]     // 限流
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(529, true)]     // 上游过载（不在 HttpStatusCode 的枚举里，按整数判）
    [InlineData(503, true)]
    [InlineData(400, false)]    // 请求内容的问题——再发一次也一样
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public void IsTransientStatus_TruthTable(int status, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.IsTransientStatus((HttpStatusCode)status));
    }

    // ⚠️ 这一条钉的是「两层共用一个谓词」，而 **OpenAI 那一半走的是生产调用点**
    // （`OpenAiChatAdapter.CallAsync` → `OpenAiHttp.PostAsync` → `RetryPolicy.ShouldRetry`）——
    // 不是直接调 `PostAsync`。区别是实测出来的：直接调 `PostAsync` 时，把生产里
    // `Transient = RetryPolicy.IsTransientStatus(...)` 改成恒真，测试照样绿（变异 M5）——
    // 因为这条用例从来没读过生产抛出的那个**标志**，只读了状态码。
    // ⇒ 现在断言的是 `ShouldRetry(生产抛出的那个异常)`。
    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public async Task BothProtocols_ClassifyTheSameStatusTheSameWay(int status, bool expected)
    {
        // OpenAI：真适配器 + 测试专属传输层，抛出的就是生产路径的那个异常。
        var chat = new OpenAiChatAdapter(Def(Protocols.OpenAiChat) with { Endpoint = LocalEndpoint.Url });
        var openAi = await Assert.ThrowsAsync<ModelCallFailedException>(() =>
            chat.CallAsync(Request(), CancellationToken.None,
                transport: new LocalEndpoint.StubHandler((HttpStatusCode)status)));

        Assert.Equal(status, (int)Assert.IsType<HttpStatusCode>(OpenAiHttp.LastStatus));
        Assert.Equal(expected, RetryPolicy.ShouldRetry(openAi));

        // Claude：走适配器自己的映射（SDK 的异常类 → 状态码）。
        Assert.Equal(expected, RetryPolicy.ShouldRetry(
            new ModelCallFailedException(ClaudeAdapter.Describe(SdkException(status)))
            {
                Transient = ClaudeAdapter.IsTransient(SdkException(status)),
            }));

        // 同一张表给同一个答案（两条协议读的都是它）。
        Assert.Equal(expected, RetryPolicy.IsTransientStatus((HttpStatusCode)status));
    }

    // 传输失败（没拿到响应）：两条链路都是瞬态。
    [Fact]
    public void TransportFailures_AreTransient_OnBothProtocols()
    {
        Assert.True(ClaudeAdapter.IsTransient(
            new Anthropic.Exceptions.AnthropicIOException("I/O exception", new HttpRequestException("refused"))));

        // OpenAI 那条的传输失败落在 `ShouldWrap` 的兜底里（取消已经在 ShouldWrap 放行了）。
        Assert.True(OpenAiHttp.ShouldWrap(new HttpRequestException("refused"), CancellationToken.None));
    }

    // 400 是「请求内容的问题」：再发十次也还是 400，而它的人话要原样给用户。
    [Fact]
    public void BadRequest_IsNotTransient()
    {
        Assert.False(ClaudeAdapter.IsTransient(SdkException(400)));
        Assert.False(ClaudeAdapter.IsTransient(new Anthropic.Exceptions.AnthropicInvalidDataException("bad body")));
    }

    // ---- 退避曲线：单调、不封顶、确定性 ----

    [Fact]
    public void DelayBefore_GrowsExponentially_WithoutACap()
    {
        // ⚠️ 上下界都断：只断下界的话，一个恒等 0 的实现会绿（而它等于没有退避）。
        Assert.InRange(RetryPolicy.DelayBefore(1).TotalMilliseconds, 400, 600);
        Assert.InRange(RetryPolicy.DelayBefore(2).TotalMilliseconds, 800, 1_200);
        Assert.InRange(RetryPolicy.DelayBefore(3).TotalMilliseconds, 1_600, 2_400);
        Assert.InRange(RetryPolicy.DelayBefore(4).TotalMilliseconds, 3_200, 4_800);

        // ⚠️ **曲线不封顶**（2026-10-26 改：原来第 5 次起封在 5 秒）。
        // 每一格都是「500ms × 2^(n-1) 带 ±20% 抖动」，而这里逐格钉住——
        // 「不封顶」是个**决定**（封顶会让十次重试一直按 5 秒的节奏去撞同一个限流窗口），
        // 被悄悄改回去时必须有一条红，而不是靠谁记得。
        var total = 0d;
        var nominalTotal = 0d;
        for (var retry = 1; retry <= RetryPolicy.MaxRetries; retry++)
        {
            var ms = RetryPolicy.DelayBefore(retry).TotalMilliseconds;
            var nominal = RetryPolicy.BaseDelayMilliseconds * Math.Pow(2, retry - 1);
            Assert.InRange(ms, nominal * 0.8, nominal * 1.2);
            total += ms;
            nominalTotal += nominal;
        }

        // 十次重试的等待之和 ≈ 511.5 秒（约 8 分 32 秒）——`RetryPolicy` 的类注释引用的就是这个量级。
        // ⚠️ 边界按**标称和的 ±20%** 给，不写死一个数：抖动是逐格独立的，十格叠起来
        // 可能落在标称值的任何一侧（实测 545.6 秒，比标称高 6.7%）——写死区间等于让这条
        // 断言去猜抖动，而抖动一改（+20% → +25%）它就变成一条与策略无关的假红。
        Assert.InRange(total, nominalTotal * 0.8, nominalTotal * 1.2);
        Assert.InRange(total / 1000, 480, 620);   // 「约 8 分钟出头」这个量级本身也要钉住
    }

    [Fact]
    public void DelayBefore_IsDeterministic()
    {
        // 抖动不用随机数：同一个序号永远同一个值（否则曲线无法被钉住）。
        Assert.Equal(RetryPolicy.DelayBefore(3), RetryPolicy.DelayBefore(3));
    }

    [Fact]
    public void DelayBefore_RejectsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.DelayBefore(0));
    }

    // ---- 循环 ----

    [Fact]
    public async Task CallAsync_TransientFailuresThenSuccess_RetriesAndReports()
    {
        var adapter = new Scripted();
        adapter.Fail(Transient());
        adapter.Fail(Transient());
        adapter.Succeed();

        var waits = new List<TimeSpan>();
        var outcome = await RetryPolicy.CallAsync(adapter, Request(), CancellationToken.None,
            delay: (d, _) => { waits.Add(d); return Task.CompletedTask; });

        Assert.Equal(3, adapter.Calls);
        Assert.Equal(3, outcome.Attempts);
        Assert.Equal(2, waits.Count);
        // 两次等待：第一次之后短、第二次之后长。
        Assert.True(waits[0] < waits[1]);
        Assert.InRange(waits[0].TotalMilliseconds, 400, 600);
        Assert.InRange(waits[1].TotalMilliseconds, 800, 1_200);
    }

    // ⚠️ 次数是「重试 10 次」不是「共 10 次」：一次全部失败 = 11 次真实请求。
    // 两个方向都断——少一次和多一次都会让这条红。
    [Fact]
    public async Task CallAsync_GivesUpAfterTenRetries_WhichIsElevenCalls()
    {
        var adapter = new Scripted();
        for (var i = 0; i <= RetryPolicy.MaxRetries + 5; i++) adapter.Fail(Transient());

        var ex = await Assert.ThrowsAsync<ModelCallFailedException>(() =>
            RetryPolicy.CallAsync(adapter, Request(), CancellationToken.None,
                delay: (_, _) => Task.CompletedTask));

        Assert.Equal(RetryPolicy.MaxRetries + 1, adapter.Calls);
        Assert.Equal(11, adapter.Calls);
        // 原话保留 + 说清试过了；且交出去的那一份**不再**标瞬态（结构上防止外层再套一层）。
        Assert.Contains("上游 503", ex.Message);
        Assert.Contains("已自动重试 10 次", ex.Message);
        Assert.False(ex.Transient);
    }

    [Fact]
    public async Task CallAsync_NonTransientFailure_ThrowsImmediately()
    {
        var adapter = new Scripted();
        adapter.Fail(new ModelCallFailedException("请求被上游拒绝（400）"));

        var waits = 0;
        var ex = await Assert.ThrowsAsync<ModelCallFailedException>(() =>
            RetryPolicy.CallAsync(adapter, Request(), CancellationToken.None,
                delay: (_, _) => { waits++; return Task.CompletedTask; }));

        Assert.Equal(1, adapter.Calls);
        Assert.Equal(0, waits);
        Assert.Equal("请求被上游拒绝（400）", ex.Message);   // 原样，不包「重试了 N 次」
        Assert.False(ex.Transient);
    }

    // 用户中断不是失败：取消原样冒出去（CommandDispatcher 靠这个类型分界发不发 error 事件）。
    [Fact]
    public async Task CallAsync_CallerCancellation_PropagatesUnchanged()
    {
        using var cts = new CancellationTokenSource();
        var adapter = new Scripted();
        adapter.Fail(new OperationCanceledException(cts.Token));
        cts.Cancel();

        var waits = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryPolicy.CallAsync(adapter, Request(), cts.Token,
                delay: (_, _) => { waits++; return Task.CompletedTask; }));

        Assert.Equal(1, adapter.Calls);
        Assert.Equal(0, waits);
    }

    // 记账口：每次失败都调一次（含最后那次），成功**不**调（成功那行由调用方记，
    // 因为只有它知道那个回复能不能用）。
    [Fact]
    public async Task CallAsync_ReportsEveryFailure_RatherThanTheLastOnly()
    {
        var adapter = new Scripted();
        adapter.Fail(Transient("第一次"));
        adapter.Fail(Transient("第二次"));
        adapter.Succeed();

        var seen = new List<(int Index, string Message)>();
        await RetryPolicy.CallAsync(adapter, Request(), CancellationToken.None,
            delay: (_, _) => Task.CompletedTask,
            onFailedAttempt: f => seen.Add((f.Index, f.Failure.Message)));

        Assert.Equal([(1, "第一次"), (2, "第二次")], seen);
    }

    // ---- 工具：按状态码造一个**真的** SDK 异常（走 SDK 自己的工厂，不手拼异常类） ----

    private static Exception SdkException(int status) =>
        (Exception)typeof(Anthropic.Exceptions.AnthropicExceptionFactory)
            .GetMethod("CreateApiException", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [ (HttpStatusCode)status, """{"error":{"type":"x","message":"y"}}""" ])!;

    /// <summary>
    /// 本机假端点：交给 `PostAsync` 的那个 handler（测试专属参数，不是全局钩子）。
    /// ⚠️ 换的是**生产路径读的那个位置**，不是测试自己的复算——否则「生产里那一行判断错了」在测试里看不见。
    /// </summary>
    private sealed class LocalEndpoint
    {
        public static string Url => "http://local.invalid";

        public sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"scripted\"}}"),
                });
        }
    }

    private sealed class Scripted : IModelAdapter
    {
        private readonly Queue<Func<ModelReply>> _scripted = new();
        public int Calls { get; private set; }
        public ResolvedEndpoint? Endpoint => null;
        public string Protocol { get; set; } = Protocols.ClaudeMessages;

        public Scripted Fail(Exception ex) { _scripted.Enqueue(() => throw ex); return this; }
        public Scripted Succeed() { _scripted.Enqueue(Pong); return this; }

        public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            Calls++;
            if (_scripted.Count == 0) throw new InvalidOperationException("脚本用完了");
            return Task.FromResult(_scripted.Dequeue()());
        }
    }
}

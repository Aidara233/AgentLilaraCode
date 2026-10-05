using System.Net;
using Lilara.Core.Protocol.OpenAi;

namespace Lilara.Core.Tests;

// OpenAiHttp.Describe / Truncate —— 纯函数、离线；原本是 private，测试够不着，故抽成 internal。
public class OpenAiHttpDescribeTests
{
    // 兜底分支也会印状态码，故每条断言必须落在只有那条分支才给得出的专属词上，不能只断状态码数字。

    [Fact]
    public void Describe_MapsUnauthorized()
    {
        var message = OpenAiHttp.Describe(HttpStatusCode.Unauthorized, "");

        Assert.Contains("已吊销", message);
        Assert.DoesNotContain("模型调用失败（", message);   // 兜底形状，出现即说明走错了分支
    }

    [Fact]
    public void Describe_MapsForbidden()
    {
        var message = OpenAiHttp.Describe(HttpStatusCode.Forbidden, "");

        Assert.Contains("权限", message);
        Assert.DoesNotContain("模型调用失败（", message);
    }

    [Fact]
    public void Describe_MapsNotFound()
    {
        var message = OpenAiHttp.Describe(HttpStatusCode.NotFound, "");

        Assert.Contains("端点或模型不存在", message);
        Assert.DoesNotContain("模型调用失败（", message);
    }

    [Fact]
    public void Describe_MapsTooManyRequests()
    {
        var message = OpenAiHttp.Describe(HttpStatusCode.TooManyRequests, "");

        Assert.Contains("限流", message);
        Assert.DoesNotContain("模型调用失败（", message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Describe_Maps5xxAsRetryable(HttpStatusCode status)
    {
        var message = OpenAiHttp.Describe(status, "");

        Assert.Contains(((int)status).ToString(), message);
        Assert.Contains("重试", message);                    // 兜底没有「重试」
        Assert.DoesNotContain("模型调用失败（", message);
    }

    // 兜底分支必须带上响应体——上游的错误细节通常只在这里，而专用分支是人话、不带原文。
    [Fact]
    public void Describe_FallsBackToTheBodyForOtherStatuses()
    {
        var message = OpenAiHttp.Describe(HttpStatusCode.BadRequest, "model not found");

        Assert.Contains("400", message);
        Assert.Contains("model not found", message);
    }

    [Fact]
    public void Describe_TruncatesALongBody()
    {
        var message = OpenAiHttp.Describe(HttpStatusCode.BadRequest, new string('x', 500));

        Assert.Contains("…", message);
        // 200 个 x + 省略号，而不是 500 个。
        Assert.DoesNotContain(new string('x', 300), message);
    }

    [Fact]
    public void Truncate_LeavesAShortBodyAlone()
    {
        var exact = new string('y', 200);

        Assert.Equal(exact, OpenAiHttp.Truncate(exact));
    }

    [Fact]
    public void Truncate_CutsAtTheBoundary()
    {
        var result = OpenAiHttp.Truncate(new string('z', 201));

        Assert.Equal(new string('z', 200) + "…", result);
    }

    // ---- 上游原话要带上：真正的病因常常只在那里 ----
    // ⚠️ 体是**实测原样抄下来的**（本机网关 403），不是我编的一个「像那么回事」的形状——
    // 我第一版就编错了：以为 `cause` 是独立字段，其实它**包在 `error.message` 的文本里**。
    [Fact]
    public void Describe_Forbidden_IncludesWhatTheUpstreamSaid()
    {
        const string body = """{"error":{"message":"CC Switch local proxy failed while handling Codex endpoint /chat/completions. Provider: 高数; model: x; upstream_status: HTTP 521; cause: 上游错误 (521):","type":"upstream_error","code":"cc_switch_upstream_error"}}""";

        var message = OpenAiHttp.Describe(HttpStatusCode.Forbidden, body);

        Assert.Contains("上游错误 (521)", message);   // 真正的病因只在 error.message 里
    }

    // `error` 是**字符串**（简写）也要认——本机网关 401 就回 `{"error":"Authentication required"}`。
    [Fact]
    public void Detail_AcceptsTheStringShorthand()
    {
        Assert.Equal("Authentication required",
            OpenAiHttp.Detail("""{"error":"Authentication required"}"""));
    }

    [Fact]
    public void Detail_ReadsTheStandardObjectShape()
    {
        Assert.Equal("bad key",
            OpenAiHttp.Detail("""{"error":{"message":"bad key","type":"invalid_request_error"}}"""));
    }

    // 非对象根 / 坏 JSON 都不能抛——`TryGetProperty` 在非对象上会直接抛（三位协议链路的 T6/T8 踩过）。
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Detail_NeverThrowsOnNonObjectBodies(string body)
    {
        var ex = Record.Exception(() => OpenAiHttp.Detail(body));

        Assert.Null(ex);
    }
}

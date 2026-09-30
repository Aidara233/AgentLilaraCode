using System.Net;
using Lilara.Core.Adapters.OpenAi;

namespace Lilara.Core.Tests;

/// <summary>
/// <c>OpenAiHttp.Describe</c> / <c>Truncate</c> —— 纯函数，离线。
/// ⚠️ 这两条路径**在真联调里从未被执行过**：T10 那轮三条链路**零 HTTP 失败**，
/// 401/403/404/429/5xx 都没碰到；而它们原本是 `private`，测试也够不着。
/// ⇒ 只能靠这里逐条钉住。**形状照抄 `ClaudeAdapterTests.Describe_*` 那张真值表**
/// （Claude 那条同样把它抽成 `internal static` 才测得到）。
/// </summary>
public class OpenAiHttpDescribeTests
{
    [Fact]
    public void Describe_MapsUnauthorized() =>
        Assert.Contains("401", OpenAiHttp.Describe(HttpStatusCode.Unauthorized, ""));

    [Fact]
    public void Describe_MapsForbidden() =>
        Assert.Contains("403", OpenAiHttp.Describe(HttpStatusCode.Forbidden, ""));

    [Fact]
    public void Describe_MapsNotFound() =>
        Assert.Contains("404", OpenAiHttp.Describe(HttpStatusCode.NotFound, ""));

    [Fact]
    public void Describe_MapsTooManyRequests() =>
        Assert.Contains("429", OpenAiHttp.Describe(HttpStatusCode.TooManyRequests, ""));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Describe_Maps5xxAsRetryable(HttpStatusCode status)
    {
        var message = OpenAiHttp.Describe(status, "");

        Assert.Contains(((int)status).ToString(), message);
        Assert.Contains("重试", message);
    }

    /// <summary>
    /// ⚠️ 兜底分支必须**带上响应体**——上游的错误细节通常只在这里，
    /// 而 401/403/404/429/5xx 那五条是人话、不带原文。这条钉住「信息没有被丢掉」。
    /// </summary>
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
}

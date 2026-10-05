using System.Net;
using Lilara.Core.Adapters.OpenAi;

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
}

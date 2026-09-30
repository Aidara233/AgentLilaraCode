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
    // ⚠️ **断言必须落在只有那条分支才给得出的措辞上，不能只断状态码数字。**
    // 实测（复审否证，控制器复现）：`Describe` 的**兜底分支**也会印状态码
    // （`模型调用失败（401）：…`），所以只写 `Contains("401")` 的话，把四条专用分支**全删掉**
    // 这个文件**照样全绿**——十条里四条是空转的。
    // ⚠️ Claude 那七条没踩这个坑是**形状的巧合**：它的 `Describe` 收的是异常、兜底印 `ex.Message`，
    // 永远不含 "401"。**照抄断言形状而不照抄那个性质，就把区分力丢了。**
    // ⇒ 每条钉一个**专属词**，并确认它**不是**兜底。

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

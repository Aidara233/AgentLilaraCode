using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

/// <summary>
/// 两个联网工具的闸门与措辞。⚠️ 三道闸必须**分开说**（轴 / 未启用 / 没 key）：
/// 混成一句「搜索失败」会把模型引向重试同一个调用，而它该做的是让用户改配置。
/// </summary>
public class WebToolsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-webtools-" + Guid.NewGuid().ToString("N"));

    private readonly FakeNetworkTransport _transport = new();

    public WebToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SessionToolContext Context(string level, bool enabled = true, string? key = "k", bool goal = false) =>
        new(_root, new BlobStore(_root), null, null,
            new NetworkAccess(new NetworkConfig
            {
                Enabled = enabled,
                Search = new NetworkSearchConfig { ApiKey = key },
            }, level, _transport),
            goal);

    private static ToolCall Call(string tool, string args) => new("c1", tool, args);

    private static string Text(ToolOutcome outcome) =>
        Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text;

    // ---- web_search ----

    [Fact]
    public async Task Search_AxisOff_RefusesWithoutCallingTransport()
    {
        var text = Text(await new WebSearchTool().RunAsync(
            Call("web_search", """{"query":"dotnet"}"""), Context("off"), default));

        Assert.Contains("联网轴", text);
        Assert.Empty(_transport.Queries);
    }

    [Fact]
    public async Task Search_AxisFetchOnly_Refuses()
    {
        var text = Text(await new WebSearchTool().RunAsync(
            Call("web_search", """{"query":"dotnet"}"""), Context("fetch"), default));

        Assert.Contains("不允许搜索", text);
        Assert.Empty(_transport.Queries);
    }

    [Fact]
    public async Task Search_GloballyDisabled_SaysWhereToEnable()
    {
        var text = Text(await new WebSearchTool().RunAsync(
            Call("web_search", """{"query":"dotnet"}"""), Context("all", enabled: false), default));

        Assert.Contains("未启用", text);
        Assert.Empty(_transport.Queries);
    }

    [Fact]
    public async Task Search_WithoutKey_SaysSo()
    {
        var text = Text(await new WebSearchTool().RunAsync(
            Call("web_search", """{"query":"dotnet"}"""), Context("all", key: null), default));

        Assert.Contains("key", text);
        Assert.Empty(_transport.Queries);
    }

    [Fact]
    public async Task Search_FormatsAnswerAndHits()
    {
        var text = Text(await new WebSearchTool().RunAsync(
            Call("web_search", """{"query":"dotnet"}"""), Context("search"), default));

        Assert.Contains("摘要：摘要内容", text);
        Assert.Contains("1. 标题一", text);
        Assert.Contains("https://example.test/1", text);
        Assert.Contains("片段一", text);
        Assert.Equal(["dotnet"], _transport.Queries);
    }

    [Fact]
    public async Task Search_TransportFailure_IsContentNotAnException()
    {
        _transport.OnSearch = (_, _, _) => throw new NetworkException("搜索被限流（429）。");

        var text = Text(await new WebSearchTool().RunAsync(
            Call("web_search", """{"query":"x"}"""), Context("all"), default));

        Assert.Contains("搜索失败", text);
        Assert.Contains("429", text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":123}""")]
    [InlineData("这不是 JSON")]
    public async Task Search_BadInput_IsAResult(string args)
    {
        var text = Text(await new WebSearchTool().RunAsync(
            Call("web_search", args), Context("all"), default));

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    // ---- web_fetch ----

    [Fact]
    public async Task Fetch_AxisSearchOnly_Refuses()
    {
        var text = Text(await new WebFetchTool().RunAsync(
            Call("web_fetch", """{"url":"https://example.test"}"""), Context("search"), default));

        Assert.Contains("不允许抓取", text);
        Assert.Empty(_transport.Urls);
    }

    [Fact]
    public async Task Fetch_FormatsStatusTitleAndBody()
    {
        var text = Text(await new WebFetchTool().RunAsync(
            Call("web_fetch", """{"url":"https://example.test/a"}"""), Context("fetch"), default));

        Assert.Contains("页面标题", text);
        Assert.Contains("URL：https://example.test/a", text);
        Assert.Contains("状态：200", text);
        Assert.Contains("正文内容", text);
        Assert.Contains("由 HTML 抽取", text);
    }

    [Fact]
    public async Task Fetch_TruncationIsExplicit()
    {
        _transport.OnFetch = (_, url, _) => Task.FromResult(new NetworkFetchResult(
            url, url, 200, "text/plain", null, "前一段", true, 4096));

        var text = Text(await new WebFetchTool().RunAsync(
            Call("web_fetch", """{"url":"https://example.test"}"""), Context("all"), default));

        Assert.Contains("已截断", text);
    }

    [Fact]
    public async Task Fetch_TransportFailure_IsContent()
    {
        _transport.OnFetch = (_, _, _) => throw new NetworkException("`x.test` 解析到了内网 / 本机地址，拒绝抓取。");

        var text = Text(await new WebFetchTool().RunAsync(
            Call("web_fetch", """{"url":"https://x.test"}"""), Context("all"), default));

        Assert.Contains("抓取失败", text);
        Assert.Contains("内网", text);
    }

    // ---- finish_goal ----

    [Fact]
    public async Task FinishGoal_InGoalMode_Acknowledges()
    {
        var text = Text(await new FinishGoalTool().RunAsync(
            Call("finish_goal", "{}"), Context("off", goal: true), default));

        Assert.Contains("目标收尾", text);
    }

    [Fact]
    public async Task FinishGoal_OutsideGoal_SaysNothingChanged()
    {
        var text = Text(await new FinishGoalTool().RunAsync(
            Call("finish_goal", "{}"), Context("off", goal: false), default));

        Assert.Contains("没有做任何改动", text);
    }
}

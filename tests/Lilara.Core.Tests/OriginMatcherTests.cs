using Lilara.Core.Control;

namespace Lilara.Core.Tests;

// WS 握手的 Origin 判据。⚠️ 这个类的存在理由是**默认值变了**（2026-11-05）：
// 头绑 0.0.0.0 之后页面会从局域网 IP / localhost 打开，逐字相等会把它们全部**静默**拒绝
//（握手 403 时浏览器不把响应体交给页面 JS ⇒ 页面只会「已断开·重连中」）。
//
// ⚠️ 同时要防另一半：**别把守卫放开成「谁都能连」**。放开的只有主机这一维，端口仍然钉死。
public class OriginMatcherTests
{
    // ---- 通配主机 + 钉死端口（默认模式）----

    [Theory]
    [InlineData("http://127.0.0.1:17990")]
    [InlineData("http://localhost:17990")]
    [InlineData("http://192.168.1.5:17990")]
    [InlineData("http://[::1]:17990")]
    [InlineData("HTTP://Some-Box.local:17990")]
    public void DefaultPattern_AcceptsAnyHostOnTheHeadPort(string origin) =>
        Assert.True(OriginMatcher.Matches("http://*:17990", origin));

    [Theory]
    [InlineData("http://evil.example")]        // 端口 80
    [InlineData("http://evil.example:8080")]
    [InlineData("http://192.168.1.5:17988")]   // 核心端口
    [InlineData("https://192.168.1.5:17990")]  // 方案不同
    [InlineData("null")]                       // file:// 与沙箱 iframe
    [InlineData("")]
    public void DefaultPattern_RejectsEverythingElse(string origin) =>
        Assert.False(OriginMatcher.Matches("http://*:17990", origin));

    // ---- 显式字面量：回到「只认一个 origin」（收回开放的那条路）----

    [Fact]
    public void ExplicitOrigin_StillMatchesOnlyItself()
    {
        Assert.True(OriginMatcher.Matches("http://127.0.0.1:17990", "http://127.0.0.1:17990"));
        // ⚠️ 同机不同写法**不再被放行**（除非用户自己显式设成通配）——这是「收回」的语义：
        // 设了就只认它，别在背后又替用户宽容一格。
        Assert.False(OriginMatcher.Matches("http://127.0.0.1:17990", "http://localhost:17990"));
        Assert.False(OriginMatcher.Matches("http://127.0.0.1:17990", "http://192.168.1.5:17990"));
    }

    // ---- `*` 单独一个字符 = 任意 origin（只该在调试时用）----

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("https://anything")]
    [InlineData("null")]
    public void Star_AcceptsAnyOrigin(string origin) =>
        Assert.True(OriginMatcher.Matches(OriginMatcher.AnyOrigin, origin));

    // ---- 默认端口归一：浏览器认为 `https://x` 与 `https://x:443` 是同一个 origin ----

    [Theory]
    [InlineData("https://example.com", "https://example.com:443", true)]
    [InlineData("http://example.com", "http://example.com:80", true)]
    [InlineData("http://example.com", "http://example.com:8080", false)]
    public void MissingPort_MeansTheSchemesDefault(string pattern, string origin, bool expected) =>
        Assert.Equal(expected, OriginMatcher.Matches(pattern, origin));

    // ---- 解析不了 ⇒ 回落逐字相等（**不许变成放行**）----

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparsablePattern_DoesNotBecomeAPass(string pattern) =>
        Assert.False(OriginMatcher.Matches(pattern, "http://127.0.0.1:17990"));

    [Fact]
    public void UnparsableButIdentical_StillMatches() =>
        Assert.True(OriginMatcher.Matches("garbage", "garbage"));

    // 不带方括号的 IPv6 有歧义（几个冒号都可能是端口分隔符）⇒ 不猜，回落逐字相等。
    [Fact]
    public void UnbracketedIpv6_IsNotGuessed()
    {
        Assert.False(OriginMatcher.TrySplit("http://::1:17990", out _, out _, out _));
        Assert.True(OriginMatcher.Matches("http://[::1]:17990", "http://[::1]:17990"));
    }

    // ---- 横幅要把模式说成人话：星号不解释就等于没印 ----

    [Fact]
    public void Describe_ExplainsTheWildcard()
    {
        var text = OriginMatcher.Describe("http://*:17990");

        Assert.Contains("17990", text);
        Assert.Contains("任意主机", text);
    }

    [Fact]
    public void Describe_ForAnExplicitOrigin_NamesIt() =>
        Assert.Contains("http://127.0.0.1:17990", OriginMatcher.Describe("http://127.0.0.1:17990"));
}

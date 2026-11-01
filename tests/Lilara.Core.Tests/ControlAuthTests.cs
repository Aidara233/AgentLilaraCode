using Lilara.Core.Control;

namespace Lilara.Core.Tests;

// 判据同 `AllowedOriginResolverTests`：**环境变量赢了必须说得清**；默认（不校验）不许被悄悄改掉。
//
// ⚠️ 这个类要防的是两类静默：
//   ① 鉴权**默认开着** ⇒ 页面、curl、测试驱动、将来的 CLI 头一起被挡在门外，
//      而症状长得像「核心没起来」（`401` 在页面上只是一句话都说不清的失败）；
//   ② 开着鉴权而 token **比较写错**（空串算过 / 只比前缀 / 逐字节短路）⇒ 闸变成了摆设。
public class ControlAuthTests
{
    private static Func<string, string?> Env(string? value) =>
        n => n == ControlAuth.EnforceEnvVar ? value : null;

    // ---- 默认：不校验 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("no")]
    [InlineData("随便什么")]
    public void IsEnforced_OnlyAcceptsExplicitTruthyValues(string? value) =>
        Assert.False(ControlAuth.IsEnforced(Env(value)));

    // ⚠️ 只认**明确真值**：把「任何非空」当真会让一个拼错的值悄悄把用户的脚本全挡掉。
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("on")]
    [InlineData(" Yes ")]
    public void IsEnforced_AcceptsTheTruthySpellings(string value) =>
        Assert.True(ControlAuth.IsEnforced(Env(value)));

    // 环境变量名是跨进程契约（用户照着横幅/文档设的），改名要连横幅与文档一起改。
    [Fact]
    public void EnforceEnvVar_HasTheExpectedName() =>
        Assert.Equal("LILARA_AUTH", ControlAuth.EnforceEnvVar);

    // ---- token 本身 ----

    [Fact]
    public void GenerateToken_IsUrlSafeAndLongEnough()
    {
        var token = ControlAuth.GenerateToken();

        // 32 字节 base64url 无填充 = 43 个字符；熵够，而且能直接塞进 URL / `?token=` 而不必转义。
        Assert.Equal(43, token.Length);
        Assert.All(token, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
            $"token 里出现了不能进 URL 的字符：{c}"));
    }

    [Fact]
    public void GenerateToken_IsNotRepeated()
    {
        var a = ControlAuth.GenerateToken();
        var b = ControlAuth.GenerateToken();

        Assert.NotEqual(a, b);
    }

    // ---- 比较 ----

    [Fact]
    public void Matches_AcceptsTheExactToken()
    {
        var token = ControlAuth.GenerateToken();

        Assert.True(ControlAuth.Matches(token, token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    public void Matches_RejectsShortOrEmpty(string presented) =>
        Assert.False(ControlAuth.Matches(presented, ControlAuth.GenerateToken()));

    [Fact]
    public void Matches_RejectsPrefixOfTheRealToken()
    {
        var token = ControlAuth.GenerateToken();

        Assert.False(ControlAuth.Matches(token[..10], token));
    }

    [Fact]
    public void Matches_RejectsMissingOnEitherSide()
    {
        var token = ControlAuth.GenerateToken();

        Assert.False(ControlAuth.Matches(null, token));
        Assert.False(ControlAuth.Matches(token, null));
        // ⚠️ 「没开鉴权」（expected 为 null）交给调用方（`ControlServer` 的 `AuthRequired`）判，
        // 这里不许把 null 当成「谁都能过」——那是把闸门放进这个纯函数里。
        Assert.False(ControlAuth.Matches("", ""));
    }

    // ---- 跨进程契约：页面（`wwwroot/app.js`）必须按这两个字面量交凭据 ----

    [Fact]
    public void WireFormat_MatchesWhatThePageSends()
    {
        Assert.Equal("Bearer ", ControlAuth.BearerPrefix);
        Assert.Equal("token", ControlAuth.QueryParam);
    }
}

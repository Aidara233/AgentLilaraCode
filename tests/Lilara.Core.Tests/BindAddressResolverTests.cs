using Lilara.Core.Control;

namespace Lilara.Core.Tests;

// 判据同 `AllowedOriginResolverTests`：**环境变量赢了必须说得清**，默认值不许被悄悄换掉；全程离线（环境是注入的）。
//
// ⚠️ 这个类要防的是两类静默：
//   ① 对外监听悄悄成了默认（核心没有鉴权 ⇒ 本机的模型、工具、可读文件一起交出去）；
//   ② 用户设了 `LILARA_BIND` 而横幅不响 / 地址写法坏掉（IPv6 不带方括号）⇒「我开了，怎么连不上」。
public class BindAddressResolverTests
{
    private static Func<string, string?> Env(string? value) =>
        n => n == BindAddressResolver.EnvVar ? value : null;

    // ---- 默认：仍是本机 ----

    [Fact]
    public void Resolve_NothingSetGivesTheLoopbackDefault()
    {
        var resolved = BindAddressResolver.Resolve(_ => null);

        Assert.Equal("127.0.0.1", resolved.Address);
        Assert.Equal(BindAddressSource.Default, resolved.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankValueFallsBackToDefault(string? value)
    {
        var resolved = BindAddressResolver.Resolve(Env(value));

        Assert.Equal(ControlServer.DefaultBindAddress, resolved.Address);
        Assert.Equal(BindAddressSource.Default, resolved.Source);
    }

    // 兜底那条走 `ControlServer.DefaultBindAddress`（不是就地再写一个字面量）⇒ 改默认值只改一处。
    [Fact]
    public void Default_ComesFromControlServer_NotASecondLiteral() =>
        Assert.Equal("127.0.0.1", ControlServer.DefaultBindAddress);

    // ---- 显式设定：照抄，并交出「是它赢了」 ----

    [Fact]
    public void Resolve_ConfiguredValueWinsAndIsTrimmed()
    {
        var resolved = BindAddressResolver.Resolve(Env("  0.0.0.0  "));

        Assert.Equal("0.0.0.0", resolved.Address);
        Assert.Equal(BindAddressSource.Configured, resolved.Source);
    }

    // 环境变量的名字是跨进程契约（用户照着文档设的），改名要连文档与横幅一起改。
    [Fact]
    public void EnvVar_HasTheExpectedName() =>
        Assert.Equal("LILARA_BIND", BindAddressResolver.EnvVar);

    // ---- 「是不是只在本机」：判不出来时按对外算（警告宁可多响一次）----

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.1.2.3", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("LocalHost", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("my-box.local", false)]
    [InlineData("*", false)]
    public void IsLoopbackOnly_JudgesAddresses(string address, bool expected) =>
        Assert.Equal(expected, BindAddressResolver.IsLoopbackOnly(address));

    // ---- URL 写法：IPv6 必须带方括号 ----

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("::1", "[::1]")]
    [InlineData("[::1]", "[::1]")]
    [InlineData("::", "[::]")]
    [InlineData("*", "*")]
    public void HostForUrl_BracketsIpv6Literals(string address, string expected) =>
        Assert.Equal(expected, BindAddressResolver.HostForUrl(address));

    // ---- 每个来源都有一句人话（横幅要印它）----

    [Theory]
    [InlineData(BindAddressSource.Configured)]
    [InlineData(BindAddressSource.Default)]
    public void Describe_EverySourceHasAHumanSentence(BindAddressSource source)
    {
        var text = BindAddressResolver.Describe(source);

        Assert.False(string.IsNullOrWhiteSpace(text));
        // 来源要能被认出来：显式那条必须点名环境变量，否则用户不知道去哪改。
        if (source == BindAddressSource.Configured)
            Assert.Contains(BindAddressResolver.EnvVar, text);
    }
}

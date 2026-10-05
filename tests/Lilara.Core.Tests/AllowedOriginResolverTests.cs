using Lilara.Core.Control;

namespace Lilara.Core.Tests;

// 判据同 `EndpointResolverTests`：「环境变量赢了必须说得清」，不是「别让它赢」；全程离线（环境是注入的）。
//
// ⚠️ 这个类要防的是**头与核心各读各的端口**那个缺陷：头从 `LILARA_HEAD_PORT` 取端口，
// 而核心曾经把 `127.0.0.1:7790` 写死。两份副本无同步 ⇒ 改头端口后核心静默拒绝 WS
//（握手被拒时浏览器不把响应体交给页面 JS ⇒ 页面只会「已断开·重连中」）。
public class AllowedOriginResolverTests
{
    // 假的环境读取器：按名字返回，没设的给 null。
    private static Func<string, string?> Env(string? name, string? value) =>
        n => n == name ? value : null;

    private static Func<string, string?> HeadPort(string? value) => Env(AllowedOriginResolver.HeadPortEnvVar, value);

    // ---- 优先级：显式 > 头端口推导 > 默认 ----

    [Fact]
    public void Resolve_ExplicitOriginWinsOverHeadPort()
    {
        var getEnv = (string n) => n switch
        {
            AllowedOriginResolver.OriginEnvVar => "http://127.0.0.1:9000",
            AllowedOriginResolver.HeadPortEnvVar => "7791",
            _ => null,
        };

        var resolved = AllowedOriginResolver.Resolve(getEnv);

        Assert.Equal("http://127.0.0.1:9000", resolved.Origin);
        Assert.Equal(AllowedOriginSource.Configured, resolved.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankExplicitOriginFallsThroughToHeadPort(string? explicitOrigin)
    {
        var getEnv = (string n) => n switch
        {
            AllowedOriginResolver.OriginEnvVar => explicitOrigin,
            AllowedOriginResolver.HeadPortEnvVar => "7791",
            _ => null,
        };

        var resolved = AllowedOriginResolver.Resolve(getEnv);

        Assert.Equal("http://127.0.0.1:7791", resolved.Origin);
        Assert.Equal(AllowedOriginSource.Environment, resolved.Source);
    }

    [Fact]
    public void Resolve_HeadPortDerivesTheOriginTheHeadWouldServe()
    {
        var resolved = AllowedOriginResolver.Resolve(HeadPort("7791"));

        Assert.Equal("http://127.0.0.1:7791", resolved.Origin);
        Assert.Equal(AllowedOriginSource.Environment, resolved.Source);
    }

    // 非数字（头的 `int.TryParse` 也失败）⇒ 两边一起回落。若这里改成用垃圾串拼 origin，
    // 头仍起在 7790 而核心放行一个不存在的 origin ⇒ 又变成静默拒绝。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-port")]
    [InlineData("7791.5")]
    public void Resolve_UnparsableHeadPortFallsThroughToDefault(string? headPort)
    {
        var resolved = AllowedOriginResolver.Resolve(HeadPort(headPort));

        Assert.Equal(ControlServer.DefaultAllowedOrigin, resolved.Origin);
        Assert.Equal(AllowedOriginSource.Default, resolved.Source);
    }

    [Fact]
    public void Resolve_NothingSetGivesTheDefault()
    {
        var resolved = AllowedOriginResolver.Resolve(_ => null);

        Assert.Equal("http://127.0.0.1:7790", resolved.Origin);
        Assert.Equal(AllowedOriginSource.Default, resolved.Source);
    }

    // ---- 环境变量的名字是**跨程序集契约**（头读的是同一个）----

    // ⚠️ 名字写错/改名的话，核心以为「没人设过头端口」而回落到 7790，
    // 头却起在用户设的那个端口上 ⇒ 静默拒绝。这条把名字钉死。
    // （同 `EndpointResolverTests.EnvVar_MatchesTheNameTheSdkReads` 的形状。）
    [Fact]
    public void HeadPortEnvVar_MatchesTheNameTheHeadReads()
    {
        Assert.Equal("LILARA_HEAD_PORT", AllowedOriginResolver.HeadPortEnvVar);
    }

    [Fact]
    public void OriginEnvVar_HasTheExpectedName()
    {
        Assert.Equal("LILARA_ALLOWED_ORIGIN", AllowedOriginResolver.OriginEnvVar);
    }

    // ---- 默认值只有一处 ----

    // 兜底那条走 `ControlServer.DefaultAllowedOrigin`（不是就地再写一个字面量）⇒
    // 改默认端口只改一处。这条在有人把字符串内联回去时会红。
    [Fact]
    public void Default_ComesFromControlServer_NotASecondLiteral()
    {
        Assert.Equal("http://127.0.0.1:7790", ControlServer.DefaultAllowedOrigin);
    }

    // ---- 同一个 origin、两种来源，调用方必须能分开 ----

    [Fact]
    public void Resolve_SameOriginFromDifferentSourcesIsDistinguishable()
    {
        var explicitSame = AllowedOriginResolver.Resolve(Env(AllowedOriginResolver.OriginEnvVar, "http://127.0.0.1:7790"));
        var byDefault = AllowedOriginResolver.Resolve(_ => null);

        Assert.Equal(explicitSame.Origin, byDefault.Origin);
        Assert.NotEqual(explicitSame.Source, byDefault.Source);
    }

    // ---- 每个来源都有一句人话（横幅要印它）----

    [Theory]
    [InlineData(AllowedOriginSource.Configured)]
    [InlineData(AllowedOriginSource.Environment)]
    [InlineData(AllowedOriginSource.Default)]
    public void Describe_SaysSomethingForEverySource(AllowedOriginSource source)
    {
        Assert.False(string.IsNullOrWhiteSpace(AllowedOriginResolver.Describe(source)));
    }
}

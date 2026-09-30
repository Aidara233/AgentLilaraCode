using Lilara.Core.Adapters;

namespace Lilara.Core.Tests;

/// <summary>
/// 端点解析（01 §7：全局层持有「端点、key、协议类型」）。
/// ⚠️ 判据不是「别让环境变量赢」，而是「赢了必须说得清」。
/// 全程离线：环境变量是**注入**的，不读真实进程环境，更不联网。
/// </summary>
public class EndpointResolverTests
{
    /// <summary>假的环境读取器：只认那一个变量名。</summary>
    private static Func<string, string?> Env(string? value) =>
        name => name == EndpointResolver.EnvVar ? value : null;

    [Fact]
    public void Resolve_ConfiguredWinsOverEnvironment()
    {
        var resolved = EndpointResolver.Resolve(
            "https://configured.example.com", Env("http://127.0.0.1:15721"));

        Assert.Equal("https://configured.example.com", resolved.Url);
        Assert.Equal(EndpointSource.Configured, resolved.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankConfiguredFallsThroughToEnvironment(string? configured)
    {
        var resolved = EndpointResolver.Resolve(configured, Env("http://127.0.0.1:15721"));

        Assert.Equal("http://127.0.0.1:15721", resolved.Url);
        Assert.Equal(EndpointSource.Environment, resolved.Source);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData("", null)]
    [InlineData("  ", "   ")]
    public void Resolve_BlankEverythingFallsThroughToOfficial(string? configured, string? fromEnv)
    {
        var resolved = EndpointResolver.Resolve(configured, Env(fromEnv));

        Assert.Equal(EndpointResolver.OfficialUrl, resolved.Url);
        Assert.Equal(EndpointSource.Default, resolved.Source);
    }

    /// <summary>
    /// 这条才是「可见性」的判据：**同一个 URL、两种来源，调用方必须能把它们分开**。
    /// 分不开的话，环境变量接管就又变回静默的了。
    /// </summary>
    [Fact]
    public void Resolve_SameUrlFromDifferentSourcesIsDistinguishable()
    {
        var fromEnv = EndpointResolver.Resolve(null, Env("http://127.0.0.1:15721"));
        var configured = EndpointResolver.Resolve("http://127.0.0.1:15721", Env(null));

        Assert.Equal(fromEnv.Url, configured.Url);
        Assert.NotEqual(fromEnv.Source, configured.Source);
    }

    /// <summary>
    /// ⚠️ 名字写错的话，解析器会以为「没人设过」，而 SDK 那边照样会去读——
    /// 于是又变回静默接管。这个名字是整条可见性链的第一个环节。
    /// </summary>
    [Fact]
    public void EnvVar_MatchesTheNameTheSdkReads()
    {
        Assert.Equal("ANTHROPIC_BASE_URL", EndpointResolver.EnvVar);
    }
}

using Lilara.Core.Adapters;

namespace Lilara.Core.Tests;

// 判据是「环境变量赢了必须说得清」，不是「别让它赢」；全程离线（环境是注入的）。
public class EndpointResolverTests
{
    // 假的环境读取器：只认那一个变量名。
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

    // 同一个 URL、两种来源，调用方必须能把它们分开。
    [Fact]
    public void Resolve_SameUrlFromDifferentSourcesIsDistinguishable()
    {
        var fromEnv = EndpointResolver.Resolve(null, Env("http://127.0.0.1:15721"));
        var configured = EndpointResolver.Resolve("http://127.0.0.1:15721", Env(null));

        Assert.Equal(fromEnv.Url, configured.Url);
        Assert.NotEqual(fromEnv.Source, configured.Source);
    }

    // 名字写错的话解析器以为「没人设过」，而 SDK 照样会读——又变回静默接管。
    [Fact]
    public void EnvVar_MatchesTheNameTheSdkReads()
    {
        Assert.Equal("ANTHROPIC_BASE_URL", EndpointResolver.EnvVar);
    }

    // ---- 每个协议族给自己的官方兜底 + 是否读环境 ----
    // 这三条都按名传参：officialUrl / envVar 排在 getEnv 之后，按位置传会把 Func 送进 string 形参（CS1503）。

    [Fact]
    public void Resolve_UsesTheCallersOfficialUrlAsFallback()
    {
        var resolved = EndpointResolver.Resolve(null, officialUrl: "https://api.openai.com", envVar: null);

        Assert.Equal("https://api.openai.com", resolved.Url);
        Assert.Equal(EndpointSource.Default, resolved.Source);
    }

    [Fact]
    public void Resolve_WithNullEnvVarReadsNoEnvironment()
    {
        // 即便环境里那变量有值，也不许它赢。
        var resolved = EndpointResolver.Resolve(
            null, officialUrl: "https://api.openai.com", envVar: null,
            getEnv: Env("http://127.0.0.1:15721"));

        Assert.Equal("https://api.openai.com", resolved.Url);
        Assert.Equal(EndpointSource.Default, resolved.Source);
    }

    [Fact]
    public void Resolve_ConfiguredStillWinsWithNullEnvVar()
    {
        var resolved = EndpointResolver.Resolve(
            "https://configured.example.com", officialUrl: "https://api.openai.com", envVar: null);

        Assert.Equal("https://configured.example.com", resolved.Url);
        Assert.Equal(EndpointSource.Configured, resolved.Source);
    }
}

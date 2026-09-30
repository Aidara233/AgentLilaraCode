namespace Lilara.Core.Adapters;

/// <summary>端点的来源——必须能说清「这个地址是谁定的」。</summary>
public enum EndpointSource
{
    /// <summary>providers/*.json 里显式配置的（最高优先）。</summary>
    Configured,

    /// <summary>环境变量 ANTHROPIC_BASE_URL（缺省时生效，但**必须可见**）。</summary>
    Environment,

    /// <summary>官方地址（兜底）。</summary>
    Default,
}

public sealed record ResolvedEndpoint(string Url, EndpointSource Source);

/// <summary>
/// 端点解析：**程序内配置 &gt; 环境变量 &gt; 官方地址**。
///
/// ⚠️ 判据是「**永不静默接管**」，不是「永不接管」。环境变量缺省时生效是允许的
/// ——很多机器（包括开发机）正是靠它把流量导向本地代理的，硬掰回官方地址会直接把
/// 一个能用的环境弄坏（实测：把本地代理忽略掉之后，`PROXY_MANAGED` 这个占位串会被
/// 当成真 key 发往官方地址，换来一个 401）。
/// 要的是：赢了的时候**说得出来它赢了**，所以返回值带着 <see cref="EndpointSource"/>，
/// 调用方必须把它显示出来。
///
/// ⚠️ 官方地址只能在这里写一次：散落成多份的话，改了一处另一处还是旧的。
/// </summary>
public static class EndpointResolver
{
    /// <summary>⚠️ 必须与 SDK 实际去读的那个名字一致，否则又变回静默接管。</summary>
    public const string EnvVar = "ANTHROPIC_BASE_URL";

    public const string OfficialUrl = "https://api.anthropic.com";

    /// <summary>
    /// ⚠️ 环境读取器是**注入**的：真实进程环境不可控，注入之后这张真值表才能离线测干净。
    /// </summary>
    public static ResolvedEndpoint Resolve(
        string? configuredEndpoint, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
            return new ResolvedEndpoint(configuredEndpoint, EndpointSource.Configured);

        var fromEnv = getEnv(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new ResolvedEndpoint(fromEnv, EndpointSource.Environment);

        return new ResolvedEndpoint(OfficialUrl, EndpointSource.Default);
    }
}

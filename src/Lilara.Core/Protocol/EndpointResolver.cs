namespace Lilara.Core.Protocol;

public enum EndpointSource
{
    /// <summary>providers/*.json 里显式配置的（最高优先）。</summary>
    Configured,

    /// <summary>环境变量 ANTHROPIC_BASE_URL（缺省时生效）。</summary>
    Environment,

    /// <summary>官方地址（兜底）。</summary>
    Default,
}

public sealed record ResolvedEndpoint(string Url, EndpointSource Source);

// 端点解析顺序：程序内配置 > 环境变量 > 官方地址。
// 判据是「永不静默接管」而非「永不接管」——环境变量缺省时生效是允许的（很多机器靠它把流量导向本地代理），
// 但要说得出来它赢了，所以返回值带 EndpointSource，调用方必须显示出来。
// 官方地址只能在这里写一次。
public static class EndpointResolver
{
    /// <summary>
    /// 端点来源的中文标签。**与 <see cref="EndpointSource"/> 的定义同处一地**——
    /// 照 <c>Axes.LabelOf</c> 的先例（标签住在定义旁边）。控制台与 dispatcher 都要印它，
    /// 各写一份会漂（改一个来源名而漏改一处，用户看到的理由就静默错了）。
    /// </summary>
    public static string Describe(EndpointSource source) => source switch
    {
        EndpointSource.Configured => "来自 provider 配置",
        EndpointSource.Environment => "来自环境变量",
        _ => "官方地址",
    };

    /// <summary>必须与 SDK 实际去读的那个名字一致，否则又变回静默接管。</summary>
    public const string EnvVar = "ANTHROPIC_BASE_URL";

    public const string OfficialUrl = "https://api.anthropic.com";

    /// <summary>
    /// envVar 传 null = 本协议族不读任何环境变量。
    /// officialUrl 让每个协议族给自己的官方兜底地址，免得 OpenAI 的请求兜底到 Anthropic 的地址。
    /// getEnv 是第 2 个形参，新增参数只能按名传。
    /// </summary>
    public static ResolvedEndpoint Resolve(
        string? configuredEndpoint,
        Func<string, string?>? getEnv = null,
        string officialUrl = OfficialUrl,
        string? envVar = EnvVar)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
            return new ResolvedEndpoint(configuredEndpoint, EndpointSource.Configured);

        // envVar 为 null = 本协议族不读任何环境变量。
        if (envVar is not null)
        {
            var fromEnv = getEnv(envVar);
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return new ResolvedEndpoint(fromEnv, EndpointSource.Environment);
        }

        return new ResolvedEndpoint(officialUrl, EndpointSource.Default);
    }
}

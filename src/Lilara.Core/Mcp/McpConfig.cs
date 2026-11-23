namespace Lilara.Core.Mcp;

/// <summary>
/// **MCP 的全局配置**（`&lt;数据根&gt;/mcp.json`，与 `shell.json` / `network.json` 同形）。
///
/// <para>⚠️ <b>它是设备级的，不是会话级的</b>：一个 MCP server 是**这个进程**要拉起的子进程
/// （或连出去的 HTTP 端点），生命周期随核心，不随会话。哪个会话能用哪些工具，
/// 今天由 `tool.setEnabled` 回答——而它还没上线（`01` §14 #24），所以 MCP 工具进全表。</para>
///
/// <para>⚠️ <b>`Enabled = false` 是默认值，而且是唯一安全的那一侧</b>：stdio 传输是
/// **任意代码执行**，比 `shell` 更隐蔽（用户看不见它跑了什么）。⇒ 配一个 server 只是
/// 「登记」，第一次真的拉起它必须是一次显式动作（控制面的 `mcp.test` / 打开总开关）。
/// 这与 `network.json` 的默认值同向：新装的核心不该在一次默认会话里悄悄出网 / 起进程。</para>
/// </summary>
public sealed record McpConfig
{
    public int Version { get; init; } = Kernel.Json.FormatVersion;

    /// <summary>总开关。关着时一个子进程也不起、一个请求也不发。</summary>
    public bool Enabled { get; init; }

    public IReadOnlyList<McpServerConfig> Servers { get; init; } = [];
}

/// <summary>
/// 一个 MCP server 的登记项。
///
/// <para>⚠️ <b>`ToolPermission` 存的是<b>字符串</b>，不是 <see cref="Kernel.PermissionClass"/></b>：
/// 配置是用户可以手改的，而一个拼错的类别名在这里**不能被静默丢掉**——
/// 那会让「我以为它被标成了只读」变成一次真的自动放行。⇒ 解析发生在使用点，
/// 解析失败会被记进快照的 `problems`，并按默认类别（`Writing`）跑。</para>
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>插件级 id（小写字母 / 数字 / 连字符），同时是工具名的前缀与日志里的名字。</summary>
    public string Id { get; init; } = "";

    /// <summary>单个 server 的开关。**默认关**：登记只登记；必须显式启用才会拉起子进程。</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 传输。支持 `stdio` 与 `http`（MCP Streamable HTTP）；未知值会保留并明确报错。
    /// </summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>stdio 的可执行文件。⚠️ 不走 shell；PATH 解析由 `McpHost` 负责。</summary>
    public string Command { get; init; } = "";

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>HTTP transport 的 Streamable HTTP endpoint。只在 `Transport = "http"` 时使用。</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// 环境变量名，变量值作为 Bearer token 发送。⚠️ token 本身绝不落盘，也不进快照/日志。
    /// 使用认证的 HTTP endpoint 必须为 HTTPS。
    /// </summary>
    public string? BearerTokenEnvironmentVariable { get; init; }

    /// <summary>
    /// 显式允许 endpoint 解析到 loopback / 私网 / link-local。默认 false。
    /// 开启等于用户明确授权连接内网地址；云 metadata 地址仍会被拒绝。
    /// </summary>
    public bool AllowPrivateNetwork { get; init; }

    /// <summary>子进程的工作目录。空 ⇒ 核心的工作目录。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// 子进程的环境变量。⚠️ **不继承父进程环境**（`InheritEnvironmentVariables = false`）：
    /// 那会把核心的 API key 一起交给一个第三方 server。只有这里列出的才传。
    /// 值支持 `${NAME}` 形式的引用，由 `McpHost` 从**当前进程环境**解析——
    /// 于是密钥不必写进 JSON（配置会被头显示、会被日志带走）。
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>一次 `tools/call` 的时限（秒）。超时发取消并回一句人话，不挂死一步。</summary>
    public int TimeoutSeconds { get; init; } = McpLimits.DefaultTimeoutSeconds;

    /// <summary>
    /// 工具名 → 权限类别（字符串）。未列出的工具取默认 <see cref="Kernel.PermissionClass.Writing"/>
    /// （`04` §5 已定：MCP 声明不了自己的类别，不声明就打中档）。
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolPermission { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// MCP 的硬上限。⚠️ 与 `NetworkLimits` / `ShellLimits` 同一条纪律：
/// **数值只有这一份**，配置层夹紧、控制面命令拒绝越界。
/// </summary>
public static class McpLimits
{
    /// <summary>登记项最多几个。防一份手滑的配置把启动拖成几十次进程拉起。</summary>
    public const int MaxServers = 32;

    /// <summary>单个 server 最多贡献几个工具。超了**明确失败**，不截断（截断是假信息）。</summary>
    public const int MaxToolsPerServer = 128;

    /// <summary>单个工具的描述最多多少字节。它进的是前缀最前端，按字节算不是按字数。</summary>
    public const int MaxDescriptionBytes = 16 * 1024;

    /// <summary>单个工具的参数 schema 最多多少字节（原文，不改写）。</summary>
    public const int MaxSchemaBytes = 64 * 1024;

    /// <summary>子进程 stderr 保留最后几行（诊断用；不无限增长）。</summary>
    public const int MaxStderrLines = 200;

    /// <summary>默认调用时限（秒）。</summary>
    public const int DefaultTimeoutSeconds = 60;

    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 600;

    /// <summary>一次 `tools/list` 最多等多久（秒）。握手与列举共用这个上限。</summary>
    public const int ConnectTimeoutSeconds = 30;

    /// <summary>掉线之后，下一次调用**不重连**：工具仍在表里，调用只回一句「不在线」。</summary>
    public const string OfflineSuffix = "（MCP 服务当前不在线：工具仍在表里，调用被拒；修好后用 `mcp.reload` 重连。）";
}

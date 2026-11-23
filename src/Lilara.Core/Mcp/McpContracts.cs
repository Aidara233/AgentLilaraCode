using Lilara.Core.Kernel;

namespace Lilara.Core.Mcp;

/// <summary>
/// MCP 的中立结果类型——**SDK 的类型到此为止，不许往上漏**
/// （`04` §1.2 的镜像判据：进来的能力是用户的形状，而「用户的形状」是我们的契约，不是 SDK 的）。
///
/// <para>⚠️ 它们刻意不是 <see cref="Block"/>：映射到块需要 <see cref="IToolContext"/>
/// （图片要先进库），而那件事只有工具做得到。⇒ 宿主只交事实，工具做投影。</para>
///
/// <para>⚠️ <b>类型名带 <c>Mcp</c> 前缀、且不嵌套</b>：嵌套在 <see cref="McpContent"/> 里时，
/// 一个叫 <c>Text</c> 的嵌套类型会与 <see cref="McpResource"/> 的 <c>Text</c> 参数撞名
/// （CS8866），而那个错误看起来完全不像命名问题。</para>
/// </summary>
public abstract record McpContent;

public sealed record McpText(string Value) : McpContent;

public sealed record McpImage(byte[] Data, string MimeType) : McpContent;

/// <summary>嵌入资源。⚠️ <c>Text</c> 与 <c>Bytes</c> 恰有一个非空（MCP 的形状）。</summary>
public sealed record McpResource(string? Text, byte[]? Bytes, string? MimeType, string? Uri) : McpContent;

/// <summary>结构化结果。⚠️ 序列化成 JSON 文本交给模型——不丢弃，也不假装它是文本。</summary>
public sealed record McpStructured(string Json) : McpContent;

/// <summary>
/// 这次结果里有一种我们今天不认识的块（audio / resource_link / 未来的新类型）。
/// ⚠️ **必须说出来**：静默丢掉它，模型会以为服务端只回了那些它看得见的块。
/// </summary>
public sealed record McpUnsupported(string Kind) : McpContent;

/// <summary>一次工具调用的结果。⚠️ <c>IsError</c> 也是内容（`ToolOutcome` 的判据）。</summary>
public sealed record McpCallResult(IReadOnlyList<McpContent> Content, bool IsError)
{
    /// <summary>非 null ⇒ 调用**没有真的发生**（掉线 / 超时 / 服务端拒绝），正文是这句理由。</summary>
    public string? Failure { get; init; }

    public static McpCallResult Fail(string reason) => new([], false) { Failure = reason };
}

/// <summary>快照里的一条工具。`Permission` 是**生效的**类别（配置解析失败时已经是兜底值）。</summary>
public sealed record McpToolView(
    string Name, string RemoteName, string Description, PermissionClass Permission);

/// <summary>快照里的一个 server。⚠️ 控制面与头都读这一份，不各自拼。</summary>
public sealed record McpServerView(
    string Id,
    bool Enabled,
    string Transport,
    string? Endpoint,
    bool HasBearerToken,
    string? BearerTokenEnvironmentVariable,
    bool AllowPrivateNetwork,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, string> ToolPermission,
    string State,
    string? Error,
    IReadOnlyList<McpToolView> Tools,
    IReadOnlyList<string> StderrTail);

/// <summary>
/// MCP 的整份快照。⚠️ `Problems` 放**配置层**的问题（id 非法、transport 不支持、
/// 权限类别拼错、环境变量引用未定义），与某个 server 的运行时错误分开：
/// 前者是「你的配置有问题」，后者是「它现在连不上」。
/// </summary>
public sealed record McpView(
    bool Enabled,
    IReadOnlyList<McpServerView> Servers,
    IReadOnlyList<string> Problems);

/// <summary>`mcp.test` 的返回值。⚠️ 它**不改主机表**——那是 `mcp.reload` 的事。</summary>
public sealed record McpTestResult(
    string Id, bool Ok, IReadOnlyList<string> Tools, IReadOnlyList<string> Problems, string? Error);

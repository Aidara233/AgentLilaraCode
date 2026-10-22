using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `web_fetch`——「打开这一页看看」。与核心内建的 `fetch` **不是一回事**：
/// 那个取回的是**你自己历史里的旧工具结果**，这个取回的是**公网上的网页**。
/// 名字因此分开（`web_` 前缀），不靠参数区分子行为——那正是 `fetch` 的注释里
/// 反对过的「让模型先判断这个工具有没有另一个模式」。
///
/// <para>⚠️ <b>安全边界全在传输层</b>（`HttpNetworkTransport`）：这个工具只判
/// 「联网轴 / 配置 / 参数」，不自己解析地址、不自己跟重定向。</para>
/// </summary>
public sealed class WebFetchTool : ITool
{
    public const string NameOf = "web_fetch";

    public string Name => NameOf;

    /// <summary>消耗型：读网络，不改本机状态（`01` §3.5）。</summary>
    public PermissionClass PermissionClass => PermissionClass.Consuming;

    public string Description =>
        "打开一个 http / https 网页，返回抽取出来的正文（HTML 会去掉标签，不是原始源码）。"
        + "只用于看网页内容；想找网页，用 web_search。"
        + "本机 / 内网地址默认被拒绝；非白名单域名只允许 80 / 443 端口。"
        + "受 `network` 轴与核心联网配置约束；被拒绝时会说明是哪一道闸。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "要打开的绝对 URL（http 或 https）。"
            }
          },
          "required": ["url"]
        }
        """;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var url = ToolArgs.String(call.ArgumentsJson, "url");

        if (string.IsNullOrWhiteSpace(url))
            return ToolText.Of("缺少 `url` 参数。要打开哪个网页？");

        if (context.Network is not { } network)
            return ToolText.Of("这个会话没有联网上下文，抓取不可用。");

        if (network.DenialFor(NetworkCapability.Fetch) is { } denial)
            return ToolText.Of(denial);

        NetworkFetchResult result;
        try
        {
            result = await network.Transport.FetchAsync(network.Config, url.Trim(), ct);
        }
        catch (NetworkException ex)
        {
            return ToolText.Of($"抓取失败：{ex.Message}");
        }

        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(result.Title))
            sb.Append(result.Title.Trim()).Append('\n');

        sb.Append("URL：").Append(result.FinalUrl).Append('\n');
        if (!string.Equals(result.RequestedUrl, result.FinalUrl, StringComparison.Ordinal))
            sb.Append("（从 ").Append(result.RequestedUrl).Append(" 重定向而来）\n");

        sb.Append("状态：").Append(result.StatusCode)
          .Append(" · ").Append(result.ContentType ?? "未知类型")
          .Append(" · ").Append(Size(result.BytesRead)).Append('\n');

        var isHtml = result.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
        if (isHtml)
            sb.Append("（正文由 HTML 抽取；链接只剩文字，表格会变成一串行。）\n");

        sb.Append("---\n");
        sb.Append(result.Text.Length == 0 ? "（没有可读的文本内容。）" : result.Text);

        if (result.Truncated)
        {
            sb.Append("\n\n（已截断：只读了前 ").Append(Size(result.BytesRead))
              .Append("。要看更多，换一个更小的页面，或让用户在联网配置里提高上限。）");
        }

        return new ToolOutcome
        {
            Content = [new Block.TextBlock(sb.ToString())],
            Framing = new ToolFraming(NameOf,
            [
                new ToolFramingField("url", result.FinalUrl),
                new ToolFramingField("状态", result.StatusCode.ToString()),
                new ToolFramingField("字节", result.BytesRead.ToString()),
            ]),
            // 依赖是 URL：下一步若同一个 URL 再取，内容变了是**世界变了**，不是缓存。
            DependsOn = result.FinalUrl,
            // 网络内容不可重演：没有指纹。
            Fingerprint = null,
        };
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };
}

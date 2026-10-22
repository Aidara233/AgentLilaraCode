using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `web_search`——「网上有没有这件事」。与 `ls` / `read` / `search` 那三个本地工具是**两个世界**：
/// 它们只看工作目录，这个看公网。
///
/// <para>⚠️ <b>它常驻工具表，哪怕联网关着</b>：工具表是前缀的一部分，
/// 「开着才出现」会让第一次拨轴全废一次前缀（同 `fetch` 那条）。关着时它只回一句人话，
/// 一个请求都不发。</para>
///
/// <para>⚠️ <b>三道闸都在这一个结果里说清</b>：联网轴不许、全局配置没开、没配 key——
/// 三种「不能跑」各有各的下一步，混成一句「搜索失败」会把模型引向重试同一个调用。</para>
/// </summary>
public sealed class WebSearchTool : ITool
{
    public const string NameOf = "web_search";

    public string Name => NameOf;

    /// <summary>消耗型：读网络，不改本机状态（`01` §3.5）。</summary>
    public PermissionClass PermissionClass => PermissionClass.Consuming;

    public string Description =>
        "在公网上搜索。返回若干条结果：标题、URL、摘要。"
        + "搜到的网页要看正文，用 web_fetch 打开其中一条的 URL。"
        + "受 `network` 轴与核心联网配置约束；被拒绝时会说明是哪一道闸。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "要搜索的内容。用关键词或一句话都行。"
            }
          },
          "required": ["query"]
        }
        """;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var query = ToolArgs.String(call.ArgumentsJson, "query");

        if (string.IsNullOrWhiteSpace(query))
            return ToolText.Of("缺少 `query` 参数。要搜什么？");

        if (query.Length > ToolLimits.MaxWebSearchQueryChars)
            return ToolText.Of(
                $"`query` 太长了（{query.Length} 字符，上限 {ToolLimits.MaxWebSearchQueryChars}）。用关键词再搜一次。");

        if (context.Network is not { } network)
            return ToolText.Of("这个会话没有联网上下文，搜索不可用。");

        if (network.DenialFor(NetworkCapability.Search) is { } denial)
            return ToolText.Of(denial);

        NetworkSearchResult result;
        try
        {
            result = await network.Transport.SearchAsync(network.Config, query, ct);
        }
        catch (NetworkException ex)
        {
            return ToolText.Of($"搜索失败：{ex.Message}");
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.Answer))
            sb.Append("摘要：").Append(result.Answer.Trim()).Append('\n');

        if (result.Hits.Count == 0)
        {
            if (sb.Length == 0) sb.Append($"没有找到与 \"{query}\" 相关的结果。");
        }
        else
        {
            for (var i = 0; i < result.Hits.Count; i++)
            {
                var hit = result.Hits[i];
                sb.Append(i + 1).Append(". ").Append(hit.Title.Trim()).Append('\n');
                sb.Append("   ").Append(hit.Url).Append('\n');
                if (!string.IsNullOrWhiteSpace(hit.Snippet))
                    sb.Append("   ").Append(Clip(hit.Snippet)).Append('\n');
            }
        }

        return new ToolOutcome
        {
            Content = [new Block.TextBlock(sb.ToString().TrimEnd())],
            Framing = new ToolFraming(NameOf,
            [
                new ToolFramingField("查询", query),
                new ToolFramingField("结果", result.Hits.Count.ToString()),
            ]),
            // ⚠️ 没有指纹：搜索结果随时间和地区变，属于「不可重演」那一类。
            // 别给一个「差不多」的值——假指纹会声称世界没动过。
            DependsOn = null,
            Fingerprint = null,
        };
    }

    /// <summary>摘要可能很长（Tavily 的 content 有时是整段）。一条结果只留开头一段。</summary>
    private static string Clip(string text)
    {
        var oneLine = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (oneLine.Contains("  ", StringComparison.Ordinal))
            oneLine = oneLine.Replace("  ", " ", StringComparison.Ordinal);

        return oneLine.Length <= ToolLimits.MaxSearchLineChars
            ? oneLine
            : oneLine[..ToolLimits.MaxSearchLineChars] + "…";
    }
}

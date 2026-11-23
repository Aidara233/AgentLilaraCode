using Lilara.Core.Kernel;

namespace Lilara.Core.Mcp;

/// <summary>
/// **一个远端 MCP 工具在我们这一侧的投影**（`ITool`）。
///
/// <para>⚠️ <b>它只实现「执行」一个钩子</b>：MCP 原语里没有渲染 / 优化 / 取消钩子
/// （`04` §4），那三个走核心侧默认策略——所以这个类**刻意很薄**：
/// 声明来自远端，行为是转发一次调用，结果做一次投影。</para>
///
/// <para>⚠️ <b>名字不是远端名字</b>（见 <see cref="McpNames"/>）：provider 的工具名
/// 不允许点、且只有 64 个字符。`RemoteName` 才是转发时用的那个。</para>
/// </summary>
public sealed class McpTool(
    McpHost host,
    string serverId,
    string remoteName,
    string name,
    string description,
    string schema,
    PermissionClass permissionClass,
    bool requiresNetwork) : ITool
{
    public string Name { get; } = name;

    public PermissionClass PermissionClass { get; } = permissionClass;

    public string Description { get; } = description;

    public string ParametersSchemaJson { get; } = schema;

    public string ServerId { get; } = serverId;

    /// <summary>远端工具名——`tools/call` 用的是它。</summary>
    public string RemoteName { get; } = remoteName;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        if (requiresNetwork)
        {
            var network = context.Network;
            if (network is null || !string.Equals(network.Level, "all", StringComparison.Ordinal))
            {
                var level = network?.Level ?? "未知";
                return new ToolOutcome
                {
                    Content = [new Block.TextBlock(
                        $"远程 MCP 工具需要 `network=all`，当前是 `{level}`。请先为该会话启用完整联网权限。")],
                    Framing = new ToolFraming(Name,
                    [new ToolFramingField("server", ServerId), new ToolFramingField("network", "denied")]),
                };
            }
        }

        var result = await host.CallAsync(ServerId, RemoteName, call.ArgumentsJson, ct).ConfigureAwait(false);

        var framing = new ToolFraming(Name,
        [
            new ToolFramingField("server", ServerId),
            new ToolFramingField("tool", RemoteName),
        ]);

        if (result.Failure is { } failure)
            return new ToolOutcome
            {
                Content = [new Block.TextBlock(failure)],
                Framing = framing with { Fields = [.. framing.Fields, new ToolFramingField("error", "true")] },
            };

        return new ToolOutcome
        {
            Content = Project(result.Content, context),
            Framing = result.IsError
                ? framing with { Fields = [.. framing.Fields, new ToolFramingField("error", "true")] }
                : framing,
        };
    }

    /// <summary>
    /// MCP 的 content 数组 → 我们的块。
    /// ⚠️ <b>不认识的块必须留一句话</b>：静默丢掉它，模型会以为服务端只回了那些它看得见的块
    /// ——那是一条**假信息**，比「这个类型暂不支持」坏得多。
    /// </summary>
    private IReadOnlyList<Block> Project(IReadOnlyList<McpContent> content, IToolContext context)
    {
        var blocks = new List<Block>();
        foreach (var item in content)
        {
            switch (item)
            {
                case McpText text:
                    blocks.Add(new Block.TextBlock(text.Value));
                    break;

                case McpImage image:
                    blocks.Add(new Block.ImageBlock(
                        context.EnsureStored(image.Data),
                        string.IsNullOrWhiteSpace(image.MimeType) ? "image/png" : image.MimeType));
                    break;

                case McpResource { Text: { } value }:
                    blocks.Add(new Block.TextBlock(value));
                    break;

                case McpResource { Bytes: { } bytes } resource:
                    blocks.Add(new Block.StoreBlock(
                        context.EnsureStored(bytes),
                        string.IsNullOrWhiteSpace(resource.MimeType)
                            ? "application/octet-stream" : resource.MimeType));
                    break;

                case McpResource resource:
                    blocks.Add(new Block.TextBlock(
                        $"（MCP 返回了一个空资源：{resource.Uri ?? "没有 uri"}）"));
                    break;

                case McpStructured structured:
                    blocks.Add(new Block.TextBlock("structuredContent: " + structured.Json));
                    break;

                case McpUnsupported unsupported:
                    blocks.Add(new Block.TextBlock(
                        $"（MCP 结果里有一种这里暂不支持的块：{unsupported.Kind}。内容没有丢，只是没有展示。）"));
                    break;
            }
        }

        return blocks;
    }
}

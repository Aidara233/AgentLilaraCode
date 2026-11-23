using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **工具表的装配点**——`内建 ⊕ 插件 ⊕ MCP`，排序与查重都在这一处（`04` §1.3 / §9.1）。
///
/// <para>⚠️ <b>为什么必须有这一层，而不是让调用方各自拼</b>：拼法只有一份，判据才只有一份。
/// 排序键是**名字的 ordinal 升序**（`04` §9.1），不是文化序、不是来源序、更不是
/// `tools/list` 的返回序（`01` §1931 已把「从容器里拿到的顺序」判为随机）。
/// 两条各拼一遍，前缀就会在某一台机器上静默地只剩「位置 0 破了」这一个症状。</para>
///
/// <para>⚠️ <b>重名必须在这里炸，而不是在执行期</b>（`04` §9.1）：`ToolExecutor` 的
/// `ToDictionary` 也会抛，但那时**请求早就带着两份定义发出去了**。⇒ 早的那一层赢，
/// 且报出**冲突的两个来源**（只说一个名字，用户不知道该摘谁）。</para>
/// </summary>
public static class ToolTable
{
    public static IReadOnlyList<ITool> Build(
        (string Source, IReadOnlyList<ITool> Tools) builtIn,
        (string Source, IReadOnlyList<ITool> Tools)? extra = null)
    {
        var sources = new List<(string Source, IReadOnlyList<ITool> Tools)> { builtIn };
        if (extra is { } value) sources.Add(value);

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var all = new List<ITool>();

        foreach (var (source, tools) in sources)
        {
            foreach (var tool in tools)
            {
                if (seen.TryGetValue(tool.Name, out var owner))
                    throw new InvalidOperationException(
                        $"工具名 '{tool.Name}' 冲突：{owner} 与 {source} 都产出了它。"
                        + "工具表在请求最前端，重名会让整个前缀失效；请改名（MCP 侧可换 server id 或工具名）。");

                seen.Add(tool.Name, source);
                all.Add(tool);
            }
        }

        return all.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
    }
}

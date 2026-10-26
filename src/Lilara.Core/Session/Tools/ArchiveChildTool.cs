using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `archive_child`——**把一个子会话从视野里收起**（2026-10-26）。
///
/// <para>⚠️ <b>它解决的是「孩子太多了」这个具体的毛病</b>：一个父会话派出去五六个孩子、
/// 逐个落定之后，尾块那一行「子会话 a 空闲 · b 空闲 · c 空闲 …」会**每步都出现**，
/// 而它此刻全是噪音——模型既不打算再跟它们说话，也不该为了「看看进度」去
/// `tell_child` 催一句（`CorePrompts.SubagentNorm` 那条约束堵的正是这个）。
/// 归档让那一行**变短**，而记录一条不少。</para>
///
/// <para>⚠️ <b>它是<b>可逆标记</b>，不是删除</b>：落在 <see cref="SessionSpec.ArchivedAt"/> 上
/// ——目录不动、历史不动、`ListSessions` 照列；控制台一句
/// <c>session archive &lt;id&gt; --unarchive</c> 就把它放回来。⇒ 它够不上「写」那一档的代价，
/// 判据与 `release` / `plan` 逐字相同：<b>工具不改任何东西，它只是说出一句话</b>，
/// 而写 spec 与发事件都在 `CommandDispatcher`（<see cref="ToolOutcome.Archive"/>）。</para>
///
/// <para>⚠️ <b>它不停止那个孩子</b>（与 `session archive` 的区别，如实写下）：归档只改
/// **视野**。一个还在跑的孩子归档之后照样跑完、照样把回报送进父的记录——
/// 而那是**对的**：停止推进有它自己的入口（父中断 = 整棵子树停），
/// 让「收起」顺手扼杀一份在飞的工作会让这两件事的语义搅在一起。</para>
/// </summary>
public sealed class ArchiveChildTool : ITool
{
    /// <summary>⚠️ 常量：与 <see cref="TellChildTool.NameOf"/> 的理由相同——名字只写一遍。</summary>
    public const string NameOf = "archive_child";

    public string Name => NameOf;

    /// <summary>
    /// **只读**：工具本身不写盘、不动别人的会话（见类型注释）。
    /// ⚠️ 落地的后果（写 <see cref="SessionSpec.ArchivedAt"/>）是核心的动作，
    /// 判据同 `release` 的线、`finish_goal` 的轴。
    /// </summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    /// <summary>
    /// ⚠️ **描述只说模型看不出来的三件事**（`00` §2.2）：① 它改变的是「你看得见什么」，
    /// 不是「它存不存在」；② 收起之后 `tell_child` 不再认它；③ 什么时候才该用
    /// （落定了、结论收下了）。**不写**「这会让你的上下文变短」那类关于读者记忆的断言。
    /// </summary>
    public string Description =>
        "把一个子会话从你的视野里收起：它不再出现在尾块那行子会话里，tell_child 也不再认它。"
        + "⚠️ 只收起、不删除——记录一条不少，人可以在界面上把它放回来。"
        + "适合：它已经落定、结论你已经收下，而且你不再需要跟它说话。"
        + "⚠️ 别用它去「催进度」：收起不会让它跑得更快，也不会收回还没到的回报。"
        + "`child` 必须是**你自己派生出来的**会话 id。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "child": {
              "type": "string",
              "description": "要收起的子会话 id（spawn 的结果里给过）。"
            }
          },
          "required": ["child"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var childId = ToolArgs.String(call.ArgumentsJson, "child");

        if (string.IsNullOrWhiteSpace(childId))
            return Task.FromResult(ToolText.Of("`child` 得给：收起哪一个子会话？"));

        // ⚠️ **没有派生上下文 ⇒ 如实说**，不猜（同 `spawn` / `tell_child`）。
        if (context.Subagents is not { } scope)
            return Task.FromResult(ToolText.Of("这个会话没有派生上下文，没有可收起的子会话。"));

        // ⚠️ **校验在产出之前**（结果一落盘就改不了）：判据是**扫描 `Children`**，
        // 不是「id 非空就行」——后者会让模型把**别人的**会话从视野里抹掉（越权，
        // 而且它自己看不出来）。⚠️ 已经归档的孩子**不在 `Children` 里**
        // （`SessionStore.ChildrenOf` 一处过滤），所以重复归档落在这里，说的是实话：
        // 「它不是你的子会话」——并且下面那句名单会告诉模型此刻还剩哪些。
        var known = scope.Children.FirstOrDefault(
            c => string.Equals(c.SessionId, childId, StringComparison.Ordinal));
        if (known is null)
        {
            var list = scope.Children.Count == 0
                ? "你现在没有（未归档的）子会话。"
                : "你的子会话：" + string.Join(" / ", scope.Children.Select(c => c.SessionId));
            return Task.FromResult(ToolText.Of($"`{childId}` 不是你的子会话，没有收起。{list}"));
        }

        return Task.FromResult(new ToolOutcome
        {
            Content =
            [
                new Block.TextBlock(
                    $"已把子会话 {childId} 从视野里收起。它不再出现在子会话那一行，也不再接受 tell_child；"
                    + "记录一条不少，人可以在界面上把它放回来。"),
            ],
            Archive = new ArchiveRequest(childId),
        });
    }
}

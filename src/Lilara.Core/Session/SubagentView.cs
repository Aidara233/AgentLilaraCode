using Lilara.Core.Kernel;

namespace Lilara.Core.Session;

/// <summary>
/// **派生关系在尾块里的那一行**（2026-10-24）。
///
/// <para>⚠️ <b>它进尾块而不是进记录</b>：子是「此刻有什么在跑」，不是「发生过什么」
/// ——与轴值同一个判据（`00` §2.4「状态进槽位、事件进时间线」）。写进 `history.jsonl`
/// 会让模型以为用户又说了句话，而且每变一次就多一条记录。</para>
///
/// <para>⚠️⚠️ <b>这一行必须是「低频」的，否则它会毁掉浮动上下文那条降噪</b>：
/// `Stepper.StepFloatingPair` 的判据是**尾块正文逐字比较**（变了才写新的一份
/// `floating.jsonl`）。⇒ **绝不**放步数、耗时、时间戳、最后一次活动时间——
/// 那些每步都变，等于每步写一份读数，而「每步一份」正是 2026-10-23 那批要消灭的东西。
/// 只放**状态迁移级**的信息（一个子会话一生大约变三四次）。</para>
///
/// <para>⚠️ <b>排序由 <c>SessionStore.ChildrenOf</c> 保证（ordinal 按 id）</b>，
/// 这里不再排一次：两处各排一遍的话，一处改了另一处不红。</para>
/// </summary>
public static class SubagentView
{
    /// <summary>
    /// 渲染成一行；没有派生关系时返回 <c>null</c>（⇒ 尾块里那一整行**不出现**，
    /// 而不是出现一个空的「子会话：」——同 `SystemBlock.Wrap` 那条
    /// 「空的不能长得像有内容」）。
    /// </summary>
    public static string? Line(SubagentScope? scope)
    {
        if (scope is null) return null;

        var parts = new List<string>(2);

        // ⚠️ 子会话也要知道「我是谁的子」：它无法从别处读到这件事（工具描述是通用的），
        // 而 `tell_parent` 的可用性正是由它决定的。一行 id 比一句散文便宜，也更好核对。
        if (scope.ParentSessionId is { } parent) parts.Add($"父会话 {parent}");

        if (scope.Children.Count > 0)
            parts.Add("子会话 " + string.Join(" · ", scope.Children.Select(
                c => $"{c.SessionId} {StatusWord(c.Status)}")));

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    /// <summary>
    /// 状态 → 一个词。⚠️ **只认磁盘上真有的那两个**（`SessionStatus` 的类型注释：
    /// `Running` 只写内存，句柄这里够不着）——未知取值原样吐出，不编。
    /// </summary>
    private static string StatusWord(string status) => status switch
    {
        SessionStatus.WaitingForInput => "等你回答",
        SessionStatus.Idle => "空闲",
        _ => status,
    };
}

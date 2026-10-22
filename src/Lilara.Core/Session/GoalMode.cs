using Lilara.Core.Kernel;

namespace Lilara.Core.Session;

/// <summary>
/// **goal 模式的唯一判据**——主动性轴是不是 `goal`（2026-10-22）。
///
/// <para>⚠️ <b>不另立「目标状态」字段</b>：`finish_goal` 的作用只是把轴拨回 `standard`
/// （用户原话），所以「有没有目标」就是「轴是不是 goal」。多存一份会让两者可能分叉，
/// 而分叉时没有哪一份能说自己是对的。</para>
///
/// <para>⚠️ <b>目标完成不自动停在「finish_goal 那一步」</b>：它是一次工具调用，
/// 普通工具循环会再走一步，让模型收尾总结——这是用户明确要的「多跑一步」。</para>
/// </summary>
public static class GoalMode
{
    /// <summary>这个会话此刻是否在 goal 模式。</summary>
    public static bool IsActive(SessionState state) =>
        state.Axes is { } axes
        && axes.TryGetValue(Axes.Initiative, out var value)
        && string.Equals(value, "goal", StringComparison.Ordinal);
}

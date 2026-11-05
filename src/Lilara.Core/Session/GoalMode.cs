using Lilara.Core.Kernel;

namespace Lilara.Core.Session;

/// <summary>
/// **主动性轴上「核心要不要自己再推一步」的判据**——`goal` 与 `iterate` 两档的工具。
///
/// <para>⚠️ <b>不另立「目标状态」字段</b>：`finish_goal` 的作用只是把轴拨回 `standard`
/// （用户原话），所以「有没有目标」就是「轴是不是 goal」。多存一份会让两者可能分叉，
/// 而分叉时没有哪一份能说自己是对的。</para>
///
/// <para>⚠️ <b>目标完成不自动停在「finish_goal 那一步」</b>：它是一次工具调用，
/// 普通工具循环会再走一步，让模型收尾总结——这是用户明确要的「多跑一步」。</para>
///
/// <para>⚠️ <b><see cref="IsActive"/> 与 <see cref="AutoContinues"/> 是两个问题，别合并</b>
/// （2026-11-03）：
/// <list type="bullet">
///   <item><see cref="IsActive"/> 答「`finish_goal` 算不算数」——**只有 `goal`**。</item>
///   <item><see cref="AutoContinues"/> 答「模型停回合、而队列空的时候要不要再推一步」
///   ——`goal` **与 `iterate`**。</item>
/// </list>
/// 把两者并成一个谓词会让 `iterate` 下 `finish_goal` 忽然生效（它明确是「绝不能调用」），
/// 或者让 `goal` 少一条出口——两个方向都不对。</para>
/// </summary>
public static class GoalMode
{
    /// <summary>这个会话此刻是否在 goal 模式（= 有没有一个模型自己声明完成的目标）。</summary>
    public static bool IsActive(SessionState state) =>
        state.Axes is { } axes
        && axes.TryGetValue(Axes.Initiative, out var value)
        && string.Equals(value, "goal", StringComparison.Ordinal);

    /// <summary>
    /// **这一档会自己一直推下去**（2026-11-03 用户裁定）：`goal` 或 `iterate`。
    ///
    /// <para>⚠️ <b>为什么 `iterate` 必须在这里</b>：那一档的语义是「围绕固定 T0 长期任务反复
    /// 规划与执行」。而普通工具循环只在「调了工具」时才续——模型一句「这一段做完了」的纯文本
    /// 收尾就会让整档**停在 Idle 等用户**，于是 `iterate` 实际退化成 `step`，
    /// 连它那句「按 T0 规划下一段」的一次性提醒都要等用户下次说话才说得出来（那时已经过期）。
    /// 实测（2026-11-03）：`iterate` + 连排三条纯文本回复 ⇒ 模型只被调 **1 次**。</para>
    ///
    /// <para>⚠️ <b>代价，用户明确接受</b>：`iterate` <b>没有模型侧出口</b>（`finish_goal` 在这一档
    /// 被拒，T0 也不可完成）⇒ 与 `goal` 一样不设步数上限。停下来只有三条路：
    /// <b>用户 interrupt</b>、<b>用户把主动性轴改回 `standard`</b>、<b>模型调用失败 / 拒答 / 空回复</b>。
    /// ⇒ 头与控制面必须让这个停法看得见（`Axes` 里 `iterate` 那条等级解释就在做这件事）。</para>
    /// </summary>
    public static bool AutoContinues(SessionState state) =>
        IsActive(state) || IsIterating(state);

    /// <summary>这个会话此刻是否在 `iterate` 档。</summary>
    public static bool IsIterating(SessionState state) =>
        state.Axes is { } axes
        && axes.TryGetValue(Axes.Initiative, out var value)
        && string.Equals(value, Axes.Iterate, StringComparison.Ordinal);
}

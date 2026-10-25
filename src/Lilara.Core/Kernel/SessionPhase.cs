namespace Lilara.Core.Kernel;

/// <summary>
/// 会话的**活动相位**——给头显示的四态。与 <see cref="SessionStatus"/> 是**两个问题**：
///
/// <para>⚠️ <b><see cref="SessionStatus"/> 是磁盘/内存的状态镜像</b>（`Idle` / `Running` /
/// `WaitingForInput`），而 `Running` 只进内存、从不落盘；它回答的是「这一刻有没有一步在跑」。
/// 每一步结束都会发一条 <c>session.state</c>（值是当时的 `State.Status`）——所以照它画，
/// 头会在每步之间看到一次 `Idle`，一步一闪。</para>
///
/// <para>⚠️ <b>本类型的四态回答的是「这一步在干什么」</b>，只在**真的换了活计**时才变：
/// 模型调用期间是 <see cref="Generating"/>，工具执行期间是 <see cref="Tools"/>，
/// 停下来等人是 <see cref="Waiting"/>，真的没有下一步了才是 <see cref="Idle"/>。
/// 判据**只在本核心里**（`Stepper` 知道模型调用的边界、`CommandDispatcher` 知道续推与否），
/// 头不许自己推——推出来的东西在核心改判据那天会静默分叉。</para>
///
/// <para>⚠️ 取值是**字符串字面量**（线上契约），不是 C# 枚举——理由同 <see cref="SessionStatus"/>：
/// 枚举会被序列化成 camelCase，与线上/磁盘两边的守卫打架。</para>
/// </summary>
public static class SessionPhase
{
    /// <summary>闲置：没有下一步了（循环真的停下 / 取消 / 失败）。</summary>
    public const string Idle = "Idle";

    /// <summary>模型在生成：请求已发出、回复还没回来（含重试等待）。</summary>
    public const string Generating = "Generating";

    /// <summary>工具在跑：回复里有 `tool_use`，正在执行它们（含等审批之外的一切耗时）。</summary>
    public const string Tools = "Tools";

    /// <summary>等你：挂起在一条询问上（`WaitingForInput` 的相位形态）。</summary>
    public const string Waiting = "Waiting";
}

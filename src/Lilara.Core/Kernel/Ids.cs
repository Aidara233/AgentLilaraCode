namespace Lilara.Core.Kernel;

/// <summary>
/// id 的生成。**全项目只此一处**——会话 id 与记录（`TimelineEvent`）id 是同一个形状。
///
/// <para>⚠️ **12 位小写 hex**（48 bit，约 2.8×10¹⁴）：这是既有会话 id 的宽度，不是新选的。
/// 它**不是**为了抗碰撞而取的安全宽度，是「人要看、要念、要写进 URL」与「够用」的折中。
/// ⇒ **别拿它当不可猜测的凭据**（它不是）。</para>
///
/// <para>⚠️ **判别式用不着全局唯一**——`TimelineEvent` 的 id 只在**一个会话的记录**里区分行，
/// 而会话 id 只在**一个数据根**里区分。48 bit 远超这两个范围的实际用量。</para>
/// </summary>
public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// **后台任务的 id**（2026-10-27）。⚠️ 它带 <c>job-</c> 前缀，而这是**判据不是装饰**：
    ///
    /// <para>会话 id 也是 12 位 hex，而「等待一个任务」（`wait`）要同时接受**两类**目标
    /// ——后台进程（`job-…`）与子会话（`…`）。⚠️ 两者格式相同的话，`wait("a1b2c3d4e5f6")`
    /// 根本分不出它指的是哪个 ⇒ 只能靠「先查任务表、再查子会话」的顺序猜，
    /// 而那个顺序一旦猜错，模型等的是一个**永远不会结束**的东西。</para>
    ///
    /// <para>⚠️ 前缀与 <see cref="Injected.JobPrefix"/> **是同一个字符串**：注入那一对的 id
    /// 就是「<c>job-…</c> 加后缀」，两处各拼一遍会漂（而漂了的表现是「通知的 id 与任务的 id
    /// 对不上」，模型据此去 `job_output` 会查无此人）。</para>
    /// </summary>
    public static string NewJob() => Injected.JobPrefix + Guid.NewGuid().ToString("N")[..12];
}

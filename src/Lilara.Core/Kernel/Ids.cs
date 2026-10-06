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
}

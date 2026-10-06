namespace Lilara.Core.Kernel;

/// <summary>
/// 取时间的**唯一**入口。`src/` 里除本文件外不许出现 <c>DateTime.Now</c> / <c>DateTimeOffset.UtcNow</c>
/// ——守卫在 <c>ArchitectureTests.Clock_IsOnlyReadThroughCoreClock</c>。
///
/// <para>⚠️ <b>它刻意只是一个静态方法，不是注入的 <c>TimeProvider</c> 参数。</b>选它之前核过：
/// 今天**没有任何一个测试需要假时钟**（测试自己用 <c>DateTimeOffset.UtcNow</c>，且不断言精确时刻）
/// ⇒ 注入到 <c>Stepper</c> / <c>SessionHandle</c> / <c>CoreHost</c> / <c>CommandDispatcher</c>
/// 会牵动约 50 个构造点，那是**为零收益付噪音**。
/// ⚠️ 而且它有个更坏的后果——<b>默认值会写在多处</b>：
/// 每个可选参数各写一次 <c>TimeProvider? = null</c>，就多一个会漂的地方
/// （`EndpointResolver` 与 `Axes.LabelOf` 都是「默认值只许有一处出处」的既有先例）。</para>
///
/// <para>⚠️ <b>但也别把它读成「将来也不注入」。</b>本方法是**那个将来**的落点：
/// 一旦某个测试真的需要固定时刻，改的只有这一个函数（以及它下面那 9 个调用点，
/// 全是机械替换）——**而不是先铺 50 个构造点再发现没人用**。</para>
///
/// <para>⚠️ <b>本文件曾是零行的缺口</b>，而它不是「差一个工具」：`01` §8 的落盘布局里
/// <c>createdAt</c> / <c>archivedAt</c> / <c>at</c> / <c>claims[].at</c> 全是时间，
/// 而**新增字段的默认值是「现在」时，反序列化路径根本不经过那个默认值**
/// （STJ 不走构造器）⇒ 「缺失 = 取当前时刻」是一条**从来不存在**的规则。
/// 先把入口收成一处，才谈得上给那条规则立条款。</para>
/// </summary>
public static class CoreClock
{
    /// <summary>当前时刻，**UTC**。位移（本地时区）是显示层的事，不是这里的。</summary>
    public static DateTimeOffset Now() => DateTimeOffset.UtcNow;
}

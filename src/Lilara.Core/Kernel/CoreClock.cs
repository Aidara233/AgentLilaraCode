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

    /// <summary>
    /// **心跳文件的路径**（数据根下的一个空文件）。
    ///
    /// <para>⚠️ <b>它的存在只回答一个问题：「上一次那个进程是<b>正常退出</b>的吗」</b>
    /// ——正常退出（`quit` / Ctrl-C）会删掉它；**强杀 / 断电不会**。
    /// ⇒ 启动时「文件在不在」就是那条判据，而它**不需要**任何额外记录。</para>
    ///
    /// <para>⚠️ <b>为什么不能只靠「进程根本不在了」</b>：核心是唯一的（数据根锁），
    /// 所以「没有别的核心在跑」是常态，判不出「它上一次是被杀死的」。</para>
    ///
    /// <para>⚠️ <b>它是 L2 派生数据</b>（`01` §8.0）：丢了（比如手工删掉）最多让下一次启动
    /// 多跑一次收尾扫描——**而那是无害的**（收尾幂等，见 `Recovery`）。</para>
    /// </summary>
    public static string HeartbeatPath(string dataRoot) => Path.Combine(dataRoot, ".lilara-running");
}

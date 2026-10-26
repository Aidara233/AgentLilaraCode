using System.Net;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

/// <summary>
/// **一次模型调用的重试策略**：哪些失败值得再发一次、两次之间等多久、最多几次。
///
/// <para>⚠️ <b>重试的是「那一次模型调用」，不是「那一步」</b>。循环住在
/// <see cref="CallAsync"/> 里，而它被 <see cref="Session.Stepper"/> 在**装配完请求之后、
/// 执行工具之前**调用 ⇒ 一次重试不碰记录、不碰 `Turn`、不碰工具副作用。
/// 反过来说，这也要求**请求本身是幂等的**——而它确实是：核心永远发全量 canonical
/// （`00` §6），同一个 <see cref="ModelRequest"/> 再发一次逐字节相同，不存在
/// Responses 那种「链状态走了一步」的问题（那条本轮压根没有实现）。</para>
///
/// <para>⚠️ <b>判据只有一条：<see cref="ModelCallFailedException.Transient"/></b>。
/// 这里**不解析人话、不认异常类型**——因为「可重试」这件事只有**唯一知道 HTTP 状态码的那一层**
/// （两个协议各自的适配器）判得出来，而它们翻成人话之后，状态码就没了。
/// ⇒ 适配器在抛出时打标，这里只问那个标志（本仓那条「两层判据必须共用一个谓词」的反向用法：
/// **产生判据的地方和读判据的地方不是同一层**时，把判据做成一个字段跟着异常走）。</para>
///
/// <para>⚠️ <b>什么不重试</b>：400 / 422（请求内容的问题，再发一次也一样）、
/// 401 / 403（key 问题）、404（模型 / 端点写错），以及用户取消（<see cref="Cancellation"/>
/// 那条判据在适配器里就已经把取消放行了，它根本不会变成 <see cref="ModelCallFailedException"/>）。
/// 瞬态那一侧就是 <see cref="IsTransientStatus"/> 那张表：408 / 425 / 429 / ≥500。</para>
///
/// <para>⚠️ <b>没有读 `Retry-After`</b>：Claude SDK 的异常只交出 `StatusCode` 与 `ResponseBody`
/// （本地复现过：反射 `AnthropicApiException` 的公开属性只有这两样 + `InnerException`），
/// 拿不到响应头 ⇒ 只给 OpenAI 那条链路实现会让两条协议的重试节奏分叉。
/// 代价写在这里：目前不读取上游的等待建议；本地退避按指数增长，十次重试的等待之和约 8 分 32 秒，然后如实报失败。</para>
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// **最多重试几次**（用户裁定，2026-10-21：十次）。⚠️ 它是「重试」的次数，不是总尝试次数——
    /// 一次全部失败 = **11 次调用**（首发 + 10 次重试）。
    /// </summary>
    public const int MaxRetries = 10;

    /// <summary>第一次重试前的基础等待。</summary>
    internal const int BaseDelayMilliseconds = 500;

    /// <summary>
    /// 这个失败值不值得再发一次。⚠️ **只认标志**，别在这里按消息内容猜
    /// （那会让「上游 5xx。这是临时的」这句话变成一个被字符串匹配的协议）。
    /// </summary>
    public static bool ShouldRetry(Exception ex) =>
        ex is ModelCallFailedException { Transient: true };

    /// <summary>
    /// HTTP 状态码的瞬态判据。⚠️ **两条协议共用这一张表**（`OpenAiHttp` 与 `ClaudeAdapter`
    /// 都调它）——各判一份的漂法很具体：一条协议把 429 当临时、另一条忘了。
    ///
    /// <para>408 / 425 也收：它们是「这一次没赶上」，与 429 / 5xx 同一类。
    /// ⚠️ 409 / 422 不收——那是「请求内容的问题」，再发一次不会变。</para>
    /// </summary>
    public static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        // 425 没有具名成员；写成整型模式，与「≥500」共处一个 `is`。
        || (int)status is 425 or >= 500;

    /// <summary>
    /// 第 <paramref name="retry"/> 次重试**之前**等多久（<paramref name="retry"/> 从 1 起）。
    ///
    /// <para>指数退避从 0.5 秒开始，每次翻倍，再乘一个确定性的 ±20% 抖动；不设等待上限。
    /// 抖动不用随机数，同一个重试序号永远给出相同值，便于测试并错开多个会话的重试节奏。</para>
    /// </summary>
    public static TimeSpan DelayBefore(int retry)
    {
        if (retry < 1) throw new ArgumentOutOfRangeException(nameof(retry), retry, "重试序号从 1 起。");

        // 十次重试的生产路径远低于 TimeSpan 上限；饱和处理也让此公开方法对大序号保持安全。
        var exponent = Math.Min(retry - 1, 62);
        var backoff = BaseDelayMilliseconds * Math.Pow(2, exponent);
        var milliseconds = Math.Min(TimeSpan.MaxValue.TotalMilliseconds,
            backoff * (0.8 + 0.4 * Spread(retry)));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// 重试用尽之后的那个异常：**保留原话，再补上「试了几次」**。
    ///
    /// <para>⚠️ 不补那句话的话，用户看到的与「根本没重试」一模一样——而两者的下一步完全不同
    /// （前者该去查上游，后者该去查配置）。</para>
    ///
    /// <para>⚠️ <b>交出去的这一份 `Transient == false`</b>：它已经被重试过了，
    /// 任何外层的重试循环（今天没有，但别让它长出来）都**不该再套一层**。
    /// 这是结构性的保证，不是一条要靠读注释记住的约定。</para>
    /// </summary>
    public static ModelCallFailedException Exhausted(Exception last) =>
        new($"{last.Message}（已自动重试 {MaxRetries} 次，仍然失败）", last);

    /// <summary>
    /// 一次**失败了的请求**（给记账用，见 <see cref="CallAsync"/> 的 <c>onFailedAttempt</c>）。
    /// ⚠️ 时刻由循环自己取：每个真实请求各有各的窗口，而调用方在外面取不到它们。
    /// </summary>
    public readonly record struct FailedAttempt(
        DateTimeOffset StartedAt, DateTimeOffset EndedAt, int Index, Exception Failure);

    /// <summary>
    /// 一次**成功**的调用。⚠️ <paramref name="StartedAt"/> 是**成功那一次请求**的开始时刻
    /// （不是整串的起点）：账本里那一行的窗口就该是真正发出并拿到回复的那一次。
    /// ⚠️ 成功的这一行**由调用方记**（见 <c>onFailedAttempt</c> 的契约）：只有它知道
    /// 「这个回复能不能被用」。⚠️ 2026-10-22 起压缩不再走这条路（它是一次普通步里的
    /// `compact` 工具调用），这里的「能不能用」只剩普通步的含义。
    /// </summary>
    public sealed record Outcome(ModelReply Reply, DateTimeOffset StartedAt, int Attempts);

    /// <summary>
    /// **重试循环本身**——两个调用点（`Stepper.StepAsync` / `provider.test` 的探针）共用这一份。
    /// ⚠️ 2026-10-22 之前还有第三个（`Stepper.CompactAsync`）；压缩改成工具之后，
    /// 「被限流」重新只经过 `StepAsync` 这一处。
    ///
    /// <para>⚠️ <b>失败不是瞬态 ⇒ 原样抛</b>（不包、不改消息）：调用方对那几种失败的措辞
    /// 全都是一句能直接给用户看的人话。
    /// ⚠️ <b>等待期间的取消原样冒出去</b>（`OperationCanceledException`，token 是调用方那个）
    /// ——`CommandDispatcher` 靠它把「用户中断」与「失败」分开，混了的话每次中断都会变成一条红色告警。</para>
    ///
    /// <para>⚠️ <paramref name="delay"/> 只为**可测性**存在：生产路径不传，走真实等待；
    /// 测试传一个立即返回的委托，否则「十次退避」会把测试拖成四十多秒。</para>
    ///
    /// <para>⚠️ <b>记账的分工（「每条请求恰好一行账」）</b>：<paramref name="onFailedAttempt"/>
    /// 收下**每一次失败**（瞬态、非瞬态、被取消都算）——非瞬态与取消会原样抛出去，
    /// 所以**这一钩子是它们**在账本里留痕的唯一机会；而**成功那一次由调用方在拿到
    /// <see cref="Outcome"/> 之后记**，因为只有它知道那个回复能不能用。
    /// 两条路合起来，账本的条数 = 上游真正被打的次数，不多也不少。
    /// ⚠️ 唯一的空白是「两次重试之间的等待里被取消」：那时没有新请求，上一行账已经在。</para>
    ///
    /// <para>⚠️ <b><paramref name="onRetry"/> 与 <paramref name="onFailedAttempt"/> 是两件事</b>：
    /// 前者**只在真的要再试一次时**、在**等待之前**、带着**即将等待多久**调一次（给「头要看见重试」
    /// 这种报告用）；后者每一次失败都调（记账）。⚠️ 调用方**不要**自己再判一遍「要不要重试」——
    /// 那个判据只有这里一处（`ShouldRetry` + `MaxRetries`），各判一份就会漂成
    /// 「账本说重试了、事件说没有」。</para>
    /// </summary>
    public static async Task<Outcome> CallAsync(
        IModelAdapter adapter, ModelRequest request, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<FailedAttempt>? onFailedAttempt = null,
        Action<FailedAttempt, TimeSpan>? onRetry = null)
    {
        delay ??= static (d, token) => Task.Delay(d, token);

        for (var retry = 0; ; retry++)
        {
            var startedAt = CoreClock.Now();
            ModelReply reply;
            try
            {
                reply = await adapter.CallAsync(request, ct);
            }
            catch (Exception ex)
            {
                // ⚠️ **先记账，再判要不要重试**：最后那一次失败也要留痕，否则「重试用尽」
                // 的账本里少了真正决定去留的那一次。
                var failed = new FailedAttempt(startedAt, CoreClock.Now(), retry + 1, ex);
                onFailedAttempt?.Invoke(failed);

                if (!ShouldRetry(ex)) throw;
                if (retry >= MaxRetries) throw Exhausted(ex);

                // ⚠️ **报告在等待之前**：那条「马上要等 2 秒」对客户端的用处全在等待**期间**
                // （等待之后再说就是一句历史）。⚠️ 顺序也是刻意的：账先记、再报告、再等。
                var wait = DelayBefore(retry + 1);
                onRetry?.Invoke(failed, wait);
                await delay(wait, ct);
                continue;
            }

            return new Outcome(reply, startedAt, retry + 1);
        }
    }

    // 确定性的「散列」：把 retry 打散到 [0,1)。常数取自 splitmix64，够用即可——
    // 这里不需要均匀性，只需要「相邻两次不一样」。
    private static double Spread(int retry)
    {
        var x = unchecked((ulong)retry * 0x9E3779B97F4A7C15UL);
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (x >> 11) * (1.0 / (1UL << 53));
    }
}

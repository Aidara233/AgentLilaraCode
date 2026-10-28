using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// **「上次调用上下文总长度」——一份测量、一处文案**（2026-10-27）。
///
/// <para>⚠️ <b>它为什么是一个类型，而不是两段各自算的代码</b>：这句话今天有<b>两个</b>消费者——
/// ① 浮动读数里那一行（`SystemBlock` 的尾块，模型读它）；② 控制面
/// <c>GET /sessions/{id}/context</c>（头读它）。两处各算一遍的后果本仓已经栽过：
/// 尾块说「未知」而 `/context` 报出更早一次的值，两边都「看着对」，
/// 而用户手上没有任何办法判断该信哪一个。</para>
///
/// <para>⚠️ <b>取哪一条调用，判据只有这一处</b>：本会话**最后一次真实请求**
/// （<c>purpose=session</c>，按 <c>startedAt</c> 排序取末条）。它没报输入 ⇒ 报<b>未知</b>，
/// <b>不回退</b>到更早那条已知值——「上次调用的上下文长度」问的是那一次，
/// 用更早的冒充它就是在编一个数（`08` §4.1：没有可靠计数时返回 unknown）。</para>
///
/// <para>⚠️ <b>总数怎么算</b>：归一化输入（未命中）+ 缓存读取；Claude 的缓存写入是**独立输入部分**，
/// 另加；OpenAI 系的上游输入已含写入，加第二遍就把同一批 token 数两遍（格式 8 的口径，见 `08` §3.1）。
/// 旧账本行（<c>FormatVersion &lt; 8</c>）先过一遍归一，否则旧 OpenAI 行的「输入 + 缓存读取」会重复。
/// ⚠️ 输出不算进来：问的是「上下文有多长」，不是「这次花了多少」。</para>
/// </summary>
public static class ContextUsage
{
    /// <summary>没有可用读数时的那句话。⚠️ 它必须点出「为什么未知」，否则读者会去找一个不存在的产出。</summary>
    public const string UnknownLine = "上次调用上下文总长度：未知（尚无调用或上游未报告用量）。";

    /// <summary>
    /// 上下文到这条线就**必须**压（2026-10-27 用户裁定）：900k tokens。
    ///
    /// <para>⚠️ <b>它是一条与「主动性轴」「模型」都无关的硬线</b>：越过它之后，尾块里那句话从
    /// 「goal / iterate 下你可以自己决定压不压」变成命令式的「现在就压」，而 `compact` 的授权
    /// 也**不再看主动性轴**（见 <see cref="Tools.CompactTool.Parse"/>）。理由是这一格没有选择：
    /// 不压的话下一步就是上游的 400 / 截断，而那时连摘要都写不出来了。</para>
    ///
    /// <para>⚠️ <b>为什么是一个平值，而不是按模型的窗口算</b>（写清了才好改）：窗口是模型属性，
    /// 而这里是「离爆还有多少」的保守估计——900k 对 1M 窗口留了 10%。窗口小于 1M 的模型会先撞自己的
    /// 上限（那时上游会拒，尾块也早就把长度报出来了）。要改成「窗口的 90%」就得把窗口搬进判据，
    /// 那是另一件事，别在这里悄悄做。</para>
    /// </summary>
    public const long AutoCompactThreshold = 900_000;

    /// <summary>越过 <see cref="AutoCompactThreshold"/> 没有。<c>null</c>（不知道长度）⇒ <c>false</c>。</summary>
    public static bool OverLimit(long? total) => total >= AutoCompactThreshold;

    /// <summary>
    /// 上下文是否已越过那条硬线——**尾块那句命令与 `compact` 的授权读的是它**。
    ///
    /// <para>⚠️ <b>它刻意取的是「最近一次<b>报过</b>输入的调用」，不是「最近一次调用」</b>
    /// （2026-10-27 实测出来的差别）。理由是不对称的代价：
    /// 漏判「没超限」（该提示而没提示）会导致下一步上游报错 / 截断，那时连摘要都写不出来了；
    /// 误判「超限」（不该提示而提示）最坏只是让模型多做一次压缩。⇒ 这一格宁可偏保守。
    /// ⚠️ 具体触发它的现实情形：某次回复没报用量（`usage` 缺失）——按「最近一次调用」判的话，
    /// 安全带会在那一格**忽然松开**，而上下文并没有变小。</para>
    ///
    /// <para>⚠️ <b>它与 <see cref="Line"/> 的锚不同，这是有意的</b>：那一行是**事实陈述**
    /// （「上次调用」是多少就是多少，没报就说未知，绝不拿更早的值冒充）；这里是**安全判据**。
    /// 两个问题（「上次调用有多长」/「还压不压得动」）本来就该有两个答案。</para>
    /// </summary>
    public static bool OverLimit(UsageStore? usage, string sessionId) =>
        OverLimit(LastReported(usage, sessionId).Total);

    /// <summary>
    /// 本会话**最近一次报了输入**的调用，以及它在归一口径下的输入总量。
    /// ⚠️ 未知用量的行被跳过（见 <see cref="OverLimit(UsageStore?, string)"/> 里那段代价分析）。
    /// </summary>
    public static (UsageCall? Call, long? Total) LastReported(UsageStore? usage, string sessionId)
    {
        var calls = usage?.ForSession(sessionId);
        if (calls is null) return (null, null);
        for (var i = calls.Count - 1; i >= 0; i--)
        {
            if (TotalOf(calls[i]) is not { } total) continue;
            return (calls[i], total);
        }
        return (null, null);
    }

    /// <summary>
    /// 本会话**最后一次真实请求**，以及它在归一口径下的输入总量。
    /// <c>Total</c> 为 <c>null</c> ⇔ 那次调用没报输入（或本会话一次都没调过）。
    /// </summary>
    public static (UsageCall? Last, TokenUsage? Usage, long? Total) Measure(UsageStore? usage, string sessionId)
    {
        var last = usage?.ForSession(sessionId).LastOrDefault(c => c.Purpose == "session");
        if (last is null) return (null, null, null);
        // ⚠️ **不回退**到更早那条已知值：问的是「那一次」有多长，拿别的冒充就是编一个数。
        if (last.Usage is not { InputTokens: not null } recorded) return (last, null, null);
        return (last, Normalize(last, recorded), TotalOf(last, recorded));
    }

    /// <summary>那一行文案。⚠️ 措辞里的「含缓存、不含输出、非本次估算」是判据的一部分，别删。</summary>
    public static string Line(UsageStore? usage, string sessionId) =>
        Measure(usage, sessionId) is { Total: { } total }
            ? $"上次调用上下文总长度：{total} tokens（上游报告的总输入，含缓存；不含输出，非本次估算）。"
            : UnknownLine;

    /// <summary>一次的归一口径总输入；没报输入 ⇒ <c>null</c>。</summary>
    private static long? TotalOf(UsageCall call) =>
        call.Usage is { InputTokens: not null } recorded ? TotalOf(call, recorded) : null;

    private static long TotalOf(UsageCall call, TokenUsage recorded)
    {
        var usage = Normalize(call, recorded);
        return usage.InputTokens!.Value
               + (usage.CacheReadTokens ?? 0)
               // Claude 的缓存写入是独立输入部分；OpenAI 输入已含写入，加第二遍就把同一批数两遍。
               + (call.Protocol == Protocols.ClaudeMessages ? usage.CacheWriteTokens ?? 0 : 0);
    }

    /// <summary>⚠️ 归一化必须**按行**做（行上有 <c>formatVersion</c>），不能按「今天的格式」一把梭。</summary>
    private static TokenUsage Normalize(UsageCall call, TokenUsage recorded) =>
        call.FormatVersion < UsageCall.NormalizedInputSinceFormat
            ? UsageJson.NormalizeLegacyInput(recorded, call.Protocol)
            : recorded;
}

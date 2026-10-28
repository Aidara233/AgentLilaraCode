using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

/// <summary>
/// **前缀缓存的显式断点**（2026-10-28）：三族协议各自的「标记哪一段可复用」的方言，
/// 以及一处共用的**位置判据**。
///
/// <para>⚠️ <b>为什么需要它</b>：三族对「哪一段进缓存」的默认行为不一样——
/// <list type="bullet">
///   <item><b>Anthropic</b>：**没有隐式缓存**。只有标了 `cache_control` 的断点之前的内容才会被写进缓存
///   ⇒ 不标记就等于全程全价。</item>
///   <item><b>OpenAI</b>：默认隐式（≥1024 tokens、按 128 token 步进匹配前缀）；
///   **GPT-5.6 及更新**支持显式断点（`prompt_cache_breakpoint` + `prompt_cache_options.mode=explicit`），
///   更早的模型只认隐式。</item>
///   <item><b>DeepSeek / 兼容网关</b>：自动前缀缓存，没有断点概念（标了也没用）。</item>
/// </list>
/// ⇒ 断点是**按 provider 开关的能力**（`ProviderDefinition.CacheMode`），不是默认行为：
/// 默认 `auto` 时三条链路发出的字节与今天**逐字节相同**。</para>
///
/// <para>⚠️ <b>位置判据只有一处（本类）</b>：断点必须落在**每步都变的东西之前**。
/// 今天唯一每步变的是末尾那对浮动读数（id 以 `floating-` 开头）⇒
/// <see cref="LastStableIndex"/> 从末尾往前跳过它们。三族各自的 builder 只负责
/// 「把断点挂在那个事件上」，**不各自重新判断位置**——各判一遍就会漂。</para>
/// </summary>
public static class CacheBreakpoint
{
    /// <summary>不标记断点（默认）：交给上游的隐式缓存，或干脆不缓存。</summary>
    public const string Auto = "auto";

    /// <summary>标显式断点。⚠️ 只对支持它的端点有意义：Anthropic 全部、OpenAI GPT-5.6+。</summary>
    public const string Explicit = "explicit";

    /// <summary><see cref="ProviderDefinition.CacheMode"/> 的合法取值。</summary>
    public static readonly IReadOnlyList<string> Modes = [Auto, Explicit];

    /// <summary>
    /// 这条记录是不是**每步都在变**的注入（今天只有浮动读数那对）。
    ///
    /// <para>⚠️ 判据是 id 前缀 `floating-`：它是核心自己的约定（`Reclaim` / `Injected` 同一条），
    /// 而**不是**「内容看起来像读数」——后者会随文案改动漂。</para>
    /// </summary>
    public static bool IsVolatile(TimelineEvent evt) =>
        evt.Id.StartsWith("floating-", StringComparison.Ordinal);

    /// <summary>
    /// **最后一个可以当断点的历史事件**的索引（没有 ⇒ -1）。
    ///
    /// <para>⚠️ 从末尾往前跳过 <see cref="IsVolatile"/> —— 断点落在它们之后等于把「每步都在变的字节」
    /// 写进缓存前缀，每次请求都会因为那一段而不命中（这正是 10-28 之前 21 万只命中 8k 的机制）。</para>
    /// <para>⚠️ 只认消息事件（`MessageEvent`）：工具结果在 Chat 协议里是 `role:"tool"` 的字符串正文，
    /// 挂不了断点；三族统一跳过它，位置判据才只有一处。</para>
    /// </summary>
    public static int LastStableIndex(IReadOnlyList<TimelineEvent> timeline)
    {
        for (var i = timeline.Count - 1; i >= 0; i--)
        {
            if (IsVolatile(timeline[i])) continue;
            if (timeline[i] is TimelineEvent.MessageEvent) return i;
        }

        return -1;
    }
}

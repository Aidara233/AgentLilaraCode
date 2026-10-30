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
    /// 这条记录**挂得住断点吗**——必须是消息事件，且带至少一个**文本块**（2026-10-30）。
    ///
    /// <para>⚠️ <b>为什么这不是「实现细节」而是判据的一半</b>：三族的方言都只能挂在**文本**上
    /// （Claude 的 `cache_control` 在 `TextBlockParam` 上、Chat 的 `prompt_cache_breakpoint` 在
    /// `{type:"text"}` 分片上、Responses 在 `input_text` 上）。而「最后一条消息」**不一定带文本**：</para>
    /// <list type="bullet">
    ///   <item><b>只有 `tool_use` 的模型消息</b>——模型连着调工具、一句话都没说时就是它，而这在
    ///   工具链上是**常态**（`read` → `read` → `edit` 一路到底）。</item>
    ///   <item><b>只有图片的用户消息</b>——2026-10-29 起 `session.send` 收 `image` 块，纯图提问合法。</item>
    /// </list>
    /// <para>不判这一条时，位置判据会把断点指给它们，而三族的 builder 只能**静默不打**
    /// （Markdown 里没有可挂的块）：于是那一步**根本没写历史缓存**，下一步只能命中
    /// system+premier 那点头部。表现就是「大部分步命中、**隔一阵子**掉一次」——掉的时机取决于
    /// 模型那一步说了话没有，而它和缓存毫无关系。⇒ 位置判据往前挪一条到**最近一条挂得住的**
    /// 消息上（多半是用户那句话），代价只是尾段晚一步进缓存。</para>
    ///
    /// <para>⚠️ Chat / Responses 那两条链路**本来就这么做**（它们在自己的消息数组上从后往前
    /// 找「content 是纯字符串」的那一条，`MarkCacheBreakpoints` / `OpenAiResponsesCache`）——
    /// 也就是说：同一个问题它们早答对了，只有 Claude 这条照「最后一条消息」直取。
    /// **同一个判据三处各写一份，就是它漂掉的方式。**</para>
    /// </summary>
    public static bool CanCarryBreakpoint(TimelineEvent evt) =>
        evt is TimelineEvent.MessageEvent m
        && m.Blocks.Any(b => b is Block.TextBlock);

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
            // ⚠️ **「是消息」不够**（2026-10-30）：还要「挂得住」。只有 `tool_use` 的模型消息
            // 与纯图用户消息都**没有文本块**，断点指过去只能静默丢掉（见 CanCarryBreakpoint）。
            if (CanCarryBreakpoint(timeline[i])) return i;
        }

        return -1;
    }
}

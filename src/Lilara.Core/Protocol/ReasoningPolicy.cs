using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

// 各协议能不能承载推理——一个显式的地方，而不是散在三个适配器里的 if。
//
// ⚠️ 2026-10-17 拆成两个问题（**此前只有一个，而那一个是错的**）：
//   ① `CanCarry`（**块**回不回内容）：Claude 能（thinking 块，要求 signature）；
//      Chat / Responses 的 content 没有推理块的位置 ⇒ 不能。
//   ② `CanReplayAsReasoningContent`（**`reasoning_content` 字段**回不回传）：
//      Chat 在**带工具**时必须回传——DeepSeek 思考模式的硬要求（见下）；Responses 放在
//      reasoning 项里（`OpenAiResponsesRequest` 自己写），不走这个字段 ⇒ false。
// ⚠️ 把 ① 的答案当成 ② 的答案，就是这条 400 的来源：
//   `The reasoning_content in the thinking mode must be passed back to the API.`
//   ——DeepSeek 只要**请求带 `tools`**，历史里每条 assistant 消息的 `reasoning_content`
//   （**空串也算**）都必须原样回传，哪怕那一轮没有发起工具调用。
//   项目此前在**两处**同时把它丢了：响应映射不读这个字段（思考根本没进记录），
//   请求构造又按「Chat 不承载推理」把块整块过滤掉。
public static class ReasoningPolicy
{
    /// <summary>
    /// **块**能不能进 content。Claude 能；两条 OpenAI 协议的 content 里没有推理块的位置。
    /// ⚠️ 它**不**回答「`reasoning_content` 字段要不要回传」——那是
    /// <see cref="CanReplayAsReasoningContent"/>。
    /// </summary>
    public static bool CanCarry(string protocol) => protocol switch
    {
        Protocols.ClaudeMessages => true,
        Protocols.OpenAiChat => false,
        Protocols.OpenAiResponses => false,
        _ => false,   // 未知协议：保守选择「不能」，宁可丢也不伪造
    };

    /// <summary>
    /// 这个推理块发得出去吗（作为 **content 里的块**）。硬约束来自 SDK 类型、不是上游协议：
    /// ThinkingBlockParam 的 Signature 与 Thinking 都是 required，没有不带 signature 的重载，
    /// 用官方 SDK 无法构造无签名 thinking 块。上游其实接受 "signature":""（直接发回 HTTP 200），
    /// 发不出去只是客户端库没有那个形状。空串与 null 同义，故用 IsNullOrEmpty。
    /// </summary>
    public static bool CanReplayBlock(Block.ReasoningBlock block, string protocol) =>
        CanCarry(protocol) && !string.IsNullOrEmpty(block.Signature);

    /// <summary>
    /// **Chat 的 assistant 消息要不要带 `reasoning_content` 字段。**
    ///
    /// <para>⚠️ 条件是**这次请求有没有带 `tools`**，不是「这条消息有没有发过调用」：
    /// DeepSeek 的规则是「带工具的请求里，历史 assistant 的 reasoning_content 必须全部回传」。
    /// 不带工具的纯聊天，它不看这个字段——多发一个不是错误，但那是白传（而且会让从没思考过的
    /// 会话在线上多出一个键）。</para>
    ///
    /// <para>⚠️ **只对 Chat 成立**：Responses 的推理住在 `type:"reasoning"` 项里
    /// （`OpenAiResponsesRequest` 负责），没有这个字段。</para>
    /// </summary>
    public static bool CanReplayAsReasoningContent(string protocol, bool carriesTools) =>
        protocol == Protocols.OpenAiChat && carriesTools;

    /// <summary>
    /// 一条 assistant 消息的 `reasoning_content` **值**。返回 null = **整个键不写**。
    ///
    /// <para>⚠️ <b>这一格是「原样回传 + 缺了兜底」里兜底的那一半</b>：记录里没有推理块时
    /// 给**空串**而不是省略。实测的两种 400 都指向这一格——一是回传丢了
    /// （`must be passed back`），二是那条消息压根没有这个字段
    /// （`Missing reasoning_content field …`）。而「历史上没有推理块」有两条真实来路：
    /// 会话建立在这一版之前，或中途换了模型/provider。</para>
    ///
    /// <para>⚠️ <b>为什么不选「换模型就彻底不带这个字段」</b>：那要求记录能回答
    /// 「这条消息是哪个模型产的」，而记录里没有这个事实。为了让「换了模型」不出错而
    /// 给历史编一个来源，正是 `00` §5「不许伪造」要挡的事。空串的语义是
    /// 「这一轮的思考**没有留下来**」——它是真的，不是编的。</para>
    /// </summary>
    public static string? ReasoningContentOf(IReadOnlyList<Block> blocks, string protocol, bool carriesTools)
    {
        if (!CanReplayAsReasoningContent(protocol, carriesTools)) return null;

        // 空串也参与拼接：`""` 是「思考了但没有正文」，与「没有推理块」在记录里不同
        // （后者给的是兜底空串，字面结果一样，但语义来自上一条注释）。
        var parts = blocks.OfType<Block.ReasoningBlock>()
            .Where(r => r.Kind == ReasoningKind.Text)
            .Select(r => r.Text)
            .ToList();

        return parts.Count == 0 ? "" : string.Concat(parts);
    }

    /// <summary>按目标协议过滤块列表，保序。过滤后可能为空——推理模型可能只有思考没有正文。</summary>
    public static IReadOnlyList<Block> FilterFor(IReadOnlyList<Block> blocks, string protocol) =>
        blocks.Where(b => b switch
        {
            Block.ReasoningBlock r => CanReplayBlock(r, protocol),
            _ => true,
        }).ToList();
}

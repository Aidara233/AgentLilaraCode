using Lilara.Core.Adapters;

namespace Lilara.Core.Canonical;

// 各协议能不能承载推理——一个显式的地方，而不是散在三个适配器里的 if。
// Claude：能承载（要求 signature）；Chat / Responses：完全不暴露推理 → 不能承载，丢且不伪造。
public static class ReasoningPolicy
{
    public static bool CanCarry(string protocol) => protocol switch
    {
        Protocols.ClaudeMessages => true,
        Protocols.OpenAiChat => false,
        Protocols.OpenAiResponses => false,
        _ => false,   // 未知协议：保守选择「不能」，宁可丢也不伪造
    };

    // 这个推理块发得出去吗。硬约束来自 SDK 类型、不是上游协议：ThinkingBlockParam 的
    // Signature 与 Thinking 都是 required，没有不带 signature 的重载，用官方 SDK 无法
    // 构造无签名 thinking 块。上游其实接受 "signature":""（直接发回 HTTP 200），
    // 发不出去只是客户端库没有那个形状。空串与 null 同义，故用 IsNullOrEmpty。
    public static bool CanReplayBlock(Block.ReasoningBlock block, string protocol) =>
        CanCarry(protocol) && !string.IsNullOrEmpty(block.Signature);

    // 按目标协议过滤块列表，保序。过滤后可能为空——推理模型可能只有思考没有正文。
    public static IReadOnlyList<Block> FilterFor(IReadOnlyList<Block> blocks, string protocol) =>
        blocks.Where(b => b switch
        {
            Block.ReasoningBlock r => CanReplayBlock(r, protocol),
            _ => true,
        }).ToList();
}

using Lilara.Core.Adapters;

namespace Lilara.Core.Canonical;

/// <summary>
/// 各协议**能不能承载推理**——一个显式的地方，而不是散在三个适配器里的 if。
///
/// ⚠️ 这是「协议怪癖」的一种（`00` §1.2），但它是**纯数据**，所以放在 canonical 旁边，
/// 让三个适配器读同一份判断。
///
/// 规则来源（ApiPlan spec §10.2 + 控制器实测）：
/// - Claude：content 里有 thinking 块 → **能承载**；⚠️ 但**要求 signature**
///   （`ThinkingBlockParam.Signature` 是 `required`，见 `CanReplayBlock`）。
/// - Chat / Responses：**完全不暴露推理** → 不能承载，**丢且不伪造**。
/// </summary>
public static class ReasoningPolicy
{
    public static bool CanCarry(string protocol) => protocol switch
    {
        Protocols.ClaudeMessages => true,
        Protocols.OpenAiChat => false,
        Protocols.OpenAiResponses => false,
        _ => false,   // 未知协议：保守选择「不能」，宁可丢也不伪造
    };

    /// <summary>
    /// 这个推理块**发得出去吗**。
    /// ⚠️ 实测确认的硬约束：`ThinkingBlockParam` 的 `Signature` 与 `Thinking` 都是 `required`，
    /// 且该类**没有不带 signature 的构造重载** —— 无签名的 thinking 块在 Anthropic 线上
    /// **无法表达**。而本机网关的 `deepseek-flash` 恰好**不返回 signature**。
    /// 所以无签名 = 发不出去 = 只能丢。**这不是保守，是协议没有那个形状。**
    /// ⚠️ **空串与 null 同义**：上游眼里都是「没有签名」。用 `is not null` 会让 `""` 漏过去，
    /// 发出 `"signature":""`——值逃出约束，正是本项目反复付代价的那类缺陷。故用 `IsNullOrEmpty`。
    /// </summary>
    public static bool CanReplayBlock(Block.ReasoningBlock block, string protocol) =>
        CanCarry(protocol) && !string.IsNullOrEmpty(block.Signature);

    /// <summary>
    /// 按目标协议过滤块列表。**保序**。
    /// ⚠️ 过滤后可能为空——调用方必须能处理（见 Review Focus #2：推理模型可能只有思考没有正文）。
    /// </summary>
    public static IReadOnlyList<Block> FilterFor(IReadOnlyList<Block> blocks, string protocol) =>
        blocks.Where(b => b switch
        {
            Block.ReasoningBlock r => CanReplayBlock(r, protocol),
            _ => true,
        }).ToList();
}

using Lilara.Core.Protocol.Claude;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

// 协议 → 适配器：焊死的 switch，不做插件注册表。
//
// ⚠️ **重试不在这里**（用户 2026-10-21 的十次重试）：适配器只负责「认出这次失败值不值得再发」
// （`ModelCallFailedException.Transient`），循环住在 `RetryPolicy.CallAsync` 的**调用点**
// ——`Stepper`（会话 / 压缩）与 `provider.test`（探针）。⚠️ 别做成包装层：
// 那样两个调用点都会再套一层循环，「最多十次」会变成 121 次，而且每次失败会在账本里记两行。
public static class AdapterFactory
{
    public static IModelAdapter Create(ProviderDefinition provider) => provider.Protocol switch
    {
        Protocols.ClaudeMessages => new ClaudeAdapter(provider),
        Protocols.OpenAiChat => new OpenAiChatAdapter(provider),
        Protocols.OpenAiResponses => new OpenAiResponsesAdapter(provider),
        _ => throw new ModelCallFailedException(
            $"未知协议 '{provider.Protocol}'。目前支持：{string.Join(" / ", Protocols.All)}。"),
    };
}

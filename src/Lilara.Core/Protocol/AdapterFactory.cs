using Lilara.Core.Protocol.Claude;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Kernel;
using Lilara.Core.Providers;

namespace Lilara.Core.Protocol;

// 协议 → 适配器：焊死的 switch，不做插件注册表。
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

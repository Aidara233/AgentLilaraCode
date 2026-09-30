using Lilara.Core.Adapters.Claude;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters;

/// <summary>
/// 协议 → 适配器。⚠️ 焊死的 switch（04 §1.2），不做插件注册表。
/// 加协议 = 加一个 case + 一个适配器类 + <see cref="Protocols"/> 里一个常量。
/// </summary>
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

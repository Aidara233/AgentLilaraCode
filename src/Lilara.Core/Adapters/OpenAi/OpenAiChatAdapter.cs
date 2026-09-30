using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>OpenAI Chat 适配器。⚠️ 骨架——Task 5、6、9 依次填入真实实现。</summary>
public sealed class OpenAiChatAdapter : IModelAdapter
{
    public const string ProtocolId = Protocols.OpenAiChat;

    /// <summary>⚠️ 私有字段必须有，不能用 primary constructor 的裸参数——
    /// 骨架时期参数没被用到会报 `CS9113`（未读参数），而 `TreatWarningsAsErrors` 会拦下它。</summary>
    private readonly ProviderDefinition _provider;

    public OpenAiChatAdapter(ProviderDefinition provider) => _provider = provider;

    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
        throw new ModelCallFailedException("OpenAI Chat 适配器尚未实现。");
}

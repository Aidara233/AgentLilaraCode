using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class AdapterFactoryTests
{
    private static ProviderDefinition Def(string protocol) => new()
    {
        Id = "p", Protocol = protocol, ApiKey = "k",
        DefaultModel = "m", Modality = "vision",
    };

    [Fact]
    public void Create_ClaudeMessages() =>
        Assert.IsType<ClaudeAdapter>(AdapterFactory.Create(Def(Protocols.ClaudeMessages)));

    [Fact]
    public void Create_OpenAiChat() =>
        Assert.IsType<Adapters.OpenAi.OpenAiChatAdapter>(AdapterFactory.Create(Def(Protocols.OpenAiChat)));

    [Fact]
    public void Create_OpenAiResponses() =>
        Assert.IsType<Adapters.OpenAi.OpenAiResponsesAdapter>(AdapterFactory.Create(Def(Protocols.OpenAiResponses)));

    [Fact]
    public void Create_UnknownProtocol_ThrowsWithAHumanMessage()
    {
        var ex = Assert.Throws<ModelCallFailedException>(() => AdapterFactory.Create(Def("gemini")));

        Assert.Contains("gemini", ex.Message);
        Assert.Contains(Protocols.ClaudeMessages, ex.Message);   // 列出支持的
    }

    [Fact]
    public void Protocols_AreDistinctStrings()
    {
        // ⚠️ 三个常量必须互不相同，且与 ClaudeAdapter.ProtocolId 一致——
        // 否则 AdapterFactory 的分派与适配器自身的守卫会对不上（这正是之前
        // 「Repl 硬编码 claude-messages」那类缺陷的同一个坑）。
        Assert.Equal(ClaudeAdapter.ProtocolId, Protocols.ClaudeMessages);
        Assert.Equal(3, new[] { Protocols.ClaudeMessages, Protocols.OpenAiChat, Protocols.OpenAiResponses }
            .Distinct().Count());
    }
}

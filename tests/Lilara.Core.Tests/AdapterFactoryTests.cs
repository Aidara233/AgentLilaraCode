using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class AdapterFactoryTests
{
    private static ProviderDefinition Def(string protocol) => new()
    {
        Id = "p", Protocol = protocol, ApiKey = "k",
        DefaultModel = "m", Modality = "vision",
    };

    [Theory]
    [InlineData("claude-messages", typeof(Lilara.Core.Protocol.Claude.ClaudeAdapter))]
    [InlineData("openai-chat", typeof(Lilara.Core.Protocol.OpenAi.OpenAiChatAdapter))]
    [InlineData("openai-responses", typeof(Lilara.Core.Protocol.OpenAi.OpenAiResponsesAdapter))]
    public void Create_DispatchesTheOnDiskProtocolStrings(string onDiskValue, Type expected)
    {
        // 落盘的协议字符串必须按字面量守，常量在这里是被测对象。
        var adapter = AdapterFactory.Create(Def(onDiskValue));

        Assert.IsType(expected, adapter);
    }

    [Fact]
    public void Create_UnknownProtocol_ThrowsWithAHumanMessage()
    {
        var ex = Assert.Throws<ModelCallFailedException>(() => AdapterFactory.Create(Def("gemini")));

        Assert.Contains("gemini", ex.Message);
        Assert.Contains(Protocols.ClaudeMessages, ex.Message);   // 列出支持的
    }

    [Fact]
    public void Protocols_PinTheOnDiskWireValues()
    {
        // 这些字符串会落盘（spec.json 的 model.protocol、providers/*.json 的 protocol），
        // 必须按字面量守——若断言也走常量，「常量被改」在类型系统与测试里都自洽。
        Assert.Equal("claude-messages", Protocols.ClaudeMessages);
        Assert.Equal("openai-chat", Protocols.OpenAiChat);
        Assert.Equal("openai-responses", Protocols.OpenAiResponses);

        // 且必须互不相同。
        Assert.Equal(3, Protocols.All.Distinct().Count());
    }
}

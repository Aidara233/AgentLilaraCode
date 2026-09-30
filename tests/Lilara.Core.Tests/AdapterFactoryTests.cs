using Lilara.Core.Adapters;
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
    [InlineData("claude-messages", typeof(Adapters.Claude.ClaudeAdapter))]
    [InlineData("openai-chat", typeof(Adapters.OpenAi.OpenAiChatAdapter))]
    [InlineData("openai-responses", typeof(Adapters.OpenAi.OpenAiResponsesAdapter))]
    public void Create_DispatchesTheOnDiskProtocolStrings(string onDiskValue, Type expected)
    {
        // ⚠️ 字面量故意写死——这些是**落盘的**字符串，常量在这里是被测对象。
        // 这条比 IsType 那三条更强：它用的是真实会出现在 providers/*.json 里的值。
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
        // ⚠️ 这里**故意写死字面量**，不引用 `Protocols.*` 常量。
        //
        // 这些字符串会落盘（spec.json 的 model.protocol、providers/*.json 的 protocol）。
        // 如果断言也走常量，那么「常量被人改了」在**类型系统内自洽、在测试里也自洽**——
        // 唯一的症状是用户既有的 providers 文件突然不认了，而那已经是运行期故障。
        //
        // 所以：**落盘的字符串，必须由测试按字面量守。**
        // 这与「单一来源」的直觉相悖，但常量在这里是被测对象，不是参照物——
        // 就像断言 API 的线上契约时，不该引用自己的常量。
        Assert.Equal("claude-messages", Protocols.ClaudeMessages);
        Assert.Equal("openai-chat", Protocols.OpenAiChat);
        Assert.Equal("openai-responses", Protocols.OpenAiResponses);

        // 且必须互不相同（重复的 switch 臂常量本来就是编译错误，这里是行为层的双保险）
        Assert.Equal(3, Protocols.All.Distinct().Count());
    }
}

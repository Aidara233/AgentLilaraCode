using Lilara.Core.Kernel;
using Lilara.Core.Protocol;

namespace Lilara.Core.Tests;

public class ProviderModelCatalogTests
{
    [Theory]
    [InlineData(Protocols.OpenAiChat, "http://localhost:8123/v1/models", false)]
    [InlineData(Protocols.OpenAiResponses, "http://localhost:8123/v1/models", false)]
    [InlineData(Protocols.ClaudeMessages, "http://localhost:8123/v1/models", true)]
    public void Resolve_UsesProtocolCatalogEndpoint(string protocol, string expectedUrl, bool claude)
    {
        var provider = new ProviderDefinition
        {
            Id = "p", Protocol = protocol, ApiKey = "secret", DefaultModel = "configured-model",
            Modality = "text", Endpoint = "http://localhost:8123/",
        };

        var resolved = ProviderModelCatalog.Resolve(provider);
        Assert.Equal(expectedUrl, resolved.Url);
        Assert.Equal(claude, resolved.Claude);
    }

    [Fact]
    public void ParseOpenAi_ExtractsDistinctSortedIdsWithoutMutatingConfiguration()
    {
        var provider = new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.OpenAiChat, ApiKey = "secret", DefaultModel = "my-custom-model",
            Modality = "text", Models = ["manually-configured"],
        };
        var before = (provider.DefaultModel, provider.Models);

        var ids = ProviderModelCatalog.ParseOpenAi("""{"data":[{"id":"z-model"},{"id":"a-model"},{"id":"z-model"},{"name":"no-id"}]}""");

        Assert.Equal(["a-model", "z-model"], ids);
        Assert.Equal(before.DefaultModel, provider.DefaultModel);
        Assert.Equal(before.Models, provider.Models);
    }

    [Fact]
    public void ParseClaude_UsesDataIds()
    {
        var ids = ProviderModelCatalog.ParseClaude("""{"data":[{"id":"claude-sonnet"},{"id":"claude-haiku"}]}""");
        Assert.Equal(["claude-haiku", "claude-sonnet"], ids);
    }

    [Fact]
    public void Parse_RejectsUnexpectedShape()
    {
        Assert.Throws<InvalidOperationException>(() => ProviderModelCatalog.ParseOpenAi("{}"));
    }
}

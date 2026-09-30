using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class T9EndpointSurfaceTests
{
    private static ProviderDefinition Def(string protocol) => new()
    {
        Id = "gw", Protocol = protocol, ApiKey = "k",
        DefaultModel = "m", Modality = "vision",
    };

    [Theory]
    [InlineData("http://h:1", "http://h:1/v1/chat/completions")]
    [InlineData("http://h:1/", "http://h:1/v1/chat/completions")]
    [InlineData("http://h:1//", "http://h:1/v1/chat/completions")]
    public void ComposeUrl_NormalizesTrailingSlash(string endpoint, string expected)
    {
        Assert.Equal(expected, OpenAiHttp.ComposeUrl(endpoint, "/v1/chat/completions"));
    }

    [Fact]
    public void BothOpenAiAdaptersExposeTheirResolvedEndpoint()
    {
        var chat = new OpenAiChatAdapter(Def(Protocols.OpenAiChat));
        var resp = new OpenAiResponsesAdapter(Def(Protocols.OpenAiResponses));

        Assert.NotNull(chat.Endpoint);
        Assert.NotNull(resp.Endpoint);
        Assert.Equal(EndpointSource.Default, chat.Endpoint!.Source);
        Assert.Equal(OpenAiHttp.OfficialUrl, resp.Endpoint!.Url);
    }

    [Fact]
    public void NullEndpointMeansNoPlaceholderNoUrl()
    {
        var adapter = new UnconfiguredAdapter();

        Assert.Null(adapter.Endpoint);
    }
}

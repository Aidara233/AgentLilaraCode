using Lilara.Core.Adapters.Claude;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class ClaudeAdapterTests
{
    [Fact]
    public void Constructor_RejectsWrongProtocol()
    {
        var wrong = new ProviderDefinition
        {
            Id = "openai",
            Protocol = "openai-chat",
            ApiKey = "x",
            DefaultModel = "gpt",
            Modality = "vision",
        };

        var ex = Assert.Throws<ArgumentException>(() => new ClaudeAdapter(wrong));

        Assert.Contains("claude-messages", ex.Message);
    }

    [Fact]
    public void Constructor_AcceptsClaudeProtocol()
    {
        var def = new ProviderDefinition
        {
            Id = "claude",
            Protocol = "claude-messages",
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        };

        var adapter = new ClaudeAdapter(def);

        Assert.NotNull(adapter);
    }
}

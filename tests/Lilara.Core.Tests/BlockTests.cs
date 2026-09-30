using System.Text.Json;
using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

public class BlockTests
{
    [Fact]
    public void RoundTrip_PreservesDiscriminator()
    {
        Block original = new Block.TextBlock("看看这个报错");

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<Block>(json, Json.Options);

        Assert.Equal(original, back);
        Assert.Contains("\"kind\":\"text\"", json);
    }

    [Fact]
    public void Serialize_DoesNotEscapeNonAscii()
    {
        Block original = new Block.TextBlock("你好");

        var json = JsonSerializer.Serialize(original, Json.Options);

        Assert.Contains("你好", json);
        Assert.DoesNotContain("\\u4f60", json);
    }

    [Fact]
    public void RoundTrip_ThroughBlockList()
    {
        var original = new List<Block> { new Block.TextBlock("a"), new Block.TextBlock("b") };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<List<Block>>(json, Json.Options);

        Assert.Equal(original, back);
    }
}

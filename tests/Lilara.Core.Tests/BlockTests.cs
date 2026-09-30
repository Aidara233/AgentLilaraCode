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

    /// <summary>
    /// 推理块必须能原样往返（00 §5 铁律：逐字回传，永不可优化）。
    /// ⚠️ 签名是不透明凭据，落盘再读回必须**逐字节相同**——差一个字符上游就 400。
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesReasoningSignature()
    {
        Block original = new Block.ReasoningBlock("opaque-sig-==", "想了一会儿");

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<Block>(json, Json.Options);

        // 落盘形状与 text 一致：camelCase 属性 + 小写判别式
        Assert.Contains("\"kind\":\"reasoning\"", json);
        Assert.Contains("\"signature\":\"opaque-sig-==\"", json);
        Assert.Equal(original, back);
    }

    /// <summary>混合列表的顺序必须逐字保住：[reasoning, text] 就是 [reasoning, text]。</summary>
    [Fact]
    public void RoundTrip_PreservesBlockOrderInMixedList()
    {
        var original = new List<Block>
        {
            new Block.ReasoningBlock("sig", "想"),
            new Block.TextBlock("说"),
        };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<List<Block>>(json, Json.Options);

        Assert.Equal(original, back);
    }
}

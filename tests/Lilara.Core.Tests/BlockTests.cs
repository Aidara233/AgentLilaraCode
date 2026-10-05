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

    // 签名是不透明凭据，落盘再读回必须逐字节相同——差一个字符上游就 400。
    [Fact]
    public void RoundTrip_PreservesReasoningSignature()
    {
        Block original = new Block.ReasoningBlock("opaque-sig-==", "想了一会儿");

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<Block>(json, Json.Options);

        Assert.Contains("\"kind\":\"reasoning\"", json);
        Assert.Contains("\"signature\":\"opaque-sig-==\"", json);
        Assert.Equal(original, back);
    }

    // 混合列表的顺序必须逐字保住。
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

    [Fact]
    public void ReasoningBlock_SignatureIsOptional()
    {
        // 网关可能返回不带 signature 的 thinking 块；当必填会整条推理存不下来。
        Block original = new Block.ReasoningBlock(Signature: null, Text: "想想", Kind: ReasoningKind.Text);

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<Block>(json, Json.Options);

        Assert.Equal(original, back);
        Assert.Contains("\"kind\":\"reasoning\"", json);
    }

    [Fact]
    public void ReasoningBlock_PreservesSignatureByteForByte()
    {
        // signature 是不透明凭据，上游会校验——绝不能 trim 或重编码。
        const string sig = "ErUBCkYIBRgCIkAxY±+/= 空格 也保留";
        Block original = new Block.ReasoningBlock(sig, "想", ReasoningKind.Text);

        var back = JsonSerializer.Deserialize<Block>(
            JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal(sig, ((Block.ReasoningBlock)back!).Signature);
    }

    [Fact]
    public void RedactedReasoning_RoundTrips()
    {
        // redacted 是加密载荷：原样保留、不解释。
        Block original = new Block.ReasoningBlock(null, "ZW5jcnlwdGVk", ReasoningKind.Redacted);

        var back = JsonSerializer.Deserialize<Block>(
            JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal(ReasoningKind.Redacted, ((Block.ReasoningBlock)back!).Kind);
    }

    [Fact]
    public void ReasoningBlock_NormalisesANullKindToText()
    {
        // Kind=null 会被 WhenWritingNull 静默丢字段，回读成默认值。
        Block original = new Block.ReasoningBlock("s", "t", null!);

        var back = JsonSerializer.Deserialize<Block>(
            JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal(ReasoningKind.Text, ((Block.ReasoningBlock)back!).Kind);
        Assert.Equal(original, back);
    }

    [Fact]
    public void ReasoningBlock_NormalisesANullKindSetThroughWith()
    {
        // record 的 with 走 init setter，绕过主构造器的属性初始化器。
        var original = new Block.ReasoningBlock("s", "t");

        var mutated = original with { Kind = null! };

        Assert.Equal(ReasoningKind.Text, mutated.Kind);

        // 泛型推断成静态 ReasoningBlock 时判别式会整个丢掉，读回抛 NotSupportedException。
        var back = JsonSerializer.Deserialize<Block>(
            Json.StableSerialize(mutated), Json.Options);
        Assert.Equal(mutated, back);
    }

    [Fact]
    public void ReasoningBlock_NormalisesAnExplicitJsonNull()
    {
        // STJ 会把字面 null 绑到构造参数上。
        var json = """{"kind":"reasoning","signature":"s","text":"t","reasoningKind":null}""";

        var back = (Block.ReasoningBlock)JsonSerializer.Deserialize<Block>(json, Json.Options)!;

        Assert.Equal(ReasoningKind.Text, back.Kind);
    }

    [Fact]
    public void ReasoningBlock_SurvivesJsonMissingTheKindField()
    {
        // 缺字段时落到默认值 ReasoningKind.Text（与归一化无关）。
        var json = """{"kind":"reasoning","signature":"s","text":"t"}""";

        var back = (Block.ReasoningBlock)JsonSerializer.Deserialize<Block>(json, Json.Options)!;

        Assert.Equal(ReasoningKind.Text, back.Kind);
    }

    [Fact]
    public void StableSerialize_KeepsTheDiscriminator()
    {
        Assert.Contains("\"kind\":\"text\"", Json.StableSerialize(new Block.TextBlock("你好")));
        Assert.Contains("\"kind\":\"reasoning\"", Json.StableSerialize(new Block.ReasoningBlock("s", "想")));
    }

    [Fact]
    public void StableSerialize_IsByteStable()
    {
        // 同一内容两次渲染逐字节相同，不依赖字典枚举序。
        Assert.Equal(
            Json.StableSerialize(new Block.TextBlock("你好")),
            Json.StableSerialize(new Block.TextBlock("你好")));
    }
}

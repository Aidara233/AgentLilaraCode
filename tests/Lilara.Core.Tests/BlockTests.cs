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

    [Fact]
    public void ReasoningBlock_SignatureIsOptional()
    {
        // ⚠️ 实测：本机网关的 deepseek-flash 返回 {"type":"thinking","thinking":"..."}
        // **没有 signature**。把它当必填，整条推理就存不下来。
        Block original = new Block.ReasoningBlock(Signature: null, Text: "想想", Kind: ReasoningKind.Text);

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<Block>(json, Json.Options);

        Assert.Equal(original, back);
        Assert.Contains("\"kind\":\"reasoning\"", json);
    }

    [Fact]
    public void ReasoningBlock_PreservesSignatureByteForByte()
    {
        // ⚠️ signature 是不透明凭据，上游会校验——绝不能 trim 或重编码
        const string sig = "ErUBCkYIBRgCIkAxY±+/= 空格 也保留";
        Block original = new Block.ReasoningBlock(sig, "想", ReasoningKind.Text);

        var back = JsonSerializer.Deserialize<Block>(
            JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal(sig, ((Block.ReasoningBlock)back!).Signature);
    }

    [Fact]
    public void RedactedReasoning_RoundTrips()
    {
        // redacted 是加密载荷：原样保留、不解释（ApiPlan spec §10.2）
        Block original = new Block.ReasoningBlock(null, "ZW5jcnlwdGVk", ReasoningKind.Redacted);

        var back = JsonSerializer.Deserialize<Block>(
            JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal(ReasoningKind.Redacted, ((Block.ReasoningBlock)back!).Kind);
    }

    [Fact]
    public void ReasoningBlock_NormalisesANullKindToText()
    {
        // ⚠️ 实测过：Kind=null 会被 `WhenWritingNull` 静默丢字段，回读成默认值，
        // 于是 record **往返后不等于自己**。归一之后不可能再出现那种块。
        Block original = new Block.ReasoningBlock("s", "t", null!);

        var back = JsonSerializer.Deserialize<Block>(
            JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal(ReasoningKind.Text, ((Block.ReasoningBlock)back!).Kind);
        Assert.Equal(original, back);
    }

    [Fact]
    public void ReasoningBlock_NormalisesANullKindSetThroughWith()
    {
        // ⚠️ record 的 `with` 走 init setter，**绕过主构造器的属性初始化器**。
        // 归一写在初始化器上时，这条路会把 null 放进来 → 字段被丢 → 往返后不等于自己。
        var original = new Block.ReasoningBlock("s", "t");

        var mutated = original with { Kind = null! };

        Assert.Equal(ReasoningKind.Text, mutated.Kind);

        // ⚠️ 这里必须走 `Json.StableSerialize`（参数是基类 `Block`），**不能**写
        // `JsonSerializer.Serialize(mutated, Json.Options)`：后者泛型推断成静态类型
        // `ReasoningBlock`，判别式 `"kind"` 会整个丢掉，读回时直接
        // `NotSupportedException: must specify a type discriminator`（实测踩到过）。
        // 这正是 `StableSerialize` 把参数钉死成 `Block` 的理由——写这条测试时就撞上了。
        var back = JsonSerializer.Deserialize<Block>(
            Json.StableSerialize(mutated), Json.Options);
        Assert.Equal(mutated, back);
    }

    [Fact]
    public void ReasoningBlock_NormalisesAnExplicitJsonNull()
    {
        // ⚠️ STJ 会把字面 `null` 绑到构造参数上——这正是 `??` 存在的理由，之前没测过
        var json = """{"kind":"reasoning","signature":"s","text":"t","reasoningKind":null}""";

        var back = (Block.ReasoningBlock)JsonSerializer.Deserialize<Block>(json, Json.Options)!;

        Assert.Equal(ReasoningKind.Text, back.Kind);
    }

    [Fact]
    public void ReasoningBlock_SurvivesJsonMissingTheKindField()
    {
        // ⚠️ 这条钉的是**反序列化的默认值**：缺字段时落到 `ReasoningKind.Text`。
        // 它**不是**归一化的回归守卫——缺字段本来就会落到默认值，归一前它也一样绿。
        // 真正的守卫是上面两条：`with` 那条守构造路径的归一，JSON-null 那条守 `??`。
        var json = """{"kind":"reasoning","signature":"s","text":"t"}""";

        var back = (Block.ReasoningBlock)JsonSerializer.Deserialize<Block>(json, Json.Options)!;

        Assert.Equal(ReasoningKind.Text, back.Kind);
    }

    [Fact]
    public void StableSerialize_KeepsTheDiscriminator()
    {
        // ⚠️ 这条测试是「静态类型陷阱」的守卫。
        // 计划早期版本的测试写的是「两个 Block.TextBlock 序列化结果相等」——
        // 两个操作数**推断出同一个静态类型**，于是**必然相等**，判别式丢了也照样绿。
        // 那是一条**空转的测试**。
        Assert.Contains("\"kind\":\"text\"", Json.StableSerialize(new Block.TextBlock("你好")));
        Assert.Contains("\"kind\":\"reasoning\"", Json.StableSerialize(new Block.ReasoningBlock("s", "想")));
    }

    [Fact]
    public void StableSerialize_IsByteStable()
    {
        // 同一内容两次渲染逐字节相同——不依赖字典枚举序
        Assert.Equal(
            Json.StableSerialize(new Block.TextBlock("你好")),
            Json.StableSerialize(new Block.TextBlock("你好")));
    }
}

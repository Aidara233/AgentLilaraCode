using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Control;
using Lilara.Core.Persistence;
using System.Text.Json;

namespace Lilara.Core.Tests;

public class WireFormatTests
{
    // wire 与磁盘的 options 必须是两个不同实例——Converters 是可变 List，
    // 共用会让 wire 的改动同时改掉落盘格式（而 history.jsonl 只追加、永不重写）。
    [Fact]
    public void WireOptions_AreNotTheSameInstanceAsDiskOptions()
    {
        Assert.NotSame(Json.Options, WireJson.Options);

        // Converters 也必须是各自独立的 List，否则「两个 options 对象」只是表象。
        Assert.NotSame(Json.Options.Converters, WireJson.Options.Converters);
    }

    // 两边策略值目前一致；钉住它是为了「哪天有人只改了一边」立刻显形。
    [Fact]
    public void WireOptions_CurrentlyMirrorTheDiskPolicies()
    {
        Assert.Equal(Json.Options.PropertyNamingPolicy, WireJson.Options.PropertyNamingPolicy);
        Assert.Equal(Json.Options.DefaultIgnoreCondition, WireJson.Options.DefaultIgnoreCondition);
        Assert.Equal(Json.Options.Converters.Count, WireJson.Options.Converters.Count);
    }

    // 事件必须带判别式——头靠它分派。
    [Fact]
    public void WireEvent_CarriesItsDiscriminator()
    {
        var json = WireEvent.StableSerialize(new WireEvent.StepBegin("s1", 3, DateTimeOffset.UnixEpoch));

        Assert.Contains("\"kind\":\"session.step.begin\"", json);
    }

    // axis.changed 的判别式；漏了注册不会报错，只会序列化成 {}（基类没有属性）。
    [Fact]
    public void WireEvent_AxisChangedCarriesItsDiscriminatorAndValues()
    {
        var json = WireEvent.StableSerialize(new WireEvent.AxisChanged("s1", "effort", "max"));

        Assert.Contains("\"kind\":\"axis.changed\"", json);
        Assert.Contains("\"axis\":\"effort\"", json);
        Assert.Contains("\"value\":\"max\"", json);
    }

    // 嵌套 Block 的判别式取自静态声明类型，少了它头收到一串没有类型的块。
    [Fact]
    public void WireEvent_NestedBlocksKeepTheirDiscriminator()
    {
        var json = WireEvent.StableSerialize(new WireEvent.MessageAppended(
            "s1", "user", [new Block.TextBlock("你好")], DateTimeOffset.UnixEpoch));

        Assert.Contains("\"kind\":\"message.appended\"", json);
        Assert.Contains("\"kind\":\"text\"", json);          // 嵌套块的判别式
        Assert.Contains("\"text\":\"你好\"", json);
    }

    // 无签名推理块在 wire 上必须与磁盘同形（signature 键整个不出现），否则头读到 null 而不是「没有这个键」。
    [Fact]
    public void WireEvent_ReasoningBlockWithoutSignature_OmitsTheKey()
    {
        var json = WireEvent.StableSerialize(new WireEvent.MessageAppended(
            "s1", "model", [new Block.ReasoningBlock(null, "想")], DateTimeOffset.UnixEpoch));

        Assert.Contains("\"reasoningKind\":\"text\"", json);
        Assert.DoesNotContain("signature", json);
    }

    // 信封：seq 与 topic 是流的属性，与载荷分开。
    [Fact]
    public void Frame_IsATransportEnvelopeAroundAPayload()
    {
        var frame = new WireFrame.Event("session:s1", 42, new WireEvent.StateChanged("s1", "Idle", 1));

        var json = JsonSerializer.Serialize<WireFrame>(frame, WireJson.Options);

        Assert.Contains("\"topic\":\"session:s1\"", json);
        Assert.Contains("\"seq\":42", json);
        Assert.Contains("\"kind\":\"session.state\"", json);   // 载荷自己的判别式仍在
    }

    // 回执只有两类；Accepted=false 的 Reason 是「命令本身不合法」，不是「这次调用失败」（后者走 WireEvent.Error）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ack_CarriesOnlyTwoOutcomes(bool accepted)
    {
        var ack = new WireFrame.Ack("c1", accepted, accepted ? "s1" : null, accepted ? null : "会话不存在。", null);

        var json = JsonSerializer.Serialize<WireFrame>(ack, WireJson.Options);

        Assert.Contains($"\"accepted\":{(accepted ? "true" : "false")}", json);
        Assert.Contains("\"id\":\"c1\"", json);
    }
}

using Lilara.Core.Canonical;
using Lilara.Core.Control;
using Lilara.Core.Persistence;
using System.Text.Json;

namespace Lilara.Core.Tests;

public class WireFormatTests
{
    /// <summary>
    /// ⚠️ **G3 的核心断言**：wire 的 options 与磁盘的 options 必须是**两个不同的实例**。
    ///
    /// <para>不是洁癖：`Json.Options.Converters` 是**可变的 `List`** 挂在共享静态实例上。
    /// 哪个人为了 wire 往上加一个 converter，就**同时改了落盘格式**——
    /// 而 `history.jsonl` 只追加、永不重写，改一次就是一次数据迁移。
    /// 这条测试让那次「顺手共用」立刻红。</para>
    /// </summary>
    [Fact]
    public void WireOptions_AreNotTheSameInstanceAsDiskOptions()
    {
        Assert.NotSame(Json.Options, WireJson.Options);

        // ⚠️ 而且 `Converters` 也必须是各自独立的列表——
        // 共享同一个 List 实例的话，「两个 options 对象」只是表象。
        Assert.NotSame(Json.Options.Converters, WireJson.Options.Converters);
    }

    /// <summary>
    /// ⚠️ 两边的**策略值**目前是一致的（camelCase / 枚举按名 / 丢 null）。
    /// 钉住它是为了让「哪天有人只改了一边」立刻显形——
    /// 那时要么是有意的契约分叉（应当同时更新这条测试并写明理由），要么是个 bug。
    /// </summary>
    [Fact]
    public void WireOptions_CurrentlyMirrorTheDiskPolicies()
    {
        Assert.Equal(Json.Options.PropertyNamingPolicy, WireJson.Options.PropertyNamingPolicy);
        Assert.Equal(Json.Options.DefaultIgnoreCondition, WireJson.Options.DefaultIgnoreCondition);
        Assert.Equal(Json.Options.Converters.Count, WireJson.Options.Converters.Count);
    }

    /// <summary>⚠️ 事件必须带判别式——头靠它分派。</summary>
    [Fact]
    public void WireEvent_CarriesItsDiscriminator()
    {
        var json = WireEvent.StableSerialize(new WireEvent.StepBegin("s1", 3, DateTimeOffset.UnixEpoch));

        Assert.Contains("\"kind\":\"session.step.begin\"", json);
    }

    /// <summary>
    /// ⚠️ **嵌套块的判别式也要在**——`WireEvent` 的载荷里带 `IReadOnlyList&lt;Block&gt;`，
    /// 而块自己的多态判别式同样取自静态声明类型（这里是 `Block`，正好对）。
    /// 少了它，头收到一串没有类型的块，渲染不出来。
    /// </summary>
    [Fact]
    public void WireEvent_NestedBlocksKeepTheirDiscriminator()
    {
        var json = WireEvent.StableSerialize(new WireEvent.MessageAppended(
            "s1", "user", [new Block.TextBlock("你好")], DateTimeOffset.UnixEpoch));

        Assert.Contains("\"kind\":\"message.appended\"", json);
        Assert.Contains("\"kind\":\"text\"", json);          // 嵌套块的判别式
        Assert.Contains("\"text\":\"你好\"", json);
    }

    /// <summary>
    /// ⚠️ 推理块的无签名形状在 wire 上必须与磁盘**同形**（`signature` 键整个不出现）。
    /// 头将来要显示推理摘要，形状不一致会让它读到 null 而不是「没有这个键」。
    /// </summary>
    [Fact]
    public void WireEvent_ReasoningBlockWithoutSignature_OmitsTheKey()
    {
        var json = WireEvent.StableSerialize(new WireEvent.MessageAppended(
            "s1", "model", [new Block.ReasoningBlock(null, "想")], DateTimeOffset.UnixEpoch));

        Assert.Contains("\"reasoningKind\":\"text\"", json);
        Assert.DoesNotContain("signature", json);
    }

    /// <summary>信封：`seq` 与 `topic` 是**流**的属性，与载荷分开。</summary>
    [Fact]
    public void Frame_IsATransportEnvelopeAroundAPayload()
    {
        var frame = new WireFrame.Event("session:s1", 42, new WireEvent.StateChanged("s1", "Idle", 1));

        var json = JsonSerializer.Serialize<WireFrame>(frame, WireJson.Options);

        Assert.Contains("\"topic\":\"session:s1\"", json);
        Assert.Contains("\"seq\":42", json);
        Assert.Contains("\"kind\":\"session.state\"", json);   // 载荷自己的判别式仍在
    }

    /// <summary>
    /// ⚠️ 回执**只有两类**（`03` §3）。这条钉住「业务失败不许挤进来」：
    /// `Accepted=false` 的 `Reason` 语义是「**命令本身不合法**」，
    /// 不是「这次调用失败了」——后者走 `WireEvent.Error`。
    /// </summary>
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

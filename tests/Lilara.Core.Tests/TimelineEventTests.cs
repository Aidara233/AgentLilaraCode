using System.Text.Json;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class TimelineEventTests
{
    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = at,
            From = Speaker.User,
            Blocks = [new Block.TextBlock("你好")],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<TimelineEvent>(json, Json.Options);

        var message = Assert.IsType<TimelineEvent.MessageEvent>(back);
        Assert.Equal(at, message.At);
        Assert.Equal(Speaker.User, message.From);
        Assert.Equal(new Block.TextBlock("你好"), Assert.Single(message.Blocks));
    }

    [Fact]
    public void RoundTrip_IsByteForByteStable()
    {
        // ⚠️ 不能写 Assert.Equal(original, back)：Blocks 是集合，
        // record 的自动相等对集合成员用引用相等，那条断言必挂。
        // 往返字节相等覆盖面更强——它正是「序列化不丢判别式」想守的东西。
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.Model,
            Blocks = [new Block.TextBlock("看看这个报错")],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<TimelineEvent>(json, Json.Options);

        Assert.Equal(json, JsonSerializer.Serialize(back, Json.Options));
    }

    [Fact]
    public void RoundTrip_PreservesModelSpeaker()
    {
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.Model,
            Blocks = [new Block.TextBlock("hi")],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = (TimelineEvent.MessageEvent)JsonSerializer
            .Deserialize<TimelineEvent>(json, Json.Options)!;

        Assert.Equal(Speaker.Model, back.From);
    }

    [Fact]
    public void Serialize_UsesStableDiscriminator()
    {
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.User,
            Blocks = [],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);

        Assert.Contains("\"kind\":\"message\"", json);
    }
}

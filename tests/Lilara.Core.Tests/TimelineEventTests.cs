using System.Text.Json;
using Lilara.Core.Kernel;

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
        // record 自动相等对集合成员用引用相等，故断言走字节相等。
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

    [Fact]
    public void Serialize_WritesSpeakerAsItsNameNotItsOrdinal()
    {
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.Model,
            Blocks = [],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);

        // 落盘枚举必须是名字：整数既不可读，也会因重排静默重解释既有历史。
        Assert.Contains("\"from\":\"model\"", json);
        Assert.DoesNotContain("\"from\":1", json);
    }

    [Fact]
    public void Serialize_UsesLowercaseStringValuesForEnumsAndDiscriminators()
    {
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.User,
            Blocks = [new Block.TextBlock("x")],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);

        Assert.Contains("\"kind\":\"message\"", json);
        Assert.Contains("\"from\":\"user\"", json);
    }
}

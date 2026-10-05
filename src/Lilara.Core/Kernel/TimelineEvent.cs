using System.Text.Json.Serialization;

namespace Lilara.Core.Kernel;

// 发言者。最小闭环只有这两种。
public enum Speaker
{
    User,
    Model,
}

// 记录 = 事件时间线 + 两个固定槽位（头、尾）。本计划只实现时间线，头尾槽位要等插件系统。
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MessageEvent), "message")]
public abstract record TimelineEvent
{
    public required DateTimeOffset At { get; init; }

    // 用户 / 模型的发言。工具调用记录将来也走这里。
    public sealed record MessageEvent : TimelineEvent
    {
        public required Speaker From { get; init; }
        public required IReadOnlyList<Block> Blocks { get; init; }
    }

    // 稳定序列化入口。参数类型必须是 TimelineEvent（基类），不能写成泛型 T——
    // 同 Json.StableSerialize 的陷阱：推断成具体子类会丢掉判别式 "kind"，
    // 而且写的时候静默、直到反序列化才炸。事件泵广播它，写错的帧要到解析才发现。
    public static string StableSerialize(TimelineEvent value) =>
        System.Text.Json.JsonSerializer.Serialize(value, Kernel.Json.Options);
}

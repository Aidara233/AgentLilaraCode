using System.Text.Json.Serialization;
using Lilara.Core.Canonical;

namespace Lilara.Core.Records;

/// <summary>发言者。最小闭环只有这两种。</summary>
public enum Speaker
{
    User,
    Model,
}

/// <summary>
/// 记录 = 事件时间线 + 两个固定槽位（头、尾）（01 §3）。
/// 本计划只实现时间线——头尾两个槽位要等插件系统。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MessageEvent), "message")]
public abstract record TimelineEvent
{
    public required DateTimeOffset At { get; init; }

    /// <summary>用户 / 模型的发言。工具调用记录将来也走这里（一块的两面，00 §5）。</summary>
    public sealed record MessageEvent : TimelineEvent
    {
        public required Speaker From { get; init; }
        public required IReadOnlyList<Block> Blocks { get; init; }
    }
}

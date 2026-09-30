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

    /// <summary>
    /// 稳定序列化入口。**参数类型必须是 <see cref="TimelineEvent"/>（基类），不能写成泛型 `T`。**
    ///
    /// <para>⚠️ **与 <see cref="Canonical.Json.StableSerialize(Canonical.Block)"/> 是同一个陷阱**，
    /// 实测（不是推测）：
    /// <code>
    /// Serialize&lt;TimelineEvent&gt;(evt) → {"kind":"message","from":"user",...}   ✓
    /// Serialize(evt)  // MessageEvent → {"from":"user",...}                     ✗ 判别式整个丢了
    /// </code>
    /// 而**错误只在反序列化时才炸**——写的时候静默，读的时候才失败。</para>
    ///
    /// <para>⚠️ 它存在的直接原因是**事件泵要广播它**（控制面）：广播走的是
    /// 「拿到一个 <c>TimelineEvent</c> 就发出去」，若在那里写成泛型，
    /// 头收到的每一帧都会缺 <c>kind</c>，而且**直到头去解析才发现**。</para>
    /// </summary>
    public static string StableSerialize(TimelineEvent value) =>
        System.Text.Json.JsonSerializer.Serialize(value, Canonical.Json.Options);
}

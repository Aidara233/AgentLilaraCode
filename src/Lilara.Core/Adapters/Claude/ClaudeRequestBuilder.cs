using Anthropic.Models.Messages;
using Lilara.Core.Canonical;
using Lilara.Core.Records;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Adapters.Claude;

/// <summary>
/// canonical → Claude Messages 请求。
/// ⚠️ 纯函数：同一个 ModelRequest 必须构造出逐字节相同的请求（00 §5 铁律 1）。
/// 这里是前缀稳定性的第一道闸——顺序、块数、无时间戳，全都不能漂。
/// </summary>
public static class ClaudeRequestBuilder
{
    public static MessageCreateParams Build(ModelRequest request)
    {
        // Review Focus #1：空 messages 会被 API 400 拒绝，早失败
        if (request.Timeline.Count == 0)
            throw new ArgumentException("时间线为空，无法构造请求", nameof(request));

        return new MessageCreateParams
        {
            Model = request.ModelId,
            MaxTokens = request.MaxTokens,
            Messages = BuildMessages(request.Timeline, request.Protocol),
            System = BuildSystem(request.SystemBlocks),
            OutputConfig = new OutputConfig { Effort = ToEffort(request.Effort) },
        };
    }

    /// <summary>
    /// canonical 时间线 → 协议消息列表。
    /// ⚠️ **合并相邻同角色**的事件。拒答不写模型事件，于是
    /// 「问 → 拒答 → 再问」在时间线上就是连着两条 user。
    /// API 参考自相矛盾（SDK README 说同角色会被合并；错误表却把
    /// `roles must alternate` 列为 400）——不赌歧义，构造期自己合并，
    /// 发出去的形状就永远是交替的。顺带这条也让将来的 tool 回合更忠实。
    /// ⚠️ 纯函数：同一份时间线必须构造出逐字节相同的列表（00 §5 铁律 1）。
    /// </summary>
    private static List<MessageParam> BuildMessages(IReadOnlyList<TimelineEvent> timeline, string protocol)
    {
        var messages = new List<MessageParam>();

        // 用 bool 而不是 Role 做比较：Role 是 SDK 的 union 类型，
        // 直接比它要么依赖它的相等语义、要么多一次装箱，没必要。
        bool? pendingIsUser = null;
        List<ContentBlockParam>? pending = null;

        void Flush()
        {
            if (pending is null) return;
            messages.Add(new MessageParam
            {
                Role = pendingIsUser!.Value ? Role.User : Role.Assistant,
                Content = pending,
            });
            pending = null;
        }

        foreach (var evt in timeline)
        {
            var message = (TimelineEvent.MessageEvent)evt;
            var isUser = message.From == Speaker.User;
            var blocks = ToContentBlocks(message, evt, protocol);

            if (pending is not null && pendingIsUser == isUser)
            {
                pending.AddRange(blocks);
                continue;
            }

            Flush();
            pending = blocks;
            pendingIsUser = isUser;
        }

        Flush();
        return messages;
    }

    /// <summary>
    /// 一条 canonical 消息 → 协议内容块列表。
    /// ⚠️ 按**原始顺序**映射，绝不能只挑 text：
    /// 推理块必须逐字回传给上游（00 §5 铁律），且顺序是 [reasoning, text]，
    /// 顺序或内容一动，上游就 400 `invalid_request_error`。
    /// </summary>
    private static List<ContentBlockParam> ToContentBlocks(
        TimelineEvent.MessageEvent message, TimelineEvent evt, string protocol)
    {
        var content = new List<ContentBlockParam>();
        foreach (var b in message.Blocks)
        {
            switch (b)
            {
                case CanonicalBlock.TextBlock t:
                    content.Add(new TextBlockParam { Text = t.Text });
                    break;

                case CanonicalBlock.ReasoningBlock r:
                    // ⚠️ 必须过能力表：签名缺失时**发不出去**（见 ReasoningPolicy.CanReplayBlock）。
                    // 实测：ThinkingBlockParam 的 Signature 是 required 且类无无参外构造器——
                    // 既不能赋 null（CS8601），也不能用 `null!` 骗编译器（null 是真会发生的），
                    // 更不能省掉（CS9035）。丢弃是**协议本身的限制**，不是我们的取舍。
                    if (!ReasoningPolicy.CanReplayBlock(r, protocol))
                        break;

                    content.Add(new ThinkingBlockParam
                    {
                        Thinking = r.Text,
                        // ⚠️ 这里的 `!` 与上面那个被否掉的**不是一回事**：
                        // CanReplayBlock 刚刚确认过 Signature is not null，所以压制是有依据的。
                        // 别把它当惯例——下一个人看到它要知道为什么合法。
                        Signature = r.Signature!,
                    });
                    break;

                default:
                    // 认不出的块**抛**，不静默丢——静默丢正是本轮要修的缺陷本身
                    throw new ArgumentException(
                        $"时间线里有无法翻译的块（{b.GetType().Name}），无法构造请求", nameof(evt));
            }
        }

        // Review Focus #1 的同类：`Content = []` 会被 API 400 拒绝，
        // 且 Blocks 是普通 required 列表、没有非空不变量，真能走到这里
        if (content.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        return content;
    }

    /// <summary>
    /// ⚠️ 没有系统块时**不设 System**，而不是设成空串或空表——
    /// 空值会变成一个真实的空 system 块，白白改变前缀。
    /// </summary>
    private static MessageCreateParamsSystem? BuildSystem(IReadOnlyList<CanonicalBlock> blocks)
    {
        // ⚠️ 先 materialize 再判空——判 blocks.Count 是错的：
        // 输入非空但全是非 TextBlock 时，过滤后是空列表，
        // 会发出一个**空的 system 块**，正是上面那条注释要防的事。
        var text = blocks.OfType<CanonicalBlock.TextBlock>()
                        .Select(t => new TextBlockParam { Text = t.Text })
                        .ToList();

        // ⚠️ 用早返回而不是三元：`text.Count == 0 ? null : text` 会让可空流经
        // MessageCreateParamsSystem 的隐式转换，触发 CS8604（本项目是警告即错误）
        if (text.Count == 0) return null;

        return text;
    }

    /// <summary>
    /// 思考强度轴 → 协议参数。
    /// ⚠️ SDK 支持五个档：low / medium / high / xhigh / max。
    /// 项目的轴（01 §3.5）目前只用低/中/高三档，但配置是用户写的——
    /// 把 SDK 支持的档位如实透传，比替用户猜要诚实。
    /// ⚠️ 未知值**抛异常，不静默降级**：`_ => Effort.High` 会让 "xhihg" 这种拼写错误
    /// 悄悄跑成 High，而这正是最难查的一类 bug。
    /// </summary>
    private static Effort ToEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new ArgumentException(
            $"未知的思考强度 '{effort}'。有效值：low / medium / high / xhigh / max", nameof(effort)),
    };
}

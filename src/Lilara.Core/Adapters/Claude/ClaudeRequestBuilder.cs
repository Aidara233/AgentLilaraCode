using Anthropic.Models.Messages;
using Lilara.Core.Canonical;
using Lilara.Core.Records;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Adapters.Claude;

// canonical → Claude Messages 请求。纯函数：同一个 ModelRequest 必须构造出逐字节相同的请求（前缀稳定性）。
public static class ClaudeRequestBuilder
{
    public static MessageCreateParams Build(ModelRequest request)
    {
        // 空 messages 会被 API 400 拒绝，早失败。
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

    // canonical 时间线 → 协议消息列表。合并相邻同角色的事件：拒答不写模型事件，
    // 于是时间线上会出现连着两条 user，而上游把 `roles must alternate` 列为 400。
    private static List<MessageParam> BuildMessages(IReadOnlyList<TimelineEvent> timeline, string protocol)
    {
        var messages = new List<MessageParam>();

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

    // 一条 canonical 消息 → 协议内容块列表。按原始顺序映射，绝不能只挑 text：
    // 推理块必须逐字回传（含签名），顺序或内容一动上游就 400 `invalid_request_error`。
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
                    // 必须过能力表：签名缺失时 SDK 构不出 ThinkingBlockParam（Signature 是 required）。
                    // 拦住我们的是客户端库的类型，不是上游协议——实测无签名 thinking 块上游回 HTTP 200 并正常作答。
                    if (!ReasoningPolicy.CanReplayBlock(r, protocol))
                        break;

                    content.Add(new ThinkingBlockParam
                    {
                        Thinking = r.Text,
                        // CanReplayBlock 刚确认过 Signature is not null，这里的 `!` 有依据。
                        Signature = r.Signature!,
                    });
                    break;

                default:
                    // 认不出的块抛，不静默丢。
                    throw new ArgumentException(
                        $"时间线里有无法翻译的块（{b.GetType().Name}），无法构造请求", nameof(evt));
            }
        }

        // `Content = []` 会被 API 400 拒绝；Blocks 没有非空不变量，真能走到这里。
        if (content.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        return content;
    }

    // 没有系统块时不设 System，而不是设成空串或空表：空值会变成一个真实的空 system 块，白白改变前缀。
    private static MessageCreateParamsSystem? BuildSystem(IReadOnlyList<CanonicalBlock> blocks)
    {
        // 先 materialize 再判空：输入非空但全是非文本块时过滤后是空列表，会发出一个空的 system 块。
        var text = blocks.OfType<CanonicalBlock.TextBlock>()
                        .Select(t => new TextBlockParam { Text = t.Text })
                        .ToList();

        // 用早返回而不是三元：三元会让可空流经隐式转换触发 CS8604（本项目警告即错误）。
        if (text.Count == 0) return null;

        return text;
    }

    // 思考强度轴 → 协议参数。SDK 支持五档：low / medium / high / xhigh / max，如实透传。
    // 未知值抛异常，不静默降级（否则拼写错误会悄悄跑成某一档）。
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

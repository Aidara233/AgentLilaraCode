using Anthropic.Models.Messages;
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
            Messages = request.Timeline.Select(ToMessage).ToList(),
            System = BuildSystem(request.SystemBlocks),
            OutputConfig = new OutputConfig { Effort = ToEffort(request.Effort) },
        };
    }

    private static MessageParam ToMessage(TimelineEvent evt)
    {
        var message = (TimelineEvent.MessageEvent)evt;
        var role = message.From == Speaker.User ? Role.User : Role.Assistant;

        var texts = message.Blocks.OfType<CanonicalBlock.TextBlock>().ToList();

        // Review Focus #1 的同类：`Content = []` 会被 API 400 拒绝，
        // 且 Blocks 是普通 required 列表、没有非空不变量，真能走到这里
        if (texts.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        // 单块走字符串形式（协议原生形状，最省），多块走块列表
        if (texts.Count == 1)
            return new MessageParam { Role = role, Content = texts[0].Text };

        return new MessageParam
        {
            Role = role,
            Content = texts
                .Select(t => (ContentBlockParam)new TextBlockParam { Text = t.Text })
                .ToList(),
        };
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

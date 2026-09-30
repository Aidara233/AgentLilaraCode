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
    /// ⚠️ 没有系统块时**不设 System**，而不是设成空串——
    /// 空串会变成一个真实的空 system 块，白白改变前缀。
    /// </summary>
    private static MessageCreateParamsSystem? BuildSystem(IReadOnlyList<CanonicalBlock> blocks)
    {
        if (blocks.Count == 0) return null;

        return new List<TextBlockParam>(
            blocks.OfType<CanonicalBlock.TextBlock>()
                  .Select(t => new TextBlockParam { Text = t.Text }));
    }

    /// <summary>
    /// 思考强度轴 → 协议参数。项目的轴是低/中/高（01 §3.5）→ low/medium/high；
    /// 「max」是 SDK 侧多出来的档位，顺带映射；未知值兜底为 high。
    /// </summary>
    private static Effort ToEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.High,
    };
}

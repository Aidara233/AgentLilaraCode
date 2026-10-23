using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `compact`——**用一段摘要截断此前的历史**（2026-10-22，压缩从「核心自己跑一次模型调用」改成工具）。
///
/// <para>⚠️ <b>它只在用户下发过压缩指令时算数</b>（`IToolContext.CompactRequested`）：
/// 没指令时调用它，就是一句人话 + 什么都不做——不能假装收尾，更不能真的截断历史
/// （用户没要求，而截断是<b>不可逆</b>的：被压掉的那段在渲染里就只剩摘要了）。</para>
///
/// <para>⚠️ <b>工具本身什么都不写</b>：检查点、清指令、开回收窗口全在
/// <see cref="Stepper"/> 里。理由是 `IToolContext` 只读（`04` §5.1）——工具没有写盘的手。
/// 它做的唯一一件事是<b>把摘要写进自己的参数</b>，而参数是记录的一部分；
/// 推进器在「这批调用真的执行过」（`Batch.ExecutedCallIds`）之后才动手。
/// ⇒ 一句「已请求压缩」是<b>两个判据都成立之后</b>才说得出的话。</para>
///
/// <para>⚠️ <b>为什么摘要进参数，而不是走「回复正文」</b>（本仓上一版的形态）：
/// 回复正文里会混进推理块（思考模型每回合都带 `reasoning_content`），而「必须纯文本」
/// 那条判据把整条摘要判死过一次（2026-10-22 的真实账本）。摘要落在参数里，
/// 它的形态就是一段 JSON 字符串，而模型自己下一轮看得见它——不需要另一条前置合成消息。</para>
///
/// <para>⚠️ <b>权限：不归闸门管</b>（<see cref="GatedByPermission"/>）。判据写在那个属性上：
/// 它只动加载边界、不碰世界，而它本来就只在用户指令期内生效。`off` 档下也应该压得成——
/// 压不成时连这次会话本身都会撑爆。</para>
/// </summary>
public sealed class CompactTool : ITool
{
    public const string NameOf = "compact";

    /// <summary>
    /// 摘要的长度上限（字符）。⚠️ <b>它和 <see cref="ReleaseTool"/> 那种「进记录就不管了」不同</b>：
    /// 摘要<b>每一步都会随历史重发</b>，所以它的大小是持续的上下文成本，而不只是一条记录的体积。
    /// 20k 字符对中文约一到两万 token——够写「目标 / 事实 / 决定 / 任务 / id 引用」，
    /// 又不至于让下一步的输入凭空翻倍。
    ///
    /// <para>⚠️ schema 里的 <c>maxLength</c> 与 <see cref="Parse"/> 的校验<b>用的是这一个常量</b>：
    /// 两处各写一个数就会漂，而漂的表现是「schema 放行、核心不认」（模型被上游拒绝或是一次白跑）。</para>
    /// </summary>
    public const int MaxSummaryChars = 20_000;

    public string Name => NameOf;

    /// <summary>只读：它不改工作目录、不联网、不写库。真正的落点（检查点）在推进器。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    /// <summary>见 <see cref="ITool.GatedByPermission"/>：压缩是底层机制，不看档位。</summary>
    public bool GatedByPermission => false;

    public string Description =>
        "用一段摘要代替此前全部对话——上下文快撑爆、或者用户要求压缩时调它。"
        + "把摘要写进 summary：保留当前目标、已确认事实、关键决定和约束、未完成任务、"
        + "重要的文件路径与工具结果 id、当前状态和不确定的地方。"
        + "调用之后，此前的记录不再发给模型（要用还取得回），而<b>这次调用本身及它之后的一切</b>会保留——"
        + "所以先把自己需要的东西读完再调。"
        + $"摘要不超过 {MaxSummaryChars} 个字符。只有用户要求压缩时它才会生效；否则它什么也不做。";

    public string ParametersSchemaJson => $$"""
        {
          "type": "object",
          "properties": {
            "requested": {
              "type": "boolean",
              "const": true,
              "description": "必须为 true——用它确认这次调用是有意发起的，而不是误触发。"
            },
            "summary": {
              "type": "string",
              "maxLength": {{MaxSummaryChars}},
              "description": "代替此前全部对话的摘要正文。"
            }
          },
          "required": ["requested", "summary"]
        }
        """;

    /// <summary>
    /// 这次调用的摘要，**且它真的算数**；否则 <c>null</c>，同时交回一句为什么要拒绝。
    ///
    /// <para>⚠️ <b>判据只有一处</b>：推进器（写检查点）与 <see cref="RunAsync"/>（回哪句话）
    /// 都调这一个函数——两处各判一次，就是本仓栽过的「两层判据必须共用一个谓词」。
    /// 「有没有指令」也在这里判，所以工具那边不需要再读一次上下文。</para>
    /// </summary>
    public static string? Parse(ToolCall call, IToolContext context, out string refusal)
    {
        refusal = "";

        if (!context.CompactRequested)
        {
            refusal = "当前没有压缩指令，什么都没做。只有用户要求压缩时它才会生效。";
            return null;
        }

        // ⚠️ `requested` 是**显式的意图确认**，不是形式主义：摘要顶掉的是不可逆的历史，
        // 而模型误触发一个「看起来无害的只读工具」的代价，在这里是最高的那一档。
        if (!ToolArgs.Bool(call.ArgumentsJson, "requested"))
        {
            refusal = "没有压缩：`requested` 必须是 true。确认要压缩时带上它和 summary 再调一次。";
            return null;
        }

        var summary = ToolArgs.String(call.ArgumentsJson, "summary");
        if (string.IsNullOrWhiteSpace(summary))
        {
            refusal = "没有压缩：`summary` 缺失或是空的。请把摘要正文写进 summary 再调一次。";
            return null;
        }

        if (summary.Length > MaxSummaryChars)
        {
            refusal = $"没有压缩：摘要 {summary.Length} 个字符，超过上限 {MaxSummaryChars}。"
                      + "请压缩到上限之内再调一次。";
            return null;
        }

        return summary;
    }

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
        Task.FromResult(ToolText.Of(
            Parse(call, context, out var refusal) is null
                ? refusal
                // ⚠️ 这句是**两个判据都成立之后**才说得出的：这次调用真的执行过（工具被调用了）、
                // 而且它会在推进器里兑现（指令在、参数合格）。篇幅压到最短——它进上下文，没有信息量。
                : "已请求压缩。此前的记录将由这段摘要代替；本调用与它之后的一切保留。"));
}

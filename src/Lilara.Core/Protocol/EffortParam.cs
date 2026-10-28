namespace Lilara.Core.Protocol;

/// <summary>
/// **思考强度要不要进请求参数**（2026-10-28）——按 provider 开的开关，默认关。
///
/// <para>⚠️ <b>为什么默认关（而不是「无条件发」）</b>：`reasoning_effort` 不是所有端点都认。
/// 已知的事实是三方向的——真 OpenAI 的推理模型认（Chat 上 `reasoning_effort`，Responses 上
/// `reasoning.effort`）；DeepSeek 认并自己映射档位；而 **GLM / doubao / 各种网关代理
/// 对未知参数的处理不一致**（有的 400 `unsupported_parameter`，有的静默忽略）。
/// 本仓的 provider 里有大量这类端点 ⇒ 无条件发等于拿「本来能跑的会话」去换一条新字段。</para>
///
/// <para>⚠️ <b>为什么必须有个开关，而不是干脆不发</b>：`Axes.Effort` 是 <c>InContext = false</c>
/// 的纯参数轴（`01` §3.5：模型不必被告知自己的推理预算）。它**只有**进请求参数这一条路
/// ——两条 OpenAI 链路此前**一次都没读过** `ModelRequest.Effort`（只有 Claude 那条读），
/// 于是用户在头上下拉改 effort，线上字节**逐字节相同**。轴静默失效比报错难查得多：
/// 没有错误、没有日志，只有一个永远不变的档位。</para>
///
/// <para>⚠️ <b>取值为什么不转换</b>：本仓的 5 档 <c>low / medium / high / xhigh / max</c> 是
/// 真 OpenAI 与 DeepSeek 两侧合法值的**子集**，原样送出去两边都认（DeepSeek 自己把
/// medium / xhigh 映射到 high）。在这里再写一张映射表就是 G10 那种第二份副本——
/// 档名一处（`Axes`）、发出去一处（本开关），中间不插翻译层。</para>
/// </summary>
public static class EffortParam
{
    /// <summary>不发这个字段（默认）。请求字节与加这个能力之前**逐字节相同**。</summary>
    public const string Auto = "auto";

    /// <summary>发：Chat 走顶层 `reasoning_effort`，Responses 走 `reasoning.effort`。</summary>
    public const string Reasoning = "reasoning";

    /// <summary><see cref="Kernel.ProviderDefinition.EffortParam"/> 的合法取值。</summary>
    public static readonly IReadOnlyList<string> Modes = [Auto, Reasoning];
}

/// <summary>
/// **Responses 请求要不要 `include: ["reasoning.encrypted_content"]`**（2026-10-28）——
/// 同样是按 provider 开的开关，默认关。
///
/// <para>⚠️ <b>为什么本仓必须靠它才能回传推理</b>：真 OpenAI 的推理项有两种「拿得回来」的路径——
/// 服务端保存后用 <c>previous_response_id</c> 续链，或客户端把带 <c>encrypted_content</c>
/// 的项原样重放。本仓<b>不走前一条</b>（全量重放自己的时间线，不用
/// <c>previous_response_id</c>）⇒ 只剩后一条。没有这个字段，响应里的 reasoning 项就没有密文，
/// 下一轮发回去的只是一段可读摘要；而真 OpenAI 对「没有 id / 密文」的推理项是
/// **接受但丢弃**（cookbook：「the API will simply discard any reasoning items that aren't
/// relevant」）——那等于推理回传功能形同不存在。</para>
///
/// <para>⚠️ <b>为什么也按端点开</b>：<c>include</c> 是 OpenAI 的形状，兼容网关不一定认
/// （未知 `include` 值可能 400）；而密文对<b>每条</b> reasoning 项都会让它变大，
/// 那是真实的 token 体积，不是免费的。</para>
/// </summary>
public static class ReasoningInclude
{
    /// <summary>不发（默认）。请求字节与加这个能力之前**逐字节相同**。</summary>
    public const string Auto = "auto";

    /// <summary>发 `include: ["reasoning.encrypted_content"]`。</summary>
    public const string Mode = "include";

    /// <summary><see cref="Kernel.ProviderDefinition.ReasoningInclude"/> 的合法取值。</summary>
    public static readonly IReadOnlyList<string> Modes = [Auto, Mode];
}

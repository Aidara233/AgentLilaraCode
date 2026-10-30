using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

/// <summary>
/// **一块内容这个协议翻译不了**时，给模型看的那句话（2026-10-29）。
///
/// <para>⚠️ <b>为什么不是抛</b>：记录**只追加**（`01` §4）。一条翻译不了的块只要进了
/// `history.jsonl`，此后的**每一次**构造请求都会抛——于是那个会话永远发不出消息，
/// 而用户手里没有任何工具能删掉那一行。抛在这里买到的是「不要静默丢」，
/// 而代价是**一条坏记录换一个坏会话**：两个都不可接受，但只有一个是不可逆的。</para>
///
/// <para>⚠️ <b>而它<b>不</b>等于静默丢</b>：占位是**模型看得见的正文**，它明说
/// 「这里本来有一块 X，没能发出去」。⇒ 「少一条信息」仍然会**被说出来**，
/// 只是不再拿整条会话陪葬。与 `TimelineRender` 里那条「库对象找不到了」同一条判据。</para>
///
/// <para>⚠️ <b>调用方要分清两件事</b>：<see cref="Skippable"/> 只回答「调用点自己没表达的
/// 那一块」（正文 / 工具调用 / 走独立字段的推理块都返回 <c>null</c>，否则它们会各多出一条
/// 假的「已省略」）；<see cref="Note"/> 是**无条件**的那句话（工具结果那条路只认正文，
/// 其余一律要一句交代）。</para>
/// </summary>
internal static class UnsupportedBlock
{
    /// <summary>正文、工具调用、推理块都由调用点自己表达 ⇒ <c>null</c>（不要多一条注记）。</summary>
    public static string? Skippable(Block block) => block switch
    {
        Block.TextBlock or Block.ToolUseBlock or Block.ReasoningBlock => null,
        _ => Note(block),
    };

    /// <summary>**无条件**给一句话（工具结果那条路用：它只认正文，其余都得交代一句）。</summary>
    public static string Note(Block block) => block switch
    {
        // ⚠️ 图片这句话在**两种**情形下都对：提供商不吃图（`03` §9.2 的渲染降级会把它换成
        // 占位，走不到这里）、以及字节没解出来（`ImageBlock.Bytes` 为空）。
        Block.ImageBlock image =>
            $"（这里有一张图片（{image.MediaType}），但它没能被发出去——当前提供商或链路不收图，已省略。）",

        Block.StoreBlock store =>
            $"（这里本该有一段内容（库对象 {store.MediaType}），但没有被解开，已省略。）",

        Block.TextBlock text => text.Text,

        _ => $"（这里有一块 {block.GetType().Name} 内容，这一条链路翻译不了，已省略。）",
    };
}

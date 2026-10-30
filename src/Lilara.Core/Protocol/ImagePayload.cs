using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol;

/// <summary>
/// **把一块图片变成协议要的那串东西**（2026-10-29）。
///
/// <para>⚠️ <b>它不是"又一层抽象"</b>：三条链路要的东西不同（Claude 要
/// <c>source.base64</c>、两条 OpenAI 要一个 <c>data:</c> URL），而**唯一的共同前提**
/// 是「字节已经贴在块上」（<see cref="Block.ImageBlock.Bytes"/>，由 `TimelineRender` 填）。
/// ⇒ 这里只回答两件事：<b>这块发得出去吗</b>、<b>那串东西怎么拼</b>。
/// </para>
///
/// <para>⚠️ <b>为什么"发得出去"要单独一个谓词</b>：挡在它前面的有三道闸（入口校验、
/// 渲染期降级、库对象丢失），而**每道闸的失败方式都不一样**。少了这一问，适配器会在
/// 缺字节时拼出一个 `data:image/png;base64,`（空内容）——上游回 400，
/// 而错误信息里不会提到那张图。</para>
/// </summary>
internal static class ImagePayload
{
    /// <summary>字节在不在（`TimelineRender` 没贴 / 库里没有 ⇒ 假）。</summary>
    public static bool CanSend(Block.ImageBlock image) => image.Bytes is { Length: > 0 };

    /// <summary>`data:image/png;base64,….`（两条 OpenAI 链路用它）。</summary>
    public static string DataUrl(Block.ImageBlock image) =>
        $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes!)}";

    /// <summary>光秃秃的 base64（Claude 的 `source.data`）。</summary>
    public static string Base64(Block.ImageBlock image) => Convert.ToBase64String(image.Bytes!);
}

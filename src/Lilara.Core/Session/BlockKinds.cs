using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// **哪些块可以从外面发进一个会话**（2026-10-29）。
///
/// <para>⚠️ <b>这个类存在的理由是一道「单向门」</b>：记录**只追加**（`01` §4）。一条翻译不了的块
/// 一旦落进 `history.jsonl`，此后每一次构造请求都会在适配器里炸——而盘上没有删除键。
/// 在一次真事故的形状里（用户发一张图），代价是**那个会话永远说不出话**，
/// 而没有任何人会看到一句「是你那张图的问题」。</para>
///
/// <para>⚠️ <b>而「适配器兜底」不能取代这一层</b>：适配器只看得见**块**，看不见「这是谁发的、
/// 意图是什么」。伪造一条模型形状的块（`toolUse`）不会让上游 400，只会让模型相信它自己
/// 调过那个工具——那是**语义**上的坏，不是渲染能补的。⇒ 入口判「形状」，适配器判「能不能翻译」。</para>
///
/// <para>⚠️ <b>两道判据都要有，且只管各自那一半</b>：这里拒绝的是「本来就不该进来」的东西；
/// 这里放行的东西（比如一张图）在**不支持它的提供商**上仍然要能渲染成占位——
/// 那是 <c>TimelineRender</c> 按提供商模态做的降级（`03` §9.2），不是这里的第二条判据。</para>
/// </summary>
public static class BlockKinds
{
    /// <summary>
    /// **认得的图片媒体类型**——⚠️ <b>定义在 <see cref="Storage.MediaTypes"/></b>（本层不能反过来
    /// 依赖会话层，`ArchitectureTests` 钉着层向）。这里只是给会话入口用的名字。
    /// </summary>
    public static IReadOnlyList<string> ImageMediaTypes => MediaTypes.ImageTypes;

    public static bool IsImage(string? mediaType) => MediaTypes.IsImage(mediaType);

    /// <summary>
    /// **这批块能不能进会话**。返回拒的理由；<c>null</c> = 放行。
    ///
    /// <para>⚠️ <b>措辞要给下一步</b>（同 `Gate.DenyReason` 那条）：模型读不到这句话，
    /// 读它的是**发那条命令的人**——所以每一句都要说清「换什么形状」或「先做什么」。</para>
    /// </summary>
    /// <param name="blocks">客户端的原样块。</param>
    /// <param name="blobs">
    /// 库。⚠️ 图片**只认库里已有的字节**：`ImageBlock` 只带地址，而发一个地址进来
    /// 等于让渲染期去读一个可能不存在的对象——那是「一次静默的失败」。
    /// </param>
    public static string? Reject(IReadOnlyList<Block> blocks, BlobStore blobs)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                // 正文与（极端情况下的）推理块：都是**可以承载的**内容。
                case Block.TextBlock:
                case Block.ReasoningBlock:
                    continue;

                // ⚠️ **伪造模型调用**：`toolUse` 是模型输出的形状（`01` §3.6）。
                // 放进用户消息里不会 400，只会让模型相信「我调过它」——而配对的那条结果
                // 永远不存在。⇒ 拒，并告诉人该怎么表达。
                case Block.ToolUseBlock use:
                    return $"用户消息里不能带工具调用（`{use.Name}`）——那是模型输出的形状。"
                         + "要它做某件事，用普通文本说（它会自己选工具）。";

                // ⚠️ **库里的地址块**：它的 `mediaType` 可以是任意东西（包括核心自己的块数组
                // JSON），解引用之后能变出**任意块**——包括伪造的 `toolUse`。
                // ⇒ 它是核心内部形状，不是客户端形状。
                case Block.StoreBlock store:
                    return $"`store` 是核心内部的地址块（{store.Sha256[..Math.Min(8, store.Sha256.Length)]}…），"
                         + "不能直接发进来。要发内容，用 text / image。";

                case Block.ImageBlock image:
                    // ⚠️ 类型先判：一个 `image/x-executable` 就算在库里也不会被翻译。
                    if (!IsImage(image.MediaType))
                        return $"图片的媒体类型只认 {string.Join(" / ", ImageMediaTypes)}"
                             + $"（给的是 '{image.MediaType}'）。";

                    // ⚠️ 地址必须能兑现：渲染期要拿它去读库，读不到就只能降级成一句
                    // 「这张图没了」——而那时用户已经发过一条消息了，看不见自己的图。
                    if (!blobs.Has(image.Sha256))
                        return $"这张图不在库里（sha256 {image.Sha256[..Math.Min(8, image.Sha256.Length)]}…）"
                             + "——先 `POST /files` 上传拿到 ref，再发这条消息。";

                    continue;

                default:
                    return $"不认得的块（{block.GetType().Name}），不能发进会话。";
            }
        }

        return null;
    }
}

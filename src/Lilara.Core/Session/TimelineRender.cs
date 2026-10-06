using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// **记录 → 这一刻的形态**：把盘上那份时间线渲染成「现在要用的那串事件」。
///
/// <para>⚠️ <b>这个类存在的唯一理由是「只有一份」</b>：两条路都要它——
/// ① `Stepper` 构造请求（喂给模型的那份）；② `SessionQueries.HistoryOf`（头与控制台看的那份）。
/// 各写一份的后果是本仓反复栽过的那个形状：**两处副本只改一处**，
/// 于是模型看到的与用户看到的开始不一样，而且两边都「看起来是对的」。</para>
///
/// <para>两步，次序是刻意的（<see cref="Render"/> 里有说明）：解库引用 → 换回收渲染。</para>
/// </summary>
public static class TimelineRender
{
    public static IReadOnlyList<TimelineEvent> Render(SessionStore store, SessionHandle handle)
    {
        var raw = store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var blobs = new BlobStore(store.DataRoot);

        // ⚠️ **次序：先解引用，再按记录的 id 回收。**
        // 反过来的话，被回收的那条会先变成注记 ⇒ 后面拿到的就不是内容了。
        // ⇒ 回收只动**已经解好的那次渲染**，记录一个字不动（`01` §8.0.2 一 / 八）。
        //
        // ⚠️ 这里**曾经还有第三步「解 `Ref`」**（把 `fetch` 结果的引用换成被引用那条的内容）。
        // 那一步连同 `Ref` 这个机制一起删了（2026-10-06，用户裁定，理由见
        // `TimelineEvent.ToolResultEvent.Ref` 的注释）——`fetch` 的结果今天带的是**内容**。
        return [.. Reclaim.Apply([.. raw.Select(e => Dereference(e, blobs))], handle.Reclaimed)];
    }

    /// <summary>
    /// 把一个工具结果里的库对象换成真内容——**给 `fetch` 取回时用**。
    ///
    /// <para>⚠️ <b>它是 `Dereference` 的一个有名字的入口，不是第二份实现</b>：
    /// 库对象有<b>两种</b>存法（原文 / 块数组 JSON），读的人必须分得开。
    /// `fetch` 若自己判一遍，两条路的判据就开始漂——而漂的后果是把 `read` 的内容
    /// 当成一段 JSON 交给模型。</para>
    /// </summary>
    public static TimelineEvent.ToolResultEvent Dereference(TimelineEvent.ToolResultEvent result, BlobStore blobs) =>
        result with { Content = [.. result.Content.SelectMany(b => Dereference(b, blobs))] };

    /// <summary>
    /// 把一个事件里指向库的块换成真内容。
    ///
    /// <para>⚠️ <b>库对象渲染成「内容本身」，模型不该知道库存在</b>（`01` §8.4）。
    /// 而 <see cref="Block.StoreBlock"/> 只带 `mediaType`，**它是不是文本、编码是什么，
    /// 都只能看字节**——所以这里按 `mediaType` 分两条路读。</para>
    ///
    /// <para>⚠️ <b>它返回的是一串块，不是一个块</b>（2026-10-06 改）：混排 / 多块的结果
    /// 进库时存的是**块数组 JSON**（<see cref="BlockList.MediaType"/>），解出来就该还原成
    /// **那一串块**——否则「一段混排内容」会退化成「一个装着 JSON 的文本块」。</para>
    ///
    /// <para>⚠️ <b>读不出来时给一段说得清的话，不抛</b>：抛会让**整个会话再也发不出消息**
    /// （记录只追加，那条坏引用永远在）。而「库对象没了」是**可以自愈的**——
    /// 模型看到一句「这里本该有内容、但它丢了」，至少这一步还能走完。</para>
    /// </summary>
    private static IReadOnlyList<Block> Dereference(Block block, BlobStore blobs)
    {
        if (block is not Block.StoreBlock store) return [block];

        var bytes = blobs.TryGet(store.Sha256);
        if (bytes is null)
            return [new Block.TextBlock($"（这里本该有一段内容，但库对象 {store.Sha256[..8]}… 找不到了）")];

        // 单文本块存的是**原始字节**，其余存的是**块数组 JSON**——两条路必须分得开，
        // 混着读会让 `read` 的库对象吐出一段 JSON 来（而它本该是原文）。
        if (store.MediaType == BlockList.MediaType)
        {
            try
            {
                var blocks = System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<Block>>(
                    bytes, Json.Options);
                // 反序列化可能是 null（字面的 "null"）⇒ 退回下面那句提示，别让调用点拿到 null。
                if (blocks is { Count: > 0 }) return blocks;
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏对象 ⇒ 与「找不到」同一条处理：说一句人话，别把整步打掉。
            }

            return [new Block.TextBlock($"（库对象 {store.Sha256[..8]}… 读不出来）")];
        }

        return
        [
            new Block.TextBlock(
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                    .GetString(bytes)),
        ];
    }

    /// <summary>事件里的每个块都过一遍 <see cref="Dereference"/>；消息事件与工具结果**都要**。</summary>
    private static TimelineEvent Dereference(TimelineEvent evt, BlobStore blobs) => evt switch
    {
        TimelineEvent.MessageEvent m => m with { Blocks = [.. m.Blocks.SelectMany(b => Dereference(b, blobs))] },
        TimelineEvent.ToolResultEvent r => r with { Content = [.. r.Content.SelectMany(b => Dereference(b, blobs))] },
        _ => evt,
    };
}

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
/// <para>三步，次序是刻意的（<see cref="Render"/> 里有说明）：解库引用 → 换回收渲染 → **加框架**。</para>
/// </summary>
public static class TimelineRender
{
    public static IReadOnlyList<TimelineEvent> Render(SessionStore store, SessionHandle handle)
    {
        var raw = store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var blobs = new BlobStore(store.DataRoot);

        // ⚠️ **次序：先解引用，再按记录的 id 回收，最后加框架。**
        // 反过来的话，被回收的那条会先变成注记 ⇒ 后面拿到的就不是内容了。
        // ⇒ 回收只动**已经解好的那次渲染**，记录一个字不动（`01` §8.0.2 一 / 八）。
        //
        // ⚠️ **框架排在回收之后，这是它独立成格的<b>全部理由</b>**（`01` §3.7）：
        // 回收换的是整个 `Content`。若框架先拼进 `Content`，这一步就会**连它一起吃掉**。
        // 排在这里 ⇒ 回收只换内容、框架一个字不动。
        //
        // ⚠️ 这里**曾经还有一步「解 `Ref`」**（把 `fetch` 结果的引用换成被引用那条的内容）。
        // 那一步连同 `Ref` 这个机制一起删了（2026-10-06，用户裁定，理由见
        // `TimelineEvent.ToolResultEvent.Ref` 的注释）——`fetch` 的结果今天带的是**内容**。
        //
        // ⚠️ **判定在<b>这里</b>做，而且只在这里**（2026-10-11）——它**不是**一个后台维护的
        // 集合，而是一次渲染的**纯函数输入**：
        //
        // <list type="number">
        //   <item><b>判定的输入必须是这一刻的记录。</b>判据是「这一刻喂给模型的东西」，
        //   而记录一步一变 ⇒ 任何缓存的集合都会在某个时刻**陈旧**。陈旧的表现不是「晚一步」，
        //   是**取不回**：注记上写着 <c>fetch(id)</c>，而模型照它发 `fetch` 时，
        //   缓存里还没有那个 id ⇒ `SnapshotOf` 在它自己那一趟的时间线里找不到，
        //   回一句「没有这一条」——**而内容明明还在**。</item>
        //
        //   <item><b>它原先是个内存账</b>（<c>SessionHandle.Reclaimed</c> + <c>SetReclaimed</c>，
        //   由 <c>session.gc</c> 命令写入）。那条路有两个洞：① 它**要求有人记得喊**
        //   （`00` §4 那四个触发源一个都不存在 ⇒「重启即重新决定」实际是「只有你手动喊」）；
        //   ② 更要命的是**没人能保证它在每次渲染前都是新的**——我第一版把它挂在推进器里，
        //   结果答复那条路（`ResumeAsync`）落下结果之后直接推下一步，**不经过**那个落点，
        //   于是注记与 `fetch` 在同一个窗口里对不上；而 `SessionHandle.Load` 出来的那份
        //   更是**恒为空集**（它压根不经过任何重算点）⇒ 「重启即重新决定」名存实亡。</item>
        //
        //   <item><b>它不违反 §11.7 的两档判据</b>：那一节分的「窗口触发（捡）／压力触发（买）」
        //   说的是**要不要主动制造失效**。判定本身**不制造任何失效**——记录一步一变
        //   本来就是每步破前缀的原因，判据只是**读**它。⇒ 这是「捡」那一档，
        //   而且落点正是 §11.7 指定的那一刻：「**第一步真的要拿它渲染**」。</item>
        // </list>
        //
        // ⚠️ **代价如实说**：每次渲染多一次时间线扫描 + 对候选结果各一次 `Has`。
        // 它跑在原本就要 `ReadTimeline` + 逐块解引用的那条路上，边际成本是一次 stat。
        // ⚠️ 它收不了几样东西（保护带 `ProtectedRecentResults = 4` 是**占位值**）⇒
        // 它的主要效果是**让注记可兑现**，不是省上下文。别把它读成「一个激进的回收策略」。
        var reclaimed = Reclaim.Decide(raw, blobs);
        var applied = Reclaim.Apply([.. raw.Select(e => Dereference(e, blobs))], reclaimed);
        return [.. applied.Select(WithFraming)];
    }

    /// <summary>
    /// 把框架渲染成**一个独立的文本块**，恒定在<span>内容之前</span>（`01` §3.7）。
    ///
    /// <para>⚠️ <b>「独立的块」是这条机制的全部要害，不是排版偏好</b>：模型只能靠
    /// 「这是一个<b>块</b>」来区分框架与内容。合并进同一个块（或拼成一段文本）之后，
    /// **一个首行恰好长得像框架的内容**就能冒充框架，而模型**没有任何办法分辨**
    /// ——那正是 §3.6 ①之二警告的「最坏的失败不是少了信息，是**多了一条假信息**」。</para>
    ///
    /// <para>⚠️ <b>没有 <c>Framing</c> 的记录原样返回</b>（老记录、以及不需要框架的工具）
    /// ⇒ 逐字节与今天相同。</para>
    /// </summary>
    private static TimelineEvent WithFraming(TimelineEvent evt) =>
        evt is TimelineEvent.ToolResultEvent { Framing: { } framing } r
            ? r with { Content = [new Block.TextBlock(framing.Render()), .. r.Content] }
            : evt;

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

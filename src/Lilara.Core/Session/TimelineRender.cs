using System.Text;
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
/// <para>⚠️ <b>两条入口只在「投递截断」这一件事上分叉</b>（2026-10-17）：
/// <see cref="RenderForModel"/> 给模型看，超过 <c>ToolLimits.MaxResultBytes</c> 的结果
/// 只喂开头一段并附一条可兑付的注记；<see cref="Render(SessionStore, SessionHandle)"/>
/// 给头 / 控制台看，不截断——那是**查看存档**，不是**投递**。
/// 其余步骤（**压缩投影** → 解库引用 → 换回收渲染 → 加框架）是同一份私有重载。</para>
/// </summary>
public static class TimelineRender
{
    /// <summary>头 / 控制台看的形态：完整内容 + 回收注记。**不**做投递截断。</summary>
    public static IReadOnlyList<TimelineEvent> Render(SessionStore store, SessionHandle handle)
    {
        var raw = store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        return Render(store, handle, raw, staleProbe: null, truncateForDelivery: false);
    }

    /// <summary>
    /// **喂给模型的那一份**——比 <see cref="Render(SessionStore, SessionHandle)"/> 多一条
    /// **投递截断**（`ToolLimits.MaxResultBytes`，2026-10-17）。
    ///
    /// <para>⚠️ <b>为什么截断住在渲染层，而不是工具层</b>：模型可以自己写 <c>.csproj</c> /
    /// 测试体，而工具结果里的一次 `dotnet test` 输出动辄上兆。若工具侧就砍，
    /// 被砍掉的字节**根本没有进库**，`fetch` 取回的只是截短的那份；
    /// 而投递期截断让**记录里保留全量**，模型用 `fetch(id, offset)` 就能接着看——
    /// 不像重跑那样花时间，也可复现。</para>
    ///
    /// <para>⚠️ <b>只保留开头</b>（用户 2026-10-17 裁定）：尾部换成一条注记，
    /// 它给出**这条结果的稳定 id** 与**下一个偏移**。⚠️ 注记里的偏移必须**可兑付**——
    /// 下一个偏移 = 我们实际交付的字节数（注记自己不算），并让它回退到字符边界；
    /// 否则 `fetch(offset)` 会把注记的一段再喂回来，或者漏掉字节。</para>
    ///
    /// <para>⚠️ <b>截断必须是纯的</b>：只依赖存储字节与固定预算，不按剩余上下文动态算——
    /// 同一步里渲染两次必须逐字节相同（`00` §5 铁律 1）。</para>
    /// </summary>
    public static IReadOnlyList<TimelineEvent> RenderForModel(
        SessionStore store, SessionHandle handle, Kernel.IToolContext staleProbe)
    {
        var raw = store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        return Render(store, handle, raw, staleProbe, truncateForDelivery: true);
    }

    /// <summary>
    /// 两条入口（外加压缩那一次加载）共用的那几步：**压缩投影** → 解库引用 → 按记录 id 回收
    /// → 投递截断 → 加框架 → 汇总摘要。
    ///
    /// <para>⚠️ <b>压缩投影在<b>最前</b>，而这是 2026-10-17 合并两个独立实现时的关键一条</b>
    /// （`75efa1a` 的持久压缩 × 本批的投递截断，两者都改过这个类）：投影换的是**看哪一段历史**，
    /// 回收/截断/框架换的是**怎么渲染那一段** ⇒ 后者必须以投影后的 `visible` 为输入。
    /// 反过来（在完整 `raw` 上回收再切）会让判定看到压缩窗口之外的记录，
    /// 而 `Reclaim.Decide` 的距离判据（「最近 N 条」）数的就会是**已经被压缩掉的条数**。</para>
    ///
    /// <para>⚠️ <b>摘要事件必须插在<b>最前</b>、且在框架之前</b>：摘要不是工具结果
    /// （`WithFraming` 不会碰它），而它代表的是被压缩掉的那一整段历史，位置就是它所在的位置。</para>
    /// </summary>
    private static IReadOnlyList<TimelineEvent> Render(
        SessionStore store, SessionHandle handle, IReadOnlyList<TimelineEvent> raw,
        Kernel.IToolContext? staleProbe, bool truncateForDelivery)
    {
        // ⚠️ **压缩投影**（`75efa1a`）：换的是「看哪一段历史」，不是「怎么渲染」。
        var projection = ContextCompaction.Project(store, handle, raw);
        var visible = projection.Suffix;
        var blobs = new BlobStore(store.DataRoot);

        // ⚠️ **判定在<b>这里</b>做，而且只在这里**（2026-10-11）——它**不是**一个后台维护的
        // 集合，而是一次渲染的**纯函数输入**：判定的输入必须是这一刻的记录。
        // 任何缓存的集合都会在某个时刻**陈旧**，而陈旧的表现不是「晚一步」，是**取不回**。
        var reclaimed = new HashSet<string>(Reclaim.Decide(visible, blobs), StringComparer.Ordinal);

        if (staleProbe is not null)
        {
            // ⚠️ **两条判据取<b>并集</b>**（`01` §11.3）：可恢复那一条管「内容自己就够」，
            // 点杀那一条管「这个源后来被动过」。并集而不是交集，因为两者收的理由不同。
            foreach (var r in visible.OfType<TimelineEvent.ToolResultEvent>())
            {
                // ⚠️ **过时的那一条仍然要过 `Recoverable`**：注记上写着「fetch(id) 取回」，
                // 而取不回的东西换成注记就是**一句假话**。
                if (Reclaim.Stale(r, staleProbe) && Reclaim.Recoverable(r.Content, blobs))
                    reclaimed.Add(r.Id);
            }
        }

        var applied = Reclaim.Apply([.. visible.Select(e => Dereference(e, blobs))], reclaimed);

        // ⚠️ 工具名要从 `tool_use` 取：投递预算按工具分（`fetch` 例外），
        // 而 `ToolResultEvent` 只有协议 `CallId`。⚠️ 从 `visible` 取（不是 `raw`）：
        // 只给**这次真的要发出去的那些调用**建表。
        var toolNames = visible.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .GroupBy(b => b.CallId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        List<TimelineEvent> result =
        [
            .. applied
                .Select(evt => truncateForDelivery ? Truncate(evt, toolNames) : evt)
                .Select(WithFraming).Where(IsRenderable),
        ];

        // ⚠️ 摘要事件的 id 是合成 id（`compaction:{id}`），**从不出现在记录里**——
        // 它和尾块合成对一样，是「只活在这份渲染产物里」的东西。
        if (projection.Checkpoint is { } checkpoint)
            result.Insert(0, ContextCompaction.SummaryEvent(checkpoint));

        return result;
    }

    /// <summary>
    /// 一次工具结果的**投递形态**：超预算的单文本块只留开头，尾部换成一条注记。
    ///
    /// <para>⚠️ <b>只处理单文本块</b>：混排 / 图片没有「字节前缀」的语义；
    /// 它们按块数组原样交给模型（单条内容本身有保存上限兜着）。</para>
    ///
    /// <para>⚠️ <b>已经被回收换成注记的不再截断</b>：那条注记本身很短，而且它给的 id
    /// 与这里的 id 是同一个——两层注记叠在一起只会让模型分不清该照着哪句做。</para>
    /// </summary>
    private static TimelineEvent Truncate(TimelineEvent evt, IReadOnlyDictionary<string, string> toolNames)
    {
        if (evt is not TimelineEvent.ToolResultEvent { Content: [Block.TextBlock text] } result) return evt;

        var budget = Tools.ToolLimits.DeliveryBudgetOf(toolNames.GetValueOrDefault(result.CallId));
        var bytes = Encoding.UTF8.GetBytes(text.Text);
        if (bytes.Length <= budget) return evt;

        // ⚠️ **只保留开头**，尾部换成注记；`offset` = 实际交付的字节数（注记不算），
        // 并且回退到字符边界——否则 `fetch(offset)` 会把注记的一段再喂回来。
        var delivered = DeliveryPrefixBytes(bytes, budget);
        var head = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes, 0, delivered);
        var note = TruncationNoteText(result.Id, delivered, bytes.Length);
        return result with { Content = [new Block.TextBlock(head + note)] };
    }

    /// <summary>
    /// 注记——**渲染的一个输入，不是记录里的标记**（与 <see cref="Reclaim.NoteText"/> 同族）。
    ///
    /// <para>⚠️ 必含三样：**截断标记** · **稳定 id** · **下一个偏移**。
    /// 少了第一样，模型以为自己看到了全部；少了第二样，取不回；少了第三样，模型不知道从哪接。
    /// ⚠️ 偏移给的是**字节位置**，与 `read` 的 `offset` 语义一致。</para>
    ///
    /// <para>⚠️ <b>不许写「什么时候截断的」</b>：一旦可变就每步破一次前缀
    /// （渲染必须是纯的）。同一份记录必须得到逐字节相同的一句话。</para>
    /// </summary>
    public static string TruncationNoteText(string stableId, int delivered, int total) =>
        $"\n\n[已截断] 这条结果共 {total} 字节，只显示了前 {delivered} 字节。"
        + $"余下部分用 fetch(id=\"{stableId}\", offset={delivered}) 取回；原文已保存，不必重跑工具。";

    /// <summary>
    /// 预算内的**字节前缀**，回退到 UTF-8 字符边界：被切开的半个字符属于我们，不属于内容。
    /// ⚠️ 解析时必须用**原来那段字节** 0..n 去解码，而不是先解码再按字符数切——
    /// 后者在中文上会多切或少切。
    /// </summary>
    private static int DeliveryPrefixBytes(byte[] bytes, int budget)
    {
        var cut = Math.Min(budget, bytes.Length);
        if (cut == 0 || cut == bytes.Length) return cut;

        // 切点落在续字节（10xxxxxx）上 ⇒ 往前走到那个字符的引导字节。
        // ⚠️ 走到引导字节本身就够：它前面的字符（0..cut）都是完整的。
        // 在这个位置上「那个字符装不装得下」不用再判——它整个都不在 [0, cut) 里。
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
        return cut;
    }

    /// <summary>同上，但**多一条判据**：被判定为**过时**的结果也换成注记（`01` §11.3 判据轴上的**点杀**）。

    /// <summary>
    /// ⚠️ <b>「这条记录要不要进「现在要用的那串事件」」</b>。
    ///
    /// <para>今天只有一种被挡在外面：<see cref="TimelineEvent.ReleaseEvent"/>——
    /// 它是**给我们自己看的锚**（倒查 / 线的判据），而不是要喂给模型的东西。</para>
    ///
    /// <para>⚠️ <b>它挡的是「渲染」，不是「记录」</b>：释放事件照样落盘、照样是判定的输入
    /// （`Reclaim.Decide` 从**记录**读它）。</para>
    /// </summary>
    private static bool IsRenderable(TimelineEvent evt) => evt is not TimelineEvent.ReleaseEvent;

    /// <summary>
    /// 把框架渲染成**一个独立的文本块**，恒定在<span>内容之前</span>（`01` §3.7）。
    ///
    /// <para>⚠️ <b>「独立的块」是这条机制的全部要害，不是排版偏好</b>：模型只能靠
    /// 「这是一个<b>块</b>」来区分框架与内容。合并进同一个块之后，
    /// **一个首行恰好长得像框架的内容**就能冒充框架，而模型**没有任何办法分辨**。</para>
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
    /// 库对象有<b>两种</b>存法（原文 / 块数组 JSON），读的人必须分得开。</para>
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
    /// <para>⚠️ <b>读不出来时给一段说得清的话，不抛</b>：抛会让**整个会话再也发不出消息**
    /// （记录只追加，那条坏引用永远在）。而「库对象没了」是**可以自愈的**。</para>
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
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
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

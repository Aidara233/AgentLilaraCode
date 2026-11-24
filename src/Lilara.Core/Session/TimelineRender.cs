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

        // ⚠️ **头这份不解图片字节**（2026-10-29）：它有 `GET /store/{sha256}` 自己去取，
        // 而一次历史渲染要为每张图读一遍盘是没道理的（一屏几张 5 MB 的图）。
        // 「谁需要字节」只有一条判据：**要发给模型的那一份**。
        return Render(store, handle, raw, truncateForDelivery: false, materializeImages: false);
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
        SessionStore store, SessionHandle handle, IReadOnlyList<TimelineEvent>? extra = null)
    {
        var raw = store.ReadTimeline(handle.ProjectKey, handle.SessionId);

        // ⚠️ **图在这里"解成字节"，不吃的提供商在这里降级成占位**（2026-10-29，`03` §9.2）。
        // 两条判据都只有这一处：
        //   · 要字节 ⇒ 因为适配器只看得见块、看不见库（`04` §5）；
        //   · 不吃图 ⇒ 因为**历史上留下的图**在换提供商之后仍然在记录里，而记录只追加
        //     ——不降级的话，那个会话从此每一步都被上游拒（用户无从补救：他删不掉那些块）。
        var rendered = Render(store, handle, raw, truncateForDelivery: true, materializeImages: true);

        // ⚠️ **半截的那条模型消息要当场说明白**（2026-11-07，用户裁定：流式模式下打断要保留
        // 半截生成结果）。⚠️ 加的是**渲染期的一块注记**，不是正文：盘上只有事实
        // （`MessageEvent.Interrupted`），那句话由 `Interruption.TruncatedText()` 一处给。
        // ⚠️ **只在「这条里没有 `tool_use`」时加**：带工具调用的消息是**完整**的那一种
        // （工具相位被打断时它一字不缺），往一条带 `tool_use` 的 assistant 消息末尾补一块正文
        // 会让部分上游把它读成「调用之后还有话」——那是另一类协议形状。
        rendered = [.. rendered.Select(MarkTruncated)];

        // ⚠️ **浮动块不经过这里**（2026-10-26 用户裁定）：它由推进器在**装配请求时**现插
        // （`Stepper.FloatingContextPair`），不落库、也不从任何文件读回来。
        // ⇒ 渲染层只管记录：少一条来源，就少一条会漂的判据。
        if (extra is not { Count: > 0 }) return rendered;

        // ⚠️ **追加在末尾**（2026-10-28 用户裁定，改掉 10-26 的「插在最前面」）。
        // 理由两条，第二条是硬的：
        // ① 「此刻的读数」本来就该是**最后读到的**那一份——放在最前面反而要求读者回头找它，
        //    历史越长它离末尾越远，越容易被当成过时前言。
        // ② **前缀缓存**（可验证，不是偏好）：这一对的 id 带步号（`floating-{turn}-*`）、
        //    正文里带「上次调用上下文总长度」这类每步都变的数 ⇒ 只要它待在**历史之前**，
        //    历史段就从它的第一个 token 起与上一步不匹配 ⇒ 除 system/premier/tools 之外
        //    **一次都不命中**（实测：21 万上下文只命中 8k，恰好是头部那一段）。
        //    挪到末尾之后，共同前缀 = 头部 + 整段历史（纯追加），变化被关在最后一对里，
        //    **不挡任何东西**。
        //
        // ⚠️ **一次请求里只许出现一个真实块**：这一对的 id 由 turn 决定，记录里若已有同号
        // （崩溃后重放、或核心在步末补过占位），插进去就是**两条同 `CallId` 的 `tool_use`**
        // ⇒ 上游按 `call_id` 配对时会撞车，正是「一组调用里出现两个块」那种形态。
        // ⇒ 只要占用了同一个 id，这一条就不插。
        var taken = rendered.SelectMany(IdsOf).ToHashSet(StringComparer.Ordinal);
        var fresh = extra.Where(e => IdsOf(e).All(id => !taken.Contains(id))).ToList();
        // ⚠️ 末尾（不是开头）：见上面那段——顺序同时决定「读者在哪看到状态」与「缓存命中多少」。
        return fresh.Count == 0 ? rendered : [.. rendered, .. fresh];
    }

    /// <summary>
    /// 一条事件在**配对**意义上占用的 id：消息自己的 id 与它里面每个 `tool_use` 的 `CallId`；
    /// 结果的事件 id 与 `CallId`（同号是常态，但别处不保证，两个都算）。
    ///
    /// <para>⚠️ 它只有 <see cref="RenderForModel"/> 一个用途：判断「这块插进去会不会撞上已有的那一对」。
    /// 判据必须比「事件 id 相同」更宽——撞车真正发生在上游按 `CallId` 配对那一步。</para>
    /// </summary>
    /// <summary>
    /// **半截的模型消息：补一块「它没说完」的注记**（2026-11-07）。
    ///
    /// <para>⚠️ <b>为什么不是把这句话写进正文</b>：正文是**模型自己说过的话**，
    /// 往里掺一句我们的，等于让记录说它没说过的话（`01` §3.6 ①之二）。
    /// 盘上只存 `Interrupted = true` 这个事实，一句人话在这里现渲染。</para>
    ///
    /// <para>⚠️ **带 `tool_use` 的消息一律不加**：那种消息是完整的（工具相位被打断），
    /// 而一块跟在 `tool_use` 之后的正文在部分上游是另一种形状。</para>
    /// </summary>
    private static TimelineEvent MarkTruncated(TimelineEvent evt) =>
        evt is TimelineEvent.MessageEvent { Interrupted: true } m
        && !m.Blocks.OfType<Block.ToolUseBlock>().Any()
            ? m with { Blocks = [.. m.Blocks, new Block.TextBlock(Interruption.TruncatedText())] }
            : evt;

    private static IEnumerable<string> IdsOf(TimelineEvent evt) => evt switch
    {
        TimelineEvent.MessageEvent m =>
            [evt.Id, .. m.Blocks.OfType<Block.ToolUseBlock>().Select(u => u.CallId)],
        TimelineEvent.ToolResultEvent r => [evt.Id, r.CallId],
        _ => [evt.Id],
    };

    /// <summary>
    /// 两条入口（外加压缩那一次加载）共用的那几步：**压缩投影** → 解库引用 → 按记录 id 回收
    /// → 投递截断 → 加框架 → 汇总摘要。
    ///
    /// <para>⚠️ <b>压缩投影在<b>最前</b>，而这是 2026-10-17 合并两个独立实现时的关键一条</b>
    /// （`75efa1a` 的持久压缩 × 本批的投递截断，两者都改过这个类）：投影换的是**看哪一段历史**，
    /// 回收/截断/框架换的是**怎么渲染那一段** ⇒ 后者必须以投影后的 `visible` 为输入。
    /// 反过来（在完整 `raw` 上回收再切）会让判定看到压缩窗口之外的记录。</para>
    ///
    /// <para>⚠️ <b>摘要事件必须插在<b>最前</b>、且在框架之前</b>：摘要不是工具结果
    /// （`WithFraming` 不会碰它），而它代表的是被压缩掉的那一整段历史，位置就是它所在的位置。</para>
    ///
    /// <para>⚠️ <b>渲染<b>不</b>决定回收，只读已经落盘的窗口</b>（2026-10-17 更正）：
    /// `Reclaim.Decide` 只看 `ReclaimEvent` / `ReleaseEvent` 这两种记录——前者由
    /// `session.gc` / 压缩在开窗那一刻解析好，后者由模型画线（⚠️ 而**线要先在「会话落定」那一刻
    /// 兑成一条窗口**，判据与理由见 `Reclaim.ReleaseIds` / `Reclaim.Decide`）。渲染<b>不带</b> `staleProbe`、
    /// 也不数保护带：那些判据会在每一步重新解析，而回收改的是前缀中段
    /// ⇒ 每步破一次缓存，且模型刚读到的内容在下一步就消失（`00` §3：热路径不做事后清理）。</para>
    /// </summary>
    private static IReadOnlyList<TimelineEvent> Render(
        SessionStore store, SessionHandle handle, IReadOnlyList<TimelineEvent> raw,
        bool truncateForDelivery, bool materializeImages)
    {
        // ⚠️ **压缩投影**（`75efa1a`）：换的是「看哪一段历史」，不是「怎么渲染」。
        var projection = ContextCompaction.Project(store, handle, raw);
        var visible = projection.Suffix;
        var blobs = new BlobStore(store.DataRoot);

        // ⚠️ **这个提供商吃不吃图**（2026-10-29，`03` §9.2 的实现）。⚠️ 判据只有这一处：
        // 声明缺席（旧 providers/*.json / 已删除的定义）**按吃图处理**——那批会话以前
        // 可能就靠图工作，把「没声明」读成「不许」会静默改变它们的行为。
        var vision = !materializeImages || DeclaresVision(store, handle);

        // ⚠️ **判定只读记录里的窗口**（2026-10-17）：窗口在什么地方被写下来，
        // 回收就只在什么地方发生。渲染本身是纯的、不碰文件系统、不做时间判定。
        var reclaimed = new HashSet<string>(Reclaim.Decide(visible, blobs), StringComparer.Ordinal);

        var applied = Reclaim.Apply(
            [.. visible.Select(e => Dereference(e, blobs, materializeImages, vision))], reclaimed);

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
        //
        // ⚠️ **只给「摘要不在记录里」的检查点插**（2026-10-22，压缩改成工具）：
        // 新检查点的摘要在那条 `compact` 调用的参数里，而那条调用就在 `visible` 末尾——
        // 模型带着它去请求，再插一条 `From = Speaker.User` 的合成消息就是同一段话说两遍。
        // 旧检查点（那一行里还带着 `summary`）与「记录里找不到那条调用」的异常形态 ⇒ 照旧插。
        //
        // ⚠️ **摘要取不到就不插**：一个只有包边、中间空着的「历史摘要」会让模型去猜
        // 那里被吃掉了什么（`SystemBlock.Wrap` 的同一条判据）——那不重建历史，只多一条假信息。
        if (projection.Checkpoint is { } checkpoint
            && !ContextCompaction.SummarySitsInCall(checkpoint, visible)
            && ContextCompaction.SummaryOf(checkpoint, raw) is { Length: > 0 } summary)
            result.Insert(0, ContextCompaction.SummaryEvent(checkpoint, summary));

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

    /// <summary>
    /// ⚠️ <b>「这条记录要不要进「现在要用的那串事件」」</b>。
    ///
    /// <para>今天有两种被挡在外面：<see cref="TimelineEvent.ReleaseEvent"/>（线）与
    /// <see cref="ReclaimEvent"/>（窗口记录）——它们都是**给我们自己看的锚**
    /// （倒查 / 回收的判据），而不是要喂给模型的东西。</para>
    ///
    /// <para>⚠️ <b>它挡的是「渲染」，不是「记录」</b>：两种记录照样落盘、照样是判定的输入
    /// （`Reclaim.Decide` 从**记录**读它）。</para>
    /// </summary>
    private static bool IsRenderable(TimelineEvent evt) =>
        evt is not TimelineEvent.ReleaseEvent and not ReclaimEvent;

    /// <summary>
    /// 把框架渲染成**一个独立的文本块**，恒定在<span>内容之前</span>（`01` §3.7）。
    ///
    /// <para>⚠️ <b>「独立的块」是这条机制的全部要害，不是排版偏好</b>：模型只能靠
    /// 「这是一个<b>块</b>」来区分框架与内容。合并进同一个块之后，
    /// **一个首行恰好长得像框架的内容**就能冒充框架，而模型**没有任何办法分辨**。</para>
    ///
    /// <para>⚠️ <b>没有 <c>Framing</c> 的记录原样返回</b>（老记录、以及不需要框架的工具）
    /// ⇒ 逐字节与今天相同。</para>
    ///
    /// <para>⚠️ <b>末尾那个 <c>\n</c> 是判据的一部分，不是排版</b>（2026-10-23 实测）：
    /// 文本化链路（Chat 的 <c>string.Concat</c>、Claude 的拼串）把相邻块直接接起来，
    /// 不留分隔符就会得到 <c>[read path foo]正文</c> 甚至 <c>[floating_context][lilara 上下文]</c>
    /// ——两个方括号贴在一起，「一个块套另一个块」在形状上当场失效（`01` §3.3）。
    /// ⚠️ 合成对那边（`Stepper.FloatingContextPair`）逐字加同一个换行，否则两份分叉。</para>
    /// </summary>
    // ⚠️ **这里曾经有一个 `SupersedeFloating`（2026-10-23 加、2026-10-30 删）：它把
    // 旧浮动读数整对从**渲染**里摘掉，并给最新那份加一行「第 a–b 步的读数已取代」。**
    //
    // 它为什么必须消失（而不是「留着不用也行」）——**它是这个仓唯一一处「在历史中段改写字节」**：
    //   · 每多一份读数，就把上一份从渲染里拿掉 ⇒ 请求从**那份旧读数的位置**起与上一步不同；
    //   · 那个位置离末尾多远，完全取决于「上一次读数变化」发生在多久以前（改轴、改计划、
    //     嵌 100k 档都会触发）⇒ 用户看到的是**绝大部分步命中、隔一阵子掉一大块**，
    //     而掉的那一块大小毫无规律。实测账本（`~/.lilara/usage.jsonl`，385 步那个会话）：
    //     `cached` 从 154,496 一下退到 143,616、再退到 156,672 —— `requestFingerprint` 两次都**没变**
    //     （头部没动），掉的就是历史中段那一段。⇒ 症状与「每步都破前缀」不同，更隐蔽。
    //   · 而被它摘掉的内容**模型再也看不见**（记录里还有，渲染里没有），这也和
    //     「回收只换渲染、且只在失效窗口里动手」那条纪律冲突。
    //
    // ⇒ 今天的规矩：**读数只在变化时**追加一份到历史末尾（`Stepper.AppendReadout`），
    // 旧读数**原样留着**；要把它们收起来，走 `Reclaim` 的窗口（`session.gc` / 压缩），
    // 也就是说「什么时候允许破中段前缀」永远由一条**落盘的窗口**回答，不在这里现算。
    // 需要「旧读数不该无限堆积」的判据时，请加在 `Reclaim.Resolve` 里，**不要**加回渲染。

    private static TimelineEvent WithFraming(TimelineEvent evt) =>
        evt is TimelineEvent.ToolResultEvent { Framing: { } framing } r
            ? r with { Content = [new Block.TextBlock(framing.Render() + "\n"), .. r.Content] }
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
    private static IReadOnlyList<Block> Dereference(
        Block block, BlobStore blobs, bool materializeImages = false, bool vision = true)
    {
        // ⚠️ **图片块那条路**（2026-10-29）：记录里那块是**地址**，而适配器要**字节**。
        // 三个出口都在这一个 switch 里，因为它们必须**同进同出**（谁也不会漏一条）：
        //   · 要字节、字节在、提供商吃图 ⇒ 贴 `Bytes`（JSON 形状不变，见 `ImageBlock`）；
        //   · 要字节、字节没了 ⇒ 一句「这张图找不到了」（可自愈，不抛）；
        //   · 提供商不吃图 ⇒ 一句占位（`03` §9.2 的渲染降级）——**记录里的图仍然在**，
        //     换回吃图的提供商就又能看见。
        if (block is Block.ImageBlock image)
        {
            if (!materializeImages) return [block];

            if (!vision)
                return [new Block.TextBlock(
                    $"（这里有一张图片（{image.MediaType}），但当前提供商的模态声明不是 vision，"
                    + "这张图没有随请求发出去。）")];

            var imageBytes = blobs.TryGet(image.Sha256);
            return imageBytes is null
                ? [new Block.TextBlock($"（这里本该有一张图片，但库对象 {image.Sha256[..8]}… 找不到了）")]
                : [image with { Bytes = imageBytes }];
        }

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
                // ⚠️ 解析出来的块**还要再过一遍**（2026-10-29）：一份混排结果里可以带图片，
                // 而图片的字节在**另一个**库对象里 ⇒ 不解这一步，适配器拿到的是没有字节的图。
                if (blocks is { Count: > 0 })
                    return [.. blocks.SelectMany(b => Dereference(b, blobs, materializeImages, vision))];
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
    private static TimelineEvent Dereference(
        TimelineEvent evt, BlobStore blobs, bool materializeImages = false, bool vision = true) => evt switch
    {
        TimelineEvent.MessageEvent m => m with
        {
            Blocks = [.. m.Blocks.SelectMany(b => Dereference(b, blobs, materializeImages, vision))],
        },
        TimelineEvent.ToolResultEvent r => r with
        {
            Content = [.. r.Content.SelectMany(b => Dereference(b, blobs, materializeImages, vision))],
        },
        _ => evt,
    };

    /// <summary>
    /// **这个会话绑定的提供商声明自己吃图吗**（`ProviderDefinition.Modality`）。
    ///
    /// <para>⚠️ <b>这是这个字段第一次有读取点</b>（2026-10-29，写它的时候注释就写着
    /// 「最小闭环用不到」——它一直是个没有消费者的声明）。判据是 `modality == "vision"`；
    /// 今天头那边的选项只有 vision / text 两档。</para>
    ///
    /// <para>⚠️ <b>「读不到」按吃图处理</b>：定义被删了、文件坏了、老文件里没有这一格……
    /// 这些情形的共同点是**我们不知道**。而两个方向的代价不对称——猜「吃图」最坏是上游
    /// 拒一次（可见、可改配置）；猜「不吃」会让一批本来能看的会话**静默看不见图**。</para>
    /// </summary>
    private static bool DeclaresVision(SessionStore store, SessionHandle handle)
    {
        try
        {
            var provider = new Providers.ProviderStore(store.DataRoot).Load(handle.Spec.Model.ProviderId);
            return provider is null || !string.Equals(provider.Modality, "text", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // 读不出来 ⇒ 不知道 ⇒ 不改变既有行为（见上面那条）。
        }
    }
}

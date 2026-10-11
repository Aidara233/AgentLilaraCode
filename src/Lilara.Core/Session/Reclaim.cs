using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// **回收**——`01` §8.0.2 那一整节契约的落地。
///
/// <para>⚠️ <b>回收不是「删掉」，是「换个渲染」</b>：磁盘一字不动，只改喂给模型的那份。
/// ⇒ 本文件<b>没有任何写盘调用</b>，这不是纪律，是结构（它压根拿不到写盘的手）。</para>
///
/// <para>⚠️ <b>为什么判定与执行挤在一个类里</b>：它们是<b>同一个谓词的两半</b>——
/// 「哪条可回收」与「回收长什么样」若各写一份，早晚会漂（本仓栽过的
/// 「两层判据必须共用一个谓词」）。</para>
/// </summary>
public static class Reclaim
{
    /// <summary>
    /// 保护带：最近多少条工具结果**不动**（`01` §11.3「GC 保护窗」）。
    ///
    /// <para>⚠️ <b>这个数是占位的</b>——§11.3 只写了「最近 N 步不优化」，N 本身是**未定项**。
    /// 取一个保守值并如实标注它是占位（同 `CommandDispatcher.MaxConsecutiveToolSteps`）。
    /// 按「条数」而不是 token 的理由见 §11.3：保护带保护的是**动作链**，而 token 会不可预测地塌缩。</para>
    /// </summary>
    public const int ProtectedRecentResults = 4;

    /// <summary>
    /// **那个源现在长什么样** —— 三态判定（`00` §5）**第一个真的会去读它的消费者**（2026-10-11）。
    ///
    /// <para>⚠️ <b>它把「点杀」判据从一句纸上契约变成一次真比对</b>：记录里存着
    /// <see cref="TimelineEvent.ToolResultEvent.DependsOn"/>（依赖什么）与
    /// <see cref="TimelineEvent.ToolResultEvent.Fingerprint"/>（当时什么样），
    /// 本方法**当场重解析那个指称、重算指纹、与记录里那个比**。</para>
    ///
    /// <list type="bullet">
    ///   <item><b>没依赖 / 没指纹</b>（`list_dir`、发邮件、取时间）⇒ 恒 <c>false</c>。
    ///   ⚠️ 这是 `00` §5 那条硬约束，不是漏了：<b>无指纹 ⇒ 永不可优化</b>。
    ///   ⚠️ 而且它以 <c>Fingerprint is null</c> 为准，**不看 <c>DependsOn</c> 有没有值**——
    ///   一个填了引用却没填指纹的工具，我们**判不了**它动没动，只能当作没动。</item>
    ///   <item><b>路径越出工作目录 / 文件不在了</b> ⇒ 恒 <c>false</c>。
    ///   ⚠️ 「不在了」**不是**「不动了」——一个已删的文件同样可能被重建、也可能正是模型要的信息。
    ///   要收它得另有判据（今天没有）。⇒ 走 <see cref="IToolContext.ResolvePath"/> 那**一道**
    ///   越界闸，与工具执行期共用同一个谓词（`DependsOn` 是**模型造出来的**，它不可信）。</item>
    ///   <item><b>比得上 / 比不上</b> ⇒ 逐字节相同就是「没动」，一个字节不同就是「动了」。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>它必须是纯的、而且不抛</b>：调用方是渲染那条路，读失败 / 形状坏
    /// 一律退成 <c>false</c>（= 当作没发现过时）。⚠️ 但<b>纯是调用方保证的</b>——
    /// 「哪一刻读的」由调用方冻一次（同 <c>Stepper</c> 里的 <c>now</c>），
    /// 否则**步内两次渲染**就能给出两个答案，而那是每步破前缀。</para>
    ///
    /// <para>⚠️ <b>不能把结论缓存起来当真相来源</b>（这一点是上一批刚栽过的）：
    /// 可缓存的是**位置**（状态型脏指示，`01` §12.7），不是判定。缓存的判定只在
    /// 「某个维护点跑过之后」才新鲜，而没人能保证那个维护点覆盖了所有请求路径。</para>
    /// </summary>
    public static bool Stale(TimelineEvent.ToolResultEvent result, IToolContext context)
    {
        // ⚠️ **`Fingerprint is null` 是这一句的主语**：无指纹 = 不可重演 = 永不过时。
        if (result.Fingerprint is not { } recorded) return false;
        if (string.IsNullOrWhiteSpace(result.DependsOn)) return false;

        var bytes = ReadSource(result.DependsOn, context);

        // 读不到（越界 / 不存在 / 形状坏）⇒ 不作断言。
        if (bytes is null) return false;

        // ⚠️ 重算走**工具那一份**（`ToolPaths`），不在这里另写一遍——
        // 各写一遍就是「两层判据必须共用一个谓词」那个老形状，
        // 而漂的后果是**每一条都比不上**（于是每一条都被判成过时）。
        return !string.Equals(
            ToolPaths.FingerprintOf(result.DependsOn.Replace('\\', '/'), bytes),
            recorded, StringComparison.Ordinal);
    }

    /// <summary>
    /// 按 <see cref="TimelineEvent.ToolResultEvent.DependsOn"/> 读回那个源的**字节**。
    ///
    /// <para>⚠️ <b>`DependsOn` 今天只被文件类工具写过一个相对路径</b>（`read` / `write` / `update`
    /// 都写 `rel`，`list_dir` 不写）⇒ 这里**当名义相对路径**用。而它的形状是**工具自己定的**
    /// （`01` §3.6：HTTP 工具是 URL、DB 工具是连接串）⇒ 将来出现别的形状时，
    /// **改的是这个开关**，不是 <see cref="Stale"/> 的判据。
    /// ⇒ 不认识的指称**一律返回 <c>null</c>**（= 判不了），而不是猜一种读法。</para>
    /// </summary>
    private static byte[]? ReadSource(string dependsOn, IToolContext context)
    {
        var full = context.ResolvePath(dependsOn);
        if (full is null) return null;

        try
        {
            return File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 目录、权限、竞态删掉——一律「读不到」。**不抛**：调用方在渲染那条路上。
            return null;
        }
    }

    /// <summary>
    /// 这段内容**自己就够**——不需要去库里补一个**缺失**的对象。
    ///
    /// <para>⚠️ <b>它才是「可回收」的真正判据，而它跟「在不在库里」不是一回事</b>
    /// （这一点我第一版写错了，见下面那段）。回收只换渲染、<b>记录一个字不动</b>
    /// （`01` §8.0.2 一 / 八）⇒ 只要内容**还在记录里**，`fetch` 就取得回。</para>
    ///
    /// <list type="bullet">
    ///   <item><b>内联的块</b>（小结果、混排、纯图）⇒ <b>记录里就是它本身</b> ⇒ 够。</item>
    ///   <item><b><see cref="Block.StoreBlock"/></b> ⇒ 要看那个对象在不在库里。
    ///   不在 ⇒ 内容**真的没了**，回收换出来的那句「已回收、可 fetch」就是**假话**。</item>
    ///   <item><b>空</b> ⇒ 没有内容可回收。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>它跑的永远是<b>记录</b>，不是上一次渲染的结果</b>——注记从不落盘
    /// （<see cref="Apply"/> 不写盘），所以「照着注记再判一次」这件事根本不会发生。
    /// ⇒ 同一条记录反复判定得到同一个结论，<b>回收是幂等的</b>。</para>
    ///
    /// <para>⚠️ <b>第一版错在哪</b>（记下来，因为它是本仓反复栽的那个形状）：我按
    /// `01` §8.0.2 二那句「可回收的充要条件是事实：快照真的落盘了」推出「必须在库里」，
    /// 于是**内联快照被判成不可回收**。而 §8.0.2 二自己紧跟着写着
    /// 「今天所有结果都是块、都可序列化 ⇒ <b>恰好恒真</b>」——<b>内联就是落盘</b>。
    /// 窄读一个字，造出一整类「收不了」的结果（小文件），而回收器存在的意义正是收它。
    /// ⇒ 判据落在「取不回就不许回收」，不落在「字节躺在哪个文件里」。</para>
    /// </summary>
    public static bool Recoverable(IReadOnlyList<Block> content, BlobStore blobs) =>
        content.Count > 0
        && content.All(b => b is not Block.StoreBlock store || blobs.Has(store.Sha256));

    /// <summary>
    /// 这一批该回收哪些（按 <see cref="TimelineEvent.ToolResultEvent.Id"/>）。
    ///
    /// <para>判据是<b>事实</b>，不是声明（`01` §8.0.2 二）：
    /// <b>可回收 ⟺ `fetch` 取得回 ⟺ 内容自己就够</b>（见 <see cref="Recoverable"/>）。
    /// ⇒ 把「靠工具自觉声明类别」整条换掉了，也因此不需要任何新字段。</para>
    ///
    /// <para>⚠️ <b>大小、形状都不进这个判据</b>（用户 2026-10-06 裁定）：
    /// 小文件内联、大文件留地址、混排存块数组——<b>三条一律可回收</b>。
    /// 区分只在「持久化到哪」，<b>模型视角不分大小</b>。
    /// ⇒ 判据也不区分类别（§8.0.2 三已经按类别分那件事整条摘掉了）。</para>
    ///
    /// <para>⚠️ <b>没有「本应进库却没进」这一档</b>（用户裁定「不留历史包袱，不加额外兜底」）：
    /// 内容够就够了，不够就**不算可回收**，<b>不抛也不补</b>。
    /// 老记录里那些指向缺失对象的行正落在这里。</para>
    /// </summary>
    public static IReadOnlySet<string> Decide(IReadOnlyList<TimelineEvent> timeline, BlobStore blobs)
    {
        // 保护带按「最近的工具结果」数，所以先只挑出工具结果这一类。
        var candidates = timeline.OfType<TimelineEvent.ToolResultEvent>().ToList();

        var result = new HashSet<string>(StringComparer.Ordinal);

        // 从后往前数：末尾 ProtectedRecentResults 条是保护带，不动。
        for (var i = 0; i < candidates.Count - ProtectedRecentResults; i++)
        {
            var r = candidates[i];
            if (Recoverable(r.Content, blobs)) result.Add(r.Id);
        }

        return result;
    }

    /// <summary>
    /// 注记——**渲染的一个输入，不是记录里的标记**（`01` §8.0.2 四）。
    ///
    /// <para>必含三样，一条不少（那张表列的就是这三样）：</para>
    /// <list type="number">
    ///   <item><c>[已回收]</c> 这个标记本身——缺了模型**以为自己看到了全部**</item>
    ///   <item><b>取回地址</b>——缺了拿不回来。⚠️ 给的是<b>记录</b>的稳定 id，
    ///   不是协议 <c>CallId</c>（两个 id 的分工见 §8.0.2 七）</item>
    ///   <item>「要现在的值就自己重调一次」——缺了模型不知道它还有另一条路</item>
    /// </list>
    ///
    /// <para>⚠️ <b>不写「什么工具 / 参数摘要」</b>：它们就紧挨着在 <c>tool_use</c> 里——
    /// 删了锚再把锚的信息抄回注记，抄一遍还丢了关联（§8.0.2 四）。</para>
    ///
    /// <para>⚠️ <b>不许写「什么时候回收的」「这是第几次」</b>：一旦可变就**每步破一次前缀**
    /// （`00` §5 铁律 1：渲染必须是纯的）。⇒ 同一份状态必须得到逐字节相同的一句话。</para>
    ///
    /// <para>⚠️ <b>写成一条能直接抄走的调用</b>，不是给人看的陈列形：模型下一步就要发出它。
    /// （`01` §8.0.2 六那张示意图里的 <c>fetch(call#7f3a)</c> 是<b>示意</b>，不是要发的字节。）</para>
    /// </summary>
    public static string NoteText(string stableId) =>
        $"[已回收] 这条结果被回收了。fetch(id=\"{stableId}\") 取回当时的内容；"
        + "要现在的值，就自己重调一次那个工具。";

    /// <summary>
    /// 把回收**应用**到一条已渲染的时间线上：被回收的结果换成注记，其余原样。
    ///
    /// <para>⚠️ <b>只降结果那一半</b>（§8.0.2 四）：模型消息里的 <c>tool_use</c> 一个字不动
    /// ——它是模型自己的输出（事实），而且是那里的**锚**，模型看见自己发过
    /// <c>read(src/auth.cs)</c>、紧跟一条说「这个被回收了」，关联是**位置**给的。</para>
    ///
    /// <para>⚠️ <b>记录不动</b>：这里产出的是<b>这一次渲染</b>的形态，写回盘里的仍是原文
    /// （§8.0.2 一 / 八）。⇒ 不管收多少次，<b>内容本身都还在</b>，
    /// `fetch` 便一直取得回——这正是回收敢做「只换渲染」的底气。</para>
    /// </summary>
    public static IReadOnlyList<TimelineEvent> Apply(
        IReadOnlyList<TimelineEvent> timeline, IReadOnlySet<string> reclaimed)
    {
        if (reclaimed.Count == 0) return timeline;

        return [.. timeline.Select(evt =>
            evt is TimelineEvent.ToolResultEvent r && reclaimed.Contains(r.Id)
                ? r with { Content = [new Block.TextBlock(NoteText(r.Id))] }
                : evt)];
    }

    /// <summary>
    /// **写入侧**：决定快照落在记录里还是库里——而现在「进不进库」不再是二选一。
    ///
    /// <para>⚠️ <b>字节<b>总是</b>进库</b>（2026-10-06，用户裁定 + `01` §8.4 ¶1
    /// 「凡是被记录引用的字节，都在库里」）。<see cref="BlobStore.InlineThresholdBytes"/>
    /// 只决定**记录里那份**是原文还是 <see cref="Block.StoreBlock"/> 地址：</para>
    /// <list type="bullet">
    ///   <item><b>单个文本块，过线</b> ⇒ 记录里留地址（今天的行为）</item>
    ///   <item><b>单个文本块，没过线</b> ⇒ 记录里**也**内联一份（jsonl 好手查）</item>
    ///   <item><b>混排 / 多块</b> ⇒ 记录里原样（块数组读起来正好；它不一定大，不必降成地址）</item>
    /// </list>
    ///
    /// <para>⚠️ <b>为什么小的一样要进库</b>：内容寻址让「同一个对象被两条记录引用」
    /// 只存一份，跨会话也能去重。⚠️ 而它**不是**「能不能回收」的条件——
    /// 回收的判据是内容自己在不在（见 <see cref="Recoverable"/>），内联的照样收得了。</para>
    /// </summary>
    public static IReadOnlyList<Block> Materialize(IReadOnlyList<Block> content, BlobStore blobs)
    {
        if (content.Count == 0) return content;

        // ⚠️ 单文本块存的是**原始字节**（`text/plain`），其余存的是**块数组 JSON**
        // （`BlockList.MediaType`）。两条路的库对象语义不同，别合并：合并之后
        // `read` 的库对象会变成一段 JSON，而它读出来本该是原文。
        var bytes = content is [Block.TextBlock text]
            ? Encoding.UTF8.GetBytes(text.Text)
            : Encoding.UTF8.GetBytes(BlockList.ToJson(content));
        var mediaType = content is [Block.TextBlock] ? "text/plain" : BlockList.MediaType;

        // ⚠️ **先 Put 再判**：判据用的地址就是这里写进去的那个；
        // 反过来（先判再 Put）在边界上会留下「记录说内联、库里没有」的缝。
        var sha = blobs.Put(bytes);

        // ⚠️ **「过线就留地址」与形状无关**：只让文本那一条路有内联/进库之分，
        // 会让混排莫名其妙地特例化，也把 `StoreBlock` 的 `mediaType` 变成摆设。
        // （可回收性与这个降级无关——见 `Recoverable`。）
        return bytes.Length <= BlobStore.InlineThresholdBytes ? content : [new Block.StoreBlock(sha, mediaType)];
    }
}

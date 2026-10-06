using System.Security.Cryptography;
using System.Text;

namespace Lilara.Core.Storage;

/// <summary>
/// 库：**内容寻址的不可变大对象**（`01` §8.4）。落点 `&lt;数据根&gt;/store/&lt;sha256&gt;`。
///
/// <para>⚠️ <b>名字就是这个对象的全部身份</b>：哈希一样的字节只存一份（跨会话去重），
/// 所以 <see cref="Put"/> <b>天然幂等</b>——重复写不产生第二个文件，也不改既有字节。</para>
///
/// <para>⚠️ <b>不做引用计数</b>（2026-10-06 的决定，别读成漏了）：计数是 GC 那一整块的事
/// （`01` §11），而本类只做「写 / 读 / 内容寻址」。<b>后果：库只增不减</b>——
/// 可接受，因为 `history.jsonl` 本来也只增，盘上清理本来就是 GC 的活。</para>
///
/// <para>⚠️ <b>不校验读回来的字节</b>：内容寻址的价值在**去重**，而每次读都把整个对象
/// 重新哈希一遍（图片可能几 MB）是为一个本地文件系统上的、极不可能发生的事付真金白银。
/// ⇒ 刻意不做；要做的话是 GC / 体检那条路上的事，不是每次读的事。</para>
/// </summary>
public sealed class BlobStore(string dataRoot)
{
    /// <summary>
    /// 内联进 `history.jsonl` 的**上限**（字节）；超过它就该 <see cref="Put"/> 进库、只在记录里留引用。
    ///
    /// <para>⚠️ <b>这是 `01` §14 #15 那条「快照进库的阈值」的一个保守默认，不是定论。</b>
    /// 这一条**没有唯一正确的答案**，只有取舍：</para>
    /// <list type="bullet">
    ///   <item><b>调高</b> ⇒ jsonl 更好读、更好手查；但一行会大到拖慢只追加的写与逐行读</item>
    ///   <item><b>调低</b> ⇒ 库用得更充分、记录更瘦；但小对象散进两处，可读性变差</item>
    /// </list>
    ///
    /// <para>⚠️ <b>而「文本 vs 二进制」<i>不是</i>这条线</b>——真正的判据是**体积与身份**。
    /// 图片几乎必然超线（几 KB 起），但一段超大的文本工具结果同样该进库。
    /// ⇒ 别把这条读成「图片进库、文本内联」。</para>
    ///
    /// <para>⚠️ <b>今天它的作用是「字节躺在哪」，不是「进不进库」</b>（2026-10-06 更正——
    /// 这一段原先写「今天没有写入方 ⇒ 只被测试用到」，那句话在最小工具批之后就过期了）：
    /// `ToolExecutor.Materialize` **每次都 <see cref="Put"/> 一遍**工具结果的字节
    /// （`01` §8.4 ¶1「凡是被记录引用的字节，都在库里」），过线才在记录里留
    /// <see cref="Kernel.Block.StoreBlock"/>、没过线则**内联进记录而库里同样留一份**。</para>
    ///
    /// <para>⚠️ <b>它<b>不</b>决定「能不能回收」</b>：回收的判据是「库地址算得出来且库里有」
    /// （见 `Reclaim`），与这个数无关。⇒ 别把这两件事再绑到一起：绑起来的后果是
    /// **小文件永远占着上下文**——而回收器存在的意义正是收它。本条与 `01` §8.0.2 二
    /// 那句「今天所有结果都是块、都可序列化 ⇒ 恰好恒真」说的是同一件事。</para>
    /// </summary>
    public const int InlineThresholdBytes = 8 * 1024;

    private readonly string _root = Path.Combine(dataRoot, "store");

    public string Root => _root;

    /// <summary>
    /// 字节 → 库地址（小写 hex sha256）。
    ///
    /// <para>⚠️ <b>它是<b>唯一</b>一处算库地址的地方，<see cref="Put"/> 与判定侧都只走它。</b>
    /// 各写一遍的后果是<b>静默的</b>：编码、BOM、大小写任何一处漂了，
    /// 算出来的地址就<b>永远不在库里</b> ⇒ 回收判定一条也认不出来，而且一声不吭。
    /// （本仓栽过的形状：两层判据必须共用一个谓词。）</para>
    /// </summary>
    public static string Sha256Of(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private string PathFor(string sha256) => Path.Combine(_root, sha256);

    /// <summary>
    /// 存字节，返回它的 sha256（小写 hex）。
    ///
    /// <para>⚠️ <b>先算哈希、再写文件</b>：文件名就是哈希 ⇒ 崩溃留下的只可能是
    /// 一个孤儿 <c>.tmp</c>（<see cref="AtomicFile"/> 保证），**不可能出现一个「名字说它是 A、
    /// 内容是 B」的坏对象**。</para>
    /// </summary>
    public string Put(byte[] content)
    {
        var sha = Sha256Of(content);
        var path = PathFor(sha);

        // 已经有一份逐字节相同的了 ⇒ 什么也不做（这就是去重，不是优化）。
        if (File.Exists(path)) return sha;

        AtomicFile.WriteBytes(path, content);
        return sha;
    }

    public bool Has(string sha256) => IsSha256(sha256) && File.Exists(PathFor(sha256));

    /// <summary>读回来。不存在（或 id 形状不对）返回 <c>null</c>，不抛。</summary>
    public byte[]? TryGet(string sha256) =>
        IsSha256(sha256) && File.Exists(PathFor(sha256)) ? File.ReadAllBytes(PathFor(sha256)) : null;

    /// <summary>
    /// ⚠️ <b>这个闸不是可选的</b>：sha256 会**直接变成路径段**，而
    /// <c>Path.Combine(_root, "../../../etc/passwd")</c> 会读出数据根之外的东西。
    ///
    /// <para>判据是「**它是不是一个 64 位小写 hex**」，不是「它像不像一个哈希」——
    /// 与 `CommandDispatcher.IsSafeProviderId` 同一条纪律（那里挡的是
    /// 「含路径分隔符」，这里可以更严：哈希的形状是**完全确定**的）。
    /// 顺带：拒绝 `<c>..</c>` / 绝对路径 / 大写 hex 都**由这一句包了**，不需要第二条规则。</para>
    /// </summary>
    private static bool IsSha256(string? s) =>
        s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

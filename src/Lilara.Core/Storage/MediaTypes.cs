using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

/// <summary>
/// **从字节的开头认出它是什么**（2026-10-29）。
///
/// <para>⚠️ <b>为什么靠 magic 而不是扩展名</b>：扩展名是**文件名**的一部分，而文件名是
/// **人**定的——`a.png` 里放一段 zip、`data.bin` 里放一张图，都是日常。判据必须落在
/// <b>字节</b>上，因为下游要拿这个结论去决定「按文本解码 / 当图发出去 / 当文档抽文本」，
/// 猜错的代价是**静默的乱码**（把 PNG 当 UTF-8 读出来是一堆 U+FFFD）。</para>
///
/// <para>⚠️ <b>拿不准时给 <see cref="Text"/></b>：那是**改动之前**的唯一行为
/// （`read` 一律当文本读），所以「认不出来」不会让任何既有文件突然变得读不了。</para>
/// </summary>
public static class MediaTypes
{
    public const string Text = "text/plain";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";
    public const string Pdf = "application/pdf";
    public const string Zip = "application/zip";

    /// <summary>
    /// **认得、也发得出去的图片媒体类型**（白名单）。
    ///
    /// <para>⚠️ <b>它是白名单而不是「以 image/ 开头」</b>：上游对图片类型的支持是**枚举**的，
    /// `image/tiff` 之类发过去只会 400——而不带附件是**可见**的失败，带上一个上游不收的类型
    /// 是**不可见**的（请求直接被拒，用户以为是网络问题）。</para>
    ///
    /// <para>⚠️ <b>它住在本层（Storage），不往上依赖</b>：会话入口（`BlockKinds`）与三条适配器
    /// 都读这一份——三处各写一张表就是「同一个事实的第二副本」（`ArchitectureTests` 也钉着层向）。</para>
    /// </summary>
    public static IReadOnlyList<string> ImageTypes { get; } =
        [Png, Jpeg, Gif, Webp];

    /// <summary>这个媒体类型在不在上面那张白名单里（不区分大小写）。</summary>
    public static bool IsImage(string? mediaType) =>
        mediaType is { } m && ImageTypes.Contains(m, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按 magic 认类型。⚠️ **只认到「大类」为止**：zip 不细分 docx / xlsx（那要打开看目录，
    /// 属于文档抽取那一层的事，见 `DocumentText`）。
    /// </summary>
    public static string Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return Png;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return Jpeg;
        if (bytes.Length >= 6 && bytes[..6].SequenceEqual("GIF87a"u8) || bytes.Length >= 6 && bytes[..6].SequenceEqual("GIF89a"u8))
            return Gif;
        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return Webp;
        if (bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8))
            return Pdf;
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B
            && bytes[2] is 0x03 or 0x05 or 0x07 && bytes[3] is 0x04 or 0x06 or 0x08)
            return Zip;

        return Text;
    }

    /// <summary>
    /// 这段字节**像不像文本**：前 <paramref name="sample"/> 个字节里没有 NUL，且 UTF-8 合法。
    ///
    /// <para>⚠️ 判据是「有没有 NUL + 能不能解」而不是「有没有高字节」：中文正文全是高字节，
    /// 而二进制里几乎必然出现 NUL。放宽（只查 NUL）会把 GBK 文件判成文本、然后读出一堆 U+FFFD；
    /// 收紧（要求纯 ASCII）会把所有中文源码判成二进制。</para>
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> bytes, int sample = 4096)
    {
        var head = bytes.Length > sample ? bytes[..sample] : bytes;
        if (head.IndexOf((byte)0) >= 0) return false;

        return System.Text.Encoding.UTF8.GetString(head).IndexOf('\uFFFD') < 0;
    }
}

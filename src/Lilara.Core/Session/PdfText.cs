using System.Text;
using Lilara.Core.Session.Tools;
using UglyToad.PdfPig;

namespace Lilara.Core.Session;

/// <summary>
/// **从 PDF 里把文字抠出来**（2026-10-29）。
///
/// <para>⚠️ <b>这里用了唯一一个第三方包</b>（`UglyToad.PdfPig`），而理由是成本不对称：
/// docx/xlsx/pptx 是 zip+XML、框架里就有；PDF 要解 Flate 流、字体编码表与 CMap
/// ——一篇真正能用的实现有几千行，而**现有的版本是 `-custom` 预发布**
/// ⇒ 这一条记在 `实现现状.md` 里，将来有稳定版要升。</para>
///
/// <para>⚠️ <b>抽的是"文字"，不是"版面"</b>：表格会变成一串按阅读顺序排的字，
/// 多栏排版可能交错。⚠️ 扫描件（图片型 PDF）抽出来是**空的**——那不是"读失败"，
/// 是"这份 PDF 里没有文字层"，而这句话必须**说出来**（否则模型会以为文件是空的）。</para>
///
/// <para>⚠️ <b>上限与截断同 <see cref="DocumentText"/></b>：一份 500 页的 PDF 抽完再截，
/// 等于替用户吃满内存；而截断**必须明说**。</para>
/// </summary>
public static class PdfText
{
    /// <summary>
    /// 抽正文。<c>false</c> = 读不出来（损坏 / 加密 / 需要密码），<paramref name="reason"/> 是人话。
    /// </summary>
    public static bool TryExtract(byte[] bytes, out DocumentText.Extraction? extraction, out string? reason)
    {
        extraction = null;
        reason = null;

        try
        {
            using var document = PdfDocument.Open(bytes);
            var text = new StringBuilder();
            var pages = 0;

            foreach (var page in document.GetPages())
            {
                pages++;
                var pageText = page.Text;
                if (!string.IsNullOrWhiteSpace(pageText))
                    text.Append(pageText.TrimEnd()).Append("\n\n");
            }

            if (text.Length == 0)
            {
                // ⚠️ **这句话是这条链上最容易被读错的一句**：没有文字层 ≠ 空文件。
                reason = pages == 0
                    ? "这份 PDF 里没有页面。"
                    : $"这份 PDF 有 {pages} 页，但**没有可抽取的文字层**——它多半是扫描件（整页是图片）。"
                      + "要读它，得先把页面转成图片再按图片看。";
                return false;
            }

            var (clipped, truncated) = Clip(text.ToString());
            extraction = new DocumentText.Extraction("pdf", clipped, truncated);
            return true;
        }
        catch (Exception ex)
        {
            // ⚠️ PdfPig 对加密 / 损坏的输入抛的异常类型很杂（`InvalidOperationException`、
            // `IndexOutOfRangeException`、自定义的……）⇒ 这里**宽接**，因为工具结果本来就是
            // 内容、而「读不出来」是最该被说出来的一句话。收窄会让某一种坏 PDF 把整步打掉。
            reason = $"这份 PDF 读不出来：{ex.Message}（加密或损坏的文件要先另存/解密）。";
            return false;
        }
    }

    /// <summary>按字节上限砍，退到字符边界（与 <see cref="DocumentText"/> 逐字同一条判据）。</summary>
    private static (string Text, bool Truncated) Clip(string text)
    {
        var limit = ToolLimits.MaxToolOutputBytes;
        if (Encoding.UTF8.GetByteCount(text) <= limit) return (text, false);

        var bytes = Encoding.UTF8.GetBytes(text);
        var cut = limit;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;

        return (Encoding.UTF8.GetString(bytes, 0, cut) + "\n\n（文档太长，后面没有抽出来。）", true);
    }
}

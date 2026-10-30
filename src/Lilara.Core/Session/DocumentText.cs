using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// **从 Office 文档里把文字抠出来**（2026-10-29）：docx / xlsx / pptx。
///
/// <para>⚠️ <b>零依赖是刻意的</b>：这三种格式就是 **zip + XML**（OOXML），
/// <see cref="ZipArchive"/> 与 <see cref="XDocument"/> 都在框架里。为了"读一段文字"
/// 引一个文档处理库，等于为几十行代码背上一份它的解析器、它的安全问题与它的版本约束。
/// ⇒ 这里只做**一件**事：把正文/单元格/幻灯片上的文字按顺序抽出来；
/// 样式、图片、批注、公式一概不碰（那是"另一个功能"，不是"读文档"）。</para>
///
/// <para>⚠️ <b>失败一律是"说人话的结果"，不是异常</b>（同 `read` 的四个参数错误）：
/// 一份加过密 / 结构损坏 / 其实是普通 zip 的文件，最坏只该得到一句「读不出来：…」。
/// 抛出去会把整步打掉，而工具结果本来就是内容（`01` §3.6 ②）。</para>
///
/// <para>⚠️ <b>上限在这里就砍</b>（<see cref="ToolLimits.MaxToolOutputBytes"/>）：一份 300 页的
/// docx 抽出来是几兆文字，先抽完再截等于替用户把内存吃满。⚠️ 而且**必须明说被截断**
/// ——静默截断会让模型以为自己读完了全文（"多了一条假信息"）。</para>
/// </summary>
public static class DocumentText
{
    /// <summary>抽出来的东西。</summary>
    /// <param name="Kind">`docx` / `xlsx` / `pptx`（给人看的那一格）。</param>
    /// <param name="Text">正文（按阅读顺序，`\n` 分段）。</param>
    /// <param name="Truncated">是不是因为上限被截断了（⚠️ 必须一路传到框架里）。</param>
    public sealed record Extraction(string Kind, string Text, bool Truncated);

    /// <summary>
    /// 试着当 OOXML 读。<c>false</c> = 这不是 docx/xlsx/pptx（可能是普通 zip，或损坏）。
    ///
    /// <para>⚠️ <b>判据是「zip 里有没有那个约定路径」</b>，不是扩展名：`word/document.xml`
    /// 在才是 docx。⇒ 一个改名成 `.docx` 的 zip 会得到 <c>false</c>，而调用方据此说
    /// 「这不是支持的文档」，而不是吐一屏二进制。</para>
    /// </summary>
    public static bool TryExtract(byte[] bytes, out Extraction? extraction, out string? reason)
    {
        extraction = null;
        reason = null;

        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

            if (Find(zip, "word/document.xml") is { } doc)
                extraction = new Extraction("docx", Word(doc), false);
            else if (Find(zip, "xl/workbook.xml") is { } workbook)
                extraction = new Extraction("xlsx", Excel(zip, workbook), false);
            else if (zip.Entries.Any(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)))
                extraction = new Extraction("pptx", PowerPoint(zip), false);

            if (extraction is not null)
            {
                var (text, truncated) = Clip(extraction.Text);
                extraction = extraction with { Text = text, Truncated = truncated };
            }

            return extraction is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                       or System.Xml.XmlException or NotSupportedException)
        {
            // ⚠️ 加密的 OOXML 也是 zip，但它的条目是加密流（解出来不是 XML）——
            // 会走到 XmlException 这一格。说清楚，因为「另存为」是唯一的出路。
            reason = ex is InvalidDataException
                ? "这不是一个能打开的压缩包（可能损坏，或者根本不是 Office 文档）。"
                : $"这份文档读不出来：{ex.Message}（加密过的文档要先另存为不加密的版本）。";
            return false;
        }
    }

    // ---- docx ----

    /// <summary>
    /// `word/document.xml` → 正文。⚠️ **按阅读顺序**：段落之间空一行、表格单元格用 `\t`、
    /// 换行/制表符是文档里的真结构（不是排版）。
    /// </summary>
    private static string Word(ZipArchiveEntry entry)
    {
        var doc = Load(entry);
        var text = new StringBuilder();

        // ⚠️ 取 `descendants` 而不是逐层点名 `w:body/w:p`：段落可以嵌在表格 / 内容控件里，
        // 而「只读 body 的直接子节点」会**静默漏掉整张表**。
        foreach (var element in doc.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "p":
                    // 段落只在**结束时**换行（段落里的 run 是连续的文字）。
                    text.Append('\n');
                    break;
                case "t":
                    text.Append(element.Value);
                    break;
                case "tab":
                    text.Append('\t');
                    break;
                case "br":
                case "cr":
                    text.Append('\n');
                    break;
                case "tc":
                    text.Append('\t');   // 单元格之间
                    break;
                case "tr":
                    text.Append('\n');   // 行之间
                    break;
            }
        }

        return Normalize(text.ToString());
    }

    // ---- xlsx ----

    /// <summary>
    /// 工作簿 → 每张表一段。
    ///
    /// <para>⚠️ <b>两张表要一起读</b>：单元格里存的是**共享字符串的下标**（`t="s"` ⇒ `v` 是
    /// `sharedStrings.xml` 里的第几个），直接读 `v` 会得到一串数字——而那是**最容易
    /// 被当成真数据**的那种错（表里确实有一列叫「1」「2」「3」）。</para>
    /// </summary>
    private static string Excel(ZipArchive zip, ZipArchiveEntry workbook)
    {
        var shared = new List<string>();
        if (Find(zip, "xl/sharedStrings.xml") is { } sst)
            foreach (var si in Load(sst).Descendants())
                if (si.Name.LocalName == "si")
                    shared.Add(string.Concat(si.Descendants().Where(d => d.Name.LocalName == "t").Select(d => d.Value)));

        // 表名：workbook.xml 里的 sheet（按出现顺序），与 sheetN.xml 的编号对应。
        var names = Load(workbook).Descendants()
            .Where(e => e.Name.LocalName == "sheet")
            .Select(e => e.Attribute("name")?.Value ?? "?")
            .ToList();

        var sheets = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SheetNumber(e.FullName))
            .ToList();

        var text = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
        {
            var name = i < names.Count ? names[i] : Path.GetFileNameWithoutExtension(sheets[i].Name);
            text.Append("—— 表：").Append(name).Append(" ——\n");

            foreach (var row in Load(sheets[i]).Descendants().Where(e => e.Name.LocalName == "row"))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    var type = cell.Attribute("t")?.Value;
                    var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";

                    switch (type)
                    {
                        case "s" when int.TryParse(value, out var index)
                                      && index >= 0 && index < shared.Count:
                            cells.Add(shared[index]);
                            break;
                        case "inlineStr":
                            cells.Add(string.Concat(cell.Descendants()
                                .Where(d => d.Name.LocalName == "t").Select(d => d.Value)));
                            break;
                        case "b":
                            cells.Add(value == "1" ? "true" : "false");
                            break;
                        default:
                            cells.Add(value);
                            break;
                    }
                }

                // ⚠️ 整行空的就不占一行：xlsx 里空行极多（格式化留下来的），
                // 全吐出来会把真正的内容挤到几百行之后。
                var line = string.Join('\t', cells);
                if (line.Trim().Length > 0) text.Append(line).Append('\n');
            }

            text.Append('\n');
        }

        return Normalize(text.ToString());
    }

    private static int SheetNumber(string fullName)
    {
        var name = Path.GetFileNameWithoutExtension(fullName);
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    // ---- pptx ----

    /// <summary>每张幻灯片一段正文（形状里的 `a:t`，按文档顺序）。</summary>
    private static string PowerPoint(ZipArchive zip)
    {
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SheetNumber(e.FullName))
            .ToList();

        var text = new StringBuilder();
        foreach (var slide in slides)
        {
            text.Append("—— 幻灯片 ").Append(SheetNumber(slide.FullName)).Append(" ——\n");

            foreach (var paragraph in Load(slide).Descendants().Where(e => e.Name.LocalName == "p"))
            {
                var line = string.Concat(paragraph.Descendants()
                    .Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                if (line.Length > 0) text.Append(line).Append('\n');
            }

            text.Append('\n');
        }

        return Normalize(text.ToString());
    }

    // ---- 公共小件 ----

    private static ZipArchiveEntry? Find(ZipArchive zip, string path) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>
    /// 把连续空行压成一个空行、行尾空白去掉。
    ///
    /// <para>⚠️ 这不是"美化"：OOXML 里一个段落的结束是 `</w:p>`，而 `w:br` 也是换行
    /// ⇒ 原样拼出来会有成片的空行，把真正的分段淹掉。压过之后**段落仍然是段落**。</para>
    /// </summary>
    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where((l, i) => l.Length > 0 || (i > 0 && i < text.Length));

        var result = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (blank) continue;
                blank = true;
            }
            else blank = false;

            result.Append(line).Append('\n');
        }

        return result.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 按**字节上限**砍（`ToolLimits.MaxToolOutputBytes`，与 `read` 的保存上限同一个数）。
    /// ⚠️ 返回的 <c>Truncated</c> 必须一路传到框架里：静默截断 = 「它以为读完了」。
    /// </summary>
    private static (string Text, bool Truncated) Clip(string text)
    {
        // ⚠️ 按 UTF-8 的字节数算，不按字符数：判据是「这条结果会占多少字节」，
        // 而中文一个字 3 字节——按字符算会让上限在中文文档上宽松三倍。
        var limit = ToolLimits.MaxToolOutputBytes;
        if (Encoding.UTF8.GetByteCount(text) <= limit) return (text, false);

        var bytes = Encoding.UTF8.GetBytes(text);
        var cut = limit;

        // 退到字符边界（不让最后一个字变成半个）
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;

        return (Encoding.UTF8.GetString(bytes, 0, cut) + "\n\n（文档太长，后面没有抽出来。）", true);
    }
}

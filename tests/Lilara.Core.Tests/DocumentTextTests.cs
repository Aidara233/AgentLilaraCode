using System.IO.Compression;
using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **文档这条路**（2026-10-29）：`read` 一个 docx / xlsx / pptx / pdf 时，
/// 模型拿到的是**正文**，不是一屏二进制。
///
/// <para>⚠️ <b>每一条都钉着"最容易静默错"的那一格</b>：docx 的段落顺序、xlsx 的
/// **共享字符串**（读错就得到一列数字，而它看起来完全像真数据）、pptx 的幻灯片顺序、
/// pdf 的「没有文字层」（扫描件）必须**说出来**。</para>
///
/// <para>⚠️ <b>夹具是现造的</b>（`ZipArchive` + `XDocument`，PDF 用 PdfPig 自己的
/// `PdfDocumentBuilder`）：仓里不放二进制样本——它没法 review，也没法随格式演进。</para>
/// </summary>
public class DocumentTextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-doc-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-docwork-" + Guid.NewGuid().ToString("N"));

    public DocumentTextTests()
    {
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _work })
            for (var attempt = 0; ; attempt++)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    Thread.Sleep(10 * (attempt + 1));
                }
            }
    }

    // ---- docx ----

    [Fact]
    public async Task Read_Docx_ReturnsTheParagraphsInOrder()
    {
        Write("report.docx", Word(
            """<w:p><w:r><w:t>第一段</w:t></w:r></w:p>""",
            """<w:p><w:r><w:t>第二段</w:t></w:r></w:p>"""));

        var (text, framing) = await Read("report.docx");

        Assert.Contains("第一段", text, StringComparison.Ordinal);
        Assert.Contains("第二段", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("第一段", StringComparison.Ordinal)
                    < text.IndexOf("第二段", StringComparison.Ordinal));
        Assert.Contains("docx", framing, StringComparison.Ordinal);   // 框架里写清来源
    }

    // ---- xlsx ----

    [Fact]
    public async Task Read_Xlsx_ResolvesSharedStrings_NotTheirIndexes()
    {
        // ⚠️ 这一条是整套里最要紧的一条：单元格里存的是**下标**（`t="s"`），
        // 而「0 / 1」读出来**看起来像真数据**——静默错的那种。
        Write("sheet.xlsx", Excel(
            shared: ["姓名", "年龄", "小明", "17"],
            sheetRows:
            [
                """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>""",
                """<row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>17</v></c></row>""",
            ]));

        var (text, framing) = await Read("sheet.xlsx");

        Assert.Contains("姓名", text, StringComparison.Ordinal);
        Assert.Contains("小明", text, StringComparison.Ordinal);
        Assert.Contains("17", text, StringComparison.Ordinal);
        Assert.Contains("表：Sheet1", text, StringComparison.Ordinal);   // 表名（来自 workbook.xml）
        Assert.Contains("xlsx", framing, StringComparison.Ordinal);
    }

    // ---- pptx ----

    [Fact]
    public async Task Read_Pptx_ReturnsSlideTexts()
    {
        Write("deck.pptx", PowerPoint("开场", "结论"));

        var (text, framing) = await Read("deck.pptx");

        Assert.Contains("开场", text, StringComparison.Ordinal);
        Assert.Contains("结论", text, StringComparison.Ordinal);
        Assert.Contains("幻灯片 1", text, StringComparison.Ordinal);
        Assert.Contains("pptx", framing, StringComparison.Ordinal);
    }

    // ---- pdf ----

    [Fact]
    public async Task Read_Pdf_ReturnsTheTextLayer()
    {
        Write("paper.pdf", Pdf("Hello from the PDF"));

        var (text, framing) = await Read("paper.pdf");

        Assert.Contains("Hello", text, StringComparison.Ordinal);
        Assert.Contains("pdf", framing, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfWithoutTextLayer_SaysSo_InsteadOfReturningNothing()
    {
        // 一份**没有文字层**的 PDF（只有一页、没有文字）——扫描件就是这种形状。
        var bytes = EmptyPdf();

        var ok = PdfText.TryExtract(bytes, out _, out var why);

        Assert.False(ok);
        Assert.Contains("没有可抽取的文字层", why!, StringComparison.Ordinal);
    }

    // ---- 兜底：普通 zip 与坏文档 ----

    [Fact]
    public async Task Read_APlainZip_SaysItIsNotASupportedDocument()
    {
        Write("data.zip", Zip(("readme.txt", Encoding.UTF8.GetBytes("hi"))));

        var (text, _) = await Read("data.zip");

        Assert.Contains("不是支持的文档类型", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sniff_RecognizesTheShapesItClaimsTo()
    {
        Assert.Equal(MediaTypes.Png, MediaTypes.Sniff(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==")));
        Assert.Equal(MediaTypes.Jpeg, MediaTypes.Sniff([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]));
        Assert.Equal(MediaTypes.Pdf, MediaTypes.Sniff("%PDF-1.7\n"u8.ToArray()));
        Assert.Equal(MediaTypes.Zip, MediaTypes.Sniff(Zip(("a", [1]))));
        Assert.Equal(MediaTypes.Text, MediaTypes.Sniff("就是一句中文"u8.ToArray()));
        Assert.True(MediaTypes.LooksLikeText("普通文本"u8.ToArray()));
        Assert.False(MediaTypes.LooksLikeText([0x89, 0x50, 0x00, 0x01]));
    }

    // ---- 脚手架 ----

    private void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_work, name), bytes);

    private async Task<(string Text, string Framing)> Read(string path)
    {
        var outcome = await new ReadTool().RunAsync(
            new ToolCall("c1", "read", $$"""{"path":"{{path}}"}"""),
            new SessionToolContext(_work, new BlobStore(_root)), CancellationToken.None);

        var text = string.Concat(outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));
        var framing = outcome.Framing is { } f
            ? string.Join(" | ", f.Fields.Select(x => x.Key + "=" + x.Value))
            : "";

        return (text, framing);
    }

    private static byte[] Zip(params (string Path, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (entryPath, entryBytes) in entries)
            {
                var entry = zip.CreateEntry(entryPath);
                using var stream = entry.Open();
                stream.Write(entryBytes);
            }

        return buffer.ToArray();
    }

    private static byte[] Word(params string[] paragraphs) => Zip(
        ("[Content_Types].xml", Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""")),
        ("word/document.xml", Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>"""
            + string.Concat(paragraphs)
            + "</w:body></w:document>")));

    private static byte[] Excel(string[] shared, string[] sheetRows) => Zip(
        ("xl/workbook.xml", Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">"""
            + """<sheets><sheet name="Sheet1" sheetId="1" r:id="rId1" """
            + """xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"/></sheets></workbook>""")),
        ("xl/sharedStrings.xml", Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">"""
            + string.Concat(shared.Select(s => $"<si><t>{s}</t></si>"))
            + "</sst>")),
        ("xl/worksheets/sheet1.xml", Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>"""
            + string.Concat(sheetRows)
            + "</sheetData></worksheet>")));

    private static byte[] PowerPoint(params string[] slides) => Zip(
    [
        ("ppt/presentation.xml", Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"/>""")),
        .. slides.Select((text, i) => ($"ppt/slides/slide{i + 1}.xml", Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" """
            + """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:txBody>"""
            + $"<a:p><a:r><a:t>{text}</a:t></a:r></a:p>"
            + "</p:txBody></p:sp></p:spTree></p:cSld></p:sld>"))),
    ]);

    /// <summary>用 PdfPig 自己的 builder 造一份**真的有文字层**的 PDF。</summary>
    private static byte[] Pdf(string text)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
        return builder.Build();
    }

    /// <summary>一份**有页面但没有文字**的 PDF（扫描件的形状：只有图 / 只有版式）。</summary>
    private static byte[] EmptyPdf()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        return builder.Build();
    }
}

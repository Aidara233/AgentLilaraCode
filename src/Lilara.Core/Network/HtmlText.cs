using System.Text;

namespace Lilara.Core.Network;

/// <summary>
/// HTML → 正文。**无依赖的小扫描器**，不是通用解析器——目标只是「把网页读成模型能用的文字」。
///
/// <para>⚠️ <b>它刻意不用正则</b>：正则版本在 `<script>` 里出现 `</script>` 一类字符串时
/// 会错位，而且大页面上容易回溯爆炸。逐字符扫描慢一点，但行为是可解释的。</para>
///
/// <para>⚠️ <b>它不保证保真</b>：表格会变成一串行、链接只剩文字。结果里必须写明
/// 「这是抽取的正文」——否则模型会把「没抽到」当成「页面上没有」。</para>
/// </summary>
internal static class HtmlText
{
    private static readonly string[] DroppedBlocks = ["script", "style", "noscript", "template", "svg"];

    private static readonly string[] BlockTags =
    [
        "p", "div", "br", "hr", "li", "tr", "table", "section", "article", "header", "footer",
        "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "ul", "ol", "dl", "dt", "dd",
        "form", "fieldset", "figure", "figcaption", "main", "nav", "aside",
    ];

    public static (string? Title, string Text) ToText(string html)
    {
        var title = ExtractTitle(html);

        foreach (var block in DroppedBlocks)
            html = DropElement(html, block);

        html = DropComments(html);

        var sb = new StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }

            sb.Append(html, i, lt - i);

            var gt = html.IndexOf('>', lt + 1);
            if (gt < 0) break;      // 没闭合的尾巴不是内容

            var tag = TagName(html, lt + 1, gt);
            if (tag.Length > 0 && BlockTags.Contains(tag, StringComparer.Ordinal))
                sb.Append('\n');

            i = gt + 1;
        }

        var text = Collapse(DecodeEntities(sb.ToString()));
        return (title, text);
    }

    private static string? ExtractTitle(string html)
    {
        var open = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;

        var start = html.IndexOf('>', open);
        if (start < 0) return null;

        var end = html.IndexOf("</title", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;

        var raw = html[(start + 1)..end].Trim();
        var title = Collapse(DecodeEntities(raw));
        return title.Length == 0 ? null : title;
    }

    /// <summary>把 `<tag …>…</tag>` 整段删掉（大小写不敏感，允许属性里有 `>`）。</summary>
    private static string DropElement(string html, string tag)
    {
        var open = $"<{tag}";
        var close = $"</{tag}";
        var sb = new StringBuilder(html.Length);
        var i = 0;

        while (true)
        {
            var at = html.IndexOf(open, i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { sb.Append(html, i, html.Length - i); break; }

            // 必须是完整标签名：`<scriptx>` 不是 script。
            var after = at + open.Length;
            if (after < html.Length && !(char.IsWhiteSpace(html[after]) || html[after] == '>' || html[after] == '/'))
            {
                sb.Append(html, i, after - i);
                i = after;
                continue;
            }

            sb.Append(html, i, at - i);

            var end = html.IndexOf(close, after, StringComparison.OrdinalIgnoreCase);
            if (end < 0) break;     // 没闭合：剩下的都不可信，丢掉

            var closeEnd = html.IndexOf('>', end + close.Length);
            i = closeEnd < 0 ? html.Length : closeEnd + 1;
        }

        return sb.ToString();
    }

    private static string DropComments(string html)
    {
        var sb = new StringBuilder(html.Length);
        var i = 0;
        while (true)
        {
            var at = html.IndexOf("<!--", i, StringComparison.Ordinal);
            if (at < 0) { sb.Append(html, i, html.Length - i); break; }

            sb.Append(html, i, at - i);
            var end = html.IndexOf("-->", at + 4, StringComparison.Ordinal);
            if (end < 0) break;
            i = end + 3;
        }
        return sb.ToString();
    }

    private static string TagName(string html, int start, int end)
    {
        var i = start;
        while (i < end && char.IsWhiteSpace(html[i])) i++;
        if (i < end && html[i] == '/') i++;

        var begin = i;
        while (i < end && (char.IsLetterOrDigit(html[i]) || html[i] == '-' || html[i] == ':')) i++;
        return html[begin..i].ToLowerInvariant();
    }

    private static string DecodeEntities(string text)
    {
        if (!text.Contains('&')) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&') { sb.Append(text[i]); continue; }

            var semi = text.IndexOf(';', i + 1);
            // 实体名不会太长；找不到分号就按字面量保留。
            if (semi < 0 || semi - i > 12) { sb.Append(text[i]); continue; }

            var body = text[(i + 1)..semi];
            if (TryEntity(body, out var ch))
            {
                sb.Append(ch);
                i = semi;
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    private static bool TryEntity(string body, out char ch)
    {
        ch = '\0';

        if (body.StartsWith('#'))
        {
            var digits = body[1..];
            var hex = digits.StartsWith('x') || digits.StartsWith('X');
            if (hex) digits = digits[1..];

            if (digits.Length == 0) return false;
            if (!int.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber
                                         : System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out var code))
                return false;

            if (code <= 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) return false;
            // 只交回 BMP：代理对在这个「一行一句」的正文里不值得引入额外复杂度。
            if (code > 0xFFFF) return false;

            ch = (char)code;
            return true;
        }

        ch = body switch
        {
            "amp" => '&',
            "lt" => '<',
            "gt" => '>',
            "quot" => '"',
            "apos" => '\'',
            "nbsp" => ' ',
            "mdash" => '—',
            "ndash" => '–',
            "hellip" => '…',
            "ldquo" => '“',
            "rdquo" => '”',
            "lsquo" => '‘',
            "rsquo" => '’',
            _ => '\0',
        };
        return ch != '\0';
    }

    /// <summary>行尾去空白、连续空行压成一个、整体去首尾空白。</summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var blank = true;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (!blank) sb.Append('\n');
                blank = true;
                continue;
            }

            if (sb.Length > 0 && !blank) sb.Append('\n');
            sb.Append(line);
            blank = false;
        }

        return sb.ToString().Trim();
    }
}

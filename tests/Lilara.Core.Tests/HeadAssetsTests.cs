using System.Text.RegularExpressions;

namespace Lilara.Core.Tests;

/// <summary>
/// **头的静态资源与它自己保持自洽**——一条不需要浏览器就能跑的检查。
///
/// <para>⚠️ <b>它为什么存在</b>：头是**零构建**的前端，`head/wwwroot/` 就是最终产物，
/// 于是「我改了、你说没效果」可以有四种成因（跑了另一份构建 / 没重建 / 浏览器缓存 /
/// 改错了副本），而它们在页面上**长得一模一样**。</para>
///
/// <para>⚠️ <b>而这一批正是栽在这里</b>（2026-10-06）：我在 `index.html` 里加了
/// `id="gc"` 的按钮、在 `app.js` 里加了 `$("gc").onclick = …`，
/// 而**在跑的那份产物只重建了 Debug**——用户跑 Release ⇒ 连按钮都看不见，
/// 且**一声不吭**（`app.js` 里那行赋值根本没执行到）。</para>
///
/// <para>⚠️ <b>这条守卫只能覆盖「同一份源码内部自洽」</b>，覆盖不了「跑的是旧构建」——
/// 那一半靠 `head.csproj` 的 <c>CopyToOutputDirectory="Always"</c>
/// （副本永远被覆盖，残留不下来）与页面底栏那个「页面 …B @…」自述（点开就知道是哪一份）。
/// 把边界写清楚，免得下一个人以为这条守卫管得更多。</para>
/// </summary>
public class HeadAssetsTests
{
    private static string WebRoot() =>
        Path.Combine(RepoRoot(), "head", "wwwroot");

    private static string Read(string name) => File.ReadAllText(Path.Combine(WebRoot(), name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Lilara.Core", "Kernel")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// ⚠️ **`app.js` 里用 `$("…")` 抓的每一个 id，`index.html` 里都得有**——
    /// 否则那行（或那段函数）在运行时**静默什么都不做**。
    ///
    /// <para>这一条直接钉着这次的事故形状：按钮是后加的，而处理器抓的是它的 id；
    /// 两边分居两个文件，改漏一个**没有任何编译期信号**。</para>
    ///
    /// <para>⚠️ 它**不**覆盖「脚本抛在赋值之前」那类（那是顺序 / 异常的问题，不是 id 的问题），
    /// 也**不**覆盖「跑的是旧构建」。</para>
    /// </summary>
    [Fact]
    public void EveryIdAppJsLooksUp_ExistsInIndexHtml()
    {
        var html = Read("index.html");
        var js = Read("app.js");

        // 只看 `$("…")` 这个取元素的形状（`$$`、选择器、模板串都排掉）。
        var looked = Regex.Matches(js, "\\$\\(\"([a-zA-Z][\\w-]*)\"\\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // ⚠️ 探针：正则写歪了会得到一个空集，下面那条断言就**空真**了。
        // （加/删控件不会让这个数腐得很厉害，而它红了就说明该复核这一段。）
        Assert.True(looked.Count > 20, $"只找到 {looked.Count} 个 id，正则多半没匹配上");

        var declared = Regex.Matches(html, "id=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = looked.Where(id => !declared.Contains(id)).ToList();

        Assert.Empty(missing.Select(id =>
            $"app.js 抓 `$(\"{id}\")`，而 index.html 里没有 id=\"{id}\" 的元素——"
            + "那一段会在运行时静默什么都不做。"));
    }

    /// <summary>
    /// ⚠️ **每个按钮都要有人接线**：`index.html` 里的 `<button id="x">` 必须在 `app.js` 里
    /// 出现一次 `$("x").onclick` 之类的赋值。
    ///
    /// <para>反过来的漏法（有按钮、没处理器）用户看到的是**一个点了没反应的按钮**——
    /// 与「后端坏了」分不开。这一批的「回收」按钮正是这么加的，所以钉住它。</para>
    /// </summary>
    [Fact]
    public void EveryButton_HasAHandlerInAppJs()
    {
        var html = Read("index.html");
        var js = Read("app.js");

        var buttons = Regex.Matches(html, "<button[^>]*\\bid=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(buttons);   // 同上的探针

        var unbound = buttons
            .Where(id => !js.Contains($"$(\"{id}\").onclick", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(unbound.Select(id =>
            $"按钮 `{id}` 在 index.html 里，而 app.js 里没有 `$(\"{id}\").onclick` —— 它点了没反应。"));
    }
}

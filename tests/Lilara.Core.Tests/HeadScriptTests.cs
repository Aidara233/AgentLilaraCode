using System.Text.RegularExpressions;

namespace Lilara.Core.Tests;

/// <summary>
/// <b>`head/wwwroot/app.js` 的守卫。</b>
///
/// <para>⚠️ 为什么这个文件存在（2026-10-07）：头那 74KB 的 `app.js` **一条测试都没有**，
/// 而它在同一天里栽了**两次同一形状**的错——「<b>把不是一串块的东西喂给收一串块的函数</b>」，
/// 两次都<b>不报错</b>或报得离现场很远，而且**两次都不是测试抓到的，是用户报的</b>：</para>
///
/// <list type="number">
///   <item><b>喂一个块</b>：<c>const fmt = (b) =&gt; b.map(textOf).join("")</c> 里的 <c>b</c>
///   其实是**一个块** ⇒ <c>b.filter is not a function</c> ⇒ 打开会话时中栏整段渲染中断，
///   **连历史都画不出来**。</item>
///   <item><b>喂那个槽信封</b>：同一行代码在另一条路径上收到 <c>{system, premier, floating}</c>
///   或一个字符串 ⇒ <c>filter</c> 静默跳过全部 ⇒ 每个槽显示「（空）」，<b>一点错都不报</b>。</item>
/// </list>
///
/// <para>⇒ 这里钉的是**形状**：谁可以调 <c>textOf</c>、<c>textOf</c> 不许被当成函数值传出去、
/// 槽的那一格必须能在**空**的时候也画出来。</para>
///
/// <para>⚠️ <b>边界要说清：它读的是源码文本，不是运行行为。</b>它拦得住「谁又用错了」，
/// 拦不住「渲染出来长什么样」。真正的 E2E 在浏览器里。</para>
///
/// <para>⚠️ <b>它只读 `head/wwwroot/app.js` 那一份</b>——`head/bin/{Debug,Release}/…/wwwroot/`
/// 是构建时复制过去的副本，按名字遍历时**排在源码前面**（写这些守卫时踩到过：
/// 读的是 Release 那份旧拷贝，于是「变异落上却不红」，白查了三轮）。</para>
/// </summary>
public sealed class HeadScriptTests
{
    private static string AppJs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentLilaraCode.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "head", "wwwroot", "app.js");
        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    /// <summary>代码行（去掉注释行）——注释里的 `<see cref="textOf"/>` 不是调用点。</summary>
    private static List<(int Line, string Text)> CodeLines(string js) =>
        [.. js.Split('\n')
            .Select((text, i) => (Line: i + 1, Text: text.Trim()))
            .Where(x => !x.Text.StartsWith("//", StringComparison.Ordinal)
                     && !x.Text.StartsWith("///", StringComparison.Ordinal)
                     && !x.Text.StartsWith("/*", StringComparison.Ordinal)
                     && !x.Text.StartsWith("*", StringComparison.Ordinal))];

    /// <summary>
    /// ⚠️ <b>`textOf` 的入参是**一串块**，所以它永远不许被当成「一个元素 → 文本」的函数值传出去。</b>
    ///
    /// <para>它一旦出现在 <c>map</c> 里当回调（<c>b.map(textOf)</c>），那个 <c>b</c> 的元素
    /// 就会被当成一整串块——**而数组的元素是块，不是数组** ⇒ 收到单个块 ⇒
    /// <c>.filter is not a function</c>。本仓实测栽的正是这个形状。</para>
    ///
    /// <para>⇒ 判据：代码行里出现 <c>textOf</c> 而**后面没有括号**，就是把它当引用传 ⇒ 红。
    /// （定义那一行的 <c>function textOf(</c> 带括号，天然不触发。）</para>
    /// </summary>
    [Fact]
    public void TextOf_IsNeverPassedAsAFunctionValue()
    {
        var offenders = CodeLines(AppJs())
            .Where(x => Regex.IsMatch(x.Text, @"\btextOf\b(?!\s*\()"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "textOf 的入参是**一串块**，不能当成 map 的回调传（那样每个元素只拿到一个块）。\n"
            + "要么写 textOf(blocks)，要么用别的函数。命中：\n"
            + string.Join("\n", offenders.Select(o => $"  app.js:{o.Line}  {o.Text}")));
    }

    /// <summary>
    /// ⚠️ <b>三个槽必须走<b>同一个</b>助手，而那个助手里必须判空。</b>
    ///
    /// <para>⚠️ <b>这条 2026-10-11 改过形状，而改的是<b>判据</b>不是措辞</b>：
    /// 原来只盯浮动槽那一格（那时它写着「尚未实现」，所以核交的是 `[]`）。
    /// 现在三格都可能是空数组（用户删了预设 / 没配过首要块 / 一条轴都没进上下文），
    /// ⇒ 真正要防的从「浮动那一格忘了判空」变成「**三格里有一格走了另一条路**」——
    /// 后者才是那种「只在空槽下才炸、而三格里你只测了有内容的那两格」的形状。</para>
    ///
    /// <para>⚠️ 裸的 <c>slots.floating.map(...)</c> 在槽为 `[]` 时**不抛**——但它一旦写成
    /// <c>slots.floating.map(textOf)</c>（或任何「把槽当成一串块来 map」的形状），
    /// 就落进「块级逻辑被抄了第二遍」那类里。下面第二条断言仍然盯它。</para>
    /// </summary>
    [Fact]
    public void EverySlot_GoesThroughTheSameEmptySafeHelper()
    {
        var js = AppJs();

        // 三个槽各自的取用点：抓 `助手名($("槽的 id"), …)` 里的助手名。
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in new[] { "slot-system", "slot-premier", "floating-body" })
        {
            var m = Regex.Match(js, @"(\w+)\(\s*\$\(""" + Regex.Escape(id) + @"""\)\s*,");
            Assert.True(m.Success, $"找不到槽 '{id}' 的取用点——它要么被删了，要么绕开了那个助手。");
            calls[id] = m.Groups[1].Value;
        }

        Assert.True(calls.Values.Distinct(StringComparer.Ordinal).Count() == 1,
            "三个槽必须走**同一个**助手（各自一套 = 总有一格忘了判空，而那只在空槽下才炸）。"
            + $"实际：{string.Join(" · ", calls.Select(kv => $"{kv.Key}→{kv.Value}"))}");

        // ⚠️ 而那个助手本身不许裸取：它必须是「先判空、再交给 slotText」的形状。
        var helper = Regex.Escape(calls["floating-body"]);
        Assert.True(
            Regex.IsMatch(js, @"const\s+" + helper + @"\s*=\s*\([^)]*\)\s*=>\s*\{[^}]*slotText\("),
            $"助手 '{calls["floating-body"]}' 必须把块交给 slotText（空数组由它收），别自己抽文本。");

        // ⚠️ **不许**出现「裸取槽里的块」：`slots.X.map(...)` 说明有人在块这一层
        // 手写遍历，而块级逻辑只有 `textOf` 一份实现（两处各写一遍 = 改一处忘一处，静默）。
        Assert.False(Regex.IsMatch(js, @"slots\.\w+\.map\("),
            "别在槽上直接 .map(...)：块级抽文本只有 textOf 一份实现。交给 slotText(...)。");
    }

    /// <summary>
    /// ⚠️ <b>块的抽文本在 `slotText` 里不重复实现</b>——它只负责在 `textOf` 前面补「空」那一格。
    ///
    /// <para>⚠️ <b>不要求全文件只有一处 `b.kind === "text"`</b>：`bodyOf` 与 `renderHistoryItem`
    /// 各有一处，而它们**真的不一样**（前者要带上 `toolUse`，后者要区分 `store` / `image`）。
    /// 这里只钉「`slotText` 自己不许再写一遍」——那个副本才是纯粹多余的那一个。</para>
    /// </summary>
    [Fact]
    public void SlotText_DelegatesToTextOf()
    {
        var js = AppJs();

        Assert.Contains("function slotText(blocks)", js, StringComparison.Ordinal);
        Assert.Matches(@"return\s+textOf\(blocks\)", js);

        // 取出 slotText 的函数体（到下一个顶层 `}` 为止），断言它里面没有自己判块。
        var start = js.IndexOf("function slotText(blocks)", StringComparison.Ordinal);
        var end = js.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, "没找到 slotText 的函数体结尾");
        var body = js[start..end];

        Assert.False(body.Contains("b.kind", StringComparison.Ordinal),
            "slotText 里又自己判块了——块级逻辑只有 textOf 一份实现，这里只该判「空」。\n"
            + body);
    }
}

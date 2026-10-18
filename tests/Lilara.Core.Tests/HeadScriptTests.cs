using System.Text.RegularExpressions;

namespace Lilara.Core.Tests;

/// <summary>
/// <b>`src/Lilara.Head/wwwroot/app.js` 的守卫。</b>
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
/// <para>⚠️ <b>边界要说清：除 <c>AppJs_ParsesAsAScript</c> 走真解析器外，其余条目读的是
/// 源码文本，不是运行行为。</b>解析器拦得住「整份脚本根本执行不起来」（2026-10-17 加，
/// 那天的一份坏文件正是这样连 WS 都没建起来）；文本规则拦得住「谁又用错了」；
/// 两者都拦不住「渲染出来长什么样」。真正的 E2E 在浏览器里。</para>
///
/// <para>⚠️ <b>它只读 `src/Lilara.Head/wwwroot/app.js` 源码</b>——输出目录的副本由 head 项目每次构建强制覆盖。</para>
/// </summary>
public sealed class HeadScriptTests
{
    private static string AppJs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentLilaraCode.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "Lilara.Head", "wwwroot", "app.js");
        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// ⚠️ <b>`app.js` 必须能被**解析**——这是本文件其它条目全都缺的那一层。</b>
    ///
    /// <para>为什么加它（2026-10-17）：头连不上核心，浏览器第一句就是
    /// <c>Uncaught SyntaxError: Illegal return statement (app.js:656:3)</c>——
    /// 整份脚本**一行都没执行**（超时守卫、WS 连接都在里面，所以表现成「连不上」）。
    /// 根因是 `5d638ce` 整理头目录时删掉了 <c>function bodyOf(blocks) {</c> 这一行，
    /// 却把它的 <c>return …; }</c> 结尾留成了顶层语句。</para>
    ///
    /// <para>⚠️ <b>本文件原有 7 条守卫一条都没红</b>：它们全是正则读源码文本，
    /// 而语法错不在任何一条的判据里。手跑 `node --check` 也能抓（`07` 的验证命令里有），
    /// 但本机**没有 node**、它也不在 CI 里——于是这就是一条只有人能发现的漏路。
    /// 换成真正的解析器，它就成了每次 <c>dotnet test</c> 都会走的一步。</para>
    ///
    /// <para>⚠️ 用的是 <b>acorn 的 C# 移植、只解析不执行</b>（Acornima）：
    /// 没有 node 的机器上也能跑，且不把 DOM / 网络带进单测。浏览器里跑不跑得对仍不归它管——
    /// 它拦的**恰好**是「文件根本没被解析」这一类。</para>
    /// </summary>
    [Fact]
    public void AppJs_ParsesAsAScript()
    {
        var js = AppJs();

        // ⚠️ 探针：解析器**确实是同一份文本**、且失败会被标出来——
        // 否则「通过」与「读错了文件 / 压根没解析」逐字同形（`变异必须先确认落上`）。
        var broken = "function f() {\n  return 1;\n}\n  return 2;\n}";
        var failure = Record.Exception(() => new Acornima.Parser().ParseScript(broken));
        Assert.IsType<Acornima.SyntaxErrorException>(failure);

        var error = Record.Exception(() => new Acornima.Parser().ParseScript(js));

        Assert.True(error is null,
            "head/wwwroot/app.js 解析失败——浏览器会整份放弃执行（WS 也连不上）。\n"
            + error?.Message);
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

    [Fact]
    public void HistoryDetails_AndPagination_AreStructuredAndShared()
    {
        var js = AppJs();

        Assert.Contains("reasoning", js, StringComparison.Ordinal);
        Assert.Contains("toolUse", js, StringComparison.Ordinal);
        Assert.Contains("toolResult", js, StringComparison.Ordinal);
        Assert.Contains("callId", js, StringComparison.Ordinal);
        Assert.Contains("historyId", js, StringComparison.Ordinal);
        Assert.Contains("nextCursor", js, StringComparison.Ordinal);
        Assert.Contains("before=", js, StringComparison.Ordinal);
        Assert.Contains("dataset.historyId", js, StringComparison.Ordinal);
        Assert.Contains("createElement(\"details\")", js, StringComparison.Ordinal);
        Assert.DoesNotContain("signature)", js, StringComparison.Ordinal);

        // ⚠️ 2026-10-17 三条用户实测的形状，各钉一处：
        // ① 思考在**正文之前**（文本按块序输出时它会落到正文下面）；
        // ② 工具结果按 `callId` **挂到调用下面**，不是各自单列一行；
        // ③ 工具的实时结果事件有消费者（否则只有重拉 `/history` 才看得见）。
        var create = js[js.IndexOf("function createHistoryItem", StringComparison.Ordinal)..];
        var reasoningAt = create.IndexOf("reasoningDetail(b)", StringComparison.Ordinal);
        var textAt = create.IndexOf("textEl.textContent", StringComparison.Ordinal);
        Assert.True(reasoningAt >= 0 && textAt > reasoningAt,
            "createHistoryItem 里思考项必须先于正文输出（思考 → 正文 → 调用）。");

        Assert.Contains("dataset.resultCallId", js, StringComparison.Ordinal);
        Assert.Contains("[data-call-id=", js, StringComparison.Ordinal);
        Assert.Contains("case \"tool.result.appended\"", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>新建会话的目录输入是「输入即过滤的 combobox」，补全逻辑在头、core 只供 ls。</b>
    ///
    /// <para>⚠️ 2026-10-17 用户裁定换形状：原来是「逐层点『进入 X』按钮」，用户要的是
    /// 「输入 `E:/Worksp` 自动列出匹配的 Workspace」。拆法：core 的 <c>/directories</c>
    /// **保持成一个纯 ls**（给路径列子目录，不认前缀），头把输入切成
    /// 「父目录 + 前缀」、过滤、画候选。</para>
    ///
    /// <para>⚠️ 两条**静态文本守得住、运行时才会错**的形状，各钉一处：</para>
    /// <list type="number">
    ///   <item><b>父目录不是输入本身</b>：`E:/Worksp` 要去问 `E:/`，问 `E:/Worksp`
    ///   会得到「目录不存在」；而用户看到的是「打了字没候选」。</item>
    ///   <item><b>过期响应**不能盖掉**新响应</b>：打字快时先发后到，候选会停在中间状态，
    ///   且不报错。判据是 `dirSuggest` 里有序号比较且在渲染前。</item>
    /// </list>
    /// </summary>
    [Fact]
    public void NewSessionDirectoryInput_IsAComboboxFilteredInTheHead()
    {
        var js = AppJs();

        // 事实来源仍是 core 的 /directories；提交的仍是 cwd；不碰浏览器本机目录 API。
        Assert.Contains("/directories", js, StringComparison.Ordinal);
        Assert.Contains("session.create", js, StringComparison.Ordinal);
        Assert.Contains("cwd", js, StringComparison.Ordinal);
        Assert.DoesNotContain("$(\"new-project\").innerHTML", js, StringComparison.Ordinal);
        Assert.DoesNotContain("showDirectoryPicker", js, StringComparison.Ordinal);
        Assert.DoesNotContain("webkitdirectory", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File System Access", js, StringComparison.OrdinalIgnoreCase);

        // ① 前缀过滤发生在头里（core 只列、不认前缀）。
        Assert.Contains("function dirSplit", js, StringComparison.Ordinal);
        Assert.Contains(".startsWith(p)", js, StringComparison.Ordinal);
        Assert.Contains("new-project-list", js, StringComparison.Ordinal);

        // ② 拆父目录的判据：`E:/Worksp` → 父 `E:/`、前缀 `Worksp`；`E:/` → 父它自己、前缀空。
        var split = js[js.IndexOf("function dirSplit", StringComparison.Ordinal)..];
        split = split[..split.IndexOf("\n}", StringComparison.Ordinal)];
        Assert.Contains("lastIndexOf(\"/\")", split, StringComparison.Ordinal);
        Assert.Contains("lastIndexOf(\"\\\\\")", split, StringComparison.Ordinal);
        // ⚠️ 只断言那个**条件**存在是不够的（2026-10-17 变异实测：把结果改成
        // `prefix: text` 守卫照样绿）——判据必须钉住**结果那一行**：父目录是它自己、前缀为空。
        Assert.Matches(@"cut === text\.length - 1\)\s*return\s*\{\s*dir:\s*text,\s*prefix:\s*""""\s*\}", split);

        // ③ 请求次序：`dirSuggest` 里**每一处**渲染之前，都必须有一个位于最近一次
        // `await` 之后的序号比较。过期的响应只可能在 await 之后到达，所以判据是
        // 「最近一次 await 之后有守卫」——一处守卫可以保护它后面的多处同步渲染。
        // ⚠️ 这比「第一处守卫在第一次渲染前」强：后者对第二处渲染完全不设防。
        var suggest = js[js.IndexOf("async function dirSuggest", StringComparison.Ordinal)..];
        suggest = suggest[..suggest.IndexOf("\n}", StringComparison.Ordinal)];
        var lastAwait = -1;
        var lastGuard = -1;
        var bad = new List<string>();
        foreach (Match m in Regex.Matches(suggest, @"await\s|if \(seq !== dirBrowse\.seq\)|renderDirItems\("))
        {
            if (m.Value.StartsWith("await", StringComparison.Ordinal)) { lastAwait = m.Index; lastGuard = -1; }
            else if (m.Value.StartsWith("if", StringComparison.Ordinal)) lastGuard = m.Index;
            else if (lastAwait >= 0 && lastGuard < 0)
                bad.Add(suggest.Substring(Math.Max(0, m.Index - 40), Math.Min(80, suggest.Length - Math.Max(0, m.Index - 40))));
        }
        Assert.True(bad.Count == 0, "dirSuggest 里有渲染点在最近一次 await 之后、且中间没有序号守卫：\n" + string.Join("\n", bad));

        // ④ 键盘：↑/↓ 选、回车补全、Esc 收起——这是「下拉」而不是「点按钮」的核心动作。
        foreach (var key in new[] { "ArrowDown", "ArrowUp", "Enter", "Escape" })
            Assert.Contains($"e.key === \"{key}\"", js, StringComparison.Ordinal);
    }
}

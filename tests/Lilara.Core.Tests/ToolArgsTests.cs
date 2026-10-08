using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;

namespace Lilara.Core.Tests;

/// <summary>
/// 工具的**参数解析**（`01` §9.3）。
///
/// <para>⚠️ <b>这一组守的是一条纪律，不是几个用例</b>：模型是**不可信输入源**
/// （见 <c>ListTool.cs</c> 里 <c>ToolArgs</c> 的那段注释）——少个键、给个数字、
/// 给一段不是 JSON 的东西，**都必须变成一条说人话的结果，而不是异常**。
/// 异常会让 <c>tool_use</c> / <c>tool_result</c> 那一对**只落一半**，
/// 而半个在拿到的不是「少点内容」，是**上游 400**（`01` §3.6 ②）。</para>
///
/// <para>⚠️ <b>「不宽容」是判据，不是洁癖</b>：<c>"42"</c>（字符串）与 <c>1.5</c>（小数）
/// 都必须给 <c>null</c>，因为**偏移不是那种量**。宽容地"帮忙解析"会让一个
/// 模型写错的参数变成一次**看着正常、实际错位**的读取——而它不会报错。</para>
/// </summary>
public class ToolArgsTests
{
    [Theory]
    [InlineData("{\"offset\":42}", 42L)]
    [InlineData("{\"offset\":0}", 0L)]
    [InlineData("{\"offset\":-1}", -1L)]        // ⚠️ 负数**这里不判合法性**——那是调用方的事（它知道语义）
    [InlineData("{\"offset\":9007199254740993}", 9007199254740993L)]  // 超 double 精度 ⇒ 证明走的是 Int64
    [InlineData("{\"offset\":1.5}", null)]      // 小数 ⇒ null（偏移不是一个"大概"的量）
    [InlineData("{\"offset\":\"42\"}", null)]   // 字符串 ⇒ null（**不**替模型猜）
    [InlineData("{\"other\":1}", null)]         // 缺键
    [InlineData("{\"offset\":null}", null)]     // 显式 null
    [InlineData("[]", null)]                    // 根不是对象
    [InlineData("not json", null)]              // 整段不是 JSON
    [InlineData("", null)]                      // 空串
    public void Number_ReadsWhatItSaysOrReturnsNull(string json, long? expected)
    {
        Assert.Equal(expected, ToolArgs.Number(json, "offset"));
    }

    [Theory]
    [InlineData("{\"path\":\"a.txt\"}", "a.txt")]
    [InlineData("{\"path\":\"\"}", "")]         // ⚠️ 空串是**值**，不是"没给"——判空串是调用方的事
    [InlineData("{\"path\":42}", null)]
    [InlineData("not json", null)]
    public void String_ReadsWhatItSaysOrReturnsNull(string json, string? expected)
    {
        Assert.Equal(expected, ToolArgs.String(json, "path"));
    }
}

/// <summary>
/// **上限只能有一个源**（本计划的 G17）。
///
/// <para>⚠️ <b>为什么这是一条测试而不是一句注释</b>：`read` 的单次上限与 `ls` 里那句
/// 「这个文件太大」若**各写一份数**，那么改一处、另一处就**开始说谎**——
/// 而**没有任何东西会红**（同族：`01` §8.0.2「两层判据必须共用一个谓词」，
/// 本仓在别处栽过四次）。</para>
/// </summary>
public class ToolLimitsTests
{
    /// <summary>
    /// ⚠️ **这条钉的是「唯一来源」这个结构本身**：两个工具都从 <see cref="ToolLimits"/> 取数，
    /// 所以「上限」在源码里**只出现一次**。
    /// </summary>
    [Fact]
    public void ToolLimits_IsTheOnlyPlaceWhereTheCapIsWrittenDown()
    {
        // 上限那个数**只许**住在 ToolLimits.cs 里。
        // 判据是「这个十进制字面量在别的工具文件里不出现」，而不是「有没有引用 ToolLimits」——
        // 后者能被"引用一下、但自己再写一份数"骗过。
        var cap = ToolLimits.MaxResultBytes.ToString();
        Assert.True(cap.Length >= 4, "上限太小了，这个判据会误报（数字串太短会撞上别处的常量）");

        // ⚠️ 扫**整个目录**而不是点几个文件名：将来新增的工具文件若自带一份数，
        // 点名的写法**看不见它**——而那正是这条测试存在的理由。
        var dir = Path.Combine(SourceDir(), "Session", "Tools");
        var offenders = Directory.EnumerateFiles(dir, "*.cs")
            .Where(f => Path.GetFileName(f) != "ToolLimits.cs")
            .Where(f => File.ReadAllText(f).Contains(cap, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"这些文件里又写了一遍上限 {cap}（它只该住在 ToolLimits.cs）：{string.Join(", ", offenders)}");
    }

    /// <summary>
    /// ⚠️ 上限必须是**有意义的正数**，而且要小到真拦得住东西。
    /// 「设成 <c>int.MaxValue</c> 就等于没设」是这类常量最常见的失效方式，而它**看起来完全正常**。
    /// </summary>
    [Fact]
    public void Caps_AreSmallEnoughToActuallyBite()
    {
        Assert.InRange(ToolLimits.MaxResultBytes, 1, 4 * 1024 * 1024);   // ≤ 4 MiB
        Assert.InRange(ToolLimits.MaxDirectoryEntries, 1, 100_000);
    }

    /// <summary>
    /// ⚠️ <b>同一个数的另一条底线：它不能小到把正常用法也拦掉</b>——
    /// 一个 4 KiB 的上限会让每个普通源文件都被截断，那等于把工具废掉。
    /// </summary>
    [Fact]
    public void Caps_AreLargeEnoughForNormalUse()
    {
        Assert.True(ToolLimits.MaxResultBytes >= 64 * 1024, "上限小于 64 KiB ⇒ 普通源文件都会被截断");
        Assert.True(ToolLimits.MaxDirectoryEntries >= 200, "上限小于 200 条 ⇒ 普通仓库的根目录都列不全");
    }

    /// <summary>找源码树的根（测试跑在 <c>bin/…</c> 下）。</summary>
    private static string SourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentLilaraCode.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Lilara.Core");
    }
}

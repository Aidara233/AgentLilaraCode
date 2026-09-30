using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 五轴（`01` §3.5）的**唯一**真相来源。
/// ⚠️ 这些测试钉的是「枚举与它声明的取值一致」——两处各写一份是这类表最容易漂的地方。
/// </summary>
public class AxisTests
{
    [Fact]
    public void FiveAxes_AreExactlyTheOnesTheDocNames()
    {
        // ⚠️ 顺序**就是** `01` §3.5 表格的顺序：主动性 · 权限 · 安全 · 验证强度 · 思考强度。
        // 它决定快照里轴的排列，也就决定头的渲染顺序——顺序不稳等于每步重排。
        Assert.Equal(["initiative", "permission", "safety", "verify", "effort"], Axes.Names);
    }

    [Fact]
    public void EveryAxis_HasAtLeastTwoValues()
    {
        foreach (var axis in Axes.Names)
            Assert.True(Axes.ValuesOf(axis)!.Count >= 2, $"轴 {axis} 至少要两个取值，否则它不是轴");
    }

    [Fact]
    public void Defaults_CoverEveryAxis_WithALegalValue()
    {
        var defaults = Axes.Defaults();
        Assert.Equal(Axes.Names.Count, defaults.Count);
        foreach (var (axis, value) in defaults)
            Assert.True(Axes.TryParse(axis, value, out _), $"默认值 {axis}={value} 不在合法取值里");
    }

    /// <summary>⚠️ 默认值必须是 `01` §3.5 表里那个「最没有意外」的档，不是随手挑的。</summary>
    [Fact]
    public void Defaults_AreTheOnesTheDocImplies()
    {
        var d = Axes.Defaults();
        Assert.Equal("step", d["initiative"]);     // 步进：核心不替你决定该不该继续（§6.1）
        Assert.Equal("standard", d["permission"]); // 标准：写入类要问
        Assert.Equal("dev", d["safety"]);          // 开发：绝不能默认「生产」
        Assert.Equal("self", d["verify"]);         // 自检
        Assert.Equal("high", d["effort"]);         // 与建会话时的既有默认一致（CommandDispatcher 取 "high"）
    }

    [Fact]
    public void UnknownAxis_IsNotKnown() => Assert.False(Axes.IsKnown("mood"));

    [Fact]
    public void UnknownValue_IsRefused_AndTheAxisIsUnchanged()
    {
        // ⚠️ 这里的「未知值」是 `production`，**不是** `prod`——
        // `prod` 是 safety 轴的**合法**取值（Step 3 的表），拿它当未知值这条测试必红。
        Assert.False(Axes.TryParse("safety", "production", out var canonical));
        Assert.Equal("", canonical);
    }

    /// <summary>
    /// ⚠️ 合法值清单必须是**从枚举拼出来的**，不是手抄的字符串。
    /// 手抄的清单在加档时会静默过期，而错误信息恰恰是用户唯一的线索。
    /// </summary>
    [Fact]
    public void LegalValuesText_ListsEveryValue_FromTheTableNotAHandCopy()
    {
        var text = Axes.LegalValuesText("safety");
        foreach (var v in Axes.ValuesOf("safety")!) Assert.Contains(v, text);
        Assert.Equal(string.Join("|", Axes.ValuesOf("safety")!), text);
    }
}

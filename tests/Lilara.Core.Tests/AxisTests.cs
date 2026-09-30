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

    /// <summary>取值是用户手打的，`PROD` 要认，并且回吐的是**表里的那个拼法**。</summary>
    [Fact]
    public void ValueCase_IsInsensitive_AndNormalizesToTheTableSpelling()
    {
        Assert.True(Axes.TryParse("safety", "PROD", out var canonical));
        Assert.Equal("prod", canonical);
    }

    /// <summary>
    /// ⚠️ 轴名走 `StringComparer.Ordinal`，**敏感是有意的**：它是协议标识符，
    /// `Safety` 不是 `safety`，应当拒绝而不是替用户猜。别"顺手统一"成忽略大小写。
    /// </summary>
    [Fact]
    public void AxisName_IsCaseSensitive()
    {
        Assert.False(Axes.TryParse("Safety", "dev", out var canonical));
        Assert.Equal("", canonical);
    }

    /// <summary>
    /// ⚠️ 表是**进程级静态**的，交出去的取值一旦可写，改一处就等于改了所有人的表
    /// （`TryParse` / `LegalValuesText` / 头的下拉会一起变，且**没有任何报错**）。
    /// 这条钉的是「交出来的是只读视图」——不是「当前值恰好是 dev|staging|prod」。
    /// </summary>
    [Fact]
    public void ValuesOf_HandsOutSomethingTheCallerCannotMutate()
    {
        var list = (IList<string>)Axes.ValuesOf("safety")!;
        Assert.Throws<NotSupportedException>(() => list[0] = "production");
        Assert.Equal("dev", Axes.ValuesOf("safety")![0]);   // 静态表毫发无损
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

    /// <summary>
    /// ⚠️ **老会话（磁盘上没有 `axes`）载入后必须拿到默认值**，而不是空字典。
    /// 空字典会让头渲染五个空白下拉，用户分不清「没设过」与「头读不到」。
    /// </summary>
    [Fact]
    public void Load_OfALegacyStateFile_FillsInTheDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "lilara-axis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new Lilara.Core.Persistence.SessionStore(root);
            var pk = Lilara.Core.Projects.ProjectKey.FromPath(@"C:\work");
            store.WriteState(pk, new Lilara.Core.Persistence.SessionState
            {
                SessionId = "s1", Status = "Idle", Turn = 0, Pending = [],   // ⚠️ 刻意不设 Axes
            });
            store.WriteSpec(pk, new Lilara.Core.Persistence.SessionSpec
            {
                SessionId = "s1", ProjectKey = pk, WorkingDirectory = @"C:\work",
                CreatedAt = DateTimeOffset.UnixEpoch,
                Model = new Lilara.Core.Persistence.ModelSelection
                {
                    ProviderId = "p", ModelId = "m", Protocol = "claude-messages",
                    Effort = "high", MaxTokens = 16000,
                },
            });

            var handle = SessionHandle.Load(store, pk, "s1");

            Assert.Equal(Axes.Defaults(), handle.Axes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}

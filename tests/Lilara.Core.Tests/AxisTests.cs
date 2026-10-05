using Lilara.Core.Kernel;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

// 五轴的唯一真相来源；钉的是「枚举与它声明的取值一致」。
public class AxisTests
{
    [Fact]
    public void FiveAxes_AreExactlyTheOnesTheDocNames()
    {
        // 顺序决定快照里轴的排列，也就决定头的渲染顺序——顺序不稳等于每步重排。
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

    // 默认值是表里那个「最没有意外」的档，不是随手挑的。
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
        // 这里的「未知值」是 production 而不是 prod——prod 是 safety 轴的合法取值。
        Assert.False(Axes.TryParse("safety", "production", out var canonical));
        Assert.Equal("", canonical);
    }

    // 取值是用户手打的：PROD 要认，且回吐表里的那个拼法。
    [Fact]
    public void ValueCase_IsInsensitive_AndNormalizesToTheTableSpelling()
    {
        Assert.True(Axes.TryParse("safety", "PROD", out var canonical));
        Assert.Equal("prod", canonical);
    }

    // 轴名走 StringComparer.Ordinal，敏感是有意的：Safety 不是 safety，应拒绝而不是替用户猜。
    [Fact]
    public void AxisName_IsCaseSensitive()
    {
        Assert.False(Axes.TryParse("Safety", "dev", out var canonical));
        Assert.Equal("", canonical);
    }

    // 表是进程级静态的，交出去的取值必须只读，否则改一处等于改了所有人的表（且没有任何报错）。
    [Fact]
    public void ValuesOf_HandsOutSomethingTheCallerCannotMutate()
    {
        var list = (IList<string>)Axes.ValuesOf("safety")!;
        Assert.Throws<NotSupportedException>(() => list[0] = "production");
        Assert.Equal("dev", Axes.ValuesOf("safety")![0]);   // 静态表毫发无损
    }

    // 合法值清单必须从枚举拼出来，手抄的会在加档时静默过期，而错误信息是用户唯一的线索。
    [Fact]
    public void LegalValuesText_ListsEveryValue_FromTheTableNotAHandCopy()
    {
        var text = Axes.LegalValuesText("safety");
        foreach (var v in Axes.ValuesOf("safety")!) Assert.Contains(v, text);
        Assert.Equal(string.Join("|", Axes.ValuesOf("safety")!), text);
    }

    // 老会话（磁盘上没有 axes）载入后必须拿到整套默认值，而不是空字典（空字典会让头渲染五个空白下拉）；
    // 断言钉的是 handle.State.Axes，轴只有这一份存储。
    // spec 里的 effort 刻意写 "low"：写 "high" 时这条对「该不该按 spec 播种」是瞎的。
    [Fact]
    public void Load_OfALegacyStateFile_FillsInTheDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "lilara-axis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new Lilara.Core.Persistence.SessionStore(root);
            var pk = Lilara.Core.Kernel.ProjectKey.FromPath(@"C:\work");
            store.WriteState(pk, new Lilara.Core.Kernel.SessionState
            {
                SessionId = "s1", Status = "Idle", Turn = 0, Pending = [],   // 刻意不设 Axes
            });
            store.WriteSpec(pk, new Lilara.Core.Kernel.SessionSpec
            {
                SessionId = "s1", ProjectKey = pk, WorkingDirectory = @"C:\work",
                CreatedAt = DateTimeOffset.UnixEpoch,
                Model = new Lilara.Core.Kernel.ModelSelection
                {
                    ProviderId = "p", ModelId = "m", Protocol = "claude-messages",
                    Effort = "low", MaxTokens = 16000,
                },
            });

            var handle = SessionHandle.Load(store, pk, "s1");

            // 表默认值的五条，除了 effort——effort 来自 spec 的播种。
            var expected = new Dictionary<string, string>(Axes.Defaults(), StringComparer.Ordinal)
            {
                [Axes.Effort] = "low",
            };
            Assert.Equal(expected, handle.State.Axes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // 「有键但不全」的 axes 也必须逐键补齐（旧判据「整份非空」漏了它，而 Stepper 用索引器读 ⇒ KeyNotFoundException）。
    // 覆盖顺序：磁盘上存的那份排最后，故 effort="max" 必须盖过 spec 里的 "low"——
    // 反过来的话，setAxis 改的轴会在下一次重启时被 spec 悄悄改回去。
    [Fact]
    public void Load_OfAPartialAxesFile_TopsUpTheMissingKeys_AndLetsTheStoredOnesWin()
    {
        var root = Path.Combine(Path.GetTempPath(), "lilara-axis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new Lilara.Core.Persistence.SessionStore(root);
            var pk = Lilara.Core.Kernel.ProjectKey.FromPath(@"C:\work");
            store.WriteState(pk, new Lilara.Core.Kernel.SessionState
            {
                SessionId = "s1", Status = "Idle", Turn = 0, Pending = [],
                // 5 条轴里只有 2 条：既不全、又合法
                Axes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Axes.Initiative] = "goal",
                    [Axes.Effort] = "max",
                },
            });
            store.WriteSpec(pk, new Lilara.Core.Kernel.SessionSpec
            {
                SessionId = "s1", ProjectKey = pk, WorkingDirectory = @"C:\work",
                CreatedAt = DateTimeOffset.UnixEpoch,
                Model = new Lilara.Core.Kernel.ModelSelection
                {
                    ProviderId = "p", ModelId = "m", Protocol = "claude-messages",
                    Effort = "low", MaxTokens = 16000,   // 与磁盘上那份故意不同
                },
            });

            var handle = SessionHandle.Load(store, pk, "s1");
            var axes = handle.State.Axes!;

            Assert.Equal("goal", axes[Axes.Initiative]);       // 存的留着
            Assert.Equal("max", axes[Axes.Effort]);            // 磁盘的盖过 spec 的 "low"
            Assert.Equal("standard", axes[Axes.Permission]);   // 缺的按默认值补齐
            Assert.Equal("dev", axes[Axes.Safety]);
            Assert.Equal("self", axes[Axes.Verify]);
            Assert.Equal(Axes.Names.Count, axes.Count);        // 逐键齐全，一条不缺
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // null 轴名 = 未知轴，吃轴名的每个入口都不许抛（Dictionary 的 key 不许为 null）。
    // 命令路径在 IsKnown 那关就返回，走不到其余入口，故那几条的判空契约由本文件守。
    [Fact]
    public void NullAxis_IsTreatedAsUnknown_ByEveryAxisNameEntryPoint()
    {
        Assert.False(Axes.IsKnown(null));
        Assert.Null(Axes.ValuesOf(null));
        Assert.Equal("", Axes.LegalValuesText(null));
        Assert.False(Axes.TryParse(null, "dev", out var canonical));
        Assert.Equal("", canonical);            // 与「未知轴」一致，不回吐半截
        Assert.Equal("", Axes.LabelOf(null));   // null 轴名没有标签可回吐
        Assert.False(Axes.InContext(null));     // 叫不出名字的轴，一律当作「不进上下文」
    }

    // 每条轴都要有自己的中文标签、不是回退成档名——少一个真标签就是头的某一格空白。
    // 断言 label != name 是关键：LabelOf 对未知轴原样返回，故「non-empty」单独还不够。
    [Fact]
    public void EveryAxis_HasItsOwnChineseLabel_NotTheFallbackName()
    {
        foreach (var n in Axes.Names)
        {
            var label = Axes.LabelOf(n);
            Assert.False(string.IsNullOrWhiteSpace(label), $"轴 '{n}' 没有标签");
            Assert.NotEqual(n, label);
        }
    }

    // 「只有 effort 不进上下文」需要守卫：只断言 effort 侧是 false 时，
    // 把 InContext 写成恒 false 全部测试仍绿，而头上五个下拉全被画灰。
    [Theory]
    [InlineData(Axes.Initiative, true)]
    [InlineData(Axes.Permission, true)]
    [InlineData(Axes.Safety, true)]
    [InlineData(Axes.Verify, true)]
    [InlineData(Axes.Effort, false)]
    public void InContext_IsTrueForEveryAxisExceptEffort(string axis, bool expected) =>
        Assert.Equal(expected, Axes.InContext(axis));

    // permission 的档位顺序是契约：头的跨轴告警按 values.indexOf 判档位，
    // 重排或插入第 6 档会让头给出错误档位而测试不红。
    // AlertFrom / AlertSafety 必须落在各自轴的取值里，否则告警静默失灵。
    [Fact]
    public void PermissionValues_KeepTheGateOrder_AndTheAlertEndsAreReal()
    {
        Assert.Equal(
            ["off", "readonly", "investigate", "standard", "authorized", "trusted"],
            Axes.ValuesOf(Axes.Permission));
        Assert.Contains(Axes.AlertFrom, Axes.ValuesOf(Axes.Permission)!);
        Assert.Contains(Axes.AlertSafety, Axes.ValuesOf(Axes.Safety)!);
    }
}

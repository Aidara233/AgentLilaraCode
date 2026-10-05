using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

/// <summary>
/// ⚠️ **`provider.upsert` 的 `id` 校验**（路径穿越——实测过的缺陷）：
/// 控制台敲 `provider set ../escaped --key k` 会回「已保存」，而文件落在
/// **数据根**（`providers/` 之外）。成因是 `ProviderStore.PathFor(id) => _root/{id}.json`
/// 而 dispatcher 只校验了 Protocol / Auth。
///
/// <para>⚠️ **入口是 dispatcher**（不是控制台）：控制台与头是**两个前端**、共用一条命令通道，
/// 校验放前端会被另一个绕过（G16）。这里测的就是那条共用入口。</para>
/// </summary>
public class ProviderIdTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-pid-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-pidwork-" + Guid.NewGuid().ToString("N"));

    private readonly CommandDispatcher _dispatcher;

    public ProviderIdTests()
    {
        var host = new CoreHost(_root, _work);
        _dispatcher = new CommandDispatcher(host, new EventBus(), _ => new Stepper(host.Store, new FakeAdapter()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    /// <summary>
    /// ⚠️ 判据是「**id 是不是一个单纯的文件名**」，拒绝的是**路径语法**，不是**字符**。
    /// 每一条的来路见 <see cref="CommandDispatcher.IsSafeProviderId"/> 的注释。
    /// </summary>
    [Theory]
    [InlineData("../escaped")]        // 实测过：写到数据根
    [InlineData("..\\escaped")]       // Windows 分隔符
    [InlineData("a/b")]               // 正斜杠
    [InlineData("a\\b")]              // 反斜杠
    [InlineData("/abs")]              // 绝对路径
    [InlineData("C:\\abs")]           // 盘符
    [InlineData("")]                  // 空
    [InlineData("   ")]               // 全空白
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\0b")]              // 内嵌 NUL
    [InlineData("NUL")]               // ⚠️ Windows 设备名：`ProviderStore.Save` 抛未捕获异常 ⇒ **掀翻常驻进程**
    [InlineData("nul")]               // ⚠️ 设备名判定**大小写不敏感**（实测同样掀翻进程）
    [InlineData("CON.json")]          // ⚠️ 要**去掉扩展名再比**（`PathFor` 还会再拼一个 `.json`）
    [InlineData("NUL.txt.txt.json")]  // ⚠️ 同样是设备名；`GetFileNameWithoutExtension` 会**放行**它
    [InlineData("NUL  .json")]        // ⚠️ 尾随**空格**再带扩展名——`TrimEnd` 那一半
    [InlineData("COM1.json")]         // ⚠️ 同族，抛 FileNotFoundException('\\.\COM1')
    [InlineData("PRN")]               // ⚠️ 覆盖缺口：原先无任何用例碰 PRN/AUX 分支 ⇒ 删掉那两行**变异存活**
    [InlineData("AUX")]
    [InlineData("LPT9")]              // ⚠️ 下边界那一族也要有一条
    [InlineData("CONIN$")]            // ⚠️ 带 `$` 的伪设备；**裸 `CONIN` 是正常文件，别顺手加**
    [InlineData("CONOUT$")]
    public async Task ProviderUpsert_WithAnUnsafeId_IsRejected(string id)
    {
        var cmd = new CoreCommand.ProviderUpsert(id, "k", null, null, null, null);
        // ⚠️ `await`（不是 `.GetAwaiter().GetResult()`）：阻塞式调用落在 `[Theory]` 里会触发
        // xUnit1031，本项目 `-warnaserror` 下就是编译错误。判据与用例本身照 brief。
        var result = await _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(result.Ack);
    }

    /// <summary>
    /// ⚠️ 上一条理论只断言「回 rejected」——但这条缺陷的**危害是「文件落到错地方」**：
    /// 闸若被挪到写入**之后**，ack 仍会是 rejected，而文件**已经写出去了**。
    /// ⚠️ `_root` **就是数据根**：`PathFor("../escaped")` = `<数据根>/providers/../escaped.json`
    /// = `<数据根>/escaped.json` ⇒ 断言它**没落**。
    /// </summary>
    [Fact]
    public async Task ProviderUpsert_WithAPathTraversingId_LeavesNothingInTheDataRoot()
    {
        var cmd = new CoreCommand.ProviderUpsert("../escaped", "k", null, null, null, null);
        var result = await _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(result.Ack);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.json")));
    }

    /// <summary>
    /// ⚠️ **`本地` 必须放行**——判据不是「像不像 ASCII 标识符」。
    /// 白名单型判据（只认 `[a-z0-9-_]`）会把这类**用户正当的输入**一起挡掉，
    /// 这是本项目实测过的教训方向。
    /// </summary>
    [Theory]
    [InlineData("local")]
    [InlineData("my-provider")]
    [InlineData("p1")]
    [InlineData("本地")]                // 非 ASCII 合法——别用字符白名单把它挡了
    [InlineData("NULx")]               // ⚠️ **反向对照**：前缀型判据会误杀它（实测是正常文件）
    [InlineData("COM1x")]              // ⚠️ 反向对照：同上（实测正常写入）
    [InlineData("COM10")]              // ⚠️ 反向对照：保留表只有 1–9（实测 `COM10.json` 正常写入）
    [InlineData("COM0")]               // ⚠️ 反向对照：**下边界** 0 不是设备名（`PRN`/`AUX` 那类分支的边界）
    [InlineData("LPT0")]
    [InlineData("CONIN")]              // ⚠️ 反向对照：**裸** `CONIN` 是正常文件（只有 `CONIN$` 才是伪设备）
    public async Task ProviderUpsert_WithASafeId_IsAccepted(string id)
    {
        var cmd = new CoreCommand.ProviderUpsert(id, "k", null, null, null, null);
        var result = await _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(result.Ack);
    }

    /// <summary>
    /// ⚠️ **单独钉住那条判据本身**（它是 `internal static`，T4 的 provider-schema 要把它交出去）。
    /// 与上面两条走 dispatcher 的理论**分工不同**：那两条钉「dispatcher 确实调了它」，
    /// 这条钉「它自己的契约」——`../escaped` 与 `本地` 在**同一个**谓词下的两端。
    /// ⚠️ 因此**变异「去掉调用」时这一条仍绿**：变的是调用点，不是谓词——这是刻意的分工。
    /// </summary>
    [Theory]
    [InlineData("../escaped", false)]
    [InlineData("a/b", false)]          // ⚠️ Windows 上 `/` 也算分隔符（见实现的注释）
    [InlineData("a\\b", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a\0b", false)]
    [InlineData("NUL", false)]          // ⚠️ Windows 设备名：未捕获异常**掀翻常驻进程**（不是静默）
    [InlineData("nul", false)]          // 大小写不敏感
    [InlineData("CON.json", false)]     // 去掉扩展名再比
    [InlineData("NUL.txt.txt.json", false)]   // 第一个点之前那段才是判据（不是 GetFileNameWithoutExtension）
    [InlineData("NUL  .json", false)]         // 尾随空格 ⇒ TrimEnd
    [InlineData("COM1.json", false)]          // 1–9 那一族（走抛异常）
    [InlineData("PRN", false)]                // ⚠️ PRN / AUX 两条分支原先**零覆盖**（删掉即变异存活）
    [InlineData("AUX", false)]
    [InlineData("LPT9", false)]               // 下边界那一族
    [InlineData("CONIN$", false)]             // 带 `$` 的伪设备
    [InlineData("CONOUT$", false)]
    [InlineData("NULx", true)]                // ⚠️ 反向：前缀型会误杀
    [InlineData("COM1x", true)]               // ⚠️ 反向
    [InlineData("COM10", true)]               // ⚠️ 反向：只有 1–9
    [InlineData("COM0", true)]                // ⚠️ 反向：下边界
    [InlineData("LPT0", true)]
    [InlineData("CONIN", true)]               // ⚠️ 反向：裸 CONIN 正常（只有带 `$` 的才是伪设备）
    [InlineData("local", true)]
    [InlineData("my-provider", true)]
    [InlineData("本地", true)]
    public void IsSafeProviderId_PinsThePredicateItself(string id, bool expected) =>
        Assert.Equal(expected, CommandDispatcher.IsSafeProviderId(id));

    /// <summary>
    /// ⚠️ **上标 `¹` `³`（U+00B9 / U+00B3）也算设备名的「数字」——而别的「数字」写法不算。**
    ///
    /// <para>复审点名风险抓出的缺口：判据原先用字面量范围 `'1'..'9'`，于是
    /// `COM¹` / `LPT³` **被放行**——而实测它们与 `COM1` 一样抛未捕获异常**掀翻常驻进程**
    /// （`ProviderStore.Save` → `AtomicFile` 的 `.tmp` 写入在 `try` **之外** ⇒ 直接冒出去）。</para>
    ///
    /// <para>⚠️ **只加上标这三个，别扩**：实测 `COM⁴` `COM⁵` `COM⁹`（U+2074…）、全角 `COM１`（U+FF11）、
    /// 阿拉伯-印度 `COM١`（U+0661）**都是正常文件**。按「数字」的直觉去扩，会把判据开得比系统宽——
    /// 那是另一个方向的错（见本测试的反向那半）。</para>
    ///
    /// <para>⚠️ **这里用 `(char)0x00B9` 拼进字符串，不写进 `InlineData` 字面量**：上标是**易混淆字符**，
    /// 字面量会在某个环节被工具链规范化成普通数字 —— 于是测试**绿了却什么都没测**。
    /// 本项目吃过这个亏（`ParserTests` 的 NBSP 那两条就是这么防的）。
    /// ⇒ **不可见的输入要用字节核**；这里由构造方式保证，不赌肉眼与工具链。**</para>
    /// </summary>
    [Fact]
    public void IsSafeProviderId_RejectsSuperscriptDigits_ButNotOtherDigitForms()
    {
        var sup1 = new string((char)0x00B9, 1);   // ¹
        var sup2 = new string((char)0x00B2, 1);   // ²
        var sup3 = new string((char)0x00B3, 1);   // ³

        // ⚠️ **三个上标逐个钉**：只钉 ¹/³ 的话，删掉 `or '²'` 是**存活变异**
        //    （复审点出的——这正是「分支零覆盖」那条标准，别只铺我随手想到的那两个）。
        Assert.False(CommandDispatcher.IsSafeProviderId("COM" + sup1));
        Assert.False(CommandDispatcher.IsSafeProviderId("COM" + sup2));
        Assert.False(CommandDispatcher.IsSafeProviderId("LPT" + sup1));
        Assert.False(CommandDispatcher.IsSafeProviderId("LPT" + sup2));
        Assert.False(CommandDispatcher.IsSafeProviderId("LPT" + sup3));

        // ⚠️ 反向：别的「数字」写法**不是**设备名（实测都正常写入）⇒ 必须放行
        Assert.True(CommandDispatcher.IsSafeProviderId("COM" + (char)0x2074));  // ⁴
        Assert.True(CommandDispatcher.IsSafeProviderId("COM" + (char)0xFF11));  // １ 全角
        Assert.True(CommandDispatcher.IsSafeProviderId("COM" + (char)0x0661));  // ١ 阿拉伯-印度
    }

    /// <summary>
    /// ⚠️ **上标那条必须也走 dispatcher 一遍**（与 `CONIN$` / `PRN` / `AUX` 同一标准）。
    ///
    /// <para>复审点出：上标原先**只在谓词层**被钉，而别的不安全 id 都是**两层都钉**
    /// （`ProviderUpsert_WithAnUnsafeId_IsRejected` + 真值表）。
    /// ⇒ 只钉谓词层的缺口是：**闸被挪到写入之后**时没人发现——
    /// 谓词全绿，而设备路径上的异常照样掀翻进程。</para>
    ///
    /// <para>⚠️ 上标是**易混淆字符** ⇒ 用 `(char)` 拼、不写字面量；并**同时钉住它到底是什么码位**，
    /// 免得哪天构造写错而测试静默变成「跟 `COM1` 一样」的重复用例。</para>
    /// </summary>
    [Fact]
    public async Task ProviderUpsert_WithASuperscriptDeviceId_IsRejectedThroughTheDispatcher()
    {
        Assert.Equal(0x00B9, "¹"[0]);   // ⚠️ 先证明这个字面量就是 U+00B9（不然下面测的是别的东西）

        var cmd = new CoreCommand.ProviderUpsert("COM" + (char)0x00B9, "k", null, null, null, null);
        var result = await _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(result.Ack);
    }

    /// <summary>
    /// ⚠️ **`CONIN$` / `CONOUT$` 是伪设备，而裸 `CONIN` / `CONOUT` 是正常文件。**
    ///
    /// <para>实测走 `ProviderStore.Save`（`AtomicFile`）：`CONIN$` 抛 `IOException`、
    /// `CONOUT$` 抛 `UnauthorizedAccessException` ⇒ 未捕获 ⇒ **掀翻常驻进程**；
    /// 而 `CONIN` / `CONOUT`（无 `$`）**正常写入**。</para>
    ///
    /// <para>⚠️ **反向那半更要紧**：把 `CONIN` 也挡掉会误杀一个完全正当的名字——
    /// 而判据收窄得过头时，**症状是「少挡了」而不是「多挡了」**，没人会发现。
    /// ⇒ 两端一起钉。</para>
    /// </summary>
    [Fact]
    public void IsSafeProviderId_RejectsTheDollarPseudoDevices_ButNotTheBareNames()
    {
        Assert.False(CommandDispatcher.IsSafeProviderId("CONIN$"));
        Assert.False(CommandDispatcher.IsSafeProviderId("CONOUT$"));

        // ⚠️ 反向：裸名字两个都是**正常文件**（实测）⇒ 必须放行
        Assert.True(CommandDispatcher.IsSafeProviderId("CONIN"));
        Assert.True(CommandDispatcher.IsSafeProviderId("CONOUT"));
    }
}

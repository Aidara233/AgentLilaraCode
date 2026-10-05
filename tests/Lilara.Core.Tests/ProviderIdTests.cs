using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

// id 必须是单纯的文件名：PathFor(id) => _root/{id}.json，未校验的 ../escaped 会落到数据根之外；
// 校验必须在 dispatcher（控制台与头共用的入口），放前端会被另一个绕过。
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

    // 判据是「id 是不是一个单纯的文件名」，拒绝的是路径语法，不是字符。
    [Theory]
    [InlineData("../escaped")]        // 写到数据根
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
    [InlineData("NUL")]               // Windows 设备名：ProviderStore.Save 抛未捕获异常 ⇒ 掀翻常驻进程
    [InlineData("nul")]               // 设备名判定大小写不敏感
    [InlineData("CON.json")]          // 要去掉扩展名再比（PathFor 还会再拼一个 .json）
    [InlineData("NUL.txt.txt.json")]  // GetFileNameWithoutExtension 会放行它
    [InlineData("NUL  .json")]        // 尾随空格再带扩展名
    [InlineData("COM1.json")]         // 同族，抛 FileNotFoundException('\\.\COM1')
    [InlineData("PRN")]               // PRN/AUX 分支
    [InlineData("AUX")]
    [InlineData("LPT9")]              // 下边界那一族
    [InlineData("CONIN$")]            // 带 $ 的伪设备；裸 CONIN 是正常文件
    [InlineData("CONOUT$")]
    public async Task ProviderUpsert_WithAnUnsafeId_IsRejected(string id)
    {
        var cmd = new CoreCommand.ProviderUpsert(id, "k", null, null, null, null);
        // 必须 await：阻塞式调用落在 [Theory] 里触发 xUnit1031（-warnaserror 下即 error）。
        var result = await _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(result.Ack);
    }

    // 上一条只断言「回 rejected」，但危害是「文件落到错地方」：
    // 闸若挪到写入之后，ack 仍是 rejected，而文件已经写出去了。
    [Fact]
    public async Task ProviderUpsert_WithAPathTraversingId_LeavesNothingInTheDataRoot()
    {
        var cmd = new CoreCommand.ProviderUpsert("../escaped", "k", null, null, null, null);
        var result = await _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(result.Ack);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.json")));
    }

    // 判据不是「像不像 ASCII 标识符」：白名单型判据会把「本地」这类用户正当输入一起挡掉。
    [Theory]
    [InlineData("local")]
    [InlineData("my-provider")]
    [InlineData("p1")]
    [InlineData("本地")]                // 非 ASCII 合法——别用字符白名单把它挡了
    [InlineData("NULx")]               // 反向对照：前缀型判据会误杀它（实测是正常文件）
    [InlineData("COM1x")]              // 反向对照：同上
    [InlineData("COM10")]              // 反向对照：保留表只有 1–9
    [InlineData("COM0")]               // 反向对照：下边界 0 不是设备名
    [InlineData("LPT0")]
    [InlineData("CONIN")]              // 反向对照：裸 CONIN 是正常文件（只有 CONIN$ 才是伪设备）
    public async Task ProviderUpsert_WithASafeId_IsAccepted(string id)
    {
        var cmd = new CoreCommand.ProviderUpsert(id, "k", null, null, null, null);
        var result = await _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(result.Ack);
    }

    // 单独钉住判据本身（它是 internal static）；与走 dispatcher 的两条分工不同：
    // 那两条钉「dispatcher 确实调了它」，这条钉「它自己的契约」——故变异「去掉调用」时这条仍绿。
    [Theory]
    [InlineData("../escaped", false)]
    [InlineData("a/b", false)]          // Windows 上 / 也算分隔符
    [InlineData("a\\b", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a\0b", false)]
    [InlineData("NUL", false)]          // Windows 设备名：未捕获异常掀翻常驻进程（不是静默）
    [InlineData("nul", false)]          // 大小写不敏感
    [InlineData("CON.json", false)]     // 去掉扩展名再比
    [InlineData("NUL.txt.txt.json", false)]   // 第一个点之前那段才是判据（不是 GetFileNameWithoutExtension）
    [InlineData("NUL  .json", false)]         // 尾随空格 ⇒ TrimEnd
    [InlineData("COM1.json", false)]          // 1–9 那一族（走抛异常）
    [InlineData("PRN", false)]                // PRN / AUX 两条分支
    [InlineData("AUX", false)]
    [InlineData("LPT9", false)]               // 下边界那一族
    [InlineData("CONIN$", false)]             // 带 $ 的伪设备
    [InlineData("CONOUT$", false)]
    [InlineData("NULx", true)]                // 反向：前缀型会误杀
    [InlineData("COM1x", true)]               // 反向
    [InlineData("COM10", true)]               // 反向：只有 1–9
    [InlineData("COM0", true)]                // 反向：下边界
    [InlineData("LPT0", true)]
    [InlineData("CONIN", true)]               // 反向：裸 CONIN 正常（只有带 $ 的才是伪设备）
    [InlineData("local", true)]
    [InlineData("my-provider", true)]
    [InlineData("本地", true)]
    public void IsSafeProviderId_PinsThePredicateItself(string id, bool expected) =>
        Assert.Equal(expected, CommandDispatcher.IsSafeProviderId(id));

    // 上标 ¹ ² ³（U+00B9/B2/B3）也算设备名的「数字」，而别的「数字」写法不算：
    // 实测 COM⁴/COM⁵/COM⁹（U+2074…）、全角 COM１（U+FF11）、阿拉伯-印度 COM١（U+0661）都是正常文件。
    // 用 (char) 拼进字符串而不写 InlineData 字面量：上标是易混淆字符，字面量可能被工具链规范化成普通数字，测试绿了却什么都没测。
    [Fact]
    public void IsSafeProviderId_RejectsSuperscriptDigits_ButNotOtherDigitForms()
    {
        var sup1 = new string((char)0x00B9, 1);   // ¹
        var sup2 = new string((char)0x00B2, 1);   // ²
        var sup3 = new string((char)0x00B3, 1);   // ³

        // 三个上标逐个钉：只钉 ¹/³ 的话，删掉 or '²' 是存活变异。
        Assert.False(CommandDispatcher.IsSafeProviderId("COM" + sup1));
        Assert.False(CommandDispatcher.IsSafeProviderId("COM" + sup2));
        Assert.False(CommandDispatcher.IsSafeProviderId("LPT" + sup1));
        Assert.False(CommandDispatcher.IsSafeProviderId("LPT" + sup2));
        Assert.False(CommandDispatcher.IsSafeProviderId("LPT" + sup3));

        // 反向：别的「数字」写法不是设备名（实测都正常写入）⇒ 必须放行
        Assert.True(CommandDispatcher.IsSafeProviderId("COM" + (char)0x2074));  // ⁴
        Assert.True(CommandDispatcher.IsSafeProviderId("COM" + (char)0xFF11));  // １ 全角
        Assert.True(CommandDispatcher.IsSafeProviderId("COM" + (char)0x0661));  // ١ 阿拉伯-印度
    }

    // 上标那条也必须走 dispatcher 一遍（与 CONIN$ / PRN / AUX 同一标准）：
    // 只钉谓词层则「闸被挪到写入之后」时没人发现——谓词全绿而设备路径上的异常照样掀翻进程。
    [Fact]
    public async Task ProviderUpsert_WithASuperscriptDeviceId_IsRejectedThroughTheDispatcher()
    {
        // 这一句钉的是字面量而不是被测输入（输入用 (char)0x00B9 构造），价值是前瞻的：
        // 若有人把它「顺手清理」成字面量而那字面量已被工具链弄坏，会在这里当场失败。
        Assert.Equal(0x00B9, "¹"[0]);

        var cmd = new CoreCommand.ProviderUpsert("COM" + (char)0x00B9, "k", null, null, null, null);
        var result = await _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(result.Ack);
    }

    // CONIN$ / CONOUT$ 是伪设备（抛 IOException / UnauthorizedAccessException ⇒ 掀翻常驻进程），
    // 而裸 CONIN / CONOUT 是正常文件；两端一起钉——判据收窄得过头时症状是「少挡了」，没人会发现。
    [Fact]
    public void IsSafeProviderId_RejectsTheDollarPseudoDevices_ButNotTheBareNames()
    {
        Assert.False(CommandDispatcher.IsSafeProviderId("CONIN$"));
        Assert.False(CommandDispatcher.IsSafeProviderId("CONOUT$"));

        // 反向：裸名字两个都是正常文件（实测）⇒ 必须放行
        Assert.True(CommandDispatcher.IsSafeProviderId("CONIN"));
        Assert.True(CommandDispatcher.IsSafeProviderId("CONOUT"));
    }

    // 删的 id 必须过同一个闸，且必须在任何文件系统调用之前；判据落在盘上——删不可逆。
    [Fact]
    public async Task ProviderDelete_WithAPathTraversingId_IsRejected_AndDeletesNothingOutsideProviders()
    {
        // 诱饵文件；providers/ 必须先存在——否则 File.Exists 为假、去掉闸也不会删到诱饵，测试就没有区分力。
        Directory.CreateDirectory(Path.Combine(_root, "providers"));
        var decoy = Path.Combine(_root, "escaped.json");
        File.WriteAllText(decoy, "别动我");

        var result = await _dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("../escaped"), "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(result.Ack);
        Assert.True(File.Exists(decoy), "providers/ 之外的文件被删掉了——闸没有拦在删除之前");
        Assert.Equal("别动我", File.ReadAllText(decoy));
    }

    // 本条判据是「回 rejected」，不是「盘上没被删」：这些 id 在 PathFor 下指向不存在的东西，
    // 去掉闸不会让本条变红（只有 ../escaped 那条会，因为它有诱饵）。
    [Theory]
    [InlineData("../escaped")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    public async Task ProviderDelete_WithAnUnsafeId_IsRejected(string id)
    {
        var result = await _dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete(id), "t1", awaitCompletion: true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(result.Ack);
    }
}

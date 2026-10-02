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
    [InlineData("NUL")]               // ⚠️ Windows 设备名：写入**静默消失**（回 ✓ 而盘上无文件）
    [InlineData("nul")]               // ⚠️ 设备名判定**大小写不敏感**（实测一样消失）
    [InlineData("CON.json")]          // ⚠️ 要**去掉扩展名再比**（`PathFor` 还会再拼一个 `.json`）
    [InlineData("NUL.txt.txt.json")]  // ⚠️ 实测同样静默消失；`GetFileNameWithoutExtension` 会放行它
    [InlineData("NUL  .json")]        // ⚠️ 尾随**空格**再带扩展名——`TrimEnd` 那一半
    [InlineData("COM1.json")]         // ⚠️ 这一族走的是**抛异常**（FileNotFoundException），不是静默
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
    [InlineData("NUL", false)]          // ⚠️ Windows 设备名：写入会**静默消失**
    [InlineData("nul", false)]          // 大小写不敏感
    [InlineData("CON.json", false)]     // 去掉扩展名再比
    [InlineData("NUL.txt.txt.json", false)]   // 第一个点之前那段才是判据（不是 GetFileNameWithoutExtension）
    [InlineData("NUL  .json", false)]         // 尾随空格 ⇒ TrimEnd
    [InlineData("COM1.json", false)]          // 1–9 那一族（走抛异常）
    [InlineData("NULx", true)]                // ⚠️ 反向：前缀型会误杀
    [InlineData("COM1x", true)]               // ⚠️ 反向
    [InlineData("COM10", true)]               // ⚠️ 反向：只有 1–9
    [InlineData("local", true)]
    [InlineData("my-provider", true)]
    [InlineData("本地", true)]
    public void IsSafeProviderId_PinsThePredicateItself(string id, bool expected) =>
        Assert.Equal(expected, CommandDispatcher.IsSafeProviderId(id));
}

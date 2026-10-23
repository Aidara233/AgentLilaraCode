using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using CanonicalBlock = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **执行器到 shell 的接线**：闸门在**执行期**拦，而不是把工具从工具表里拿掉。
///
/// <para>⚠️ 这一组是「`shell` 是高危类」那句话在**真执行路径**上的落点——
/// `ShellToolTests` 只钉了 `Gate.Decide` 那张表，而这张表会不会被调用是另一回事
/// （本仓栽过：判据写在工具上、闸门却没问它）。</para>
/// </summary>
public sealed class ShellExecutorTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-shellexec-" + Guid.NewGuid().ToString("N"));
    private readonly BlobStore _blobs;

    public ShellExecutorTests()
    {
        Directory.CreateDirectory(_work);
        _blobs = new BlobStore(Path.Combine(_work, ".blobs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private IToolContext Context(ShellConfig? config = null, bool withShell = true) =>
        new SessionToolContext(_work, _blobs, shell: withShell ? new ShellAccess(config ?? new ShellConfig()) : null);

    private static CanonicalBlock.ToolUseBlock Call(string command) =>
        new("call-1", ShellTool.NameOf, $"{{\"command\":\"{command}\"}}");

    [Fact]
    public async Task BelowTrusted_TheCallIsDenied_AndTheShellNeverRuns()
    {
        // 判据是**副作用有没有发生**：命令会在工作目录里写文件，而文件不该出现。
        var marker = "executed.txt";

        foreach (var level in new[] { "off", "readonly", "investigate", "standard" })
        {
            var batch = await new ToolExecutor([new ShellTool()], _blobs, level)
                .ExecuteAsync([Call($"echo x > {marker}")], Context(), CancellationToken.None);

            Assert.Empty(batch.ExecutedCallIds);
            Assert.Contains("拒绝", Text(batch.Results.Single()));
            Assert.False(File.Exists(Path.Combine(_work, marker)), $"档位 {level} 竟然执行了");
        }
    }

    [Fact]
    public async Task AtTrusted_ItRuns()
    {
        var batch = await new ToolExecutor([new ShellTool()], _blobs, "trusted")
            .ExecuteAsync([Call("echo hi")], Context(), CancellationToken.None);

        Assert.Single(batch.ExecutedCallIds);
        Assert.Contains("命令成功", Text(batch.Results.Single()));
    }

    [Fact]
    public async Task AtAuthorized_ItAsks_AndDoesNotRunUntilApproved()
    {
        var batch = await new ToolExecutor([new ShellTool()], _blobs, "authorized")
            .ExecuteAsync([Call("echo hi > asked.txt")], Context(), CancellationToken.None);

        Assert.Empty(batch.Results);                  // 挂起：一条结果都不落
        Assert.NotNull(batch.Pending);
        Assert.Equal(AskKind.Permission, batch.Pending!.Kind);
        Assert.Contains("shell", batch.Pending.Items.Single().Question);
        Assert.False(File.Exists(Path.Combine(_work, "asked.txt")));

        // 批准之后（`Reply.Covers`）同一批真的执行。
        var approved = await new ToolExecutor([new ShellTool()], _blobs, "authorized").ExecuteAsync(
            [Call("echo hi > asked.txt")], Context(), CancellationToken.None,
            new ToolExecutor.Reply(AskKind.Permission, ["call-1"], [], new Dictionary<string, string>()));

        Assert.Single(approved.ExecutedCallIds);
        Assert.True(File.Exists(Path.Combine(_work, "asked.txt")));
    }

    [Fact]
    public async Task WithoutAShellContext_SaysSoInsteadOfGuessing()
    {
        var batch = await new ToolExecutor([new ShellTool()], _blobs, "trusted")
            .ExecuteAsync([Call("echo hi")], Context(withShell: false), CancellationToken.None);

        Assert.Contains("shell 上下文", Text(batch.Results.Single()));
    }

    private static string Text(TimelineEvent.ToolResultEvent result) =>
        string.Join("\n", result.Content.OfType<CanonicalBlock.TextBlock>().Select(b => b.Text));
}

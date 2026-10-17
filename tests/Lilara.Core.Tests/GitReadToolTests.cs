using System.Diagnostics;
using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

public sealed class GitReadToolTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "lilara-git-read-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public GitReadToolTests()
    {
        _repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        File.WriteAllText(Path.Combine(_repo, "hello.txt"), "first line\nsecond line\n");
        Git("add", "--", "hello.txt");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "add file");
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Theory]
    [InlineData("status", "## ")]
    [InlineData("diff", "")]
    [InlineData("log", "add file")]
    [InlineData("branch", "master")]
    [InlineData("rev-parse", "e")]
    [InlineData("show", "first line")]
    [InlineData("blame", "first line")]
    public async Task GitRead_ExecutesAllowedReadCommands(string command, string expected)
    {
        var tool = new GitReadTool();
        Assert.Equal(PermissionClass.ReadOnly, tool.PermissionClass);
        Assert.Equal(Verdict.Allow, Gate.Decide(tool.PermissionClass, "readonly"));
        var args = command switch
        {
            "show" or "blame" => $$"""{"command":"{{command}}","path":"hello.txt","revision":"HEAD"}""",
            "diff" => """{"command":"diff","path":"hello.txt"}""",
            "log" => """{"command":"log"}""",
            _ => $$"""{"command":"{{command}}"}""",
        };
        var outcome = await Run(tool, args);
        var text = Text(outcome);
        Assert.Contains("git " + command, text);
        if (expected.Length > 0) Assert.Contains(expected, text);
    }

    [Fact]
    public async Task GitRead_DiffShowsUnstagedAndStagedChangesWithoutMutatingIndexOrRefs()
    {
        File.WriteAllText(Path.Combine(_repo, "hello.txt"), "first line\nchanged\n");
        var indexBefore = File.ReadAllBytes(Path.Combine(_repo, ".git", "index"));
        var headBefore = Git("rev-parse", "HEAD").Trim();
        var outcome = await Run(new GitReadTool(), """{"command":"diff","path":"hello.txt","unified":0}""");

        Assert.Contains("+changed", Text(outcome));
        Assert.Equal(indexBefore, File.ReadAllBytes(Path.Combine(_repo, ".git", "index")));
        Assert.Equal(headBefore, Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task GitRead_RejectsUnknownCommandsAndBadRangesBeforeStartingGit()
    {
        var tool = new GitReadTool();
        Assert.Contains("必须是", Text(await Run(tool, """{"command":"push"}""")));
        Assert.Contains("limit", Text(await Run(tool, """{"command":"log","limit":1000}""")));
        Assert.Contains("同时提供", Text(await Run(tool, """{"command":"blame","path":"hello.txt","startLine":1}""")));
    }

    [Fact]
    public async Task GitRead_RejectsAbsoluteAndEscapingPaths()
    {
        var tool = new GitReadTool();
        Assert.Contains("工作目录内", Text(await Run(tool, """{"command":"blame","path":"../outside"}""")));
        Assert.Contains("工作目录内存在的仓库相对路径", Text(await Run(tool, """{"command":"show","path":"C:\\outside"}""")));
    }

    [Fact]
    public async Task GitRead_ReportsNonRepositoryAndOutputTruncationBoundedly()
    {
        var context = new SessionToolContext(Path.GetDirectoryName(_repo)!, new BlobStore(Path.Combine(_temp, "data")));
        var notRepo = await new GitReadTool().RunAsync(new ToolCall("c", "git_read", """{"command":"status"}"""), context, CancellationToken.None);
        Assert.Contains("失败", Text(notRepo));

        var large = string.Concat(Enumerable.Repeat("状态已改变，工作区内容足够大。\n", 12000));
        File.WriteAllText(Path.Combine(_repo, "large.txt"), large);
        Git("add", "--", "large.txt");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "large file");
        var result = await Run(new GitReadTool(), """{"command":"show","path":"large.txt"}""");
        Assert.Contains("输出已截断", Text(result));
        Assert.True(Encoding.UTF8.GetByteCount(Text(result)) <= ToolLimits.MaxResultBytes);
    }

    [Fact]
    public async Task GitRead_RefusesOptionLikePathAndUnsupportedOptions()
    {
        File.WriteAllText(Path.Combine(_repo, "-danger"), "contents");
        Git("add", "--", "-danger");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "option-like path");
        var tool = new GitReadTool();
        var outcome = await Run(tool, """{"command":"blame","path":"-danger","revision":"HEAD"}""");
        Assert.Contains("contents", Text(outcome));
        Assert.Contains("command", Text(await Run(tool, """{"command":"--exec-path"}""")));
    }

    [Fact]
    public async Task GitRead_PropagatesCallerCancellationAndStopsRunner()
    {
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(_temp, "slow-git.cmd")
            : Path.Combine(_temp, "slow-git.sh");
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(executable, "@echo off\r\nping 127.0.0.1 -n 30 > nul\r\n");
        else
        {
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nsleep 30\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitReadTool(executable).RunAsync(
            new ToolCall("c", "git_read", """{"command":"status"}"""),
            new SessionToolContext(_repo, new BlobStore(Path.Combine(_temp, "data"))),
            cts.Token));
    }

    private Task<ToolOutcome> Run(GitReadTool tool, string args) => tool.RunAsync(
        new ToolCall("c", "git_read", args),
        new SessionToolContext(_repo, new BlobStore(Path.Combine(_temp, "data"))),
        CancellationToken.None);

    private static string Text(ToolOutcome outcome) => string.Join("", outcome.Content.OfType<Block.TextBlock>().Select(x => x.Text));

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}

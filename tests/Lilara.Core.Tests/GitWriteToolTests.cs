using System.Diagnostics;
using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

public sealed class GitWriteToolTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "lilara-git-write-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public GitWriteToolTests()
    {
        _repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
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

    [Fact]
    public async Task GitWrite_DeclaresWritingPermissionAndAddsOnlySpecifiedPaths()
    {
        var tool = new GitWriteTool();
        Assert.Equal(PermissionClass.Writing, tool.PermissionClass);
        Assert.Equal(Verdict.Ask, Gate.Decide(tool.PermissionClass, "standard"));
        Assert.Equal(Verdict.Allow, Gate.Decide(tool.PermissionClass, "authorized"));

        File.WriteAllText(Path.Combine(_repo, "one.txt"), "one");
        File.WriteAllText(Path.Combine(_repo, "two.txt"), "two");
        var result = await Run(tool, """{"operation":"add","paths":["one.txt"]}""");

        Assert.Contains("已将", Text(result));
        Assert.Contains("one.txt", Git("diff", "--cached", "--name-only"));
        Assert.DoesNotContain("two.txt", Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task GitWrite_UnstagesFileWithoutChangingWorkingTree()
    {
        File.WriteAllText(Path.Combine(_repo, "file.txt"), "working content");
        Git("add", "--", "file.txt");
        var result = await Run(new GitWriteTool(), """{"operation":"unstage","paths":["file.txt"]}""");

        Assert.Contains("取消暂存", Text(result));
        Assert.Empty(Git("diff", "--cached", "--name-only"));
        Assert.Equal("working content", File.ReadAllText(Path.Combine(_repo, "file.txt")));
    }

    [Fact]
    public async Task GitWrite_CreatesCommitWithProvidedMessage()
    {
        File.WriteAllText(Path.Combine(_repo, "file.txt"), "content");
        Git("add", "--", "file.txt");
        var result = await Run(new GitWriteTool(), """{"operation":"commit","message":"save work"}""");

        Assert.Contains("已创建本地提交", Text(result));
        Assert.Contains("save work", Git("log", "-1", "--format=%s"));
        Assert.Contains("file.txt", Git("show", "--format=", "--name-only", "HEAD"));
    }

    [Theory]
    [InlineData("branch_create", "feature/test")]
    [InlineData("tag_create", "v1.0.0")]
    public async Task GitWrite_CreatesLocalBranchAndLightweightTag(string operation, string name)
    {
        var initial = Git("rev-parse", "HEAD").Trim();
        var result = await Run(new GitWriteTool(), $$"""{"operation":"{{operation}}","name":"{{name}}","revision":"HEAD"}""");
        Assert.Contains("已创建本地", Text(result));

        var actual = operation == "branch_create" ? Git("rev-parse", name).Trim() : Git("rev-parse", "refs/tags/" + name).Trim();
        Assert.Equal(initial, actual);
        if (operation == "tag_create") Assert.Equal("commit", Git("cat-file", "-t", "refs/tags/" + name).Trim());
    }

    [Theory]
    [InlineData("{\"operation\":\"add\"}")]
    [InlineData("{\"operation\":\"add\",\"paths\":[]}")]
    [InlineData("{\"operation\":\"push\"}")]
    [InlineData("{\"operation\":\"commit\",\"message\":\" \",\"paths\":[]}")]
    [InlineData("{\"operation\":\"branch_create\",\"name\":\"bad..name\"}")]
    [InlineData("{\"operation\":\"branch_create\",\"name\":\"-force\"}")]
    public async Task GitWrite_RejectsInvalidAndUnsupportedRequests(string json)
    {
        var result = await Run(new GitWriteTool(), json);
        Assert.DoesNotContain("已创建", Text(result));
        Assert.DoesNotContain("已将", Text(result));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\\\outside")]
    [InlineData(".")]
    public async Task GitWrite_RejectsEscapingAbsoluteAndRootPaths(string path)
    {
        var result = await Run(new GitWriteTool(), $$"""{"operation":"add","paths":["{{path}}"]}""");
        Assert.Contains("路径", Text(result));
    }

    [Fact]
    public async Task GitWrite_RejectsInvalidRefAndLongMessageBeforeMutation()
    {
        var branch = await Run(new GitWriteTool(), """{"operation":"branch_create","name":"name.lock"}""");
        Assert.Contains("安全", Text(branch));

        var oversized = new string('x', 17000);
        var message = System.Text.Json.JsonSerializer.Serialize(new { operation = "commit", message = oversized });
        var commit = await Run(new GitWriteTool(), message);
        Assert.Contains("上限", Text(commit));
        Assert.Equal("initial", Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task GitWrite_ReportsGitFailureWithoutClaimingSuccess()
    {
        var result = await Run(new GitWriteTool(), """{"operation":"commit","message":"nothing staged"}""");
        Assert.Contains("失败", Text(result));
        Assert.Equal("initial", Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task GitWrite_ReportsNonRepository()
    {
        var outside = Path.Combine(_temp, "outside");
        Directory.CreateDirectory(outside);
        var context = new SessionToolContext(outside, new BlobStore(Path.Combine(_temp, "data")));
        var result = await new GitWriteTool().RunAsync(new ToolCall("c", "git_write", """{"operation":"branch_create","name":"x"}"""), context, CancellationToken.None);
        Assert.Contains("失败", Text(result));
    }

    private Task<ToolOutcome> Run(GitWriteTool tool, string args) => tool.RunAsync(
        new ToolCall("c", "git_write", args),
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

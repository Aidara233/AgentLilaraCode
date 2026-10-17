using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

public sealed class FileOperationToolsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-fs-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;

    public FileOperationToolsTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private SessionToolContext Context() => new(_work, new BlobStore(Path.Combine(_tmp, "data")));
    private string Full(string path) => Path.Combine(_work, path);
    private static string Text(ToolOutcome outcome) => string.Join("", outcome.Content.OfType<Block.TextBlock>().Select(x => x.Text));
    private static ToolCall Call(string name, string json) => new("call", name, json);

    [Fact]
    public async Task MakeDirectory_CreatesOnlyOneLevelAndUsesWritingPermission()
    {
        var tool = new MakeDirectoryTool();
        Assert.Equal(PermissionClass.Writing, tool.PermissionClass);
        Assert.Equal(Verdict.Ask, Gate.Decide(tool.PermissionClass, "standard"));

        var result = await tool.RunAsync(Call("mkdir", """{"path":"new"}"""), Context(), CancellationToken.None);
        Assert.True(Directory.Exists(Full("new")));
        Assert.Contains("已创建", Text(result));

        var refused = await tool.RunAsync(Call("mkdir", """{"path":"missing/child"}"""), Context(), CancellationToken.None);
        Assert.Contains("父目录不存在", Text(refused));
        Assert.False(Directory.Exists(Full("missing")));
    }

    [Fact]
    public async Task MakeDirectory_ExistingTargetIsUnchanged()
    {
        File.WriteAllText(Full("already"), "keep");
        var result = await new MakeDirectoryTool().RunAsync(Call("mkdir", """{"path":"already"}"""), Context(), CancellationToken.None);
        Assert.Contains("已存在", Text(result));
        Assert.Equal("keep", File.ReadAllText(Full("already")));
    }

    [Theory]
    [InlineData("{\"path\":\"tree\"}")]
    [InlineData("{\"path\":\"tree\",\"recursive\":\"true\"}")]
    public async Task Delete_MissingOrInvalidRecursiveDoesNotDelete(string json)
    {
        Directory.CreateDirectory(Full("tree"));
        File.WriteAllText(Full("tree/item"), "keep");
        var result = await new DeleteTool().RunAsync(Call("delete", json), Context(), CancellationToken.None);
        Assert.True(File.Exists(Full("tree/item")));
        Assert.Contains("recursive", Text(result));
    }

    [Fact]
    public async Task Delete_RecursiveIsDestructiveAndExplicitlyDeletesDirectoryTree()
    {
        var tool = new DeleteTool();
        Assert.Equal(PermissionClass.Destructive, tool.PermissionClass);
        Assert.Equal(Verdict.Ask, Gate.Decide(tool.PermissionClass, "authorized"));
        Assert.Equal(Verdict.Allow, Gate.Decide(tool.PermissionClass, "trusted"));

        Directory.CreateDirectory(Full("tree/sub"));
        File.WriteAllText(Full("tree/sub/item"), "remove");
        var result = await tool.RunAsync(Call("delete", """{"path":"tree","recursive":true}"""), Context(), CancellationToken.None);
        Assert.False(Directory.Exists(Full("tree")));
        Assert.Contains("recursive=true", Text(result));
    }

    [Fact]
    public async Task Delete_FalseDeletesFileAndOnlyEmptyDirectory()
    {
        File.WriteAllText(Full("item"), "remove");
        var fileResult = await new DeleteTool().RunAsync(Call("delete", """{"path":"item","recursive":false}"""), Context(), CancellationToken.None);
        Assert.False(File.Exists(Full("item")));
        Assert.Contains("文件", Text(fileResult));

        Directory.CreateDirectory(Full("tree"));
        File.WriteAllText(Full("tree/item"), "keep");
        var result = await new DeleteTool().RunAsync(Call("delete", """{"path":"tree","recursive":false}"""), Context(), CancellationToken.None);
        Assert.True(File.Exists(Full("tree/item")));
        Assert.Contains("失败", Text(result));
    }

    [Fact]
    public async Task Delete_RefusesWorkingDirectoryRoot()
    {
        var result = await new DeleteTool().RunAsync(Call("delete", """{"path":".","recursive":true}"""), Context(), CancellationToken.None);
        Assert.True(Directory.Exists(_work));
        Assert.Contains("工作目录本身", Text(result));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    [InlineData("rename")]
    public async Task Path_OperationsWorkOnFiles(string operation)
    {
        File.WriteAllText(Full("a.txt"), "content");
        var destination = operation == "rename" ? "b.txt" : "sub/b.txt";
        if (operation != "rename") Directory.CreateDirectory(Full("sub"));
        var result = await new PathTool().RunAsync(
            Call("path", $$"""{"operation":"{{operation}}","source":"a.txt","destination":"{{destination}}"}"""),
            Context(), CancellationToken.None);

        Assert.Contains("已", Text(result));
        Assert.Equal("content", File.ReadAllText(Full(destination)));
        Assert.Equal(operation == "copy", File.Exists(Full("a.txt")));
    }

    [Fact]
    public async Task Path_IsWritingAndRefusesOverwriteOrDirectory()
    {
        var tool = new PathTool();
        Assert.Equal(PermissionClass.Writing, tool.PermissionClass);
        Assert.Equal(Verdict.Ask, Gate.Decide(tool.PermissionClass, "standard"));

        File.WriteAllText(Full("source"), "source");
        File.WriteAllText(Full("target"), "target");
        var conflict = await tool.RunAsync(Call("path", """{"operation":"copy","source":"source","destination":"target"}"""), Context(), CancellationToken.None);
        Assert.Contains("已存在", Text(conflict));
        Assert.Equal("source", File.ReadAllText(Full("source")));
        Assert.Equal("target", File.ReadAllText(Full("target")));

        Directory.CreateDirectory(Full("dir"));
        var directory = await tool.RunAsync(Call("path", """{"operation":"copy","source":"dir","destination":"other"}"""), Context(), CancellationToken.None);
        Assert.Contains("不支持目录", Text(directory));
        Assert.False(File.Exists(Full("other")));
    }

    [Fact]
    public async Task Path_RenameRequiresSameParentAndAllPathsStayInsideWorkspace()
    {
        Directory.CreateDirectory(Full("sub"));
        File.WriteAllText(Full("a.txt"), "keep");
        var rename = await new PathTool().RunAsync(Call("path", """{"operation":"rename","source":"a.txt","destination":"sub/b.txt"}"""), Context(), CancellationToken.None);
        Assert.Contains("同一父目录", Text(rename));
        Assert.True(File.Exists(Full("a.txt")));

        var outside = await new PathTool().RunAsync(Call("path", """{"operation":"move","source":"../escape","destination":"sub/b.txt"}"""), Context(), CancellationToken.None);
        Assert.Contains("工作目录内", Text(outside));
        Assert.Equal("keep", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public void Delete_ApprovalHashIncludesTheRecursiveChoice()
    {
        var shallow = ApprovalStore.HashArgs("""{"path":"tree","recursive":false}""");
        var recursive = ApprovalStore.HashArgs("""{"path":"tree","recursive":true}""");
        Assert.NotEqual(shallow, recursive);
    }

    [Fact]
    public void BuiltInTools_ContainsNewFileOperations()
    {
        Assert.Contains(BuiltInTools.All, t => t.Name == "mkdir");
        Assert.Contains(BuiltInTools.All, t => t.Name == "delete");
        Assert.Contains(BuiltInTools.All, t => t.Name == "path");
    }
}

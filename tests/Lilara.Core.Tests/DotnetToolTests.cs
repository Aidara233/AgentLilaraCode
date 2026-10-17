using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// `dotnet_build` / `dotnet_test` 的测试。
///
/// <para>⚠️ <b>「命令真的发出去了」用一个替身可执行文件来探</b>：本机没有 Python，
/// 而 `.cmd` 包装层会重新解释参数（引号在 cmd 里不是参数语法）⇒ 拿它当探针测出来的
/// 「参数原样到达」是假的。这里的替身是一个现编的 tiny console exe：参数经
/// <c>ArgumentList</c> 原样到达，行为由它旁边的小文件控制（输出 / 退出码 / 睡眠长度）。</para>
///
/// <para>⚠️ <b>真实构建另有两条</b>——它们证明的不是「参数拼对了」，而是**这工具真的能编、能跑测试**。</para>
/// </summary>
public sealed class DotnetToolTests : IClassFixture<DotnetStubFixture>, IDisposable
{
    private readonly DotnetStubFixture _stub;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "lilara-dotnet-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly BlobStore _blobs;

    public DotnetToolTests(DotnetStubFixture stub)
    {
        _stub = stub;
        _work = Path.Combine(_temp, "work");
        Directory.CreateDirectory(_work);
        _blobs = new BlobStore(Path.Combine(_temp, "data"));
        _stub.Reset();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_temp)) return;
        foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
    }

    // ---- 权限与闸门 ----

    [Fact]
    public void BothTools_AreWritingAndSitOnTheAskLine()
    {
        foreach (var tool in new ITool[] { new DotnetBuildTool(), new DotnetTestTool() })
        {
            Assert.Equal(PermissionClass.Writing, tool.PermissionClass);
            Assert.Equal(Verdict.Deny, Gate.Decide(tool.PermissionClass, "readonly"));
            Assert.Equal(Verdict.Ask, Gate.Decide(tool.PermissionClass, "standard"));
            Assert.Equal(Verdict.Allow, Gate.Decide(tool.PermissionClass, "authorized"));
        }
    }

    [Fact]
    public void BothTools_AreRegisteredInTheBuiltInTable()
    {
        Assert.Contains(BuiltInTools.All, t => t.Name == "dotnet_build");
        Assert.Contains(BuiltInTools.All, t => t.Name == "dotnet_test");
    }

    // ---- 参数与项目解析（全部在启动进程之前拒绝） ----

    [Theory]
    [InlineData("""{"project":"note.txt"}""")]
    [InlineData("""{"project":"-evil.csproj"}""")]
    [InlineData("""{"project":"../outside/X.csproj"}""")]
    [InlineData("""{"project":"C:\\\\outside\\\\X.csproj"}""")]
    [InlineData("""{"project":""}""")]
    [InlineData("""{"project":42}""")]
    public async Task Build_RejectsUnsafeOrWrongProjectBeforeStarting(string args)
    {
        File.WriteAllText(Path.Combine(_work, "note.txt"), "not a project");
        var result = await Run(new DotnetBuildTool(_stub.Executable), args);

        Assert.Contains("project", Text(result));
        // 参数不合法时不应该启动任何进程。
        Assert.Null(_stub.StartedArgs());
    }

    [Fact]
    public async Task Build_RejectsUnapplicableFieldsAndNonBooleanSwitches()
    {
        File.WriteAllText(Path.Combine(_work, "a.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var tool = new DotnetBuildTool(_stub.Executable);

        Assert.Contains("不支持参数", Text(await Run(tool, """{"project":"a.csproj","filter":"x"}""")));
        Assert.Contains("JSON 布尔", Text(await Run(tool, """{"project":"a.csproj","noRestore":"true"}""")));
        Assert.Contains("JSON 布尔", Text(await Run(tool, """{"project":"a.csproj","noIncremental":1}""")));
        Assert.Contains("JSON", Text(await Run(tool, "not json")));
        Assert.Null(_stub.StartedArgs());
    }

    [Fact]
    public async Task Test_RejectsUnapplicableFieldsAndBadFilters()
    {
        File.WriteAllText(Path.Combine(_work, "a.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var tool = new DotnetTestTool(_stub.Executable);

        Assert.Contains("不支持参数", Text(await Run(tool, """{"project":"a.csproj","noIncremental":true}""")));

        foreach (var bad in new[]
        {
            """{"project":"a.csproj","filter":"-force"}""",
            """{"project":"a.csproj","filter":"--logger"}""",
            """{"project":"a.csproj","filter":""}""",
            """{"project":"a.csproj","filter":"a$(rm -rf)"}""",
            JsonSerializer.Serialize(new { project = "a.csproj", filter = new string('x', 513) }),
        })
            Assert.Contains("筛选表达式", Text(await Run(tool, bad)));

        Assert.Null(_stub.StartedArgs());
    }

    [Fact]
    public async Task Test_AcceptsAReasonableFullyQualifiedNameFilter()
    {
        File.WriteAllText(Path.Combine(_work, "a.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var result = await Run(new DotnetTestTool(_stub.Executable),
            """{"project":"a.csproj","filter":"FullyQualifiedName~StepperTests|FullyQualifiedName=A.B_C"}""");

        Assert.Equal(
            ["test", "a.csproj", "--nologo", "--verbosity", "minimal", "--disable-build-servers",
             "--filter", "FullyQualifiedName~StepperTests|FullyQualifiedName=A.B_C"],
            _stub.LastArgs());
        Assert.Contains("测试成功", Text(result));
    }

    [Fact]
    public async Task Project_IsDiscoveredOnlyWhenThereIsExactlyOneCandidate()
    {
        var tool = new DotnetBuildTool(_stub.Executable);

        // 零个。
        Assert.Contains("没有", Text(await Run(tool, "{}")));
        Assert.Null(_stub.StartedArgs());

        // 多个：报错并列出候选，不猜。
        File.WriteAllText(Path.Combine(_work, "a.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_work, "b.sln"), "");
        var multiple = Text(await Run(tool, "{}"));
        Assert.Contains("多个候选", multiple);
        Assert.Contains("a.csproj", multiple);
        Assert.Contains("b.sln", multiple);

        // 恰好一个：用它。
        File.Delete(Path.Combine(_work, "b.sln"));
        var single = await Run(tool, "{}");
        Assert.Contains("构建成功", Text(single));
        Assert.Equal("a.csproj", _stub.LastArgs()[1]);
    }

    // ---- 命令构造 ----

    [Fact]
    public async Task Build_EmitsTheFixedArgumentPrefixAndOnlyKnownSwitches()
    {
        File.WriteAllText(Path.Combine(_work, "p.csproj"), "<Project />");
        await Run(new DotnetBuildTool(_stub.Executable),
            """{"project":"p.csproj","noRestore":true,"noIncremental":true}""");

        Assert.Equal(
            ["build", "p.csproj", "--nologo", "--verbosity", "minimal", "--disable-build-servers", "--no-restore", "--no-incremental"],
            _stub.LastArgs());
    }

    [Fact]
    public async Task Test_EmitsTheFixedArgumentPrefixWithFilterLast()
    {
        File.WriteAllText(Path.Combine(_work, "p.csproj"), "<Project />");
        await Run(new DotnetTestTool(_stub.Executable), """{"project":"p.csproj"}""");

        Assert.Equal(
            ["test", "p.csproj", "--nologo", "--verbosity", "minimal", "--disable-build-servers"],
            _stub.LastArgs());
    }

    // ---- 失败与输出 ----

    [Fact]
    public async Task Failure_IsReportedAsFailureWithExitCodeAndStderr()
    {
        File.WriteAllText(Path.Combine(_work, "p.csproj"), "<Project />");
        _stub.SetExit(7, stdout: "...\n", stderr: "error CS1002: ; expected\n");

        var outcome = await Run(new DotnetBuildTool(_stub.Executable), """{"project":"p.csproj"}""");
        var text = Text(outcome);

        Assert.StartsWith("构建失败", text);
        Assert.Contains("退出码 7", text);
        Assert.Contains("CS1002", text);
        Assert.Null(outcome.Fingerprint);
        Assert.Null(outcome.DependsOn);
        Assert.Equal("dotnet_build", outcome.Framing?.Tool);
        Assert.Contains(outcome.Framing!.Fields, f => f.Key == "exit" && f.Value == "7");
    }

    [Fact]
    public async Task LargeOutput_IsCollectedInFullUpToTheSaveLimit()
    {
        File.WriteAllText(Path.Combine(_work, "p.csproj"), "<Project />");

        // ⚠️ 工具**不再替核心投递截断**（2026-10-17）：中间大小的输出（远超投递预算
        // 256 KiB、但没到保存上限 4 MiB）必须**完整交回**——被砍掉的字节就永远进不了库，
        // `fetch` 也就取不回。尾部标记是这条的探针。
        var body = string.Concat(Enumerable.Repeat("middle-line\n", 40_000));
        _stub.SetExit(0, stdout: "HEAD-MARKER\n" + body + "TAIL-MARKER\n");

        var outcome = await Run(new DotnetBuildTool(_stub.Executable), """{"project":"p.csproj"}""");
        var text = Text(outcome);

        Assert.StartsWith("构建成功", text);
        Assert.Contains("HEAD-MARKER", text);
        Assert.Contains("TAIL-MARKER", text);
        Assert.DoesNotContain("已截断", text);
        Assert.DoesNotContain("已省略", text);
    }

    [Fact]
    public async Task OversizedOutput_IsCappedAtTheSaveLimitAndSaysSo()
    {
        File.WriteAllText(Path.Combine(_work, "p.csproj"), "<Project />");

        // 只有**超过保存上限**（防炸盘）时工具才丢数据，而且必须明说。
        _stub.SetExit(0, stdout: string.Concat(Enumerable.Repeat("0123456789abcdef\n", 400_000)));

        var outcome = await Run(new DotnetBuildTool(_stub.Executable), """{"project":"p.csproj"}""");
        var text = Text(outcome);

        Assert.True(Encoding.UTF8.GetByteCount(text) <= ToolLimits.MaxToolOutputBytes,
            $"结果 {Encoding.UTF8.GetByteCount(text)} 字节，超过保存上限 {ToolLimits.MaxToolOutputBytes}。");
    }

    // ---- 超时与取消 ----

    [Fact]
    public async Task Timeout_TerminatesAndSaysOutputIsIncomplete()
    {
        File.WriteAllText(Path.Combine(_work, "p.csproj"), "<Project />");
        _stub.SetSleep(TimeSpan.FromSeconds(20));

        var outcome = await DotnetRunner.RunBuildAsync(
            _stub.Executable, new ToolCall("c", "dotnet_build", """{"project":"p.csproj"}"""),
            Context(), TimeSpan.FromSeconds(2), CancellationToken.None);

        var text = Text(outcome);
        Assert.Contains("超过", text);
        Assert.Contains("不完整", text);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAndStopsTheProcess()
    {
        File.WriteAllText(Path.Combine(_work, "p.csproj"), "<Project />");
        _stub.SetSleep(TimeSpan.FromSeconds(20));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DotnetRunner.RunTestAsync(
                _stub.Executable, new ToolCall("c", "dotnet_test", """{"project":"p.csproj"}"""),
                Context(), TimeSpan.FromMinutes(2), cts.Token));
    }

    // ---- 真实构建（证明工具真的能编、能跑测试） ----

    [Fact]
    public async Task RealBuild_SucceedsThenFailsOnBrokenSource()
    {
        var project = Path.Combine(_work, "Lib");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        var source = Path.Combine(project, "Class1.cs");
        await File.WriteAllTextAsync(source, "namespace Lib; public static class Class1 { public static int One() => 1; }");

        var ok = await Run(new DotnetBuildTool(), """{"project":"Lib/Lib.csproj"}""", TimeSpan.FromMinutes(4));
        Assert.Contains("构建成功", Text(ok));

        // ⚠️ 没有 `noIncremental` 的第二次构建可能直接跳过编译（输入没变），
        // 所以「改坏后必须失败」这一条**必须**带它——否则测的是增量缓存的运气。
        await File.WriteAllTextAsync(source, "namespace Lib; public static class Class1 { public static int One() => ; }");
        var bad = await Run(new DotnetBuildTool(), """{"project":"Lib/Lib.csproj","noIncremental":true}""", TimeSpan.FromMinutes(4));
        Assert.Contains("构建失败", Text(bad));
        Assert.Contains("CS", Text(bad));
    }

    [Fact]
    public async Task RealTest_RunsAPassingTestAndReportsAFailingOne()
    {
        var project = Path.Combine(_work, "Tests");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        var source = Path.Combine(project, "SampleTests.cs");
        await File.WriteAllTextAsync(source,
            """
            using Xunit;

            public sealed class SampleTests
            {
                [Fact] public void Passes() => Assert.Equal(2, 1 + 1);
            }
            """);

        var ok = await Run(new DotnetTestTool(),
            """{"project":"Tests/Tests.csproj","filter":"FullyQualifiedName~SampleTests"}""",
            TimeSpan.FromMinutes(5));
        Assert.Contains("测试成功", Text(ok));

        await File.WriteAllTextAsync(source,
            """
            using Xunit;

            public sealed class SampleTests
            {
                [Fact] public void Passes() => Assert.Equal(2, 1 + 1);
                [Fact] public void Fails() => Assert.Equal(3, 1 + 1);
            }
            """);
        var bad = await Run(new DotnetTestTool(),
            """{"project":"Tests/Tests.csproj","filter":"FullyQualifiedName~SampleTests"}""",
            TimeSpan.FromMinutes(5));
        var text = Text(bad);
        Assert.Contains("测试失败", text);
        Assert.Contains("Fails", text);
    }

    [Fact]
    public async Task SameDirectoryRuns_AreSerializedInsteadOfColliding()
    {
        var project = Path.Combine(_work, "Lib");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(project, "Class1.cs"), "namespace Lib; public static class Class1 { }");

        var args = """{"project":"Lib/Lib.csproj","noIncremental":true}""";
        var results = await Task.WhenAll(
            Run(new DotnetBuildTool(), args, TimeSpan.FromMinutes(4)),
            Run(new DotnetBuildTool(), args, TimeSpan.FromMinutes(4)));

        // ⚠️ 断言的是「两次都完整结束」：并发撞 `obj/` 时 MSBuild 会报文件锁 / 目标已存在，
        // 而那种失败是**与源码无关**的——这正是没有互斥时这个测试会红的样子。
        Assert.All(results, r => Assert.Contains("构建成功", Text(r)));
    }

    // ---- 辅助 ----

    private SessionToolContext Context() => new(_work, _blobs);

    private async Task<ToolOutcome> Run(ITool tool, string args, TimeSpan? timeout = null)
    {
        var run = tool.RunAsync(new ToolCall("c", tool.Name, args), Context(), CancellationToken.None);
        return timeout is null ? await run : await run.WaitAsync(timeout.Value);
    }

    private static string Text(ToolOutcome outcome) =>
        string.Join("", outcome.Content.OfType<Block.TextBlock>().Select(x => x.Text));
}

/// <summary>
/// 现编一个 tiny console 替身，用来观察**参数原样到达**、构造失败输出 / 大输出 / 慢进程。
///
/// <para>⚠️ 行为由替身旁边的文件控制，而不是环境变量：环境变量会被
/// <see cref="DotnetRunner"/> 清掉 <c>DOTNET_*</c> 那一族——那正是被测的边界，
/// 探针不能绕过它。</para>
/// </summary>
public sealed class DotnetStubFixture : IDisposable
{
    private readonly string _root;
    private readonly string _exeDir;

    public DotnetStubFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "lilara-dotnet-stub-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(_root, "stub");
        Directory.CreateDirectory(project);

        File.WriteAllText(Path.Combine(project, "stub.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(project, "Program.cs"), """
            var dir = AppContext.BaseDirectory;
            File.WriteAllText(Path.Combine(dir, "args.json"), System.Text.Json.JsonSerializer.Serialize(args));
            if (File.Exists(Path.Combine(dir, "stdout.txt"))) Console.Out.Write(File.ReadAllText(Path.Combine(dir, "stdout.txt")));
            if (File.Exists(Path.Combine(dir, "stderr.txt"))) Console.Error.Write(File.ReadAllText(Path.Combine(dir, "stderr.txt")));
            if (File.Exists(Path.Combine(dir, "sleep.txt")))
                Thread.Sleep((int)(double.Parse(File.ReadAllText(Path.Combine(dir, "sleep.txt")).Trim()) * 1000));
            return File.Exists(Path.Combine(dir, "exit.txt")) ? int.Parse(File.ReadAllText(Path.Combine(dir, "exit.txt")).Trim()) : 0;
            """);

        var build = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "build", "stub/stub.csproj", "-c", "Release", "--nologo", "-v", "q" })
            build.ArgumentList.Add(arg);
        using var process = Process.Start(build)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("无法构建 dotnet 工具测试替身：" + stderr);

        _exeDir = Path.Combine(project, "bin", "Release", "net10.0");
        Executable = Path.Combine(_exeDir, OperatingSystem.IsWindows() ? "stub.exe" : "stub");
    }

    public string Executable { get; }

    public void Reset()
    {
        foreach (var name in new[] { "args.json", "stdout.txt", "stderr.txt", "exit.txt", "sleep.txt" })
        {
            var path = Path.Combine(_exeDir, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public string[] LastArgs() =>
        File.Exists(Path.Combine(_exeDir, "args.json"))
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(_exeDir, "args.json")))!
            : [];

    /// <summary>这次有没有启动过替身（`null` = 没启动）。</summary>
    public string[]? StartedArgs() =>
        File.Exists(Path.Combine(_exeDir, "args.json"))
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(_exeDir, "args.json")))
            : null;

    public void SetExit(int exitCode, string? stdout = null, string? stderr = null)
    {
        File.WriteAllText(Path.Combine(_exeDir, "exit.txt"), exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (stdout is not null) File.WriteAllText(Path.Combine(_exeDir, "stdout.txt"), stdout);
        if (stderr is not null) File.WriteAllText(Path.Combine(_exeDir, "stderr.txt"), stderr);
    }

    public void SetSleep(TimeSpan duration) =>
        File.WriteAllText(Path.Combine(_exeDir, "sleep.txt"),
            duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}

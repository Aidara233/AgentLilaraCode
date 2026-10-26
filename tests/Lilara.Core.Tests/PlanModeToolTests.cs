using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

public sealed class PlanModeToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-plan-mode-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-plan-mode-work-" + Guid.NewGuid().ToString("N"));

    public PlanModeToolTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_work, true); } catch { }
    }

    private SessionToolContext Context(string mode = "plan", string initiative = "step", SessionPlan? plan = null) =>
        new(_work, new BlobStore(_root), plan: plan, mode: mode, initiative: initiative);

    [Theory]
    [InlineData("write", "{}")]
    [InlineData("update", "{}")]
    [InlineData("delete", "{}")]
    [InlineData("mkdir", "{}")]
    [InlineData("git_write", "{\"operation\":\"add\",\"paths\":[\"a.txt\"]}")]
    public async Task PlanMode_DeniesFileMutationEvenWhenTrustedOrPreapproved(string name, string args)
    {
        var tool = new StubTool(name, PermissionClass.Writing);
        var executor = new ToolExecutor([tool], new BlobStore(_root), "trusted", mode: "plan");
        var call = new Block.ToolUseBlock("c1", name, args);
        var approved = new ToolExecutor.Reply(AskKind.Permission, ["c1"], [], new Dictionary<string, string>());

        var batch = await executor.ExecuteAsync([call], Context(), CancellationToken.None, approved);

        Assert.Empty(batch.ExecutedCallIds);
        Assert.Equal(0, tool.Runs);
        Assert.Contains("规划模式", Assert.IsType<Block.TextBlock>(
            Assert.Single(Assert.Single(batch.Results).Content)).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("dotnet_build")]
    [InlineData("dotnet_test")]
    [InlineData("path")]
    public async Task PlanMode_DoesNotApplyTheFileMutationBlockToOtherTools(string name)
    {
        var tool = new StubTool(name, PermissionClass.ReadOnly);
        var executor = new ToolExecutor([tool], new BlobStore(_root), "trusted", mode: "plan");
        var batch = await executor.ExecuteAsync(
            [new Block.ToolUseBlock("c1", name, "{}")], Context(), CancellationToken.None);

        Assert.Equal(["c1"], batch.ExecutedCallIds);
        Assert.Equal(1, tool.Runs);
    }

    [Theory]
    [InlineData("readonly")]
    [InlineData("off")]
    [InlineData("trusted")]
    public async Task PlanModeSwitch_ExitAsksRegardlessOfPermissionLevel(string permission)
    {
        var tool = new PlanModeTool();
        // 工具声明不经通用 permission 闸，因此三个档位都应得到同一 Permission Ask。
        var context = new SessionToolContext(_work, new BlobStore(_root), mode: "plan", initiative: "step");
        Assert.False(tool.GatedByPermission);
        var outcome = await tool.RunAsync(new ToolCall("c1", tool.Name,
            """{"mode":"act","reason":"需要实际修改文件"}"""), context, CancellationToken.None);
        Assert.Equal(AskKind.Permission, outcome.Ask!.Kind);
        Assert.Empty(outcome.Ask.Remember);
        Assert.Contains("需要实际修改文件", outcome.Ask.Items[0].Question, StringComparison.Ordinal);
        _ = permission; // 各档位作为独立 theory case；模式工具不读取权限档位。
    }

    [Fact]
    public async Task PlanModeSwitch_ApprovedCallReturnsSuccessWithoutAskingAgain()
    {
        var tool = new PlanModeTool();
        var context = new SessionToolContext(_work, new BlobStore(_root), mode: "plan",
            approvedCallIds: ["c1"], initiative: "step");
        var outcome = await tool.RunAsync(new ToolCall("c1", tool.Name,
            """{"mode":"act","reason":"用户已确认"}"""), context, CancellationToken.None);

        Assert.Null(outcome.Ask);
        Assert.Contains("已批准", Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanModeSwitch_IterateCannotExitAndGoalCannotExitBeforeOrdinaryPlanIsEmpty()
    {
        var tool = new PlanModeTool();
        var args = """{"mode":"act","reason":"继续执行"}""";
        var plan = new SessionPlan
        {
            Nodes = [new PlanNode { Id = "n1", Title = "一步", Body = "做一步" }],
            UpdatedAt = DateTimeOffset.UnixEpoch,
            T0 = new T0PlanNode { Title = "长期目标", Body = "持续维护" },
        };

        var iterate = await tool.RunAsync(new ToolCall("i", tool.Name, args),
            Context("plan", Axes.Iterate, plan), CancellationToken.None);
        Assert.Null(iterate.Ask);
        Assert.Contains("iterate", Assert.IsType<Block.TextBlock>(Assert.Single(iterate.Content)).Text, StringComparison.Ordinal);

        var goal = await tool.RunAsync(new ToolCall("g", tool.Name, args),
            Context("plan", "goal", plan), CancellationToken.None);
        Assert.Null(goal.Ask);
        Assert.Contains("普通计划尚未完成", Assert.IsType<Block.TextBlock>(Assert.Single(goal.Content)).Text, StringComparison.Ordinal);
    }

    private sealed class StubTool(string name, PermissionClass permission) : ITool
    {
        public int Runs { get; private set; }
        public string Name => name;
        public PermissionClass PermissionClass => permission;
        public string Description => "test";
        public string ParametersSchemaJson => "{\"type\":\"object\"}";
        public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(ToolText.Of("ran"));
        }
    }
}

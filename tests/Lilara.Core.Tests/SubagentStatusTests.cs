using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

/// <summary>
/// **子会话的状态与「别去轮询」那一条**（2026-10-26）。
///
/// <para>⚠️ <b>这一族钉的是两个真实毛病</b>：① 尾块里那一行状态以前只读孩子的 `state.json`，
/// 而 `Running` 只写内存 ⇒ **一个正在跑的子会话永远显示「空闲」**，模型据此以为它做完了；
/// ② 系统块里没有任何一句「回报会自动回来，不要主动查」的约束，于是模型会为「看看进度」
/// 反复 `tell_child`。</para>
/// </summary>
public sealed class SubagentStatusTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-substatus-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-substatuswork-" + Guid.NewGuid().ToString("N")[..8]);

    public SubagentStatusTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不该让测试红 */ }
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    /// <summary>`Running` 必须有自己的措辞——它以前根本落不到这一层。</summary>
    [Fact]
    public void Line_RendersRunningAsItsOwnWord()
    {
        var scope = new SubagentScope("p", null, 0, 1,
            [new SubagentChild("c1", SessionStatus.Running)]);

        var line = SubagentView.Line(scope);

        Assert.NotNull(line);
        Assert.Contains("运行中", line, StringComparison.Ordinal);
        Assert.DoesNotContain("空闲", line, StringComparison.Ordinal);
    }

    /// <summary>那条约定**只在真有子会话时**进尾块（没有派生关系时白付一份 token）。</summary>
    [Fact]
    public void Tail_CarriesTheSubagentNormOnlyWhenThereIsASubagentLine()
    {
        var withLine = TailText(SystemBlock.Build(
            null, null, Axes.Defaults(), subagentLine: "子会话 c1 运行中"));
        Assert.Contains(CorePrompts.SubagentNorm, withLine, StringComparison.Ordinal);

        var without = TailText(SystemBlock.Build(null, null, Axes.Defaults()));
        Assert.DoesNotContain(CorePrompts.SubagentNorm, without, StringComparison.Ordinal);

        static string TailText(PromptSlots slots) =>
            string.Concat(slots.Tail.OfType<Block.TextBlock>().Select(block => block.Text));
    }

    /// <summary>
    /// **内存优先、盘面回退**：`Running` 从不落盘，所以只读盘面的快照里它永远是 `Idle`
    /// ——这正是用户看到「永远是空闲」的原因。
    /// </summary>
    [Fact]
    public void Scope_PrefersLiveStatusOverTheDiskCopy()
    {
        var store = new SessionStore(_root);
        var parent = Spec("p", parentSessionId: null);
        store.WriteSpec("pk", parent);
        store.WriteSpec("pk", Spec("c", parentSessionId: "p"));
        // 盘面那份只可能是 Idle（`Running` 从不落盘）。
        store.WriteState("pk", new SessionState
        {
            SessionId = "c", Status = SessionStatus.Idle, Turn = 0, Pending = [],
        });

        var handle = new SessionHandle
        {
            ProjectKey = "pk",
            Spec = parent,
            State = new SessionState { SessionId = "p", Status = SessionStatus.Idle, Turn = 0, Pending = [] },
            Directory = store.SessionDirectory("pk", "p"),
        };
        var stepper = new Stepper(store, new FakeAdapter());

        // 没接活状态 ⇒ 就是盘面那份（旧行为，也是那个 bug）。
        Assert.Equal(SessionStatus.Idle, Assert.Single(stepper.SubagentScopeFor(handle).Children).Status);

        // 接上之后 ⇒ 看到「正在跑」。
        stepper.LiveStatus = id => id == "c" ? SessionStatus.Running : null;
        Assert.Equal(SessionStatus.Running, Assert.Single(stepper.SubagentScopeFor(handle).Children).Status);
    }

    private SessionSpec Spec(string id, string? parentSessionId) => new()
    {
        SessionId = id,
        ProjectKey = "pk",
        WorkingDirectory = _work,
        CreatedAt = DateTimeOffset.UnixEpoch,
        ParentSessionId = parentSessionId,
        Model = new ModelSelection
        {
            ProviderId = "local",
            ModelId = "m",
            Protocol = Protocols.ClaudeMessages,
            Effort = "medium",
            MaxTokens = 1024,
        },
    };
}

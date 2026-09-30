using Lilara.Core.Adapters;
using Lilara.Core.Persistence;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

public class SessionHandleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-handle-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;

    public SessionHandleTests() => _store = new SessionStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "claude",
        ModelId = "claude-opus-5",
        Protocol = Protocols.ClaudeMessages,
        Effort = "high",
        MaxTokens = 16000,
    };

    [Fact]
    public void Create_WritesSpecAndStateToDisk()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.True(File.Exists(handle.SpecPath));
        Assert.True(File.Exists(handle.StatePath));
        Assert.Equal(0, handle.State.Turn);
        Assert.Equal("Idle", handle.State.Status);
    }

    [Fact]
    public void Create_RecordsTheWorkingDirectoryInTheSpec()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.Equal(Path.GetFullPath(@"C:\work"), handle.Spec.WorkingDirectory);
    }

    [Fact]
    public void Load_RestoresSpecAndState()
    {
        var created = SessionHandle.Create(_store, @"C:\work", Model());

        var loaded = SessionHandle.Load(_store, created.ProjectKey, created.SessionId);

        // ⚠️ 不能写 Assert.Equal(created.State, loaded.State)：Pending 是集合，
        // record 的自动相等对集合成员用引用相等，那条断言必挂。
        Assert.Equal(created.Spec, loaded.Spec);
        Assert.Equal(created.State.SessionId, loaded.State.SessionId);
        Assert.Equal(created.State.Status, loaded.State.Status);
        Assert.Equal(created.State.Turn, loaded.State.Turn);
        Assert.Empty(loaded.State.Pending);
    }

    [Fact]
    public void SaveState_PersistsTurnCounter()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());
        handle.State = handle.State with { Turn = 3 };

        handle.SaveState(_store);

        Assert.Equal(3, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }

    [Fact]
    public void Create_ProducesDistinctSessionIdsPerCall()
    {
        var a = SessionHandle.Create(_store, @"C:\work", Model());
        var b = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.NotEqual(a.SessionId, b.SessionId);
        Assert.Equal(2, _store.ListSessions(a.ProjectKey).Count);
    }

    [Fact]
    public void Create_LeavesNoListableButUnloadableSessionIfItDiesMidway()
    {
        // ⚠️ 这条测试直接模拟「崩在两次写之间」，验证残留物是**隐形**的而不是致命的。
        // 只调 Create() 测不出顺序——必须手工造出那个中间态。
        var projectKey = Lilara.Core.Projects.ProjectKey.FromPath(@"C:\work");
        var mid = _store.SessionDirectory(projectKey, "999999999999");

        // 只写 state，不写 spec —— 正是「先 state 后 spec」崩溃后的残骸
        Directory.CreateDirectory(mid);
        _store.WriteState(projectKey, new SessionState
        {
            SessionId = "999999999999",
            Status = "Idle",
            Turn = 0,
            Pending = [],
        });

        // 隐形：ListSessions 看不到它，于是没人会去 Load 它
        Assert.Empty(_store.ListSessions(projectKey));
        Assert.Null(_store.TryReadSpec(projectKey, "999999999999"));
    }
}

using Lilara.Core.Persistence;

namespace Lilara.Core.Session;

/// <summary>
/// 一个已激活会话在内存里的把手：数据 + 目录位置。
/// ⚠️ 它会变，但它是**数据**——没有循环、没有定时器（02 §5「激活即缓存」）。
/// </summary>
public sealed record SessionHandle
{
    public required string ProjectKey { get; init; }
    public required SessionSpec Spec { get; init; }
    public required SessionState State { get; set; }
    public required string Directory { get; init; }

    public string SessionId => Spec.SessionId;
    public string SpecPath => Path.Combine(Directory, "spec.json");
    public string StatePath => Path.Combine(Directory, "state.json");
    public string HistoryPath => Path.Combine(Directory, "history.jsonl");

    public static SessionHandle Create(SessionStore store, string workingDirectory, ModelSelection model)
    {
        var projectKey = Projects.ProjectKey.FromPath(workingDirectory);
        var spec = new SessionSpec
        {
            SessionId = Guid.NewGuid().ToString("N")[..12],
            ProjectKey = projectKey,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            CreatedAt = DateTimeOffset.UtcNow,
            Model = model,
        };
        var state = new SessionState
        {
            SessionId = spec.SessionId,
            Status = "Idle",
            Turn = 0,
            Pending = [],
        };

        // ⚠️ 先 state 后 spec——顺序是刻意的，别调换。
        // 崩溃在两次写之间只可能留下「有 state、无 spec」的目录：
        // TryReadSpec 返回 null，ListSessions 静默跳过它 → 孤儿是**隐形**的。
        // 反过来（先 spec 后 state）留下的目录**能被 ListSessions 列出来**，
        // 而 ReadState 会抛 FileNotFoundException ——
        // Task 11 取 [0] 当"当前会话"，于是启动直接崩，还得手动删目录才能恢复。
        // 隐形孤儿 vs 致命孤儿：用已有的 null 守卫，不用新的错误处理。
        store.WriteState(projectKey, state);
        store.WriteSpec(projectKey, spec);

        return new SessionHandle
        {
            ProjectKey = projectKey,
            Spec = spec,
            State = state,
            Directory = store.SessionDirectory(projectKey, spec.SessionId),
        };
    }

    public static SessionHandle Load(SessionStore store, string projectKey, string sessionId) => new()
    {
        ProjectKey = projectKey,
        Spec = store.ReadSpec(projectKey, sessionId),
        State = store.ReadState(projectKey, sessionId),
        Directory = store.SessionDirectory(projectKey, sessionId),
    };

    public void SaveState(SessionStore store) => store.WriteState(ProjectKey, State);
}

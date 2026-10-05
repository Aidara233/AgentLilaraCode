using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Persistence;

// 会话在磁盘上的落点：
//   <数据根>/projects/<projectKey>/sessions/<sessionId>/{spec.json, state.json, history.jsonl}
public sealed class SessionStore(string dataRoot)
{
    private readonly string _dataRoot = dataRoot;

    public string SessionDirectory(string projectKey, string sessionId) =>
        Path.Combine(_dataRoot, "projects", projectKey, "sessions", sessionId);

    public string ProjectsRoot => Path.Combine(_dataRoot, "projects");

    public void WriteSpec(string projectKey, SessionSpec spec) =>
        AtomicFile.Write(Path.Combine(SessionDirectory(projectKey, spec.SessionId), "spec.json"),
            JsonSerializer.Serialize(spec, Json.Options));

    public SessionSpec ReadSpec(string projectKey, string sessionId) =>
        JsonSerializer.Deserialize<SessionSpec>(
            File.ReadAllText(Path.Combine(SessionDirectory(projectKey, sessionId), "spec.json")),
            Json.Options)!;

    public void WriteState(string projectKey, SessionState state) =>
        AtomicFile.Write(Path.Combine(SessionDirectory(projectKey, state.SessionId), "state.json"),
            JsonSerializer.Serialize(state, Json.Options));

    public SessionState ReadState(string projectKey, string sessionId) =>
        JsonSerializer.Deserialize<SessionState>(
            File.ReadAllText(Path.Combine(SessionDirectory(projectKey, sessionId), "state.json")),
            Json.Options)!;

    public void AppendEvent(string projectKey, string sessionId, TimelineEvent evt) =>
        HistoryLog.Append(Path.Combine(SessionDirectory(projectKey, sessionId), "history.jsonl"), evt);

    public IReadOnlyList<TimelineEvent> ReadTimeline(string projectKey, string sessionId) =>
        HistoryLog.ReadAll(Path.Combine(SessionDirectory(projectKey, sessionId), "history.jsonl"));

    // 该项目下的全部会话 id，按 spec.json 里的 CreatedAt 降序。
    // 顺序必须确定（调用方取 [0] 当当前会话），而 Directory.GetDirectories 顺序未定义；
    // 不按目录 mtime（脆弱），也不按 id（Guid 是随机的，做不了时间代理）。
    public IReadOnlyList<string> ListSessions(string projectKey)
    {
        var dir = Path.Combine(ProjectsRoot, projectKey, "sessions");
        if (!Directory.Exists(dir)) return [];

        return Directory.GetDirectories(dir)
            .Select(d => (Id: Path.GetFileName(d), Spec: TryReadSpec(projectKey, Path.GetFileName(d)!)))
            .Where(x => x.Spec is not null)
            .OrderByDescending(x => x.Spec!.CreatedAt)
            .Select(x => x.Id!)
            .ToList();
    }

    // 读不到返回 null，不抛。两种「读不到」都吞：文件不存在，和文件损坏——
    // 只判 File.Exists 不够，损坏的 spec.json 会抛 JsonException 炸掉整个启动。
    public SessionSpec? TryReadSpec(string projectKey, string sessionId)
    {
        var path = Path.Combine(SessionDirectory(projectKey, sessionId), "spec.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<SessionSpec>(File.ReadAllText(path), Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

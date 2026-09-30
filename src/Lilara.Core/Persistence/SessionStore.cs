using System.Text.Json;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Persistence;

/// <summary>
/// 会话在磁盘上的落点（01 §8）：
///   &lt;数据根&gt;/projects/&lt;projectKey&gt;/sessions/&lt;sessionId&gt;/{spec.json, state.json, history.jsonl}
/// </summary>
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

    /// <summary>该项目下已有的全部会话 id。启动时「选最近的会话」用它（01 §8.1）。</summary>
    public IReadOnlyList<string> ListSessions(string projectKey)
    {
        var dir = Path.Combine(ProjectsRoot, projectKey, "sessions");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().ToList();
    }
}

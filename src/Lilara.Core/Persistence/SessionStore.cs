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

    /// <summary>
    /// 该项目下的全部会话 id，**按创建时间降序**（最近的在最前）。
    /// 启动时「选最近的会话」用它（01 §8.1）。
    /// ⚠️ 顺序必须确定：调用方（Program.cs）取 [0] 当"当前会话"，
    /// 而 Directory.GetDirectories 的顺序是未定义的。
    /// 不按目录 mtime（脆弱），不按 id 降序（id 是随机的 Guid，做不了时间代理）——
    /// 按 spec.json 里的 CreatedAt，那是唯一权威的时间来源。
    /// </summary>
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

    /// <summary>
    /// 读不到就返回 null，不抛——调用方要的是「有没有」，不是异常。
    /// ⚠️ 两种「读不到」都要吞：**文件不存在**，和**文件损坏**。
    /// 只判 File.Exists 是不够的——一个损坏的 spec.json 会抛 JsonException，
    /// 穿透 ListSessions 炸掉整个启动，而这正是调用方那个 null 判断想防的事。
    /// </summary>
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
            // 损坏的会话目录应被跳过，而不是让整个核心起不来
            return null;
        }
    }
}

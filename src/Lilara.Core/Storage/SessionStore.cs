using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

// 会话在磁盘上的落点：
//   <数据根>/projects/<projectKey>/sessions/<sessionId>/{spec.json, state.json, history.jsonl}
public sealed class SessionStore(string dataRoot)
{
    private readonly string _dataRoot = dataRoot;

    public string SessionDirectory(string projectKey, string sessionId) =>
        Path.Combine(_dataRoot, "projects", projectKey, "sessions", sessionId);

    public string ProjectsRoot => Path.Combine(_dataRoot, "projects");

    /// <summary>
    /// 数据根下的全部项目键。
    ///
    /// <para>⚠️ **提上来是为了消灭副本**：`CommandDispatcher.ProjectDirs()` 与
    /// `ControlServer.ProjectDirs()` 曾经各有一份**私有拷贝**（逐字相同），
    /// 而 `session.create` 要校验 projectKey ⇒ 会有第三处。「同一事实别抄两遍」这条
    /// 在本仓已经破过好几次（`AuthSchemes.All`、头的轴名字面量）。</para>
    /// </summary>
    public IReadOnlyList<string> ListProjects()
    {
        if (!Directory.Exists(ProjectsRoot)) return [];
        return Directory.GetDirectories(ProjectsRoot).Select(Path.GetFileName).OfType<string>().ToList();
    }

    /// <summary>
    /// 会话目录在不在——**不读 `spec.json`**。
    ///
    /// <para>⚠️ **判「会话存在不存在」不许用 `TryReadSpec(...) is null`**：那个把
    /// **损坏的 `spec.json` 也算「不存在」**（作为**加载**判据时是对的，作为**存在性**
    /// 判据就说假话了）⇒ 坏配置的会话**永远删不掉**，而用户看到的理由是「会话不存在」。
    /// 同族的既有教训见 `CommandDispatcher.HandleProviderDelete` 的注释
    /// （那里为同一件事改用了 `ProviderStore.Delete` 的文件层返回值）。</para>
    ///
    /// <para>⚠️ 它**照样认**「只剩 state.json 的半删目录」——那正是要的：那是垃圾，删得掉才对。</para>
    /// </summary>
    public bool SessionExists(string projectKey, string sessionId) =>
        Directory.Exists(SessionDirectory(projectKey, sessionId));

    /// <summary>
    /// 删掉整个会话目录（`spec.json` / `state.json` / `history.jsonl` 一起）。
    /// **不可逆**——`history.jsonl` 是**只追加永不重写**的，这一刀之后记录就没有了。
    ///
    /// <para>返回是否真有目录被删（不存在 ⇒ `false`，调用方据此说「不存在」而不是说删成了）。</para>
    ///
    /// <para>⚠️ 失败**重抛**，理由与 `ProviderStore.Delete` 逐字相同：Windows 上目录里
    /// 某个文件被别的句柄读着时（`FileShare.Read` 不含 `FILE_SHARE_DELETE`）
    /// 抛 `IOException`，而「读」在本项目是常态。把「暂时占用」说成「删掉了」是假话。</para>
    /// </summary>
    public bool DeleteSession(string projectKey, string sessionId)
    {
        var dir = SessionDirectory(projectKey, sessionId);

        // `Directory.Delete` 对不存在的目录抛 `DirectoryNotFoundException`（不像 `File.Delete`
        // 那样静默 no-op）⇒ 必须先看再删，返回值的语义才是「真有东西被删」。
        // ⚠️ 孤儿 `.tmp`（`AtomicFile` 崩在写与 Move 之间留下的）**照删**——它在目录**里面**，
        // `Directory.Exists` 为真，走的正是这一条。别改成「spec.json 在不在」那种判据。
        if (!Directory.Exists(dir)) return false;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 3)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

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

    // 按 id 找会话：**只扫盘、不载入**。
    //
    // 会话 id 在设计上是全局唯一的（12 位随机 hex），但**盘上没有全局索引**
    // ——`sessions/` 是按项目分目录的，所以只能扫。项目数是个位数，代价可忽略。
    //
    // ⚠️ **它有两个不同需求的调用方，返回值刻意不带 handle**：
    //   · `CommandDispatcher.TryFind` / `ControlServer.TryFind` 拿到 pk 之后要 `Activate`
    //   · `session.archive` / `delete` / `deactivate` 只要**看一眼**，**不该**把会话载进内存
    //     （`Activate` 有登记进激活表的副作用，那是 `ControlServer` 的 `GET /claims`
    //      有意避开的事——一个只读动作不该产生状态副作用）
    // 把 `Activate` 塞进来就等于替第二个调用方做决定。
    //
    // ⚠️ 判据走 `TryReadSpec`：读不出来（文件不在 / 坏了）就跳过。**作为「加载」判据这是对的**
    // ——但没有 spec 就没法知道它的工作目录要算什么。**「存在性」判据另有其人**，见 `SessionExists`。
    public (string ProjectKey, SessionSpec Spec)? FindSession(string sessionId)
    {
        foreach (var projectKey in ListProjects())
        {
            if (TryReadSpec(projectKey, sessionId) is not { } spec) continue;
            return (projectKey, spec);
        }
        return null;
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

using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

// 会话在磁盘上的落点：
//   <数据根>/projects/<projectKey>/sessions/<sessionId>/{spec.json, state.json, history.jsonl}
public sealed class SessionStore(string dataRoot)
{
    private readonly string _dataRoot = dataRoot;

    /// <summary>
    /// 数据根本身。⚠️ **提上来是为了让上层能按数据根构造别的东西**——
    /// 具体是 `BlobStore`（工具的只读句柄要它）。
    ///
    /// <para>⚠️ **不用 `ProjectsRoot` 去反推**（`Path.GetDirectoryName` 那种写法）：
    /// 反推出来的路径与这个字段**不保证逐字相同**（尾分隔符 / 规范化），
    /// 而两个「同一个目录」的字符串在这类判据里就是两份能分叉的真相。
    /// 与 `ListProjects` 那次「消灭私有拷贝」同一条纪律。</para>
    /// </summary>
    public string DataRoot => _dataRoot;

    public string SessionDirectory(string projectKey, string sessionId) =>
        Path.Combine(_dataRoot, "projects", projectKey, "sessions", sessionId);

    public string ProjectsRoot => Path.Combine(_dataRoot, "projects");

    /// <summary>
    /// 数据根下的全部项目键，**按序数升序**。
    ///
    /// <para>⚠️ **提上来是为了消灭副本**：`CommandDispatcher.ProjectDirs()` 与
    /// `ControlServer.ProjectDirs()` 曾经各有一份**私有拷贝**（逐字相同），
    /// 而 `session.create` 要校验 projectKey ⇒ 会有第三处。「同一事实别抄两遍」这条
    /// 在本仓已经破过好几次（`AuthSchemes.All`、头的轴名字面量）。</para>
    ///
    /// <para>⚠️ <b>排序不是装饰，是「顺序必须确定」这条既有裁定的一部分。</b>
    /// `Directory.GetDirectories` 的返回顺序**由文件系统决定、不由我们决定**——
    /// 而调用方会取 <c>[0]</c>：`head/wwwroot/app.js` 在 `currentProject === null` 时
    /// 就取第一个当默认订阅的项目流。<b>把它交给文件系统，等于把头默认订哪个项目交给文件系统。</b></para>
    ///
    /// <para>⚠️ <b>严重性别夸大</b>：Windows/NTFS 恰按文件名序枚举，而项目键是路径规范化后的小写串
    /// ⇒ <b>在本项目唯一支持的平台上，结果碰巧就是序数序</b>。所以这是「一条没写下来的依赖」，
    /// 不是正在发生的故障。写下来 + 显式排序，是为了它在别的文件系统上不变成静默的不确定。</para>
    ///
    /// <para>⚠️ **判据是「确定性」，不是「重要性」**：这里是**序数序**，不是「最近用过的在前」。
    /// 后者要读每个项目的会话才能算出来（那是 `ListSessions` 的活），
    /// 而本方法在 `FindSession` 的扫盘路径上（每个项目都要走一次）。
    /// ⇒ 别把这里读成「第一个就是最近用的那个」。</para>
    /// </summary>
    public IReadOnlyList<string> ListProjects()
    {
        if (!Directory.Exists(ProjectsRoot)) return [];
        return Directory.GetDirectories(ProjectsRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(pk => pk, StringComparer.Ordinal)
            .ToList();
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

    /// <summary>
    /// 读 `spec.json`。**版本不符会抛 <see cref="FileFormatException"/>，不返回 null。**
    ///
    /// <para>⚠️ **「读不出来」与「版本不对」必须分开**，这是本方法刻意与 <see cref="TryReadSpec"/>
    /// 不同的地方：前者重写一份就好，后者重写会把**新版本的数据降级覆盖**掉。
    /// 折成一个 null，用户看到的是「会话不存在」——而那会把人引向删掉重来。</para>
    ///
    /// <para>它只在**显式载入**那条路上被调（`SessionHandle.Load`），
    /// 那条路上的失败本来就该冒到用户面前。**扫盘 / 列列表走 `TryReadSpec`**，
    /// 因为一个太新的会话不该让整个列表打不开。</para>
    /// </summary>
    public SessionSpec ReadSpec(string projectKey, string sessionId) =>
        JsonSerializer.Deserialize<SessionSpec>(
            FileFormat.ReadChecked(Path.Combine(SessionDirectory(projectKey, sessionId), "spec.json")),
            Json.Options)!;

    public void WriteState(string projectKey, SessionState state) =>
        AtomicFile.Write(Path.Combine(SessionDirectory(projectKey, state.SessionId), "state.json"),
            JsonSerializer.Serialize(state, Json.Options));

    /// <summary>读 `state.json`。版本不符抛 <see cref="FileFormatException"/>——理由同 <see cref="ReadSpec"/>。</summary>
    public SessionState ReadState(string projectKey, string sessionId) =>
        JsonSerializer.Deserialize<SessionState>(
            FileFormat.ReadChecked(Path.Combine(SessionDirectory(projectKey, sessionId), "state.json")),
            Json.Options)!;

    public void AppendEvent(string projectKey, string sessionId, TimelineEvent evt) =>
        HistoryLog.Append(Path.Combine(SessionDirectory(projectKey, sessionId), "history.jsonl"), evt);

    /// <summary>
    /// **一步的多个事件，一次落盘**（步是持久化的原子单位，`01` §4）。
    /// 有工具之后一步要落「模型消息（含 `tool_use`）+ N 条结果」，分 N 次追加
    /// 等于把原子单位拆成 N 个崩溃窗口。原子性从<b>一把锁 + 一次 open/close</b> 来
    /// ——见 <see cref="HistoryLog.AppendMany"/> 的注释（含它<b>没</b>解决什么）。
    /// </summary>
    public void AppendEvents(string projectKey, string sessionId, IReadOnlyList<TimelineEvent> events) =>
        HistoryLog.AppendMany(Path.Combine(SessionDirectory(projectKey, sessionId), "history.jsonl"), events);

    public IReadOnlyList<TimelineEvent> ReadTimeline(string projectKey, string sessionId) =>
        HistoryLog.ReadAll(Path.Combine(SessionDirectory(projectKey, sessionId), "history.jsonl"));

    // 该项目下的全部会话 id，按 spec.json 里的 CreatedAt 降序。
    // 顺序必须确定（调用方取 [0] 当当前会话），而 Directory.GetDirectories 顺序未定义；
    // 不按目录 mtime（脆弱），也不按 id（Guid 是随机的，做不了时间代理）。
    //
    // ⚠️ **ThenBy(Id) 不是冗余的**：CreatedAt 是 DateTimeOffset（100ns 刻度），
    // 两个会话在同一刻度上出生**是可能的**（同一毫秒内连建两个、或时间源刻度粗）。
    // 少了这个 tiebreaker，并列的那些会**退回文件系统顺序**——正是上面那句注释要防的东西，
    // 只是从「全序未定义」缩小成了「并列时未定义」。判据一样：确定的顺序要**完整**才算确定。
    public IReadOnlyList<string> ListSessions(string projectKey)
    {
        var dir = Path.Combine(ProjectsRoot, projectKey, "sessions");
        if (!Directory.Exists(dir)) return [];

        return Directory.GetDirectories(dir)
            .Select(d => (Id: Path.GetFileName(d), Spec: TryReadSpec(projectKey, Path.GetFileName(d)!)))
            .Where(x => x.Spec is not null)
            .OrderByDescending(x => x.Spec!.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
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
    //
    // ⚠️ **但「版本太新」不能跟「坏了」一起在这个循环里吞掉**（实测发现的）：
    // `FindSession` 是**按身份找**（用户点了一个具体的会话），而 `ListSessions` 是**枚举**
    // ——两者的容错要求**不同**。枚举时跳过是对的（一个太新的会话不该让整张列表打不开）；
    // 按身份找时跳过，用户会看到「会话 'xxx' 不存在」，而那句话会把人引向**删掉重来**，
    // 正好是唯一一个会丢数据的做法。
    // ⇒ 判据在这里：文件**在**、目录**在**，但读不出来且不是损坏 ⇒ **响亮地抛**。
    public (string ProjectKey, SessionSpec Spec)? FindSession(string sessionId)
    {
        foreach (var projectKey in ListProjects())
        {
            if (TryReadSpec(projectKey, sessionId) is { } spec) return (projectKey, spec);

            // 上面那个 null 可能是「没有这个会话」（正常），也可能是「有、但版本太新」（要响）。
            // 只在**目录真的在**的时候才去分辨——不然每个项目都要为一次未命中多读一次文件。
            // `ReadSpec` 会对太新的版本抛 `FileFormatException`，对损坏的抛 `JsonException`
            // ——前者冒上去（用户要看的就是它），后者照旧跳过（与 `TryReadSpec` 的契约一致）。
            var dir = SessionDirectory(projectKey, sessionId);
            if (!Directory.Exists(dir)) continue;

            try { return (projectKey, ReadSpec(projectKey, sessionId)); }
            catch (JsonException) { continue; }   // 损坏：与 TryReadSpec 同一条路，跳过
        }
        return null;
    }

    // 读不到返回 null，不抛。两种「读不到」都吞：文件不存在，和文件损坏——
    // 只判 File.Exists 不够，损坏的 spec.json 会抛 JsonException 炸掉整个启动。
    //
    // ⚠️ **第三种「读不到」刻意不吞在那里：版本比本程序新。** 见 `ReadSpec` 的注释——
    // 那个不是「这份文件坏了」，重写它会丢数据。扫盘这条路吞掉它（一个太新的会话
    // 不该让整个列表打不开），但它**必须**在读得出来的地方说出来，否则用户再也看不到。
    public SessionSpec? TryReadSpec(string projectKey, string sessionId)
    {
        var path = Path.Combine(SessionDirectory(projectKey, sessionId), "spec.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<SessionSpec>(FileFormat.ReadChecked(path), Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// **读不出来的原因**，给人看的一句话。`null` = 读得出来（或压根没有这个会话）。
    ///
    /// <para>⚠️ **存在的理由：`TryReadSpec` 把两种「读不出来」压成了一个 null**
    /// （那是它的契约，没变），而**这两种的处置方式正相反**——
    /// 「文件坏了」重写一份就好，「版本太新」重写会**丢数据**。
    /// 把它压成一个 null 之后，凡是想说原因的地方都只能猜，而猜错正好会把人引向最坏的做法。</para>
    ///
    /// <para>⚠️ 所以这个分类**住在读取点旁边**，而不是散在各个调用点各猜一遍
    /// （手工 E2E 抓到过：启动横幅把「文件坏了」也说成了「多半是更新的核心写的」）。</para>
    /// </summary>
    public string? UnreadableReason(string projectKey, string sessionId)
    {
        var path = Path.Combine(SessionDirectory(projectKey, sessionId), "spec.json");
        if (!File.Exists(path)) return null;

        try
        {
            _ = JsonSerializer.Deserialize<SessionSpec>(FileFormat.ReadChecked(path), Json.Options);
            return null;
        }
        catch (FileFormatException)
        {
            return "按更新的格式写的";
        }
        catch (JsonException)
        {
            return "文件损坏";
        }
    }
}

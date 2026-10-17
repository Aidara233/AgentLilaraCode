using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;
using Lilara.Core.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lilara.Core.Control;

// 控制面：一条 WS（双向、持久、有序）+ 一组 HTTP（一次性读 / 大对象）。
// 命令投递即返回、结果走事件；WS 收到命令立刻回 ack，步在后台跑。
public sealed class ControlServer(
    CoreHost host, CommandDispatcher dispatcher, EventBus bus, int port,
    string allowedOrigin = ControlServer.DefaultAllowedOrigin)
{
    /// <summary>
    /// 控制面默认端口。
    ///
    /// <para>⚠️ <b>选 17988 是**实测**的结果，不是审美</b>（2026-10-06）：原先的 <c>7788</c>
    /// 落在了 Windows 的**保留端口段**里（本机实测的段有 7681-7780 / 7781-7880 / 7881-7980，
    /// 通常由 Hyper-V / WSL / Docker 的 NAT 预留）⇒ 绑定时直接
    /// <c>SocketException 10013</c>，<b>核心起不来</b>。</para>
    ///
    /// <para>⚠️ <b>而保留段会在重启后变</b>——所以换一个「今天不在里面」的值**不是永久解**，
    /// 只是把一个开机就可能撞上的坑挪开。真正的处置是那条环境变量（<c>LILARA_PORT</c>）
    /// 与那句人话报错，默认值只是让常见情况能用。</para>
    /// </summary>
    public const int DefaultPort = 17988;

    /// <summary>
    /// 默认只放行头自己的 origin。
    ///
    /// <para>⚠️ <b>它必须与 <c>src/Lilara.Head/Program.cs</c> 的默认头端口是同一个数</b>——
    /// 两份副本，而 `AllowedOriginResolver` 会在环境变量缺席时用这里兜底。</para>
    /// </summary>
    internal const string DefaultAllowedOrigin = "http://127.0.0.1:17990";

    // 只监听本机（核心是本设备唯一的，本机进程即可信）。
    public const string BindAddress = "127.0.0.1";

    public string Url => $"http://{BindAddress}:{port}";

    // 给启动横幅读闸真正在接受的那个值（实例值，不是默认常量）；internal 只给同程序集读。
    internal string AllowedOrigin => allowedOrigin;

    // 用 StartAsync 而不是 RunAsync：后者阻塞，端口起不来时用户先看到提示符、进程才死。
    public async Task<WebApplication> StartAsync(CancellationToken ct)
    {
        // content root 必须在这里传：builder.WebHost.UseContentRoot(...) 会抛「Changing the host
        // configuration ... is not supported」而起不来。显式钉住 AppContext.BaseDirectory。
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls(Url);

        // 必须清掉日志 provider：提示符不带换行，而 Kestrel 默认把日志写 stdout，会搅烂用户正在敲的那行。
        builder.Logging.ClearProviders();

        var app = builder.Build();

        // 头是独立进程、独立 origin ⇒ 必须放开 CORS，否则浏览器里那个页面连不上。
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "content-type";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            if (ctx.Request.Method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }
            await next();
        });

        app.UseWebSockets();

        MapSnapshots(app);
        MapWebSocket(app);

        await app.StartAsync(ct);
        return app;
    }

    // ---- 快照 / 查询（HTTP，一次性） ----

    private void MapSnapshots(WebApplication app)
    {
        // 自检**最先**列：核心跑在哪个数据根、哪个工作目录、几个会话在内存里。
        // 「我配了 provider 怎么看不到」这类问题，九成是数据根不对——这是它唯一的答案来源。
        // ⚠️ dataRoot / workingDirectory 是本机绝对路径，在 CORS(*) 下可读——沿用 `/state` 早就
        // 接受的同一个取舍（它一直在交出 WorkingDirectory），且只绑 127.0.0.1。
        app.MapGet("/health", () => Results.Json(new
        {
            Ok = true,
            DataRoot = host.DataRoot,
            WorkingDirectory = host.WorkingDirectory,
            Sessions = host.Activated.Count(),
        }, WireJson.Options));

        // 核心自述：版本 + 真正在接受的那个 origin + 两个端点的 URL。
        // origin 读**实例值**（不是默认常量）——构造参数换过之后，这里必须跟着变。
        // ⚠️ 构造参数今天由 `AllowedOriginResolver` 从环境变量解析（`Program.cs` 的调用点）。
        // **核心不解析任何命令行参数**——所以这里没有 `--allowed-origin` 那样的旗标，
        // 配置一律走环境变量。别在这一行写一个不存在的旗标名：那会让读注释的人以为有这么个入口。
        app.MapGet("/info", () => Results.Json(new
        {
            DataRoot = host.DataRoot,
            WorkingDirectory = host.WorkingDirectory,
            CoreVersion = typeof(ControlServer).Assembly.GetName().Version?.ToString() ?? "?",
            AllowedOrigin = allowedOrigin,
            Endpoints = new { Http = Url, Ws = $"ws://{BindAddress}:{port}/ws" },
        }, WireJson.Options));

        // 每个快照都带该 topic 的 seq 高水位：头拿它去 subscribe(since:)，
        // 「拉快照 → 订阅」之间落的事件才不漏不重。
        app.MapGet("/projects", () => Results.Json(ProjectKeys(), WireJson.Options));

        // 目录浏览只在 core 设备上发生：head 只提交 core 看到的路径，不调用浏览器本机选择器。
        // 缺省返回文件系统根（Windows 盘符 / Unix 根目录），可浏览任意可读目录；不创建目录、不激活会话。
        app.MapGet("/directories", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path) && OperatingSystem.IsWindows())
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { Name = d.Name, Path = d.RootDirectory.FullName })
                    .ToList();
                return Results.Json(new
                {
                    Root = (string?)null,
                    Current = (string?)null,
                    Parent = (string?)null,
                    Directories = drives,
                    Error = (string?)null,
                }, WireJson.Options);
            }

            string current;
            try
            {
                current = string.IsNullOrWhiteSpace(path)
                    ? Path.GetPathRoot(Path.GetFullPath(host.WorkingDirectory))!
                    : Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                return Results.BadRequest(new { error = $"路径不合法：{ex.Message}" });
            }

            var currentInfo = new DirectoryInfo(current);
            if (!currentInfo.Exists)
                return Results.BadRequest(new { error = "目录不存在或不可访问。" });

            DirectoryInfo[] children;
            try { children = currentInfo.EnumerateDirectories().ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new
                {
                    Root = Path.GetPathRoot(current),
                    Current = current,
                    Parent = currentInfo.Parent?.FullName,
                    Directories = Array.Empty<object>(),
                    Error = $"目录不可读：{ex.Message}",
                }, WireJson.Options);
            }

            var directories = children
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new { Name = d.Name, Path = d.FullName })
                .ToList();

            return Results.Json(new
            {
                Root = Path.GetPathRoot(current),
                Current = current,
                Parent = currentInfo.Parent?.FullName,
                Directories = directories,
                Error = (string?)null,
            }, WireJson.Options);
        });

        app.MapGet("/projects/{pk}/sessions", (string pk) =>
            Results.Json(host.Store.ListSessions(pk), WireJson.Options));

        // 全部会话的**运行状态**，一次拿全（`01` §3.8）。
        //
        // ⚠️ **它绝不 `Activate`** —— 这是这个端点唯一真的容易写错的地方：
        // `/sessions/{id}/state` 那条路在激活表 miss 时会**载入并登记**那个会话，
        // 而那是**有副作用的读**。照着它写这个端点 ⇒ **打开一次列表 = 全库载入**，
        // 而载入会触发回收（`01` §11.7 那次事故：把「顺手捡」做成了「花钱买」）。
        // ⇒ 判据：**激活表里有就拿内存那份（它才有 `Running`），没有就直接读盘**。
        //
        // ⚠️ **四种状态里有两种是内存事实**：`Running` 只写内存、`不活跃` 是「不在激活表里」。
        // 盘上只存 `Idle` / `WaitingForInput` ⇒ 这个端点必须两个来源都看。
        app.MapGet("/sessions", () =>
        {
            var activated = host.Activated.ToDictionary(h => h.SessionId, StringComparer.Ordinal);
            var rows = new List<object>();

            // 项目序 + 项目内序都由各自的既有实现定（`ListProjects` / `ListSessions` 都是有序列举）
            // ⇒ 本端点不引入第二个排序判据。
            foreach (var pk in ProjectKeys())
            {
                foreach (var sid in host.Store.ListSessions(pk))
                {
                    var spec = host.Store.TryReadSpec(pk, sid);
                    if (spec is null) continue;     // 坏 spec 的会话列不出来（同 `ListSessions` 的取舍）

                    if (activated.TryGetValue(sid, out var live))
                    {
                        rows.Add(Row(sid, pk, active: true, live.State, spec.Title));
                        continue;
                    }

                    // ⚠️ 读不出来就**跳过**，不抛：一个坏 `state.json` 不该让整个列表 500
                    // （同 `/claims` 那条「坏文件不让它打死核心」）。
                    SessionState? disk = null;
                    try { disk = host.Store.ReadState(pk, sid); }
                    catch (Exception ex) when (ex is IOException or FileNotFoundException
                                                  or UnauthorizedAccessException)
                    {
                        // 跳过。
                    }

                    if (disk is not null) rows.Add(Row(sid, pk, active: false, disk, spec.Title));
                }
            }

            return Results.Json(rows, WireJson.Options);

            static object Row(string sid, string pk, bool active, SessionState state, string? title) => new
            {
                SessionId = sid,
                ProjectKey = pk,
                // ⚠️ 「不活跃」是**内存事实**（盘上没有它的痕迹）。头要它，因为
                // 「没在内存里」与「没在跑」不是一回事（后者还要看 `status`）。
                Active = active,
                state.Status,
                state.Turn,
                Title = title,
                // ⚠️ **待办的那次询问**：头据此在列表上标出「哪个会话在等我」，
                // 不必再去逐个 `GET /approvals`。
                //
                // ⚠️ **「没在等」是 `{}`，不是 `null`、也不是省略**。三个都试过：
                //   · `null` ⇒ `WhenWritingNull` 把**整个键**省掉 ⇒ 头的 `row.ask` 是
                //     `undefined`（同 `/claims` 的 `stale` 那条教训）
                //   · 内层可空字段 ⇒ 同一个开关把内层也写成 `{}`
                // ⇒ **契约是：头判 `row.ask?.askId`**（`{}` 本身是 truthy，不能拿它当判据）。
                // 这条**必须写在这里**，因为 `{}` 看着像「有个对象」，而它是「什么都没有」。
                //
                // ⚠️ 只交出**外部要看的三格**——`ArgsHash` / `CallId` 是内部配对用的，
                // 交出去等于把实现细节写进线上 schema。
                Ask = state.Ask is { } a
                    ? new
                    {
                        // ⚠️ 显式 `(string?)` / `(AskKind?)` 不是多余的：两个分支的匿名类型
                        // **必须逐字段同名同型**才会被编译器合并成同一个类型——否则这里是一个
                        // CS8619（可空性不匹配），而解法不是 `!`（那会撒谎），是让两支**真的是同一个形状**。
                        //
                        // ⚠️ **`Kind` 直接交枚举，不 `.ToString()`**（2026-10-11 改）：
                        // 手写的 `.ToString()` **绕开了 `WireJson.Options`**（那是 `JsonStringEnumConverter(camelCase)`）
                        // ⇒ 线上一半 `"Permission"`、一半 `"permission"`（同一条 `/state` 响应里两种都有，
                        // 见下面那个 `Ask`）。头拿 PascalCase 去比 ⇒ 「？授权」徽标画成「？提问」。
                        // ⇒ 让序列化器接管：线上拼法此后只有一个来源。
                        AskId = (string?)a.AskId,
                        ToolName = (string?)a.ToolName,
                        Kind = (AskKind?)a.Kind,
                        a.DeadlineAt,
                    }
                    : new { AskId = (string?)null, ToolName = (string?)null, Kind = (AskKind?)null,
                            DeadlineAt = (DateTimeOffset?)null },
            };
        });

        app.MapGet("/sessions/{id}/state", (string id) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();

            // 先取高水位，再取内容：反过来，落在中间的事件既不在快照里、也不会被补发，头永久少一条。
            // 代价是落在 (高水位, 内容] 的事件会重复渲染一次（头去重不了），比漏轻。
            var highWater = bus.HighWaterMark(EventBus.SessionTopic(id));

            // 走 SessionQueries（先查内存激活表）：读磁盘会在步进行中报 Idle，用户会以为没在跑、再发一次。
            return Results.Json(
                new
                {
                    handle.SessionId,
                    State = SessionQueries.StateOf(host, handle),

                    HighWater = highWater,

                    // 轴必须走顶层这个 axes：axis.changed 只在订阅之后才有，头刚开页面时无事件可等。
                    // 轴只有一份、住在 State 里；handle 必经 Load / Create（都过 CompleteAxes）⇒ 下面的 ! 不是赌。
                    Axes = PluginSessionView.Values(handle.State),
                    AxisDefinitions = PluginSessionView.Definitions(host.Plugins, handle.Spec),
                    PluginProblems = PluginSessionView.Problems(host.Plugins, handle.Spec),

                    // 整个 spec 交出去、不做子集裁剪：SessionSpec 里没有 ApiKey，所以天生不泄 key。
                    // 代价：WorkingDirectory 是本机绝对路径，在开放 CORS(*) 下可读。
                    Spec = handle.Spec,

                    // 三个槽（系统 / 首要 / 浮动）：渲染归核心、显示归头。
                    // ⚠️ **三格今天全是真的了**（2026-10-11，提示词拼装批）：
                    // 系统块自 2026-10-07 起读 `presets/<id>.md`，本批补上首要块
                    // （`premiers/<id>.md`）与浮动槽（核心每步现算的轴表）。
                    // ⚠️ **它们与请求走同一个来源**（`SystemBlock.Render`）——
                    // 各算一份的话，「头显示什么」与「模型收到什么」会悄悄分叉，
                    // 而那正是 `05` §3 那条「渲染归核心」要防的事。
                    //
                    // ⚠️ **读的时刻与「步」无关**：这里没有步的概念（这是个 HTTP 快照端点），
                    // 所以它拿的是**此刻**的文件内容与轴值。而发请求那一侧是**步开始冻结**的
                    // （见 `SystemBlock.Render` 的注释）⇒ 用户在步中途改文件时，
                    // 这几格会**先**变、而请求里要等下一步才变。那不是缺陷，是两条不同的时间轴：
                    // 快照端点回答「现在是什么」，请求回答「这一步是什么」。
                    //
                    // ⚠️ **缺席与「空」是两回事**：三格都可能是 `[]`（用户删了预设 / 没有轴），
                    // 头必须把它显示成**缺席**而不是画一张空卡——同 `05` §7.1 那条
                    // 「空的也要显示（标『（空）』）」。
                    Slots = SlotsOf(handle),

                    // ⚠️ **待回答的那次询问，含问题原文**（`01` §3.8）。
                    //
                    // ⚠️ **`State.Ask` 交不出问题**：它只记「等哪一次调用、等多久」
                    // （那是**盘面事实**），而问题原文是**渲染产物**。
                    // ⇒ 只从 `State` 取的话，**打开页面时看不见要问什么**
                    // ——而那正是头要回答它的前提。
                    //
                    // ⚠️ **问题原文从落盘的那条 `tool_use` 参数里重新解出来**
                    // （实测 2026-10-08）：它在盘上**本来就有**（那条模型消息是必须落的，
                    // 否则配对悬空），而内存那份只在「本进程亲手挂起过」时才在。
                    // 只信内存的话，**任何一次「从盘载入」都会丢问题**——不只是重启。
                    //
                    // ⚠️ 代价：`ask` 工具的参数是**模型写的**，所以这里可能解不出来
                    // （形状写错时工具本来会返回一条错误结果、**不会挂起**⇒ 正常路径上解得出）。
                    // 解不出就退回内存那份；再没有就是空 —— 头必须据此显示
                    // 「原问题没能保留」，而**不是**画一张空卡。
                    Ask = handle.State.Ask is { } pa
                        ? new
                        {
                            pa.AskId,
                            // ⚠️ 同样交枚举、不 `.ToString()`——与上面 `/sessions` 那一格**必须同拼法**
                            // （同一条响应里 `state.ask.kind` 与顶层 `ask.kind` 不一致，实测抓到过）。
                            Kind = pa.Kind,
                            pa.ToolName,
                            pa.DeadlineAt,
                            Items = ItemsOf(handle, pa) is { Count: > 0 } fromDisk
                                ? fromDisk
                                : handle.AwaitingInput?.Ask.Items ?? (IReadOnlyList<AskItem>)[],
                            Remember = handle.AwaitingInput?.Ask.Remember ?? (IReadOnlyList<AskMemory>)[],
                        }
                        : null,
                },
                WireJson.Options);
        });

        // 从落盘的那条 `tool_use` 里解出问题（`01` §3.8）。
        //
        // ⚠️ **它现在只是转调 `AskTool.ItemsOf`**——那份实现在**答完那条路**上也要用
        // （`CommandDispatcher` 渲染答复时同样要知道问了什么）。
        // 各写一份就会出现「页面看得见问题、而回答时说你没答」这种分叉，
        // 而本仓栽过同形的（两处副本只改一处）。
        //
        // ⚠️ **档位现读、传进去**（2026-10-11）：闸门那条询问的问句里要印当前档位，
        // 而重建它的人（`Gate.SecondHalfOf`）拿不到会话状态。不传的话它只能印
        // 「未知」，而用户**正是靠这句话判断该不该提权**（`01` §3.5：模型不能自提权）。
        IReadOnlyList<AskItem> ItemsOf(SessionHandle handle, PausedAsk paused) =>
            Session.Tools.AskTool.ItemsOf(host.Store, handle.ProjectKey, handle.SessionId, paused.CallId,
                handle.State.Axes?.GetValueOrDefault(Axes.Permission));

        app.MapGet("/sessions/{id}/history", (string id, int? limit, string? before) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();

            const int defaultLimit = 50;
            const int maxLimit = 200;
            var pageSize = limit ?? defaultLimit;
            if (pageSize is < 1 or > maxLimit)
                return Results.BadRequest(new { error = $"limit 必须在 1 到 {maxLimit} 之间。" });

            // 同 /state：先高水位、后内容。它是 WS 游标，不是历史分页游标。
            var highWater = bus.HighWaterMark(EventBus.SessionTopic(id));

            // 分页必须作用在核心渲染后的事件上：ReleaseEvent、库解引用、回收注记和 framing
            // 都先确定，不能直接按 history.jsonl 行切，否则 head 看到的页会和模型看到的形态分叉。
            //
            // ⚠️ **线不再被丢掉**（2026-10-17）：它**不进模型上下文**（`TimelineRender` 照样挡），
            // 但它是给**人**的锚——头要画一条分隔线（`01` §14 #32）。
            // ⇒ 在这里显式渲染成 `kind: "release"`，模型侧那份不经过这个函数。
            var events = SessionQueries.HistoryOf(host, handle).ToList();

            // ---- 配对的工具结果紧跟它那条模型消息（2026-10-17）----
            //
            // ⚠️ **这是分页单位的一次改动，不是排版偏好**：记录里的次序是「模型消息 →
            // 它那批结果」（`Stepper` 一次 append，调用序），但结果**在记录里是独立事件**，
            // 而头按事件逐条画 ⇒ 「调用都在上、结果都在下」。
            // 现在核心把它们**当成一组**交给头（组 = 模型消息 + 它发出的、且在后文出现的
            // `callId` 对应的结果），于是：
            //   ① 分页不会把「一条调用」和「它的结果」切到两页（切了那头就配不上）；
            //   ② 头既可以按组画，也可以把结果挂到调用下面——判据只此一处。
            //
            // ⚠️ 组内次序保留「消息在前、结果按出现序在后」：只看 JSON 的消费者仍读得出
            // 记录里的原始次序；「挂到调用下面」是**头的渲染选择**，不是核心替它决定。
            var grouped = GroupToolResults(events);

            var end = grouped.Count;
            if (before is not null)
            {
                if (!TryDecodeHistoryCursor(before, out var cursorSession, out var beforeId)
                    || !string.Equals(cursorSession, id, StringComparison.Ordinal))
                    return Results.BadRequest(new { error = "历史游标无效或不属于此会话。" });

                end = grouped.FindIndex(g => g.Any(e => string.Equals(e.Id, beforeId, StringComparison.Ordinal)));
                if (end < 0)
                    return Results.BadRequest(new { error = "历史游标已失效，请重新加载会话。" });
            }

            var start = Math.Max(0, end - pageSize);
            var page = grouped.GetRange(start, end - start);
            var hasMore = start > 0;
            // ⚠️ 游标指到**组的第一条**：它由组拼出，重拉时用「哪一组包含它」来找边界。
            var nextCursor = hasMore ? EncodeHistoryCursor(id, page[0][0].Id) : null;

            return Results.Json(new
            {
                highWater,
                rendered = page.SelectMany(g => g).Select(Render).ToList(),
                hasMore,
                NextCursor = nextCursor ?? "",
            }, WireJson.Options);
        });

        app.MapGet("/sessions/{id}/queue", (string id) =>
        {
            if (!TryFind(id, out var handle)) return Results.NotFound();
            return Results.Json(new { handle.IsRunning, handle.QueueDepth }, WireJson.Options);
        });

        // 统计是核心事实的只读投影；这些端点不激活会话、不触发模型或插件。
        app.MapGet("/usage/calls", (int? skip, int? limit) =>
        {
            var all = host.Usage.ReadAll();
            var offset = Math.Max(0, skip ?? 0);
            var pageSize = Math.Clamp(limit ?? 100, 1, 1000);
            return Results.Json(new
            {
                AsOf = CoreClock.Now(),
                Skip = offset,
                Limit = pageSize,
                Total = all.Count,
                Calls = all.Skip(offset).Take(pageSize).ToList(),
            }, WireJson.Options);
        });

        app.MapGet("/usage/summary", () => Results.Json(
            host.Usage.Summarize(), WireJson.Options));

        app.MapGet("/sessions/{id}/usage", (string id) =>
        {
            if (host.Store.FindSession(id) is not { } found) return Results.NotFound();
            var calls = host.Usage.ForSession(id);
            return Results.Json(new
            {
                SessionId = id,
                ProjectKey = found.ProjectKey,
                Summary = host.Usage.Summarize(new UsageFilter { SessionId = id }),
                Calls = calls,
            }, WireJson.Options);
        });

        app.MapGet("/sessions/{id}/context", (string id) =>
        {
            if (host.Store.FindSession(id) is not { } found) return Results.NotFound();
            var latest = host.Usage.ForSession(id).LastOrDefault(c => c.Usage is not null);
            return Results.Json(new
            {
                SessionId = id,
                ProjectKey = found.ProjectKey,
                Latest = latest is null ? null : new
                {
                    latest.CallId,
                    latest.StartedAt,
                    latest.EndedAt,
                    latest.ModelId,
                    latest.Protocol,
                    InputTokens = latest.Usage?.InputTokens,
                    Source = latest.Usage is null ? "unknown" : "reported",
                    RequestFingerprint = latest.RequestFingerprint,
                },
                Preview = (object?)null,
            }, WireJson.Options);
        });

        // 头唯一的轴来源（不许手抄轴表）：标签与告警的两端都从核心交出去，头里写字面量会静默失效。
        // alertFrom 是「起始档」，头的判据要按 values.indexOf 的档位序比（权限是单调阶梯）。
        // ⇒ values 的顺序（= 档位序）也是契约的一部分，别排序。
        // ⚠️ `range` 非空 ⇒ 这是一条**数值范围**轴：头应当画数字输入而不是下拉。
        // 但**即使有 range，`values` 也照样是那张网格**（见 `Axes.Grid` 的注释）——
        // 所以一个不认识 range 的头**画出来仍然是对的**（21 个选项的下拉），不会崩。
        // 这条是刻意的：让核心能先上线，头后跟。
        app.MapGet("/axes", () => Results.Json(new
        {
            Axes = PluginSessionView.Definitions(host.Plugins),
            AlertSafety = Kernel.Axes.AlertSafety,
            AlertFrom = Kernel.Axes.AlertFrom,
        }, WireJson.Options));

        // 没有声明时回 []（「没人声明」是合法且最常见的形态）。
        // 声明落在工作目录，所以要先找一个会话问它的工作目录。
        // 读 spec 不用 TryFind：本类的 TryFind 会 Activate 会话，一个 GET 端点不该产生状态副作用。
        app.MapGet("/claims", () =>
        {
            var projectKeys = ProjectKeys();

            // ⚠️ **`stale`**：`session.delete` 是两步（先摘声明、再删目录），崩在中间会留下
            // 「sessionId 指向一个已经不存在的会话」的声明；手删目录、或另一个核心删掉也会。
            // 它不是半删的会话目录（那个更糟，见 `01` §8），是**过期的项目级数据**——
            // 今天**不自动清理**（在别人正活跃地写 claims.json 时从别处删条目会打架，
            // 那属于 GC 一整块的事），但必须**看得见**，否则它会静静地骗人（「这个文件有人占着」）。
            //
            // ⚠️ 判据用 `SessionExists`（看**目录**）而不是 `FindSession`（要读得出 spec）：
            // 后者对**损坏的 spec.json** 返回 null ⇒ 一个真实存在、只是配置坏了的会话
            // 会被判成「已消失」，它的声明被误报成 stale。
            var pks = projectKeys.ToList();
            bool SessionAlive(string sessionId) =>
                pks.Any(pk => host.Store.SessionExists(pk, sessionId));

            var claims = projectKeys
                .SelectMany(pk => host.Store.ListSessions(pk)
                    .Select(id => host.Store.TryReadSpec(pk, id)))
                .Where(spec => spec is not null)
                .SelectMany(spec => new ClaimStore(spec!.WorkingDirectory).All())
                .Distinct()
                .Select(c => new
                {
                    c.SessionId, c.ProjectKey, c.Path, c.At,
                    Stale = !SessionAlive(c.SessionId),
                })
                .ToList();

            return Results.Json(claims, WireJson.Options);
        });

        // apiKey 绝不进响应（本机秘密 + CORS *）：只回「有没有」，不回「是什么」。
        app.MapGet("/providers", () =>
            Results.Json(host.Providers.List().Select(p => new
            {
                p.Id, p.Protocol, p.DefaultModel,
                // 端点 / 认证方式 / modality 是配置的一部分，要交出去（头看不到就无从核对）。
                p.Endpoint, p.AuthScheme, p.Modality,
                // 模型清单：会话视图把模型画成下拉，选项就从这里来。null = 没声明（头退回文本框）。
                p.Models,
                // key 只回 HasApiKey（能改不能看）：表单能显示「已配置」而不是空框。
                HasApiKey = p.HasApiKey,
            }), WireJson.Options));

        // 头渲染配置表单的真相来源：合法协议 / auth 都在核心（头不许手抄，曾抄错）。
        // 不交 defaultModel——它没有可信来源；头要预填模型，从 /providers 拿已有 provider 的那个值。
        app.MapGet("/provider-schema", () => Results.Json(new
        {
            Protocols = Protocols.All,
            // ⚠️ 从 `AuthSchemes.All` 读，**不是**手写的 `new[] { … }`——那份加常量时不会跟上。
            AuthSchemes = AuthSchemes.All,
            // ⚠️ 缺省认证方式也要交：不交的话头要么手抄（会漂），要么把下拉留空。
            DefaultAuthScheme = AuthSchemes.Default,
            // ⚠️ 文案与谓词同处一地（`CommandDispatcher.ProviderIdRule`）——别在这里内联，
            // 那份曾经内联在此，改谓词的人看不见它 ⇒ 人话会静默变成假话。
            ProviderIdRule = CommandDispatcher.ProviderIdRule,
        }, WireJson.Options));
    }

    // 一条事件的可渲染形态（核心渲染，头只显示）。
    /// <summary>
    /// 一条记录 → 线上那一份「可渲染的形态」。
    ///
    /// <para>⚠️ <b>必须按事件类型分派，不能强转成 <c>MessageEvent</c></b>（2026-10-06 修）：
    /// 加了 <c>ToolResultEvent</c> 之后，那句强转会让**含工具调用的会话
    /// `/history` 直接 HTTP 500**（实测：盘上 17 行、端点 500）。
    /// 而它正是头的「加载不出会话内容」的原因。</para>
    ///
    /// <para>⚠️ <b>形状上的两处刻意</b>：工具结果**没有 `from`**（它是 user 角色的载荷，
    /// 但那不是「谁说的话」），另给 <c>kind:"toolResult"</c> 与配对的 <c>callId</c>。
    /// ⇒ 头那边不认识这个 kind 时**只是不显示它**，不会崩（`textOf` 按 kind 过滤）。</para>
    /// </summary>
    /// <summary>
    /// `/state` 的三个槽——<b>与请求走同一个来源</b>（`SystemBlock.Render`），头显示什么、
    /// 模型收到什么因此**不可能分叉**（`05` §3：渲染归核心，头只显示）。
    ///
    /// <para>⚠️ <b>三格都可能是空数组，而空数组是<b>一条事实</b></b>：「这个会话没配过那一块」。
    /// 头必须把它显示成缺席（「（空）」），而不是画一张空卡——同 `05` §7.1 那条。
    /// ⚠️ 所以这里**不省略字段、也不用 <c>null</c>**：省略让「空」与「这个核心没实现它」
    /// 在线上**长得一模一样**，而那正是本批要消掉的那个歧义。</para>
    ///
    /// <para>⚠️ <b>首要块今天恒为空</b>（没有插件宿主、没有项目层配置 ⇒ `PremierPrompt` 恒 <c>null</c>
    /// ⇒ `premiers/default.md` 通常不存在）。<b>但这一格是真的</b>：往那个路径放一个文件，
    /// 它当场就有内容，而且是**模型真的收到的那一份**。别把「今天恒空」读成「还没接」。</para>
    ///
    /// <para>⚠️ <b>轴值用 <c>!</c> 是 <c>Load</c> / <c>Create</c> 的契约</b>，与同一响应里
    /// 顶层那个 <c>Axes = handle.State.Axes!</c> **同一份依据**（`CompleteAxes` 保证非空）。
    /// ⇒ 两处都从同一个 handle 读同一件事，不给其中一处编一个「差不多」的默认值——
    /// 那会让头里的两块面板显示两个不同的档位。</para>
    /// </summary>
    private object SlotsOf(SessionHandle handle)
    {
        var values = PluginSessionView.Values(handle.State);
        Lilara.Core.Plugins.PromptContributionSnapshot? contribution = null;
        string? problem = null;
        try
        {
            var frozen = host.Plugins.Freeze(values, CoreClock.Now(), handle.Spec.Plugins ?? []);
            contribution = host.Plugins.Collect(frozen, handle.Spec.Plugins ?? []);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            problem = ex.Message;
        }
        var slots = SystemBlock.Render(
            new PresetStore(host.DataRoot, PresetStore.SystemDir),
            new PresetStore(host.DataRoot, PresetStore.PremierDir),
            handle.Spec.SystemPromptPreset,
            handle.Spec.PremierPrompt,
            values, contribution);

        return new
        {
            Problem = problem,
            System = slots.System,
            Premier = slots.Premier,
            // ⚠️ **线上那一格叫 `floating`（浮动），代码里叫 Tail（尾槽位）**——
            // 两个名字指的是同一块（`01` §2.4「浮动上下文 = 槽位」/ §3.2「尾槽位」）。
            // ⇒ **别把线上那个键改名去迁就代码**：头（`app.js` 的 `renderSlots`）
            // 早就按 `floating` 读了，改名会让那一格**静默变空**。
            Floating = slots.Tail,
        };
    }

    /// <summary>
    /// **把工具结果并进那条模型消息所在的组**。组 = 一条事件（非结果），或
    /// 「模型消息 + 它发出的、且出现在它之后的那些结果」。
    ///
    /// <para>⚠️ 判定只用记录里的**事实**：`toolUse` 的 `callId` 与结果的 `CallId`。
    /// 结果若找不到配对（悬空的，正常不会发生），**自成一组**而不是被前面那组吞掉——
    /// 「有结果没调用」是必须让人看见的异常，不是可以顺手挂到别的消息下面的东西。</para>
    /// </summary>
    private static List<List<Kernel.TimelineEvent>> GroupToolResults(
        IReadOnlyList<Kernel.TimelineEvent> events)
    {
        var grouped = new List<List<Kernel.TimelineEvent>>();
        Dictionary<string, List<Kernel.TimelineEvent>> open = new(StringComparer.Ordinal);

        foreach (var evt in events)
        {
            if (evt is Kernel.TimelineEvent.MessageEvent m)
            {
                var group = new List<Kernel.TimelineEvent> { evt };
                grouped.Add(group);
                // ⚠️ 只登记**模型**消息的调用：用户消息里不会有 `toolUse`（那是模型输出），
                // 但按 speaker 判会漏掉「模型消息里夹着文本 + 调用」这一格——所以按块判，
                // 不按 speaker 判。
                foreach (var call in m.Blocks.OfType<Kernel.Block.ToolUseBlock>())
                    open[call.CallId] = group;
                continue;
            }

            if (evt is Kernel.TimelineEvent.ToolResultEvent r && open.Remove(r.CallId, out var owner))
            {
                owner.Add(evt);
                continue;
            }

            // 其他事件（release / 悬空结果）：自成一组。
            grouped.Add([evt]);
        }

        return grouped;
    }

    private static string EncodeHistoryCursor(string sessionId, string beforeId) =>        Convert.ToBase64String(Encoding.UTF8.GetBytes($"1\n{sessionId}\n{beforeId}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryDecodeHistoryCursor(
        string cursor, out string sessionId, out string beforeId)
    {
        sessionId = "";
        beforeId = "";
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('\n');
            if (parts is not ["1", var sid, var bid] || sid.Length == 0 || bid.Length == 0)
                return false;
            sessionId = sid;
            beforeId = bid;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static object Render(Kernel.TimelineEvent evt) => evt switch
    {
        Kernel.TimelineEvent.MessageEvent m => new
        {
            Kind = "message",
            Id = m.Id,
            From = m.From == Kernel.Speaker.User ? "user" : "model",
            At = m.At,
            Blocks = m.Blocks,
        },

        Kernel.TimelineEvent.ToolResultEvent r => new
        {
            Kind = "toolResult",
            Id = r.Id,
            // ⚠️ 配对 id 交出去：头要拿它跟前一条的 `toolUse` 块对起来。
            CallId = r.CallId,
            At = r.At,
            // ⚠️ 交的是**库里那个地址**，不是内容——`StoreBlock` 的大小可能远超一屏，
            // 而这个端点是给浏览器渲染的。要内容得另走（`03` §9.4 的 `GET /store/{sha256}`，还没做）。
            Blocks = r.Content,
        },

        // ⚠️ **线是给人看的**（2026-10-17）：模型那份渲染会把它挡掉
        // （`TimelineRender.IsRenderable`），而 `/history` 是头的入口——
        // 它必须交出去，否则「模型收过上下文」这件事对用户不存在（`01` §14 #32）。
        Kernel.TimelineEvent.ReleaseEvent line => new
        {
            Kind = "release",
            Id = line.Id,
            At = line.At,
            Scope = line.Scope,
            Summary = line.Summary,
        },

        _ => throw new ArgumentException($"控制面不认识这个事件类型（{evt.GetType().Name}）", nameof(evt)),
    };

    // ---- WS ----

    private void MapWebSocket(WebApplication app) => app.Map("/ws", async (HttpContext ctx) =>
    {
        // Origin 白名单是 apiKey 可选的硬前置：网页能改 endpoint 就能把流量（含 key）转到攻击者主机。
        // 放行「无 Origin」、拒绝别的 origin——浏览器握手必带 Origin，curl / 测试 / CLI 不带。
        // 这是防网页、不是鉴权（非浏览器客户端可伪造 Origin）。
        // localhost 与 127.0.0.1 不是同一个 origin（浏览器这么判），拒 localhost 是刻意的。
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (!string.IsNullOrEmpty(origin)
            && !string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            // 失败要能自解释：响应体说明被接受的是哪个页面（启动横幅里也印了同一个值）。
            // ⚠️ **但这句只有非浏览器客户端看得到**：WS 握手被拒时浏览器**不把响应体交给页面 JS**，
            // 页面只会走 `onclose`。所以横幅才是浏览器用户的自解释渠道——
            // 头那边会去拉 `/info` 比对 `location.origin`，把原因显示在页面上。
            ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await ctx.Response.WriteAsync(
                $"本核心只接受来自 {allowedOrigin} 的页面。若是浏览器页面，"
                + "请对照核心启动横幅里印的那一行（页面本身看不到这句响应体）。");
            return;
        }

        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        await PumpAsync(socket, ctx.RequestAborted);
    });

    // 一条 WS 连接：收命令、发事件。读写各一个循环——写不能在读里做（读在等服务端帧时写不出去）。
    private async Task PumpAsync(WebSocket socket, CancellationToken ct)
    {
        var subs = new Dictionary<string, EventSubscription>(StringComparer.Ordinal);
        var writeLock = new SemaphoreSlim(1, 1);

        async Task SendAsync(WireFrame frame)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes<WireFrame>(frame, WireJson.Options);
            // 串行化写：回执与事件可能同时想写，而 WebSocket.SendAsync 不允许并发调用（会抛）。
            await writeLock.WaitAsync(ct);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally { writeLock.Release(); }
        }

        // 订阅的每条流一个转发循环；seq 由 EventBus 在锁内分配 ⇒ 发出的顺序就是 seq 的顺序。
        var pumps = new List<Task>();
        void Subscribe(string topic, long? since)
        {
            // 重复订阅必须换掉旧的那条，不能直接 return：死掉的订阅占着位置，
            // 该 topic 会从此收不到任何事件（resync 后重新 subscribe 也被挡回去）。
            if (subs.TryGetValue(topic, out var stale))
            {
                stale.Dispose();
                subs.Remove(topic);
            }

            var sub = bus.Subscribe(topic, since);
            subs[topic] = sub;

            pumps.Add(Task.Run(async () =>
            {
                // 跟不上 ⇒ 拉快照重建（唯一的降级路径）。
                if (sub.NeedsResync) { await SendAsync(new WireFrame.ResyncRequired(topic)); return; }

                await foreach (var p in sub.Reader.ReadAllAsync(ct))
                    await SendAsync(new WireFrame.Event(p.Topic, p.Seq, p.Payload));

                if (sub.NeedsResync) await SendAsync(new WireFrame.ResyncRequired(topic));
            }, ct));
        }

        /// <summary>
        /// **摘掉一条订阅**（2026-10-06 加）。
        ///
        /// <para>⚠️ **在此之前核心没有退订**，而 `subs` 是**按 topic** 建的 ⇒ 头切到别的会话时
        /// 旧会话那条订阅**留着**，它的泵一直把那个会话的事件发过来，而头会丢弃它们
        /// ⇒ <b>白占一条泵与整条连接，切得越多积得越多</b>。</para>
        ///
        /// <para>⚠️ **只 `Dispose`、不 `await`**：泵靠 `Reader.ReadAllAsync` 在订阅被释放后
        /// 自然跑完（连接收尾那段的 `finally` 就是这么等的）。这里 await 会把收帧循环**卡住**。</para>
        ///
        /// <para>⚠️ 退订一个**没订过的** topic 是合法的 no-op——头可能重连过、或它的视图
        /// 已经被别处清掉了。抛的话会回一句「帧形状不对」，而那是在说谎。</para>
        /// </summary>
        void Unsubscribe(string topic)
        {
            if (subs.Remove(topic, out var sub)) sub.Dispose();
        }

        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(socket, buffer, ct);
                if (json is null) break;

                await HandleClientFrameAsync(json, Subscribe, Unsubscribe, SendAsync, ct);
            }
        }
        catch (OperationCanceledException) { }   // 头断开了；核心不关心头在不在
        catch (WebSocketException) { }           // 连接层面的断开，不是核心的错误
        finally
        {
            foreach (var sub in subs.Values) sub.Dispose();
            await Task.WhenAll(pumps.Select(p => p.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // 必须回一个 Close 才能完成关闭握手：靠 Dispose 关掉时对端报
                // 「without completing the close handshake」，头的正常断连变成一次异常。
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return null;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return sb.ToString();
        }
    }

    private async Task HandleClientFrameAsync(
        string json, Action<string, long?> subscribe, Action<string> unsubscribe, Func<WireFrame, Task> send, CancellationToken ct)
    {
        // 整帧解析都要在兜底里：头可以整个换掉，一个第三方头发错一帧不该让整条连接一起死掉。
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement.Clone();
            if (!root.TryGetProperty("kind", out var kind)) return;
            var k = kind.GetString();

            switch (k)
            {
                case "subscribe":
                    subscribe(root.GetProperty("topic").GetString()!, OptSince(root));
                    break;

                case "unsubscribe":
                    unsubscribe(root.GetProperty("topic").GetString()!);
                    break;

                case "command":
                    await HandleCommandAsync(root, send, ct);
                    break;

                default:
                    await send(new WireFrame.Ack("?", false, null, $"认不出的帧 kind='{k}'", null));
                    break;
            }
        }
        catch (Exception ex)
        {
            // 畸形帧：回一句人话，连接留着。
            // 不按异常类型白名单赌：种类数不完（如 topic:null 走 ArgumentNullException），
            // 漏一种就连整条连接一起死。
            await send(new WireFrame.Ack("?", false, null, $"帧形状不对：{ex.Message}", null));
        }

        static long? OptSince(JsonElement e) =>
            e.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
    }

    // 回执立刻发，步在后台跑。命令不合法 ⇒ rejected；业务失败 ⇒ error 事件。
    private async Task HandleCommandAsync(JsonElement root, Func<WireFrame, Task> send, CancellationToken ct)
    {
        var id = root.GetProperty("id").GetString()!;
        var payload = root.GetProperty("payload");

        CoreCommand command;
        try { command = ParseCommand(payload); }
        catch (Exception ex)
        {
            await send(new WireFrame.Ack(id, false, null, $"命令形状不对：{ex.Message}", null));
            return;
        }

        try
        {
            var result = await dispatcher.DispatchAsync(command, id, awaitCompletion: false, ct);

            await send(result.Ack switch
            {
                Ack.Rejected r => new WireFrame.Ack(id, false, null, r.Reason, null),
                Ack.Accepted a => new WireFrame.Ack(id, true, a.SessionId, null, a.Note),
                _ => new WireFrame.Ack(id, false, null, "认不出的回执", null),
            });
        }
        catch (DispatchException)
        {
            // 后台模式下 dispatcher 不抛（已自己发过 error 事件）⇒ 这里只回执，不重复发 error。
            // 别在这里补 ResyncRequired——那是流对不上的信号，与「这次调用失败」是两回事。
            await send(new WireFrame.Ack(id, true, null, null, null));
        }
    }

    // ⚠️ `internal` 而不是 `private`：`CommandSurfaceTests` 直接调它来钉「线上每条 kind 都
    // 造出它该造的那个类型」——**调真函数，不在测试里复刻一层判据**（复刻品总比真货少几行，
    // 而少的那几行恰恰是没人在看的）。仅可见性放开，行为不动。
    internal static CoreCommand ParseCommand(JsonElement payload)
    {
        var kind = payload.GetProperty("kind").GetString()!;
        return kind switch
        {
            // 两个字段全走 Opt：**键缺失 = 「没给」**（回落规则在 dispatcher）。
            // 用 GetProperty 会让「只起名、不选目录」这种常见形态抛 KeyNotFoundException。
            "session.create" => new CoreCommand.SessionCreate
            {
                Cwd = Opt(payload, "cwd"),
                Title = Opt(payload, "title"),
                // ⚠️ 数字不是字符串 ⇒ 走 OptNumber（`Opt` 只收字符串，拿它取数字会静默给 null）。
                AskTimeoutMinutes = OptNumber(payload, "askTimeoutMinutes"),
            },
            "session.activate" => new CoreCommand.SessionActivate(payload.GetProperty("sessionId").GetString()!),
            "session.interrupt" => new CoreCommand.SessionInterrupt(payload.GetProperty("sessionId").GetString()!),
            "session.start" => new CoreCommand.SessionStart(payload.GetProperty("sessionId").GetString()!),
            "session.send" => new CoreCommand.SessionSend(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("blocks").Deserialize<List<Kernel.Block>>(WireJson.Options)!),
            // 线上解析是命令的第三条线（dispatcher / CoreCommandParser / 这里）——
            // 少一条，头发的帧会静默落到 `_ => throw`。
            "session.setAxis" => new CoreCommand.SessionSetAxis(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("axis").GetString()!,
                payload.GetProperty("value").GetString()!),
            // 同 session.setAxis：线上解析是第三条线。
            // providerId / model 走 Opt：**键缺失 = 「保留旧值」**（与 provider.upsert 同一条语义）。
            // 用 GetProperty 会在头只想换模型、不提 provider 时抛 KeyNotFoundException。
            "session.configure" => new CoreCommand.SessionConfigure(
                payload.GetProperty("sessionId").GetString()!,
                Opt(payload, "providerId"), Opt(payload, "model"),
                OptNumber(payload, "askTimeoutMinutes")),
            // 同 session.setAxis：线上解析是第三条线。
            // 只有 release、没有 acquire（头的认领走控制台）；字段名是 scope（数组），不是 path。
            "claim.release" => new CoreCommand.ClaimRelease(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("scope").Deserialize<List<string>>(WireJson.Options)!),
            // ---- 销毁面 ----
            "session.deactivate" => new CoreCommand.SessionDeactivate(
                payload.GetProperty("sessionId").GetString()!),
            // ⚠️ `archived` 走 `OptBool` 而不是 GetProperty：**键缺失 = 归档**（三态的中性那一档）。
            // 用 `GetProperty(...).GetBoolean()` 会逼着头每次都显式发一个 true，
            // 而「没给」在这条命令里是**有意义的**（= 归档），不是「用户没填」。
            "session.archive" => new CoreCommand.SessionArchive(
                payload.GetProperty("sessionId").GetString()!, OptBool(payload, "archived")),
            "session.delete" => new CoreCommand.SessionDelete(
                payload.GetProperty("sessionId").GetString()!),
            // 同 session.setAxis：线上解析是第三条线。载荷只有一个会话 id（全会话、无范围）。
            "session.gc" => new CoreCommand.SessionGc(
                payload.GetProperty("sessionId").GetString()!),
            "session.compact" => new CoreCommand.SessionCompact(
                payload.GetProperty("sessionId").GetString()!),
            "provider.upsert" => new CoreCommand.ProviderUpsert(
                payload.GetProperty("id").GetString()!,
                // apiKey 必须走 Opt：GetProperty 在键缺失时抛 KeyNotFoundException，
                // 而线上的「省略」就是键不存在，语义是「保留旧 key」（用 GetProperty 会让头永远改不了已有 provider）。
                // Opt 只认字符串 ⇒ 非字符串值被静默当成没给；与 model/protocol/endpoint/auth 四个兄弟字段一致。
                Opt(payload, "apiKey"),
                Opt(payload, "model"), Opt(payload, "protocol"), Opt(payload, "endpoint"), Opt(payload, "auth"))
            {
                // 模型清单是**数组**，Opt 只认字符串 ⇒ 单独一个助手。
                // 键缺失 / 不是数组 / 数组里有非字符串 ⇒ null（= 保留旧清单），与兄弟字段同一条语义。
                Models = OptStringList(payload, "models"),
            },
            // 同 provider.upsert / claim.release：线上解析是第三条线。
            "provider.delete" => new CoreCommand.ProviderDelete(
                payload.GetProperty("id").GetString()!),
            // 连通性自检。与 provider.delete 同形（只带 id）。
            "provider.test" => new CoreCommand.ProviderTest(
                payload.GetProperty("id").GetString()!),
            // 预设：系统的系统提示词从哪来。三条同 `provider.*` 一族（id 闸也在 dispatcher）。
            // ⚠️ `content` 走 GetProperty（**必给**）——`preset.set` 没有别的字段，
            // 所以「没给 content」只可能是发命令的程序写错了；而「给了空串」是**合法值**（纯 chat）。
            // 用 `Opt` 的话两者会被并成同一个 null ⇒ 把「我要纯聊天」变成一条含义不明的命令。
            "preset.set" => new CoreCommand.PresetSet(
                payload.GetProperty("id").GetString()!,
                payload.GetProperty("content").GetString()!),
            "preset.delete" => new CoreCommand.PresetDelete(
                payload.GetProperty("id").GetString()!),
            "preset.list" => new CoreCommand.PresetList(),
            // 询问：挂起的唯一解除入口（`01` §3.8）。
            // ⚠️ `outcome` / `askId` 走 GetProperty（**必给**）：这条命令没有别的用途，
            // 缺了它俩只可能是发命令的程序写错了。而 `answers` 与 `remember` 走 Opt 形状——
            // 前者可以整个不给（cancel 就是「不答」），后者不给 = **只这一次**。
            "approval.respond" => new CoreCommand.ApprovalRespond(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("askId").GetString()!,
                payload.GetProperty("outcome").GetString()!,
                OptStringMap(payload, "answers"),
                Opt(payload, "remember")),
            _ => throw new ArgumentException($"认不出的命令 '{kind}'"),
        };

        // 字符串→字符串的映射版。⚠️ 与 `Opt` 一样「只认形状对的」：
        // 键缺失 / 不是对象 / 值不是字符串 ⇒ 空字典（对 `answers` 就是「什么都没答」）。
        // ⚠️ **值不是字符串的那一项被丢掉、其余照收**——这与 `OptStringList` 的取舍一致
        // （那里也是「有一个坏的整条算没给」……不，那里是整条 null）。这里取**逐项**：
        // 用户答的是一组独立的问题，一个坏值不该让其余答案全丢。
        static IReadOnlyDictionary<string, string> OptStringMap(JsonElement e, string name)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return map;

            foreach (var p in v.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString()!;
            }

            return map;
        }

        static string? Opt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // 数字版。⚠️ **只认 JSON 数字**——字符串 `"5"` 一律当「没给」，同上面 `Opt` 那条粒度取舍
        // （`"false"` 不被当成布尔 false）。⚠️ 用它取数字而不是 `Opt`：
        // `Opt` 只收字符串，拿它去取一个数字键会**静默给 null**——而那个 null 的含义是
        // 「用户没设超时」，即一次写错类型的参数变成一次静默的「没有上限」。
        static double? OptNumber(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var n) ? n : null;

        // 布尔版。⚠️ **只认 `true` / `false` 两个字面量**，其余（缺失 / null / 字符串 `"false"` /
        // 数字）一律当「没给」⇒ 在 `session.archive` 上就是**归档**。
        // 把 `"false"` 也认成 false 会开一个「前端传了个字符串也能改语义」的口子，
        // 而 JSON 里布尔本来就有它自己的形状。与 `Opt` / `OptStringList` 同一个粒度取舍。
        static bool? OptBool(JsonElement e, string name) => e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

        // 字符串数组版。⚠️ 与 `Opt` 一样「只认形状对的」：键缺失 / 不是数组 / 元素不是字符串 ⇒ null。
        // 元素不是字符串这一条**不**报错而是整份当没给——与 `Opt` 的取舍一致（`provider.upsert`
        // 的兄弟字段都是这个粒度），且「头传了半份清单」这种形状不会比「没传」更可信。
        static IReadOnlyList<string>? OptStringList(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;

            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                list.Add(item.GetString()!);
            }
            return list;
        }
    }

    private bool TryFind(string sessionId, out SessionHandle handle)
    {
        if (host.TryGetActivated(sessionId, out handle!)) return true;

        // ⚠️ **miss 时必须回落 `Activate`，不许在这里交磁盘那份。**
        // `SessionQueries.StateOf` / `CoreHost.TryGetActivated` / `/state` 三处都写着同一条：
        // 步进行中**磁盘上是 `Idle`**（`Running` 只写内存、到步末才落盘）。
        // 少了这条回落，一个刚被 `session.deactivate` 摘掉的会话——而头那一侧看着它还在跑——
        // 会被渲染成「空闲」，用户以为没在跑、**再点一次发送** ⇒ 队列两条 ⇒ 烧钱。
        if (host.Store.FindSession(sessionId) is not { } found) return false;

        handle = host.Activate(found.ProjectKey, sessionId);
        return true;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsWithinDirectory(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return string.Equals(path, root, PathComparison)
            || normalizedPath.StartsWith(normalizedRoot, PathComparison);
    }

    private static bool HasReparsePointBetween(DirectoryInfo current, DirectoryInfo root)
    {
        for (var dir = current; dir is not null; dir = dir.Parent)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(dir.FullName, root.FullName, PathComparison)) return false;
        }
        return true;
    }

    private IReadOnlyList<string> ProjectKeys() => host.Store.ListProjects();
}

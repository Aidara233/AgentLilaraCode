using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Records;
using Lilara.Core.Session;

namespace Lilara.Core.Console;

/// <summary>
/// 命令面（04 §1.1）。⚠️ **它不是头**：同步收发、没有视图、不订阅、不流式渲染。
/// 命令名沿用控制面协议（03 §3），只是换了个本地入口。
/// </summary>
public sealed class Repl(
    SessionStore store, ProviderStore providers, Stepper stepper, string workingDirectory, TextWriter output)
{
    private readonly SessionStore _store = store;
    private readonly ProviderStore _providers = providers;
    private readonly Stepper _stepper = stepper;
    private readonly string _workingDirectory = workingDirectory;
    private readonly TextWriter _out = output;

    /// <summary>
    /// ⚠️ 核心**不自动建会话**（01 §8.1）。这个字段由 `session create` 填，
    /// 或者由 Program.cs 在启动时「选最近的一条」填。
    /// </summary>
    public SessionHandle? Current { get; set; }

    /// <summary>
    /// 「这条命令可能改变提供商集合吗」——Program.cs 的记忆化判据。
    /// ⚠️ 必须是**同一个**判据，不能各写一份字符串：
    /// 两处各写 `"provider set "` 的话，改命令名只会改到一处，
    /// 记忆化就**静默**失效——用户看到「已保存」之后又被回一句「还没有配置提供商」。
    /// 共享判据而不是共享字面量，是因为调用方要的本来就是「是不是这条命令」这个判断。
    /// ⚠️ 自己 trim：调用方可以直接把原始行（可能带空白）递进来。
    /// </summary>
    public static bool IsProviderSetCommand(string line) =>
        line.Trim().StartsWith("provider set ", StringComparison.Ordinal);

    public async Task<bool> ExecuteAsync(string line, CancellationToken ct)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return true;

        try
        {
            if (trimmed is "quit" or "exit") return false;

            if (IsProviderSetCommand(trimmed))
            {
                HandleProviderSet(trimmed);
                return true;
            }

            if (trimmed == "session create")
            {
                HandleSessionCreate();
                return true;
            }

            if (trimmed == "history")
            {
                HandleHistory();
                return true;
            }

            if (trimmed == "status")
            {
                HandleStatus();
                return true;
            }

            await HandleMessageAsync(trimmed, ct);
            return true;
        }
        catch (ModelCallFailedException ex)
        {
            // 一行人话，REPL 继续活着（Review Focus #6）
            _out.WriteLine($"✗ {ex.Message}");
            return true;
        }
    }

    private void HandleProviderSet(string line)
    {
        // provider set <id> --key <k> [--model <m>] [--protocol <p>] [--endpoint <u>] [--auth <a>]
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = parts[2];

        string? key = null, model = null, protocol = null, endpoint = null, auth = null;
        // ⚠️ 从 3 起、每次跳两格：标志与值成对。
        // 从 3 起是因为 parts[1] == "set"。
        //
        // ⚠️ **判据是「值长得像不像标志位」，不是「位置上像不像」**（实测三组输入后定的形状）：
        //   · `--key --model`    → 值缺失       → 必须报错
        //   · `--key --model m`  → key 缺值     → 必须报错（**这条最要紧**）
        //   · `--key k --model m`→ 正常
        // 少了这个守卫，第二组会被静默读成 `key=--model` 并把 `--model m` 整个丢掉。
        // 而**不能**改成「跳过长得像标志的位置」（初版写法）——那会把第一组读成 `key=--model`，
        // 白白接住一个明显是笔误的值。两者差别只在上面那两组输入上显形。
        for (var i = 3; i < parts.Length - 1; i += 2)
        {
            var value = parts[i + 1];
            if (value.StartsWith("--", StringComparison.Ordinal))
            {
                _out.WriteLine($"参数 {parts[i]} 缺少值。");
                return;
            }

            switch (parts[i])
            {
                case "--key": key = value; break;
                case "--model": model = value; break;
                case "--protocol": protocol = value; break;
                case "--endpoint": endpoint = value; break;
                case "--auth": auth = value; break;
            }
        }

        if (key is null)
        {
            _out.WriteLine("用法：provider set <id> --key <key> [--model <model>] "
                + $"[--protocol <{string.Join("|", Protocols.All)}>] [--endpoint <url>] "
                + $"[--auth {AuthSchemes.ApiKey}|{AuthSchemes.Bearer}]");
            return;
        }

        // 协议必须是已知的——否则工厂会抛，而那时已经落盘了
        if (protocol is not null && !Protocols.All.Contains(protocol))
        {
            _out.WriteLine($"未知协议 '{protocol}'。支持：{string.Join(" / ", Protocols.All)}");
            return;
        }

        // ⚠️ --auth 同样必须校验，否则是**静默改行为**：`AuthSchemes.Bearer` 的字面量是
        //     "bearer"（小写），而 `OpenAiHttp` 里判的是 `AuthScheme == Bearer ? Bearer : x-api-key`。
        //     于是手写 `--auth Bearer`（大写）序号不等，会被**悄悄**当成 x-api-key 发出去。
        //     未知值必须拒绝且不落盘。空 = 沿用旧值。
        if (!string.IsNullOrEmpty(auth)
            && auth != AuthSchemes.ApiKey && auth != AuthSchemes.Bearer)
        {
            _out.WriteLine($"未知认证方式 '{auth}'。支持：{AuthSchemes.ApiKey} / {AuthSchemes.Bearer}");
            return;
        }

        // ⚠️ 先读旧定义，**保留本次没提到的字段**。
        // 整文件覆盖会抹掉手写配置：Endpoint / Modality / DefaultModel / Protocol
        // 都是操作者配置，本次没提到就必须活下来，否则「改 key」会静默重置它们。
        var existing = _providers.Load(id);

        _providers.Save(new ProviderDefinition
        {
            Id = id,
            // ⚠️ 操作者在**已登记且已校验**的协议集合里选（上面的 `Protocols.All.Contains`）；
            //     手写一个工厂认不出的协议的旧顾虑，在这里已由「落盘前拒绝未知协议」挡住——
            //     所以 Protocol 不再「始终由代码决定」。
            //     缺省仍是 `claude-messages`（不破坏既有用法）；已配好的协议则原样保留。
            Protocol = protocol ?? existing?.Protocol ?? Protocols.ClaudeMessages,
            ApiKey = key,
            DefaultModel = model ?? existing?.DefaultModel ?? "claude-opus-5",
            Modality = existing?.Modality ?? "vision",
            Endpoint = endpoint ?? existing?.Endpoint,
            AuthScheme = string.IsNullOrEmpty(auth) ? existing?.AuthScheme : auth,
        });

        _out.WriteLine($"✓ 提供商 '{id}' 已保存（{protocol ?? existing?.Protocol ?? Protocols.ClaudeMessages}）。");
    }

    /// <summary>
    /// 控制面 `session.create` 的本地入口（03 §3）。
    /// ⚠️ **这是显式动作**——核心不会替你决定该有个会话。
    /// </summary>
    private void HandleSessionCreate()
    {
        var provider = _providers.List().FirstOrDefault();
        if (provider is null)
        {
            _out.WriteLine("先配提供商：provider set <id> --key <key>");
            return;
        }

        var model = _providers.ToSelection(provider, null, "high", 16000);
        Current = SessionHandle.Create(_store, _workingDirectory, model);
        _out.WriteLine($"✓ 会话 {Current.SessionId} 已创建。");
    }

    private void HandleHistory()
    {
        if (Current is null)
        {
            _out.WriteLine("还没有会话。用 `session create` 建一个。");
            return;
        }

        var timeline = _store.ReadTimeline(Current.ProjectKey, Current.SessionId);
        if (timeline.Count == 0)
        {
            _out.WriteLine("（历史为空）");
            return;
        }

        foreach (var evt in timeline)
        {
            var message = (TimelineEvent.MessageEvent)evt;
            if (message.From == Speaker.User)
            {
                var userText = string.Concat(
                    message.Blocks.OfType<Block.TextBlock>().Select(b => b.Text));
                _out.WriteLine($"你  {userText}");
                continue;
            }

            _out.WriteLine($"⏺  {ModelBody(message.Blocks)}");
        }
    }

    private void HandleStatus()
    {
        if (Current is null)
        {
            _out.WriteLine("还没有会话。用 `session create` 建一个。");
            return;
        }

        _out.WriteLine($"会话 {Current.SessionId} · {Current.State.Status} · 第 {Current.State.Turn} 步");
        _out.WriteLine($"模型 {Current.Spec.Model.ModelId} · effort {Current.Spec.Model.Effort}");
        _out.WriteLine($"目录 {Current.Spec.WorkingDirectory}");
    }

    private async Task HandleMessageAsync(string text, CancellationToken ct)
    {
        if (Current is null)
        {
            // 01 §8.1：核心不替你建会话——先要提供商，再要一条显式命令。
            // ⚠️ 两句话**合成一句**说：只有「provider 缺失」这一条分支时，
            // 没配提供商的人看不到下一步该敲 `session create`，反之亦然。
            // 引导必须一次把两步都讲完，否则用户会卡在回环里。
            _out.WriteLine(_providers.List().Count == 0
                ? "还没有配置提供商。先执行 `provider set <id> --key <key>`，再执行 `session create` 建会话。"
                : "还没有会话。执行 `session create` 建一个。");
            return;
        }

        var reply = await _stepper.StepAsync(Current, text, ct);

        if (reply.Refused)
        {
            _out.WriteLine("⚠️ 模型拒绝了这次请求（stop_reason: refusal）。");
            return;
        }

        _out.WriteLine($"⏺  {ModelBody(reply.Blocks)}");

        if (reply.StopReason == "max_tokens")
            _out.WriteLine("（输出被 max_tokens 截断了）");
    }

    /// <summary>
    /// 助手回合的可显示正文。
    /// ⚠️ 回复可能**只有推理块、没有正文**（Fix 1 之后推理块确实会落盘），
    /// 于是正文是空串——不能印成一个光秃秃的 `⏺  `，那看起来像渲染坏了。
    /// 推理块的 Text 只是摘要，**显示与否是显示层的事，块本身不能因此丢**（00 §5 铁律）。
    /// </summary>
    private static string ModelBody(IReadOnlyList<Block> blocks)
    {
        var text = string.Concat(blocks.OfType<Block.TextBlock>().Select(b => b.Text));
        return text.Length == 0 ? "（这一步没有正文输出）" : text;
    }
}

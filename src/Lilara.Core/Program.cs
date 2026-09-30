using Lilara.Core.Adapters;
using Lilara.Core.Console;
using Lilara.Core.Persistence;
using Lilara.Core.Projects;
using Lilara.Core.Providers;
using Lilara.Core.Session;

var dataRoot = DataRoot.Resolve();
Directory.CreateDirectory(dataRoot);

var store = new SessionStore(dataRoot);
var providers = new ProviderStore(dataRoot);
var workingDirectory = Directory.GetCurrentDirectory();

Console.WriteLine($"LILARA — 数据根 {dataRoot}");

// ⚠️ 核心启动时**不建任何会话**（01 §8.1）。
// 这里只做一件事：把当前项目下最近的会话选出来当"当前会话"。
// 「配置完提供商自动建会话」是头的行为——控制台要建，得显式敲 `session create`。
var projectKey = ProjectKey.FromPath(workingDirectory);
var existing = store.ListSessions(projectKey);

SessionHandle? resume = null;

if (existing.Count > 0)
{
    var handle = SessionHandle.Load(store, projectKey, existing[0]);
    var provider = providers.Load(handle.Spec.Model.ProviderId);

    if (provider is null)
    {
        Console.WriteLine("  会话引用的提供商已不存在。用 `provider set` 重新配一个。");
    }
    else
    {
        resume = handle;
        Console.WriteLine($"  当前会话 {handle.SessionId}（第 {handle.State.Turn} 步）");
    }
}
else
{
    Console.WriteLine("  还没有会话。");
}

var repl = new Repl(
    store,
    providers,
    ResolveStepper(store, providers, resume),
    workingDirectory,
    Console.Out)
{
    Current = resume,
};

Console.WriteLine("  `provider set <id> --key <key>` 配置；`session create` 建会话；`history` 回放；`quit` 退出。");

while (true)
{
    Console.Write("lilara> ");
    var line = Console.ReadLine();
    if (line is null) break;

    // ⚠️ 适配器在 Repl 构造时固定，而 `provider set` 只写 store——
    // 所以每次派发前都从 store 重解析一次。否则「全新跑 → provider set → 发消息」
    // 这条最常见的首次使用路径会撞上占位适配器，而 store 明明已经写好了。
    // ⚠️ 边界：`session create` 会把当时的选择**快照**进 spec，所以换 provider 的
    // --model / effort 对**已建的会话**不生效——那需要新建会话（01 §7.2）。
    var current = repl.Current;
    repl = new Repl(store, providers, ResolveStepper(store, providers, current), workingDirectory, Console.Out)
    {
        Current = current,
    };

    if (!await repl.ExecuteAsync(line, CancellationToken.None)) break;
}

/// <summary>
/// 从 store 现解析一个 Stepper：有当前会话就按会话引用的 provider 解析，
/// 否则取 store 里的第一个 provider；一个都没有就用占位适配器。
/// ⚠️ 抽成函数是因为它必须在**每次派发前**跑，而不是启动时定死一次。
/// </summary>
static Stepper ResolveStepper(SessionStore store, ProviderStore providers, SessionHandle? current)
{
    var provider = current is null
        ? providers.List().FirstOrDefault()
        : providers.Load(current.Spec.Model.ProviderId) ?? providers.List().FirstOrDefault();

    return provider is null
        ? new Stepper(store, new UnconfiguredAdapter())
        : new Stepper(store, AdapterFactory.Create(provider));
}

/// <summary>
/// 没有可用适配器时的占位。
/// ⚠️ 它存在是为了让 REPL **能起来**——没配提供商时你仍然要能收
/// `provider set` 和 `session create`，否则引导会自锁。
/// </summary>
file sealed class UnconfiguredAdapter : IModelAdapter
{
    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
        throw new ModelCallFailedException("还没有配置提供商。");
}

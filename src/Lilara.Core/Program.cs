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
        // ⚠️ 要点名缺的是哪个提供商——只说「已不存在」，用户不知道该重配哪一条。
        Console.WriteLine(
            $"  会话引用的提供商 '{handle.Spec.Model.ProviderId}' 已不存在。用 `provider set` 重新配一个。");
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

// ⚠️ 适配器在 Repl 构造时固定，而 `provider set` 只写 store——
// 所以「该用哪个适配器」必须从 store 现解析（`StepperResolver`），
// 而不是启动时定死一次。否则「全新跑 → provider set → 发消息」这条最常见的
// 首次使用路径会撞上占位适配器，而 store 明明已经写好了。
var repl = new Repl(
    store,
    providers,
    StepperResolver.Resolve(store, providers, resume?.Spec.Model.ProviderId),
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

    if (!await repl.ExecuteAsync(line, CancellationToken.None)) break;

    // ⚠️ 只有 `provider set` 会动 store，所以只在它之后重解析。
    // 每敲一行都重建 Repl 会为 `history` / `status` 这种碰不到模型的命令白 new 一个
    // AnthropicClient。判据**共用** `Repl.IsProviderSetCommand`——两处各写一份字符串的话，
    // 改命令名只会改到一处，记忆化就**静默**失效（详见该方法的注释）。
    // 这里递原始行即可：谓词自己会 trim。
    // ⚠️ 边界：`session create` 会把当时的选择**快照**进 spec，所以换 provider 的
    // --model / effort 对**已建的会话**不生效——那需要新建会话（01 §7.2）。
    if (Repl.IsProviderSetCommand(line))
    {
        var current = repl.Current;
        repl = new Repl(
            store,
            providers,
            StepperResolver.Resolve(store, providers, current?.Spec.Model.ProviderId),
            workingDirectory,
            Console.Out)
        {
            Current = current,
        };
    }
}

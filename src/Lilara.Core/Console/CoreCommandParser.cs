using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Hosting;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Console;

// 把一行人写的文本解析成 CoreCommand。`--flag` 是人写风格的控制台语法，搬进 dispatcher
// 会泄漏到线上协议；语义校验（未知协议 / auth）留在 dispatcher，线上也要吃。
// 返回错误字符串而不是打印：打印是前端形态，头不该继承控制台的措辞。
public static class CoreCommandParser
{
    // `claim list` 是控制台的查询，不是核心命令（不做 claim.list）：解析器返回 (null,null)，
    // 由 Repl 拦截处理。判据必须由 Repl 与解析器共用——两边各写一份会漂
    //（大小写 / 空白口径不同 ⇒ claim LIST 落进 AsMessage，被当成消息发给模型）。
    public static bool IsClaimList(string line)
    {
        if (!IsClaimVerb(line)) return false;

        var t = line.Trim();
        var words = t["claim".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 1 && string.Equals(words[0], "list", StringComparison.OrdinalIgnoreCase);
    }

    // 「这一行以 claim 打头、后面是分隔符或行尾」——claim 词块入口的判据
    //（由 IsClaimList 与 Parse 的 claim 分支共用）。边界用 char.IsWhiteSpace（宽）：
    // 窄口径下 claim + NBSP + 路径会两边都不认 ⇒ 当成消息发给模型。
    public static bool IsClaimVerb(string line) => StartsWithToken(line, "claim");

    // 「以 token 打头、且它后面是分隔符或行尾」——claim 与 axis 两个词块入口共用的判据。
    // 首词大小写不敏感：放宽只是多认一条命令（可见），收紧会让这行字静默变成消息（不可见）。
    // token 之后必须是空白，否则 claimlist / axisx 也会被吞掉。
    private static bool StartsWithToken(string line, string token)
    {
        var t = line.Trim();
        if (t.Length < token.Length) return false;
        if (!t.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return false;

        return t.Length == token.Length || char.IsWhiteSpace(t[token.Length]);
    }

    // 「以 axis 打头、第二个词是 set」——axis set 词块入口的判据（首词走共用的 StartsWithToken）。
    // axis settings 落回「普通消息」：set 之后切出的第一个词是 settings 而非 set。
    // 放宽的只有动词大小写——轴名与取值仍由 dispatcher 里的 Axes.TryParse 校验并规范化。
    private static bool IsAxisSetVerb(string line)
    {
        // 首词走共用的 StartsWithToken（与 claim 同一份判据），别再单写一遍。
        if (!StartsWithToken(line, "axis")) return false;

        var words = line.Trim()["axis".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 1 && string.Equals(words[0], "set", StringComparison.OrdinalIgnoreCase);
    }

    // 返回 (command, error)：命令非 null；解析失败则 error 非 null；
    // 两者都 null = 不是命令形态的行，调用方按普通消息处理。
    // currentSessionId 是控制台专有的输入：claim src/foo 里没有会话 id，由控制台补上当前会话。
    public static (CoreCommand? Command, string? Error) Parse(string line, string? currentSessionId = null)
    {
        var trimmed = line.Trim();

        if (trimmed.StartsWith("session create", StringComparison.Ordinal)) return (new CoreCommand.SessionCreate(), null);

        if (trimmed.StartsWith("session activate ", StringComparison.Ordinal))
            return (new CoreCommand.SessionActivate(trimmed["session activate ".Length..].Trim()), null);

        if (trimmed.StartsWith("session interrupt ", StringComparison.Ordinal))
            return (new CoreCommand.SessionInterrupt(trimmed["session interrupt ".Length..].Trim()), null);

        // 动词判据走宽口径切词：字面前缀只认空格，axis<TAB>set / axis set<NBSP> 会掉进兜底，
        // 被当成消息发给会话（用户以为设了轴，实际烧了一次模型调用、轴没设）。
        if (IsAxisSetVerb(trimmed))
            return ParseAxisSet(trimmed);

        // 光一个 `axis` 不能落回「当成消息」——用户以为在设轴（`axis set` 已被上面截住）。
        if (string.Equals(trimmed, "axis", StringComparison.OrdinalIgnoreCase))
            return (null, "用法：axis set <会话 id> <轴> <值>");

        if (trimmed.StartsWith("provider set ", StringComparison.Ordinal))
            return ParseProviderSet(trimmed);

        // 控制台语法，与 `provider set` 对称：否则会造出「配得进、删不掉」的形状（只能手删文件）。
        if (trimmed.StartsWith("provider delete ", StringComparison.Ordinal))
            return ParseProviderDelete(trimmed);

        // 光一个 `provider delete` 不能落回「当成消息」——用户以为删了，而 provider 还在盘上。
        if (string.Equals(trimmed, "provider delete", StringComparison.Ordinal))
            return (null, "用法：provider delete <id>");

        if (IsClaimList(trimmed)) return (null, null);

        // 入口判据与 IsClaimList 共用 IsClaimVerb（宽口径边界也由它统一管）：
        // 这里只查位置不查前缀时，任何首词恰好 5 个字符的普通消息都会被静默变成 claim.acquire。
        if (IsClaimVerb(trimmed))
        {
            // 切词用 Split(null)：所有空白（空格、Tab、NBSP、连续多个）都算分隔符。
            var words = trimmed["claim".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // 动词大小写不敏感：claim Release src 应当做对的事，而不是落进 acquire 占一个怪路径。
            if (words.Length == 0)
                return (null, "用法：claim <路径> | claim release <路径> | claim list");

            // 走到这里只剩「多词」形态（单词的 claim list 已被上面的 IsClaimList 截走）。
            if (string.Equals(words[0], "list", StringComparison.OrdinalIgnoreCase))
                return (null, "用法：claim <路径> | claim release <路径> | claim list");

            if (string.Equals(words[0], "release", StringComparison.OrdinalIgnoreCase))
                return words.Length == 2
                    ? WithSession(currentSessionId, words[1],
                        (sid, p) => new CoreCommand.ClaimRelease(sid, [p]))
                    : (null, "用法：claim release <路径>");

            // 路径只收一个词：控制台没有引号语法，拼多余词是猜，猜错的方向是静默占错地方 ⇒
            // 宁可报错（可见），不猜（静默）。
            return words.Length == 1
                ? WithSession(currentSessionId, words[0],
                    (sid, p) => new CoreCommand.ClaimAcquire(sid, [p]))
                : (null, "路径不能含空格（控制台没有引号语法）。用一个词，或用绝对路径。");
        }

        return (null, null);

        static (CoreCommand?, string?) WithSession(
            string? sid, string path, Func<string, string, CoreCommand> make) =>
            sid is null
                ? (null, "还没有选中会话。先 `session create` 或 `session activate <id>`。")
                : (make(sid, path), null);
    }

    // `axis set <sessionId> <axis> <value>`：token 数必须正好 5（切词用 Split(null)，
    // Tab / NBSP 与空格等价，与入口判据同一口径）。少了或多了都报错，
    // 不静默取前三个、也不落回「当成消息」。
    private static (CoreCommand?, string?) ParseAxisSet(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return (null, "用法：axis set <会话 id> <轴> <值>   （轴：" + string.Join("|", Axes.Names)
                + "；取值见 `status`）");

        return (new CoreCommand.SessionSetAxis(parts[2], parts[3], parts[4]), null);
    }

    // `provider set <id> [--key <k>] [--model <m>] [--protocol <p>] [--endpoint <u>] [--auth <a>]`
    // 这里只管拆词：省略 --key ⇒ null，语义是「保留旧 key」；
    // 「新 provider 省 key 要拒」的守卫在 dispatcher，不在这里。
    private static (CoreCommand?, string?) ParseProviderSet(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = parts[2];

        string? key = null, model = null, protocol = null, endpoint = null, auth = null;

        // 从 3 起每次跳两格（标志与值成对；parts[1] == "set"）。
        // 守卫是「值长得像标志位」而非「位置上像」：--key --model m 里 key 缺值必须报错，
        // 否则 --model 会被当成 key 的值、且 --model m 整个被静默丢掉。
        // 上界 parts.Length 与下面的末尾检查缺一不可：上界 -1 会让末尾标志缺值静默保存（并回 ✓），
        // 不加末尾检查则 parts[i+1] 越界、抛异常打死常驻进程。
        for (var i = 3; i < parts.Length; i += 2)
        {
            if (i + 1 >= parts.Length) return (null, $"参数 {parts[i]} 缺少值。");

            var value = parts[i + 1];

            // 值被下一个标志占了位置 ⇒ 报错。由此「值以 -- 开头」不再可能——故意的取舍。
            if (value.StartsWith("--", StringComparison.Ordinal)) return (null, $"参数 {parts[i]} 缺少值。");

            switch (parts[i])
            {
                case "--key": key = value; break;
                case "--model": model = value; break;
                case "--protocol": protocol = value; break;
                case "--endpoint": endpoint = value; break;
                case "--auth": auth = value; break;

                // 未知标志必须拒绝：拼错的 --protcol 会被静默吞掉、照常保存并回 ✓，协议悄悄落成默认。
                default: return (null, $"未知参数 '{parts[i]}'。");
            }
        }

        // 这里不要求 --key：「新 provider 省 key 要拒」的语义在 dispatcher，别在这里再写一遍。
        return (new CoreCommand.ProviderUpsert(id, key, model, protocol, endpoint, auth), null);
    }

    // `provider delete <id>`：id 合法性由 dispatcher 判（控制台与头共用同一条判据），
    // 这里只判词数——多一个或少一个都报错、不猜（猜错的方向是删错东西）。
    private static (CoreCommand?, string?) ParseProviderDelete(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 3
            ? (new CoreCommand.ProviderDelete(parts[2]), null)
            : (null, "用法：provider delete <id>");
    }
}

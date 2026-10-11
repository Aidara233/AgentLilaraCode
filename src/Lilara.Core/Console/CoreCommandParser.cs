using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Hosting;

namespace Lilara.Core.Console;

// 把一行人写的文本解析成 CoreCommand。`--flag` 是人写风格的控制台语法，搬进 dispatcher
// 会泄漏到线上协议；语义校验（未知协议 / auth）留在 dispatcher，线上也要吃。
// 返回错误字符串而不是打印：打印是前端形态，头不该继承控制台的措辞。
public static class CoreCommandParser
{
    // `claim list` 是控制台的查询，不是核心命令（不做 claim.list）：解析器返回 (null,null)，
    // 由 Repl 拦截处理。判据必须由 Repl 与解析器共用——两边各写一份会漂
    //（大小写 / 空白口径不同 ⇒ claim LIST 落进 AsMessage，被当成消息发给模型）。
    public static bool IsClaimList(string line) => IsListVerb(line, "claim");

    // `provider list` / `session list` 同 `claim list`：控制台本地查询，解析器返回 (null,null)。
    // ⚠️ 挡在 `Parse` 之前是**必须的**——不挡的话这些行走完 `Parse` 得到 (null,null)，
    // 而 `Repl` 会把 (null,null) 当成「不是命令」⇒ 当普通消息发给模型（白烧一次调用、什么都没发生）。
    public static bool IsProviderList(string line) => IsListVerb(line, "provider");
    public static bool IsSessionList(string line) => IsListVerb(line, "session");

    /// <summary>
    /// `fetch &lt;id&gt;`——控制台**唯一有参数**的本地查询（`01` §8.0.2 六）。
    ///
    /// <para>⚠️ <b>它为什么是本地查询而<b>不是</b>核心命令</b>：取回是**只读**的
    /// （读 `store/&lt;sha256&gt;` 原样交回），而「HTTP 只读快照、改走 WS 命令」
    /// 那条边界分的是**写**。⇒ 它与 `history` / `claim list` 同一档，
    /// 不必长成一条带 ack / 事件 / 四条线的命令。</para>
    ///
    /// <para>⚠️ <b>但「不是命令」不等于「不判」</b>：把 <c>fetch abc</c> 当成普通消息
    /// 发给模型是**看得见的**错（模型收到一句莫名其妙的话），而把
    /// <c>fetch --help</c> 当成 id 是**看不见的**错（用户以为工具坏了）。
    /// ⇒ 判据：**恰好两个词**，多一个少一个都落回「当消息」——那是可看见的处理。</para>
    /// </summary>
    public static string? FetchId(string line)
    {
        if (!StartsWithToken(line, "fetch")) return null;

        var words = line.Trim()["fetch".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return words.Length == 1 ? words[0] : null;
    }

    // 三条本地查询共用的判据：「以 token 打头、后面**恰好一个词**且是 list」。
    // 走 StartsWithToken（宽口径边界）⇒ provider<TAB>list / provider list<NBSP> 都认。
    private static bool IsListVerb(string line, string token)
    {
        if (!StartsWithToken(line, token)) return false;

        var words = line.Trim()[token.Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 1 && string.Equals(words[0], "list", StringComparison.OrdinalIgnoreCase);
    }

    // 「这一行以 claim 打头、后面是分隔符或行尾」——claim 词块入口的判据
    //（由 IsClaimList 与 Parse 的 claim 分支共用）。边界用 char.IsWhiteSpace（宽）：
    // 窄口径下 claim + NBSP + 路径会两边都不认 ⇒ 当成消息发给模型。
    public static bool IsClaimVerb(string line) => StartsWithToken(line, "claim");

    // 把所有「空白串」折叠成**单个普通空格**。`Parse` 入口调它一次，于是下面那一排
    // 字面前缀判据与各种切词器**口径一致**（空格 / Tab / NBSP 等价，与 `StartsWithToken`
    // 的 `char.IsWhiteSpace` 同义），而不必逐条去放宽谓词。
    //
    // ⚠️ 只折叠**空白**，不动别的字符：消息正文不经过这里（调用方用的是自己那份 `trimmed`），
    // 所以即便将来有人把消息喂进来也不会被改写。
    // ⚠️ 不用 `string.Join(' ', Split((char[]?)null))`：那样要为每行分配一个数组；
    // 这里的行都很短，直接扫一遍更省事，也顺手避免了对空行的特判（空行返回空串）。
    private static string Normalize(string line)
    {
        var sb = new StringBuilder(line.Length);
        var inWhitespace = false;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
            {
                // 连续空白折叠成一个；开头的空白也一并丢掉（等价于原来的 `Trim`）。
                if (!inWhitespace && sb.Length > 0) sb.Append(' ');
                inWhitespace = true;
            }
            else
            {
                sb.Append(ch);
                inWhitespace = false;
            }
        }
        return sb.ToString();
    }

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

    // 「以 `session` 打头、第二个词恰好是 `create`」——`session create` 词块入口的判据。
    // 首词走共用的 `StartsWithToken`（与 claim / axis 同一份判据），第二个词单独切。
    // ⚠️ 判据是**相等不是前缀**：`session creates foo` 必须落回「当消息」——写成
    // `StartsWith("session create")` 会让它悄悄变成一条建会话命令，而用户以为在发消息。
    // （这就是本批加测试时发现的既有缺陷；`session activate` 那几条靠尾随空格躲过了它。）
    private static bool IsSessionCreateVerb(string line)
    {
        if (!StartsWithToken(line, "session")) return false;

        // 0 个词 = 光杆 `session`：由 Parse 里那条用法提示接走，这里不认它。
        var words = line.Trim()["session".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return words.Length >= 1 && string.Equals(words[0], "create", StringComparison.OrdinalIgnoreCase);
    }

    // 「以 provider 打头、第二个词是 set」——`provider set` 词块入口的判据（首词走共用的 StartsWithToken）。
    // ⚠️ **`Repl` 也用它**（决定 `provider set` 之后要不要重印端点那一行）：两边各写一份会漂，
    // 而**漂的那一边是静默的**——`provider<TAB>set` 若在这里认、在 `Repl` 不认，
    // 用户就看不到「端点刚刚被环境变量接管了」那行字（「别静默接管」那条硬约束正是靠它兑现的）。
    public static bool IsProviderSetVerb(string line)
    {
        if (!StartsWithToken(line, "provider")) return false;

        var words = line.Trim()["provider".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 1 && string.Equals(words[0], "set", StringComparison.OrdinalIgnoreCase);
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
        // ⚠️ **归一化放在这里、只放一处**：字面前缀的入口判据（下面那一排 `StartsWith("provider delete ")`）
        // 只认空格，而同文件另一批判据走宽口径（`StartsWithToken` / `Split((char[]?)null)`）。
        // 口径不一致的后果是**静默的**：`provider delete<TAB>p` 两边都不认 ⇒ 掉进 `Repl.AsMessage`
        // ⇒ 当成消息发给模型（用户以为删了、provider 还在盘上、还烧一次调用）。
        //
        // ⚠️ **为什么不逐条把谓词改宽**：切词器是混着的——`Split((char[]?)null,…)`（宽）7 处、
        // `Split(' ',…)`（窄）5 处，且 `ParseProviderSet` / `ParseSessionConfigure` 有**无守卫的
        // `parts[2]`**。只放宽谓词、不放宽切词器 ⇒ 那两处会 `IndexOutOfRangeException`
        // 掀翻常驻进程（`Repl` 只接 `DispatchException`）。归一化一次到位，且**没有**这条新崩溃路径。
        //
        // ⚠️ **不会污染消息正文**：调用方拿的是它自己那份未归一化的 `trimmed`
        //（`Repl` 的 `AsMessage(trimmed)`），本函数的归一化只在本函数内部有效。
        var trimmed = Normalize(line).Trim();

        // ⚠️ **不能写成裸 `StartsWith("session create")`**：那样 `session creates foo`
        // 也以它打头 ⇒ 被吞成一条 `session.create`，而**用户以为在发一条消息**。
        // （这条是本批加测试时才发现的既有缺陷；`session activate` 那几条靠尾随空格躲过了它，
        //  只有 `create` 因为「光杆形态也必须成立」而躲不过。）
        // 边界用 `char.IsWhiteSpace`（宽）：窄口径下 `create` 后面跟 Tab 会两边都不认 ⇒ 静默变成消息。
        if (IsSessionCreateVerb(trimmed)) return ParseSessionCreate(trimmed);

        if (trimmed.StartsWith("session activate ", StringComparison.Ordinal))
            return (new CoreCommand.SessionActivate(trimmed["session activate ".Length..].Trim()), null);

        if (trimmed.StartsWith("session interrupt ", StringComparison.Ordinal))
            return (new CoreCommand.SessionInterrupt(trimmed["session interrupt ".Length..].Trim()), null);

        // `session configure <id> [--provider <pid>] [--model <m>] [--ask-timeout <分钟>]`——改绑供应商 / 换模型。
        // 标志风格与 `provider set` 一致（同为「可空 = 保留」语义，手感该一样）。
        if (trimmed.StartsWith("session configure ", StringComparison.Ordinal))
            return ParseSessionConfigure(trimmed);

        // 动词判据走宽口径切词：字面前缀只认空格，axis<TAB>set / axis set<NBSP> 会掉进兜底，
        // 被当成消息发给会话（用户以为设了轴，实际烧了一次模型调用、轴没设）。
        if (IsAxisSetVerb(trimmed))
            return ParseAxisSet(trimmed);

        // ---- 销毁面：三条都只带一个会话 id，判词数就行（id 的合法性在 dispatcher）----
        // ⚠️ 判据走 `StartsWith` + 尾随空格，与 `session activate` / `interrupt` 同一形状：
        // 那几条靠这个空格躲开了「session xxxs 被吞成命令」的缺陷（`session create` 躲不过，
        // 所以它单独有一个 `IsSessionCreateVerb`）。
        if (trimmed.StartsWith("session deactivate ", StringComparison.Ordinal))
            return OneWord(trimmed, "session deactivate ", id => new CoreCommand.SessionDeactivate(id));

        if (trimmed.StartsWith("session delete ", StringComparison.Ordinal))
            return OneWord(trimmed, "session delete ", id => new CoreCommand.SessionDelete(id));

        if (trimmed.StartsWith("session archive ", StringComparison.Ordinal))
            return ParseSessionArchive(trimmed);

        // ---- 回收：与上面三条同一形状（只带一个会话 id，判词数就行）----
        if (trimmed.StartsWith("session gc ", StringComparison.Ordinal))
            return OneWord(trimmed, "session gc ", id => new CoreCommand.SessionGc(id));

        // 光一个 `axis` 不能落回「当成消息」——用户以为在设轴（`axis set` 已被上面截住）。
        if (string.Equals(trimmed, "axis", StringComparison.OrdinalIgnoreCase))
            return (null, "用法：axis set <会话 id> <轴> <值>");

        if (trimmed.StartsWith("provider set ", StringComparison.Ordinal))
            return ParseProviderSet(trimmed);

        // 控制台语法，与 `provider set` 对称：否则会造出「配得进、删不掉」的形状（只能手删文件）。
        if (trimmed.StartsWith("provider delete ", StringComparison.Ordinal))
            return ParseProviderDelete(trimmed);

        // `provider test <id>`：**动作**，与前两条不同——它带副作用（发一次网络请求）⇒ 走命令通道。
        // 参数少一个或词数不对都报错，不猜（猜错的方向是拿别人的配置去打网络）。
        if (trimmed.StartsWith("provider test ", StringComparison.Ordinal))
            return ParseProviderTest(trimmed);

        // 预设：系统提示词的存放（`01` §3.1）。三条**都走命令通道**——
        // 连 `list` 也走，因为头要拿它做下拉，而头只有「命令 + 快照」两层的入口。
        // ⚠️ 动词判据在**归一化后**的行上（词是单个词，折空白无妨），
        // 而**内容**取自**原始行**——见 `ParsePreset` 里那段。
        if (StartsWithToken(trimmed, "preset")) return ParsePreset(line);

        // 三条本地查询（provider / session / claim）+ 光杆动词，都不能落回「当成消息」
        // （用户以为在列东西，实际烧了一次模型调用）。
        if (IsProviderList(trimmed)) return (null, null);
        if (IsSessionList(trimmed)) return (null, null);
        if (string.Equals(trimmed, "provider", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "session", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "preset", StringComparison.OrdinalIgnoreCase))
            return (null, "用法：provider list | provider set <id> … | provider delete <id> | provider test <id>"
                + "\n      session list | session create [--cwd <目录>] [--title <名>] [--preset <id>] [--ask-timeout <分钟>]"
                + " | session activate <id> | session interrupt <id>"
                + "\n      session configure <id> [--provider <pid>] [--model <m>] [--ask-timeout <分钟>]"
                + "\n      session deactivate <id> | session archive <id> [--unarchive] | session delete <id>"
                + " | session gc <id>"
                + "\n      preset list | preset set <id> <内容> | preset delete <id>");

        // 光一个 `provider delete` 不能落回「当成消息」——用户以为删了，而 provider 还在盘上。
        if (string.Equals(trimmed, "provider delete", StringComparison.Ordinal))
            return (null, "用法：provider delete <id>");

        if (string.Equals(trimmed, "provider test", StringComparison.Ordinal))
            return (null, "用法：provider test <id>");

        // 光一个 `session configure` 不能落回「当成消息」——用户以为在改绑，实际烧了一次模型调用。
        if (string.Equals(trimmed, "session configure", StringComparison.Ordinal))
            return (null, "用法：session configure <会话 id> [--provider <pid>] [--model <m>] [--ask-timeout <分钟>]");

        // ⚠️ **光杆的销毁动词同样不能落回「当成消息」**：`session delete` 若被当成消息发出去，
        // 用户以为删了，而**会话还在、还烧了一次模型调用**。三条各给各的用法。
        if (string.Equals(trimmed, "session deactivate", StringComparison.Ordinal))
            return (null, "用法：session deactivate <会话 id>");

        if (string.Equals(trimmed, "session delete", StringComparison.Ordinal))
            return (null, "用法：session delete <会话 id>");

        if (string.Equals(trimmed, "session archive", StringComparison.Ordinal))
            return (null, "用法：session archive <会话 id> [--unarchive]");

        // ⚠️ 光杆的 `session gc` 同样不能落回「当成消息」——用户以为回收了，
        // 而那句话被当成消息发给会话、**还烧了一次模型调用**。
        if (string.Equals(trimmed, "session gc", StringComparison.Ordinal))
            return (null, "用法：session gc <会话 id>");

        if (IsClaimList(trimmed)) return (null, null);

        // 询问：挂起的唯一解除入口（`01` §3.8）。
        // ⚠️ **它必须在这里、而且光杆也要给用法**——`approval` 若落回「当成消息」，
        // 用户以为在回答，而那句话被发给模型、**会话还挂在等待里**。
        if (StartsWithToken(trimmed, "approval")) return ParseApproval(trimmed);

        if (string.Equals(trimmed, "approval", StringComparison.OrdinalIgnoreCase))
            return (null, "用法：approval allow|deny <会话 id> <askId> [--remember args|tool]"
                + "\n      approval cancel <会话 id> <askId>"
                + "\n      approval answer <会话 id> <askId> <问题id>=<值> …");

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

    // `session deactivate <id>` / `session delete <id>`：只判「前缀之后恰好一个词」。
    // 多一个词（或少了）都报错，不猜——猜错的方向是**动错会话**，而这两条一条不可逆、
    // 一条会丢掉内存里的账。
    private static (CoreCommand?, string?) OneWord(string line, string prefix, Func<string, CoreCommand> make)
    {
        var parts = line[prefix.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 1 ? (make(parts[0]), null) : (null, $"用法：{prefix.TrimEnd()} <会话 id>");
    }

    // `session archive <id> [--unarchive]`
    //
    // ⚠️ **`--unarchive` 是标志位，不带值**——所以这里切词而不是用「标志 + 值」成对的循环
    //（那种循环会把 `--unarchive` 自己当成一个值的键）。
    // ⚠️ 只有一个词 ⇒ `Archived` 是 **null**（那份三态的中性档 = 归档）。
    // 别的写法（比如 `--archive true/false`）会把「没给」与「给定 false」混起来，
    // 而那正是 `SessionArchive` 用 `bool?` 要防的事。
    private static (CoreCommand?, string?) ParseSessionArchive(string line)
    {
        var parts = line["session archive ".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1) return (new CoreCommand.SessionArchive(parts[0]), null);

        if (parts.Length == 2 && string.Equals(parts[1], "--unarchive", StringComparison.OrdinalIgnoreCase))
            return (new CoreCommand.SessionArchive(parts[0], Archived: false), null);

        return (null, "用法：session archive <会话 id> [--unarchive]");
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

    /// <summary>
    /// `approval allow &lt;askId&gt; [--remember args|tool]` · `approval deny &lt;askId&gt; [理由…]` ·
    /// `approval cancel &lt;askId&gt;` · `approval answer &lt;askId&gt; &lt;id&gt;=&lt;值&gt; [&lt;id&gt;=&lt;值&gt;…]`
    ///
    /// <para>⚠️ <b>会话 id 只有一个来源</b>：那条正在等人的会话隐含在 <paramref name="askId"/> 里
    /// ——但 `approval.respond` 要它，而控制台**没有**「哪条会话在等」的地方可查。
    /// ⇒ 用 <c>--session</c> 显式给（与别的命令同形），缺了就说清怎么给。</para>
    ///
    /// <para>⚠️ **`&lt;id&gt;=&lt;值&gt;` 这个形状是被字典类型逼出来的**：`answers` 是
    /// <c>IReadOnlyDictionary&lt;string,string&gt;</c>（问题 id → 答案），
    /// 而控制台的行是扁平词。用一个显式的分隔符比「按位置配对」好：后者在
    /// 用户少给一个答案时会**把答案错位到另一个问题上**，那是静默的。</para>
    /// </summary>
    private static (CoreCommand?, string?) ParseApproval(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        const string usage =
            "用法：approval allow|deny <会话 id> <askId> [--remember args|tool] [理由…]"
            + "\n      approval cancel <会话 id> <askId>"
            + "\n      approval answer <会话 id> <askId> <问题id>=<值> …";

        if (parts.Length < 4) return (null, usage);

        var verb = parts[1];
        var session = parts[2];
        var askId = parts[3];

        var outcome = verb.ToLowerInvariant() switch
        {
            "allow" => "submit",
            "deny" => "submit",
            "cancel" => "cancel",
            "answer" => "submit",
            _ => null,
        };

        if (outcome is null) return (null, usage);

        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? remember = null;

        if (string.Equals(verb, "allow", StringComparison.OrdinalIgnoreCase)) answers["decision"] = "allow";
        if (string.Equals(verb, "deny", StringComparison.OrdinalIgnoreCase)) answers["decision"] = "deny";

        // 余下的词：`--remember X` 是有形状的标志，`a=b` 是答案，其余归理由。
        var reason = new List<string>();
        for (var i = 4; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], "--remember", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= parts.Length) return (null, "--remember 后面要给 args 或 tool。");

                remember = parts[++i].ToLowerInvariant() switch
                {
                    "args" => nameof(AskMemory.Args),
                    "tool" => nameof(AskMemory.Tool),
                    _ => null,
                };

                if (remember is null) return (null, "--remember 只认 args 或 tool。");
                continue;
            }

            // ⚠️ **只在第一个 `=` 处切**：值里可以有 `=`（用户答复本身就可能含它）。
            var eq = parts[i].IndexOf('=');
            if (eq > 0 && !string.Equals(verb, "deny", StringComparison.OrdinalIgnoreCase))
            {
                answers[parts[i][..eq]] = parts[i][(eq + 1)..];
                continue;
            }

            reason.Add(parts[i]);
        }

        // 拒绝时余下的词是**理由**（`01` §3.8：拒绝理由比「档位不允许」有用得多）。
        if (reason.Count > 0 && answers.ContainsKey("decision"))
            answers["reason"] = string.Join(" ", reason);

        return (new CoreCommand.ApprovalRespond(session, askId, outcome, answers, remember), null);
    }

    // `session create [--cwd <目录>] [--title <名>] [--preset <id>] [--ask-timeout <分钟>]`
    //
    // ⚠️ **光杆的 `session create` 必须继续成立**（今天控制台的默认手感），
    // 所以这个解析器**不用通用的标志循环**：词数为 2 就是「什么都不给」，直接返回。
    // 用 `provider set` 那种「从 3 起每次跳两格」的循环会在这里越界。
    //
    // ⚠️ 标志名用 `--cwd` 而不是 `--project`：那个值**就是**工作目录，
    // 而「项目」是它派生出来的（`ProjectKey.FromPath`）。叫 `--project` 会让人
    // 以为可以传一个项目键进来，而那个形状是**做不了**的（见 `SessionCreate` 的注释）。
    // 语义校验（目录在不在）**在 dispatcher**，这里只拆词。
    private static (CoreCommand?, string?) ParseSessionCreate(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2) return (new CoreCommand.SessionCreate(), null);

        string? cwd = null, title = null, preset = null;
        double? askTimeout = null;

        for (var i = 2; i < parts.Length; i += 2)
        {
            if (i + 1 >= parts.Length) return (null, $"参数 {parts[i]} 缺少值。");

            var value = parts[i + 1];

            // 值被下一个标志占了位置 ⇒ 报错（与 `provider set` 同一份守卫）。
            if (value.StartsWith("--", StringComparison.Ordinal)) return (null, $"参数 {parts[i]} 缺少值。");

            switch (parts[i])
            {
                case "--cwd": cwd = value; break;
                case "--title": title = value; break;
                case "--preset": preset = value; break;

                // ⚠️ **解析成数字在这一层做**，因为「不是数字」是一条**文本**错误
                // （控制台能把它说成人话）；而「<= 0」那条判据住在命令层，与线上共用。
                case "--ask-timeout":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var mins))
                    {
                        return (null, $"`--ask-timeout` 要一个分钟数（给的是 '{value}'）。");
                    }
                    askTimeout = mins;
                    break;

                // 未知标志必须拒绝：拼错的 --cwd 会被静默吞掉、照常建出一个会话，
                // 而它**落在了核心的工作目录**而不是用户想要的地方。
                default: return (null, $"未知参数 '{parts[i]}'。");
            }
        }

        return (new CoreCommand.SessionCreate
        {
            Cwd = cwd, Title = title, Preset = preset, AskTimeoutMinutes = askTimeout,
        }, null);
    }

    // `preset set <id> <内容>` / `preset delete <id>` / `preset list`
    //
    // ⚠️ **内容按原始行原样截，不走归一化那一步**——这是本函数唯一不能用 `Normalize` 的地方，
    // 也是它为什么收的是原始 `line` 而不是 `trimmed`。理由：预设的正文**就是要发给模型的那段
    // 提示词**，换行 / Tab / 连续空格都是它的意思。折叠一次就等于改写用户写的提示词，
    // 而且**没有任何地方会报告这件事**（它只是安静地少了几格缩进）。
    // 归一化只用来**定位**（认出动词、找到 id 的边界），内容按原样截。
    //
    // ⚠️ 代价如实写下：内容**开头的空白**表达不出来（分隔用的那一格总是被跳过）。
    // 想要前导空行，用别的方式绕（或者将来换成 `preset set <id> --file <路径>`）。
    private static (CoreCommand?, string?) ParsePreset(string line)
    {
        var parts = Normalize(line).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
            return (null, "用法：preset list | preset set <id> <内容> | preset delete <id>");

        var verb = parts[1];

        if (string.Equals(verb, "list", StringComparison.OrdinalIgnoreCase))
            return parts.Length == 2
                ? (new CoreCommand.PresetList(), null)
                : (null, "用法：preset list");

        if (string.Equals(verb, "delete", StringComparison.OrdinalIgnoreCase))
            return parts.Length == 3
                ? (new CoreCommand.PresetDelete(parts[2]), null)
                : (null, "用法：preset delete <id>");

        if (string.Equals(verb, "set", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3) return (null, "用法：preset set <id> <内容>");

            // parts[2] 是 id（它没有空白 ⇒ 归一化不影响它本身）。
            // 内容从**原始行**里 id 之后开始截 —— 跳过那段分隔空白，其余一字不动。
            var start = AfterNthToken(line, 2);
            var content = start >= line.Length ? "" : line[start..];

            return (new CoreCommand.PresetSet(parts[2], content), null);
        }

        return (null, $"未知 preset 动词 '{verb}'。用法：preset list | preset set <id> <内容> | preset delete <id>");
    }

    // 「第 n 个（0 起）空白分隔词之后、跳过那段分隔空白」在**原始行**上的索引。
    // ⚠️ 它只**定位**，不折叠也不改写它扫过的任何字符（内容由调用方按原样截）。
    private static int AfterNthToken(string line, int n)
    {
        var i = 0;
        for (var token = 0; token <= n; token++)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        }

        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return i;
    }

    // `provider set <id> [--key <k>] [--model <m>] [--protocol <p>] [--endpoint <u>] [--auth <a>] [--models a,b,c]`
    // 这里只管拆词：省略 --key ⇒ null，语义是「保留旧 key」；
    // 「新 provider 省 key 要拒」的守卫在 dispatcher，不在这里。
    private static (CoreCommand?, string?) ParseProviderSet(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = parts[2];

        string? key = null, model = null, protocol = null, endpoint = null, auth = null;
        IReadOnlyList<string>? models = null;

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

                // 清单用逗号分隔：`--models a,b,c`。⚠️ 空串 ⇒ 空清单（= 清空），**不是** null（= 保留）。
                // 「空串 = 清空」这条与端点的 `""` 语义一致，别把它也当成「没给」而静默保留旧清单。
                case "--models":
                    models = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;

                // 未知标志必须拒绝：拼错的 --protcol 会被静默吞掉、照常保存并回 ✓，协议悄悄落成默认。
                default: return (null, $"未知参数 '{parts[i]}'。");
            }
        }

        // 这里不要求 --key：「新 provider 省 key 要拒」的语义在 dispatcher，别在这里再写一遍。
        return (new CoreCommand.ProviderUpsert(id, key, model, protocol, endpoint, auth) { Models = models }, null);
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

    // `provider test <id>`：同 `provider delete`，只判词数。id 的合法性（含设备名）由 dispatcher 的
    // `IsSafeProviderId` 判——**别在这里再抄一份**（唯一的差别是它只发请求、不删东西，代价小）。
    private static (CoreCommand?, string?) ParseProviderTest(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 3
            ? (new CoreCommand.ProviderTest(parts[2]), null)
            : (null, "用法：provider test <id>");
    }

    // `session configure <会话 id> [--provider <pid>] [--model <m>] [--ask-timeout <分钟>]`
    // 只管拆词：省略某个标志 ⇒ null，语义是「保留旧值」（与 `provider set` 逐字同构）。
    // 「两个都没给 ⇒ 拒」的守卫在 dispatcher，不在这里——线上也要吃同一条。
    private static (CoreCommand?, string?) ParseSessionConfigure(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sessionId = parts[2];

        string? provider = null, model = null;
        double? askTimeout = null;

        // 从 3 起每次跳两格（标志与值成对；parts[1] == "configure"）。
        for (var i = 3; i < parts.Length; i += 2)
        {
            if (i + 1 >= parts.Length) return (null, $"参数 {parts[i]} 缺少值。");

            var value = parts[i + 1];

            // 值被下一个标志占了位置 ⇒ 报错。由此「值以 -- 开头」不再可能——故意的取舍。
            if (value.StartsWith("--", StringComparison.Ordinal)) return (null, $"参数 {parts[i]} 缺少值。");

            switch (parts[i])
            {
                case "--provider": provider = value; break;
                case "--model": model = value; break;

                // 同 `ParseSessionCreate` 的 `--ask-timeout`：数字在这一层解析（文本错误说人话），
                // 「<= 0」那条判据住在命令层、与线上共用。
                case "--ask-timeout":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var mins))
                    {
                        return (null, $"`--ask-timeout` 要一个分钟数（给的是 '{value}'）。");
                    }
                    askTimeout = mins;
                    break;

                // 未知标志必须拒绝：拼错的 --provder 会被静默吞掉、照常回 ✓，改绑**没发生**而用户以为发生了。
                default: return (null, $"未知参数 '{parts[i]}'。");
            }
        }

        return (new CoreCommand.SessionConfigure(sessionId, provider, model, askTimeout), null);
    }
}

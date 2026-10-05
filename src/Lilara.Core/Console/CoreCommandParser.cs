using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Hosting;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Console;

/// <summary>
/// 把一行**人写的文本**解析成 <see cref="CoreCommand"/>。
///
/// <para>⚠️ **它之所以住在控制台这一层**：`--flag value` 是人写风格的控制台语法，
/// 核心的命令集是**类型化**的（`03` §3）。把 `--` 这种解析规则搬进 dispatcher，
/// 等于把它泄漏到线上协议里——而头发的是 JSON，不是命令行。
/// 反过来，**语义**校验（未知协议 / 未知 auth）留在 dispatcher，线上也要吃（见那里注释）。</para>
///
/// <para>⚠️ 它**返回错误字符串而不是打印**：打印是前端的形态，头不该继承控制台的措辞。</para>
/// </summary>
public static class CoreCommandParser
{
    /// <summary>
    /// ⚠️ **`claim list` 是「控制台的查询」，不是核心命令**（`03` §3：不做 `claim.list`）。
    /// 解析器对它返回 `(null, null)`（不是错误、也不是命令），**由 `Repl` 拦截并自己处理**。
    ///
    /// <para>⚠️ **这个判据必须由 `Repl` 与解析器共用——这是本方法存在的唯一理由。**
    /// 曾经两边各写一份：解析器切词（大小写不敏感、多空格都算），
    /// 而 `Repl` 用精确的 <c>trimmed == "claim list"</c> ⇒
    /// <c>claim LIST</c> / <c>claim\tlist</c> / <c>claim  list</c> 都被解析器认作查询、
    /// 却被 `Repl` 的守卫**放行** ⇒ 落进 `AsMessage` ⇒ **当成消息发给了模型**。
    /// 那正是本任务从头到尾在防的「静默落回当成消息」，**换了个入口回来**。</para>
    ///
    /// <para>⚠️ **`(null, null)` 这个返回值下游有两支**（"查询"与"不是命令"），
    /// 而 `Repl` 只接了"查询"那一支——**所以它的判据必须与这里逐字一致**。
    /// 这就是为什么它必须是**一个公开方法**，而不是两边各写一遍。</para>
    /// </summary>
    public static bool IsClaimList(string line)
    {
        if (!IsClaimVerb(line)) return false;

        var t = line.Trim();
        var words = t["claim".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 1 && string.Equals(words[0], "list", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠️ **「这一行以 `claim` 打头、且 `claim` 后面是分隔符或行尾」——`claim` **词块入口的**唯一判据。**
    /// （`axis set` 那个入口另有一份，但它**共用同一个 `StartsWithToken`**，见下。）
    ///
    /// <para>⚠️ **它必须被 <see cref="IsClaimList"/> 与 <c>Parse</c> 的 claim 分支共用**
    /// （`Repl` 经 <see cref="IsClaimList"/> 也用到它）。两处曾各写一份，而 `Parse` 那份
    /// **只查了 `trimmed[5]` 是不是空白、从不查前五个字符是什么**——
    /// 于是任何**首词恰好 5 个字符**的普通消息（`hello world` / `write a test`）都被静默
    /// 变成 `claim.acquire`：**话没发出去**，反而在 `&lt;工作目录&gt;` 下占了一条声明。
    /// ⇒ 这是「两层判据必须共用一个谓词」那类缺陷的又一次现身，
    /// **而且它正是「把判据收敛成一处」那次修复漏掉的一半。**</para>
    ///
    /// <para>⚠️ 边界用 **<see cref="char.IsWhiteSpace"/>（宽口径）**，不是 `' '` / `'\t'`（窄）：
    /// 窄口径下 `claim` + **NBSP** + 路径会**两边都不认** ⇒ 当成消息发给模型。</para>
    /// </summary>
    public static bool IsClaimVerb(string line) => StartsWithToken(line, "claim");

    /// <summary>
    /// ⚠️ **「这一行以 <paramref name="token"/> 打头、且它后面是分隔符或行尾」——
    /// `claim` 与 `axis` 两个词块入口共用的**唯一**判据。**
    ///
    /// <para>⚠️ **首词大小写不敏感**，与第二词（动词）同一口径：动词是**控制台语法**
    ///（协议里叫 `claim.release` / `session.setAxis`，都带点），用户手打 `Claim src` / `Axis set …`
    /// 是常态；而两种写错的方向不对称——**放宽只是多认一条命令（可见），
    /// 收紧则会让这行字静默变成消息发给模型（烧钱、且不可见）**。</para>
    ///
    /// <para>⚠️ **抽出它是因为两个谓词曾经「看起来同形」而其实不同**：
    /// `IsClaimVerb` 判首词用 `Ordinal`（敏感），`IsAxisSetVerb` 用 `OrdinalIgnoreCase`（不敏感），
    /// 而两处注释都写着「同形 / 与 claim 对齐」——**那句话当时是假的**。
    /// ⇒ 这正是本项目反复栽的那个形态：**一份判据的两个副本，注释宣称它们一致，而它们不一致。**
    /// **共用一个函数才是唯一守得住的「一致」。**</para>
    /// </summary>
    private static bool StartsWithToken(string line, string token)
    {
        var t = line.Trim();
        if (t.Length < token.Length) return false;
        if (!t.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return false;

        // ⚠️ **`token` 之后必须是空白（或到此为止）——词边界，不能省。**
        // 少了这一句，`"claimlist"` / `"axisx"` 也会被切出后面的词
        //（`"claimlist"[5..]` == `"list"`）⇒ 那行字被吞掉，本该当成消息发出去。
        return t.Length == token.Length || char.IsWhiteSpace(t[token.Length]);
    }

    /// <summary>
    /// ⚠️ **「这一行以 `axis` 打头、第二个词是 `set`」——`axis set` 词块入口的唯一判据。**
    ///
    /// <para>⚠️ **首词走共用的 <see cref="StartsWithToken"/>**（与 `claim` 那个谓词**同一份代码**、
    /// 同一个大小写口径）。原先它自己写了一遍「切词 + `Equals(OrdinalIgnoreCase)`」，
    /// 于是与 `IsClaimVerb` 的 `Ordinal` **并不真的同形**——而两边注释都写着「同形 / 与 claim 对齐」。
    /// **共用一个函数才是唯一守得住的「一致」。**</para>
    ///
    /// <para>⚠️ **词块判据把 `axis settings` 挡在外面**：`set` 之后切出来的第一个词是
    /// `settings` 而不是 `set` ⇒ 落回「普通消息」，与旧前缀判据带尾随空格的意图一致。
    /// ⚠️ 放宽的**只有动词大小写**——轴名与取值仍由 `Axes.TryParse` 在 dispatcher 里校验并规范化
    ///（`Safety` 不是 `safety`，应当拒绝而不是猜）。</para>
    /// </summary>
    private static bool IsAxisSetVerb(string line)
    {
        // ⚠️ **首词走共用的 `StartsWithToken`**（与 `claim` 同一份判据、同一个大小写口径）——
        // **别再在这里单写一遍**。这正是 B2 那处「看着同形、其实不同形」的修法。
        if (!StartsWithToken(line, "axis")) return false;

        var words = line.Trim()["axis".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 1 && string.Equals(words[0], "set", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析。返回 <c>(command, error)</c>：
    /// 认出的命令非 null；解析失败则 error 非 null；
    /// **两者都是 null** 表示「这不是一条命令形态的行」——调用方按**普通消息**处理。
    ///
    /// <para>⚠️ <paramref name="currentSessionId"/> 是**控制台专有**的输入：
    /// `claim src/foo` 里没有会话 id，而核心命令**必须**有（线上协议的 `claim.acquire` 带它）。
    /// ⇒ 由控制台补上「当前会话」。头走线上协议，它自己知道是哪个会话。</para>
    /// </summary>
    public static (CoreCommand? Command, string? Error) Parse(string line, string? currentSessionId = null)
    {
        var trimmed = line.Trim();

        if (trimmed.StartsWith("session create", StringComparison.Ordinal)) return (new CoreCommand.SessionCreate(), null);

        if (trimmed.StartsWith("session activate ", StringComparison.Ordinal))
            return (new CoreCommand.SessionActivate(trimmed["session activate ".Length..].Trim()), null);

        if (trimmed.StartsWith("session interrupt ", StringComparison.Ordinal))
            return (new CoreCommand.SessionInterrupt(trimmed["session interrupt ".Length..].Trim()), null);

        // ⚠️ **动词判据：先切词、再逐词比对（宽口径）——与 `claim` 那块同形，别再走字面前缀。**
        // 旧写法是 `StartsWith("axis set ", StringComparison.Ordinal)`，它**只认空格**：
        //   · `axis<TAB>set s1 safety prod`  ⇒ 不认
        //   · `axis set<NBSP>s1 safety prod` ⇒ 不认（NBSP 从网页复制粘贴就会带进来）
        // 两条都掉进最后的兜底 `(null, null)` ⇒ **当成消息发给了会话**：
        // 用户以为设了轴，实际烧了一次模型调用，而轴**没设**。这正是本任务从头防到尾的「静默接管」，
        // 也是 T8 把 `claim` 的边界改成宽口径时**漏掉的那一半**（T5 的 `axis set` 窄口径）。
        if (IsAxisSetVerb(trimmed))
            return ParseAxisSet(trimmed);

        // ⚠️ 「光一个 `axis`」同样不能落回「当成消息」——用户以为在设轴。
        // （「光一个 `axis set`」已被上面那条截住 ⇒ `ParseAxisSet` 报段数不足。旧版靠一条
        //   等值判断 `trimmed is "axis set" or "axis"` 接住它俩，词块判据把这条**并进来了**。）
        if (string.Equals(trimmed, "axis", StringComparison.OrdinalIgnoreCase))
            return (null, "用法：axis set <会话 id> <轴> <值>");

        if (trimmed.StartsWith("provider set ", StringComparison.Ordinal))
            return ParseProviderSet(trimmed);

        if (IsClaimList(trimmed)) return (null, null);

        // ⚠️ **入口判据与 `IsClaimList` 共用同一个谓词**（`IsClaimVerb`）——
        // 它们**曾经是两份副本，而且漂了**：这里那份**只查 `trimmed[5]` 是不是空白、
        // 从不查前五个字符是什么** ⇒ 任何**首词恰好 5 个字符**的普通消息
        //（`hello world` / `write a test`）都被静默变成 `claim.acquire`，
        // 于是**话没发出去、反而在工作目录下占了一条声明**。
        // ⇒ 这是 [[one-predicate-for-both-layers]] 那类缺陷的又一次现身，
        //   **而且它就发生在「把判据收敛成一处」的那次修复里**——
        //   `IsClaimList` 补了前缀校验，旁边这份没有。
        // ⚠️ 宽口径（`char.IsWhiteSpace`）也由谓词统一管着，别再在这里手写边界。
        if (IsClaimVerb(trimmed))
        {
            // `Split(null, RemoveEmptyEntries)`：**所有空白**（空格、Tab、NBSP、连续多个）
            // 都算分隔符——与上面的宽边界口径一致。
            var words = trimmed["claim".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // ⚠️ **动词大小写不敏感**——与 `Axes.TryParse` 的**取值**同一套理
            //（「取值是用户手打的，写 `Prod` 是常态」）。动词是**控制台语法**，不是协议标识符
            //（协议里叫 `claim.release`，带点）。⇒ 让 `claim Release src` 做**对**的事，
            // 而不是落进 acquire 静默占一个怪路径。
            if (words.Length == 0)
                return (null, "用法：claim <路径> | claim release <路径> | claim list");

            // ⚠️ **这一臂只剩「多词」形态**——单词的 `claim list` 已被上面的
            // `IsClaimList` 截走（它在词块**之前**）。⇒ 原先的 `(null, null)` 臂
            // **不可达，是死代码**（审阅实测：删掉它全量仍全绿）。
            // ⇒ 只留报错那一臂。
            if (string.Equals(words[0], "list", StringComparison.OrdinalIgnoreCase))
                return (null, "用法：claim <路径> | claim release <路径> | claim list");

            if (string.Equals(words[0], "release", StringComparison.OrdinalIgnoreCase))
                return words.Length == 2
                    ? WithSession(currentSessionId, words[1],
                        (sid, p) => new CoreCommand.ClaimRelease(sid, [p]))
                    : (null, "用法：claim release <路径>");

            // ⚠️ **路径只收一个词**——与 `axis set` 对多余词**报错**保持一致
            //（T5：`axis set s1 safety` 少了值就报错，不猜）。
            // 控制台**没有引号语法**，所以含空格的路径**无法表达**；
            // 把多余词拼起来是**猜**，而猜错的方向是**静默占错地方**。
            // ⇒ 宁可报错（可见），不猜（静默）。
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

    /// <summary>
    /// `axis set &lt;sessionId&gt; &lt;axis&gt; &lt;value&gt;`。
    /// ⚠️ **token 数必须正好 5**：`axis` + `set` + 会话 + 轴 + 值（调用方传进来的是
    /// **整行**，所以 `parts[0] == "axis"`、`parts[1] == "set"`，三个实参在 2 / 3 / 4）。
    /// 少了不能落回「当成消息」——用户以为设了轴，实际是把这行字发给了会话。那是静默接管。
    ///
    /// <para>⚠️ **切词用 <c>Split(null, …)</c>（所有空白）**，不是 `Split(' ')`（只认空格）：
    /// 后者与入口判据**口径不一**时，`axis set&lt;NBSP&gt;s1 safety prod` 会被入口认作命令、
    /// 却在这里切成一段 ⇒ 报一句**错得离谱**的「用法」；而 `axis&lt;TAB&gt;set …` 更是
    /// 连入口都进不来。两处都改成宽口径后，Tab / NBSP / 连续多个空格一律等价。</para>
    ///
    /// <para>⚠️ 值里不能有空格（`RemoveEmptyEntries` 把空白当分隔符）——这条命令没有 `--` 标志，
    /// 形状就是「三个词」。多出来的词同样落进「不是 5 段」那一支**报错**，不静默取前三个。</para>
    /// </summary>
    private static (CoreCommand?, string?) ParseAxisSet(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return (null, "用法：axis set <会话 id> <轴> <值>   （轴：" + string.Join("|", Axes.Names)
                + "；取值见 `status`）");

        return (new CoreCommand.SessionSetAxis(parts[2], parts[3], parts[4]), null);
    }

    /// <summary>
    /// `provider set &lt;id&gt; [--key &lt;k&gt;] [--model &lt;m&gt;] [--protocol &lt;p&gt;] [--endpoint &lt;u&gt;] [--auth &lt;a&gt;]`
    ///
    /// <para>⚠️ **`--key` 不是必填**（用户裁定 R-B：key 能改不能看 ⇒ 头读不到 key，
    /// 不该逼它重发才能改协议）。省略 ⇒ <c>ApiKey</c> 为 null，语义是「保留旧 key」。
    /// ⚠️ 那条语义的**守卫在 dispatcher**，不在这里：**新** provider 省 key 会被
    /// dispatcher 拒绝并点名 id（控制台与头两个前端吃同一套）。
    /// ⇒ 这里**只管拆词**，不再自己拦一道——否则线上那条路与这里分叉（G14：四条线）。</para>
    /// </summary>
    private static (CoreCommand?, string?) ParseProviderSet(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = parts[2];

        string? key = null, model = null, protocol = null, endpoint = null, auth = null;

        // ⚠️ 从 3 起、每次跳两格：标志与值成对。从 3 起是因为 parts[1] == "set"。
        //
        // ⚠️ **判据是「值长得像不像标志位」，不是「位置上像不像」**（实测三组输入后定的形状）：
        //   · `--key --model`    → 值缺失       → 必须报错
        //   · `--key --model m`  → key 缺值     → 必须报错（**这条最要紧**）
        //   · `--key k --model m`→ 正常
        // 少了这个守卫，第二组会被静默读成 `key=--model` 并把 `--model m` 整个丢掉。
        // 而**不能**改成「跳过长得像标志的位置」——那会把第一组读成 `key=--model`，
        // 白白接住一个明显是笔误的值。
        //
        // ⚠️ 循环上界是 `parts.Length` + 下面那句显式末尾检查——**这两样一起防的是崩溃**，
        // 与「值像标志位」那条守卫**防的是两件不同的事**（复审逐条实测后厘清）：
        //   · 上界用 `parts.Length - 1` ⇒ **末尾**那个标志位整个不被检查：
        //     `--protocol` 末尾缺值 → 静默按默认协议**保存并回 ✓**；`--endpoint` 末尾缺值 →
        //     **端点被丢掉**、请求落回兜底地址，**也是 ✓ 且无警告**（端点裁定要防的静默接管）。
        //   · 上界改成 `parts.Length` 而**不**加末尾检查 ⇒ `parts[i + 1]` 越界，
        //     抛 `IndexOutOfRangeException` ⇒ **异常逃出主循环，把一个常驻进程打死**
        //     （实测：一个词的笔误即可）。
        // ⚠️ 别把「末尾检查」和下面的「`--` 值守卫」说成「一个防御的两半」——**它们防的是不同的事，
        //   且各自独立可杀**，实测：**去掉末尾检查 → 1 红；去掉 `--` 值守卫 → 1 红**。
        for (var i = 3; i < parts.Length; i += 2)
        {
            if (i + 1 >= parts.Length) return (null, $"参数 {parts[i]} 缺少值。");

            var value = parts[i + 1];

            // ⚠️ 值被下一个标志占了位置（`--key --model m`）。少了它，`--model` 会被当成
            //    key 的值、且 `--model m` 整个丢掉——**静默地**。
            //    ⚠️ 由此「值以 `--` 开头」不再可能——这是**故意的**取舍。
            if (value.StartsWith("--", StringComparison.Ordinal)) return (null, $"参数 {parts[i]} 缺少值。");

            switch (parts[i])
            {
                case "--key": key = value; break;
                case "--model": model = value; break;
                case "--protocol": protocol = value; break;
                case "--endpoint": endpoint = value; break;
                case "--auth": auth = value; break;

                // ⚠️ 未知标志必须**拒绝**，不能忽略：`--protcol openai-chat`（拼错）会被
                // 当成一个「没见过的标志 + 它的值」静默吞掉，然后**照常保存并回一句 ✓**，
                // 而协议悄悄落成 claude-messages。用户看到 ✓ 就走了——「静默接管」的变体：
                // 他没得到自己想要的东西，也没被告知。
                default: return (null, $"未知参数 '{parts[i]}'。");
            }
        }

        // ⚠️ **这里不再要求 `--key`**（原来有一道 `if (key is null) return (null, "用法：… --key …")`）。
        // 留着它，控制台会回一句「用法」而**头那条路照常** ⇒ 两个前端分叉
        //（G14：`apiKey` 从必填变可选动的是命令的形状，四条线缺一处就是**静默失效**）。
        // 「新 provider 省 key 要拒」那条语义在 dispatcher，**别在这里再写一遍**。
        return (new CoreCommand.ProviderUpsert(id, key, model, protocol, endpoint, auth), null);
    }
}

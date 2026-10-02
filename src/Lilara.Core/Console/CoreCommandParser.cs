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

        // ⚠️ **前缀判据必须带尾随空格**（`"axis set "` 而不是 `"axis set"`）：
        // 后者会把 `axis settings` 也吃进来。既有 `session create` 就没带空格，
        // 于是 `session createX` 会被当成建会话命令——**本计划不跟着犯这个错**。
        if (trimmed.StartsWith("axis set ", StringComparison.Ordinal))
            return ParseAxisSet(trimmed);

        // ⚠️ 这一句接的是「光一个 `axis set`」（以及「光一个 `axis`」）——它们**不带尾随空格**，
        // 所以上面那条前缀判据够不着，会掉到最后的兜底 `(null, null)` ⇒ 被当成消息发给会话。
        // 用户以为在设轴，实际发了一行字出去：**静默接管**。
        if (trimmed is "axis set" or "axis")
            return (null, "用法：axis set <会话 id> <轴> <值>");

        if (trimmed.StartsWith("provider set ", StringComparison.Ordinal))
            return ParseProviderSet(trimmed);

        // ⚠️ **别去匹配精确的拼写（空格数 / 大小写）——切词。**
        // 初版用 `trimmed == "claim release"` 与 `StartsWith("claim release ")`：
        //   · `"claim  release"`（**两个空格**）→ `Trim` 只去**首尾**空白，内部两个空格**留着**
        //     ⇒ 不匹配 `StartsWith("claim release ")`（第二个字符是空格不是 `r`）
        //     ⇒ 落进 acquire 分支 ⇒ **静默占一个叫 `release` 的路径**。
        //   · `"claim Release src"` → 同样落进 acquire ⇒ **静默执行相反的动作**。
        // ⚠️ 而 `Trim` 能救的只有「尾随空格」那种（`"claim "` → `"claim"` → 命中守卫）——
        // **恰好是我第一版以为能救的那半**。真洞在 Trim 管不到的地方。
        if (trimmed == "claim" || trimmed.StartsWith("claim ", StringComparison.Ordinal)
            || trimmed.StartsWith("claim\t", StringComparison.Ordinal))
        {
            // `Split(null, RemoveEmptyEntries)`：**所有空白**（空格、Tab、连续多个）都算分隔符。
            var words = trimmed["claim".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // ⚠️ **动词大小写不敏感**——与 `Axes.TryParse` 的**取值**同一套理
            //（「取值是用户手打的，写 `Prod` 是常态」）。动词是**控制台语法**，不是协议标识符
            //（协议里叫 `claim.release`，带点）。⇒ 让 `claim Release src` 做**对**的事，
            // 而不是落进 acquire 静默占一个怪路径。
            if (words.Length == 0)
                return (null, "用法：claim <路径> | claim release <路径> | claim list");

            if (string.Equals(words[0], "list", StringComparison.OrdinalIgnoreCase))
                return words.Length == 1
                    ? (null, null)                                   // 控制台查询，Repl 拦截
                    : (null, "用法：claim <路径> | claim release <路径> | claim list");

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
    /// <para>⚠️ 值里不能有空格（`RemoveEmptyEntries` 把空格当分隔符）——这条命令没有 `--` 标志，
    /// 形状就是「三个词」。多出来的词同样落进「不是 5 段」那一支**报错**，不静默取前三个。</para>
    /// </summary>
    private static (CoreCommand?, string?) ParseAxisSet(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return (null, "用法：axis set <会话 id> <轴> <值>   （轴：" + string.Join("|", Axes.Names)
                + "；取值见 `status`）");

        return (new CoreCommand.SessionSetAxis(parts[2], parts[3], parts[4]), null);
    }

    /// <summary>
    /// `provider set &lt;id&gt; --key &lt;k&gt; [--model &lt;m&gt;] [--protocol &lt;p&gt;] [--endpoint &lt;u&gt;] [--auth &lt;a&gt;]`
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

        if (key is null)
            return (null, "用法：provider set <id> --key <key> [--model <model>] "
                + $"[--protocol <{string.Join("|", Protocols.All)}>] [--endpoint <url>] "
                + $"[--auth {AuthSchemes.ApiKey}|{AuthSchemes.Bearer}]");

        return (new CoreCommand.ProviderUpsert(id, key, model, protocol, endpoint, auth), null);
    }
}

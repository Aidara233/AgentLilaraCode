using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Hosting;
using Lilara.Core.Providers;

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
    /// </summary>
    public static (CoreCommand? Command, string? Error) Parse(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.StartsWith("session create", StringComparison.Ordinal)) return (new CoreCommand.SessionCreate(), null);

        if (trimmed.StartsWith("session activate ", StringComparison.Ordinal))
            return (new CoreCommand.SessionActivate(trimmed["session activate ".Length..].Trim()), null);

        if (trimmed.StartsWith("session interrupt ", StringComparison.Ordinal))
            return (new CoreCommand.SessionInterrupt(trimmed["session interrupt ".Length..].Trim()), null);

        if (trimmed.StartsWith("provider set ", StringComparison.Ordinal))
            return ParseProviderSet(trimmed);

        return (null, null);
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

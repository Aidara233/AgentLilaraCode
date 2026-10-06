using System.Text.Json;

namespace Lilara.Core.Storage;

/// <summary>
/// 读到一份**比自己新**的落盘文件。不是损坏，不是缺失——是**另一个版本的核心写的**。
///
/// <para>⚠️ **它必须与「读不出来」分开**：损坏是「这个文件坏了」（重写一份即可），
/// 版本不符是「你手上的程序太旧了」（重写会把新版本的数据**降级覆盖**掉）。
/// 把两者折成一个 null，用户看到的就是「会话不存在」——而它会把人引向**删掉重来**，
/// 那正好是唯一一个会丢数据的做法。</para>
/// </summary>
public sealed class FileFormatException(string message) : Exception(message);

/// <summary>
/// 落盘文件的版本闸。**所有「读一份 JSON 文件」的入口都要走这里。**
///
/// <para>⚠️ <b>它换掉的是「加法式兼容」那套逐字段回避。</b>在那之前，本仓为了不破磁盘格式，
/// 每个新字段都写成可空的、每个读取点都要处理「老文件没这个键」——代价是**逐读取点的、
/// 永久的**。有了版本号之后，缺字段就是「这份文件比这个字段老」，
/// 读的一方**当场就知道该补默认值还是该拒绝**，不必每处各推一遍。</para>
///
/// <para>⚠️ <b>但版本号只解决「我不知道这份文件的年代」这一个问题。</b>
/// 它不解决「字段被改过含义了吗」（那要靠 +1 版本的纪律），也不解决 `history.jsonl`
/// （只追加、行与行可以来自不同版本、且断电会留半行——那份的策略在 `HistoryLog`）。</para>
/// </summary>
public static class FileFormat
{
    /// <summary>
    /// 读文件并校验版本，返回**原文**（调用方自己反序列化）。
    ///
    /// <para>⚠️ 校验发生在**反序列化之前**：让 STJ 先跑一遍的话，一个太新的文件
    /// 多半会先撞上某个字段的形状不符，抛出一句关于**字段**的话——
    /// 而那会把用户引向错误的排查方向（真正的问题是版本）。</para>
    /// </summary>
    /// <exception cref="FileFormatException">文件的版本高于 <see cref="Kernel.Json.FormatVersion"/>。</exception>
    public static string ReadChecked(string path)
    {
        var text = File.ReadAllText(path);

        // 解析失败不在这里报——那是「文件坏了」，调用方各有各的处置（有的是跳过、有的是炸）。
        // 这里只拦「能解析、但版本比我新」这一种。
        using var doc = JsonDocument.Parse(text);

        if (!doc.RootElement.TryGetProperty(Kernel.Json.VersionKey, out var v)
            || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var version))
        {
            // ⚠️ **缺版本号 = 版本 0**（本字段引入之前写的那些），**不是**「未知 ⇒ 拒绝」。
            // 那批文件今天还读得进来，而且在格式冻结期间它们是**唯一**的历史——
            // 拒绝它们等于把用户已有的会话全部作废。
            return text;
        }

        if (version > Kernel.Json.FormatVersion)
            throw new FileFormatException(
                $"这份文件是按更新的格式写的（它 version={version}，本程序只认到 {Kernel.Json.FormatVersion}）：{path}"
                + "。用更新的核心打开它，或把它移走——**别覆盖它**，那会丢数据。");

        return text;
    }
}

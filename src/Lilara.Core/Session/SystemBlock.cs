using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// <b>系统块从哪来</b>——`01` §3.1 头槽位里那块（位置 0），也是 `04` §1.3 那句
/// 「核心自带零内容」的**兑现**。
///
/// <para>⚠️ <b>判定与产物挤在一个类里</b>（同 <see cref="Reclaim"/> 那条理由）：
/// 「哪条路」与「出来是什么」若各写一份，早晚会漂。</para>
///
/// <para>⚠️ <b>三条路的区别是语义，不是实现细节</b>：</para>
/// <list type="bullet">
///   <item><b>文件在、非空</b> ⇒ 那一块就是文件内容。</item>
///   <item><b>文件在、为空</b> ⇒ <b>空块 = 纯聊天</b>。这是用户<b>明说了「不要」</b>。</item>
///   <item><b>文件不在</b> ⇒ 回落 <see cref="CorePrompts.SystemFallback"/>。这是<b>「没配过」</b>。</item>
/// </list>
/// <para>⚠️ 后两条**不是同一件事**，合并会把「我要纯聊天」变成「用回内置模板」——静默地。</para>
///
/// <para>⚠️ <b>代价如实写下</b>：删掉预设文件会「复活」内置模板，所以想表达纯聊天得留一个<b>空文件</b>。
/// 选这条是因为「开箱即有默认」比「删文件表示纯聊天」常用得多，而空文件**在盘上看得见**
/// （`presets/` 里一个 0 字节的文件，比「什么都没有」好排查）。</para>
/// </summary>
public static class SystemBlock
{
    /// <summary>
    /// 读出这个会话这一次该带的那一块（或没有块）。
    ///
    /// <para>⚠️ <b>必须在「步开始」调一次并冻住，不是每次渲染时调</b>。文件是
    /// <b>每步都可能变的外部输入</b>，而 [`00` §5] 铁律 1 要求<b>渲染是纯函数</b>：
    /// 同一步内重复渲染必须<b>逐字节相同</b>。随手现读的话，用户在半步中间改了文件就会
    /// 「半新半旧 ⇒ <b>每步都破前缀</b>」——那正是 `04` §7.3 机制 1 要防的事，而它给的解法现成：
    /// <i>贡献集合每步取一次快照，与 `now` 冻结同步开始</i>（`01` §3.2）。</para>
    ///
    /// <para>⇒ <b>「实时引用」= 跨步实时</b>（下一步就生效），<b>不是</b>「渲染时现读」。
    /// 两句话都得写在这里——只写「实时」会让下一个人以为可以随手现读。</para>
    /// </summary>
    public static IReadOnlyList<Block> Render(PresetStore presets, string? presetId)
    {
        // null = 没指定 ⇒ 跟随全局默认那个名字（`CorePrompts.DefaultPreset`）。
        // ⚠️ 这只是**换了个名字去找**，不改变「找不到就回落内置模板」那条。
        var content = presets.Load(presetId ?? CorePrompts.DefaultPreset);

        // 三种「没有内容」要分开：null = 文件不在（没配过）⇒ 兜底；"" = 文件在但空（纯聊天）⇒ 空块。
        // ⚠️ 用 IsNullOrEmpty 而不是 IsNullOrWhiteSpace：只有空白字符的文件也是**用户写的**内容，
        // 把它当空会静默丢掉用户的东西；真按空白算的话，它发出去就是一段空白，看得见。
        if (content is null) return [new Block.TextBlock(CorePrompts.SystemFallback)];
        if (content.Length == 0) return [];

        return [new Block.TextBlock(content)];
    }
}

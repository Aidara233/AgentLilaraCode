using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

/// <summary>
/// 预设的落点：<c>&lt;数据根&gt;/presets/&lt;id&gt;.md</c>。**文件内容就是那段系统提示词。**
///
/// <para>⚠️ <b>为什么是纯文本而不是 JSON</b>：它是**给人编辑**的（用户随时定制）。
/// 包一层 JSON 就得处理转义、换行、以及「我少打了个引号」——而这里没有任何需要结构的东西，
/// 一份预设**就是一段话**。将来预设长出别的字段（模型 / 工具白名单）时再加壳，那时才有结构可包。</para>
///
/// <para>⚠️ <b>只读，从不创建</b>：核心不替用户生成预设文件，同 <c>session.create</c> 不替用户建目录
/// （建东西是策略，`00` §1.3）。⇒ 开箱时这个目录可能压根不存在，那是**正常状态**，
/// 由 <see cref="SystemBlock"/> 回落到内置模板。</para>
/// </summary>
public sealed class PresetStore(string dataRoot)
{
    private readonly string _root = Path.Combine(dataRoot, "presets");

    // id 会变成文件名 ⇒ 判据与 ProviderStore 同源（都是 CommandDispatcher.IsSafeProviderId 那把闸），
    // 本层不重复判（闸在命令入口，见 `IsSafeProviderId` 的注释）。
    private string PathFor(string id) => Path.Combine(_root, $"{id}.md");

    /// <summary>
    /// 读一份预设。⚠️ <c>null</c> = **文件不存在**，不是「空预设」——
    /// 这两件事在 <see cref="SystemBlock"/> 那里分得清清楚楚（不存在 ⇒ 回落内置模板；空 ⇒ 纯聊天）。
    ///
    /// <para>⚠️ <b>文件存在但内容为空 ⇒ 返回空串，不是 null</b>。这是本层唯一一处「空」的语义，
    /// 读的一侧必须原样保留：把空串当不存在会让「我要纯聊天」变成「用回内置模板」。</para>
    ///
    /// <para>⚠️ <b>只吞 <c>IOException</c> 一类的「读不到」，不吞「文件损坏」</b>——纯文本没有损坏这一说，
    /// 任何字节都是合法内容（与 JSON 那条路刻意不同）。</para>
    /// </summary>
    public string? Load(string id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>写入一份预设。原子写（同 <c>ProviderStore.Save</c>）。</summary>
    public void Save(string id, string content) => AtomicFile.Write(PathFor(id), content);

    /// <summary>
    /// 删掉一份预设，返回是否真有文件被移除。
    /// ⚠️ 判据是「**文件在不在**」，不是「<c>Load(id) is null</c>」——
    /// 后者把「空文件」也当成不存在（本层里空是**合法内容**），那样就永远删不掉一个空预设。
    /// </summary>
    public bool Delete(string id)
    {
        var path = PathFor(id);

        // File.Delete 对不存在的文件是静默 no-op ⇒ 必须先看再删（同 ProviderStore.Delete）。
        if (!File.Exists(path)) return false;

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// 有哪些预设（不带扩展名）。目录不存在 ⇒ 空表，**不抛**：没建过预设是正常状态。
    ///
    /// <para>⚠️ <b>排序是必须的</b>：顺序来自文件系统枚举，而它**不保证稳定**。
    /// 这份顺序会变成头那边的下拉、以及控制台的一行行输出——每次不一样就是「同一事实两个显示点」的
    /// 温床。按序号排，判据是 id 本身（纯函数，不依赖任何持久化状态）。</para>
    /// </summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_root)) return [];

        return [.. Directory.EnumerateFiles(_root, "*.md")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .OrderBy(x => x, StringComparer.Ordinal)];
    }
}

using Lilara.Core.Session;

namespace Lilara.Core.Hosting;

/// <summary>
/// **哪些会话现在真的有东西在替它往下走**（`01` §11.6 / §11.7）。
///
/// <para>⚠️ <b>它回答的是那个问了两轮的问题：「谁在跑」。</b>
/// `IsRunning` 只答「这一刻有一趟推进在飞」，而**队列里排着**与**一个答复等着被执行**
/// 都算「有人在动它」——那三件事恰好是 <c>SessionHandle</c> 上那三份内存。</para>
///
/// <para>⚠️ <b>判据不是「它在不在内存里」</b>（那正是 `CoreHost.Activate` 走过的弯路）：
/// 头扫一眼列表就会把会话载进内存，而那时**没有任何东西在替它往下走**。
/// 凭证说的是「有东西在动」，不是「被谁看过」。</para>
///
/// <para>⚠️ <b>打点只有两处</b>，就是 <c>SessionHandle._step</c> 翻转的那两处
/// （进推进 / 出推进）——多打一处就多一个会漂的地方，而**漂了的后果是静默的**
/// （一个「以为有人在跑」的会话，它的断裂永远没人收）。</para>
///
/// <para>⚠️ <b>今天它是「谁在等一个未来」的近似，不是精确刻画</b>：§6.1 的队列**随内存一起丢**
/// （它只在离线期有值），所以「队列非空」与「这个会话还在内存里」今天**同延**。
/// 那不是设计，是本批的形状——见 `03` §6 的记账。⇒ **别拿它当「活在内存里的会话清单」用**
/// （`Activated` 才是那个）；它们今天恰好一样，而这个「恰好」会在队列落盘那天断掉。</para>
/// </summary>
public sealed class LiveSessions
{
    // ⚠️ 判据是「有东西在动」，而那件事**只在内存里**（`Running` 从不落盘）
    // ⇒ 这个集合天然是进程内的，**不落盘**（`00` §7 判据 8：算得出来就不该持久化）。
    //
    // ⚠️ **从 2026-11 起每个条目还带一个「最后一次被碰的时刻」**（性能批）——
    // 它是「空闲卸载」的判据（见 `IsIdleSince`）。两件事住在同一个字典里是刻意的：
    // 它们问的是**同一个对象**（这个 id 在这个进程里的那一份），分两张表就要担心它们漂开
    // （一张有条目、另一张没有时，「它算不算活着」就有了两个答案）。
    private readonly Dictionary<string, Entry> _live = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>一条活跃性记录：凭证计数 + 最后一次活动时刻（`Environment.TickCount64`，单调）。</summary>
    private sealed class Entry
    {
        public int Count;
        public long LastActive;
    }

    /// <summary>
    /// 这个会话现在有东西在动吗——**加上调用方手里那个把手自己的状态**。
    ///
    /// <para>⚠️ <b>两个来源都要看，缺一不可</b>：凭证是「别的路径有没有在动它」，
    /// 而 <paramref name="handle"/> 上那三份是这个把手**自己**的那一份。
    /// 只看凭证会漏掉「刚建出来、还没进过推进、但队列里已经有话」的会话
    /// （`session.create` 走 `Register`，**不经过** <see cref="Mark"/>）。</para>
    /// </summary>
    public bool IsLive(SessionHandle handle)
    {
        lock (_gate)
            if (_live.TryGetValue(handle.SessionId, out var entry) && entry.Count > 0) return true;

        // ⚠️ `TakeResume` **不能在这里调**（它是取走即清的一次性交接槽）⇒ 换一个只读的问法。
        return handle.IsRunning
               || handle.QueueDepth > 0
               || handle.HasPendingResume;
    }

    /// <summary>
    /// **给遍历用的那个判据**：这个会话有没有东西在动。
    /// ⚠️ 它**不看 handle**——遍历拿不到可靠的那一份（会话可能没载入内存，
    /// 而「没载入」恰恰就是「没东西在动」）。⇒ 只认凭证。
    /// </summary>
    public bool IsLive(string sessionId)
    {
        lock (_gate) return _live.TryGetValue(sessionId, out var entry) && entry.Count > 0;
    }

    /// <summary>记下「这一趟推进起了」。⚠️ <b>必须与 <c>TryEnterStep()</c> 的返回同处一句</b>。</summary>
    public void Mark(string sessionId)
    {
        lock (_gate)
        {
            var entry = EntryOf(sessionId);
            entry.Count++;
            entry.LastActive = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 这一趟结束了。
    ///
    /// <para>⚠️ <b>它<b>不</b>删键</b>：`finally` 与下一趟的 `Mark` 之间**没有互斥**
    /// （`TryEnterStep` 是原子的，而这个计数不是），删键与自增交错会让计数**从 0 回到 1**
    /// ——那次自增落在被删掉的字典项上。留着零值项，让自增永远作用在同一个项上。</para>
    ///
    /// <para>⚠️ **代价如实认**：崩溃的会话会永远留着计数（键只增不减）。那是**可接受的**：
    /// 进程一重启这张表就是空的，而它本来只描述**这一次进程**的活跃性。</para>
    /// </summary>
    public void Clear(string sessionId)
    {
        lock (_gate)
            if (_live.TryGetValue(sessionId, out var entry))
            {
                if (entry.Count > 0) entry.Count--;
                // 「这一趟跑完了」也是一次活动：它是这个会话在那个时刻真的动过的证据。
                entry.LastActive = Environment.TickCount64;
            }
    }

    /// <summary>此刻有东西在动的那些会话 id ——**一次一致性快照**（调用方按它遍历）。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return [.. _live.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key)];
    }

    /// <summary>
    /// **这个会话被踢出去了**（`session.deactivate`）——把它的凭证整个撤掉。
    ///
    /// <para>⚠️ <b>与 <see cref="Clear"/> 不是一件事，别合并</b>：那个是「**这一趟**推进跑完了」
    /// 的配对动作（`Mark` 的反面，计数减一）；这个是「这个会话不在内存里了」。
    /// 用 `Clear` 顶替它会让计数与真实推进次数脱钩（下一次 `Mark` 起来的数少一格）；
    /// 用这个顶替 `Clear` 则会把**还在跑的**那一趟的凭证一起撤掉。</para>
    /// </summary>
    public void Forget(string sessionId)
    {
        lock (_gate) _live.Remove(sessionId);
    }

    /// <summary>所有**曾经**进过推进的会话 id（含计数归零的那些）。见 <see cref="Clear"/> 那条代价。</summary>
    /// <remarks>
    /// ⚠️ **2026-11 性能批之后 `src/` 里没有调用点**（心跳那一趟改走激活表了，见
    /// <see cref="Touch"/> 的注释）。保留它是因为它答的问题仍然是独立的
    /// （「这次进程里碰过谁」，**包括已经不在激活表里的**），而调试 / 将来的诊断端点要的
    /// 正是这个；⚠️ 但别拿它当「迭代维护」的入口——那会重新引入「为每个碰过的会话扫一次盘」。
    /// </remarks>
    public IReadOnlyList<string> Known()
    {
        lock (_gate) return [.. _live.Keys];
    }

    /// <summary>
    /// **这个会话被「碰过」了**——它进了内存（`Activate` / `Register`）。
    ///
    /// <para>⚠️ <b>与 <see cref="Mark"/> 的区别是「动没动」</b>：`Mark` 是「有一趟推进在飞」，
    /// 而这个只是「它被载进来了」——两者都让这个 id 有条目，但**只有 `Mark` 让 `IsLive` 答真**。</para>
    ///
    /// <para>⚠️ **它还负责记下「最后一次被碰的时刻」**（空闲卸载的输入，见 <see cref="IsIdleSince"/>）：
    /// 一个会话被载入本身就是一次活动，否则「刚载入、还没跑过」的会话会立刻被判成空闲。</para>
    ///
    /// <para>⚠️ <b>2026-11 性能批之后，心跳不再遍历这张表</b>：它走激活表
    /// （`CoreHost.Activated`，自带 `ProjectKey`，不必再扫盘找会话），而这本账在
    /// 卸载时会 `Forget` 掉对应条目。见 <see cref="Known"/> 那条注释里剩下的用途。</para>
    /// </summary>
    public void Touch(string sessionId)
    {
        lock (_gate)
        {
            _ = EntryOf(sessionId);
        }
    }

    /// <summary>
    /// **这个会话空闲够久了吗**——是 ⇒ 空闲卸载可以动手。
    ///
    /// <para>⚠️ <b>它与 <see cref="IsLive"/> 答的不是同一个问题，两者都要问</b>：
    /// 那个说「有没有东西在替它往下走」（**此刻**），这个说「上一次有什么碰它是什么时候」
    /// （**多久了**）。一个刚跑完一步、排队空了的会话是「不 live 但刚活动过」——
    /// 那个必须留着（用户正要接着用）；而一个三步之前就没人理的会话才是该摘的。</para>
    ///
    /// <para>⚠️ <b>没有条目的 id 一律算空闲</b>：没有条目 = 这个进程没见过它活动
    /// （或者它已经被摘掉了）。把「不知道」判成「忙」会让摘除永远不发生，
    /// 而判成「闲」最多多摘一次——那件事的代价只是一次从盘载入。</para>
    ///
    /// <para>⚠️ <b>时钟用 `Environment.TickCount64`（单调）而不是墙上时间</b>：
    /// 判的是「过了多久」，而墙上时间会被对时 / 夏令时往回拨——那会让「刚活动过」的会话
    /// 突然被判成空闲。它与 `DeltaPump` 用的是同一个钟。</para>
    /// </summary>
    /// <param name="nowTicks">调用方取一次的当前刻度（`Environment.TickCount64`），
    /// 让一次扫描里所有会话按**同一个时刻**比较。</param>
    public bool IsIdleSince(string sessionId, long nowTicks, TimeSpan idle)
    {
        var idleMs = (long)idle.TotalMilliseconds;

        lock (_gate)
        {
            if (!_live.TryGetValue(sessionId, out var entry)) return true;
            if (entry.Count > 0) return false;   // 有东西在动 ⇒ 不空闲（凭证优先于时刻）
            return nowTicks - entry.LastActive >= idleMs;
        }
    }

    /// <summary>
    /// **把这条记录的时刻往前拨**——只给测试用。
    ///
    /// <para>⚠️ 为什么需要它：空闲判据的唯一输入是真实流逝的时间，而一个单元测试
    /// 不该 `Thread.Sleep(10 分钟)`。⚠️ 它刻意不从生产 API 上开一个「假时钟」口子
    /// （那会让每个调用点都要想「传谁的时间」）：改的是**这一条数据的时刻**，
    /// 判据本身一个字没变。</para>
    /// </summary>
    internal void BackdateForTest(string sessionId, TimeSpan ago)
    {
        lock (_gate)
            if (_live.TryGetValue(sessionId, out var entry))
                entry.LastActive = Environment.TickCount64 - (long)ago.TotalMilliseconds;
    }

    private Entry EntryOf(string sessionId)
    {
        if (!_live.TryGetValue(sessionId, out var entry))
            _live[sessionId] = entry = new Entry { LastActive = Environment.TickCount64 };
        return entry;
    }
}

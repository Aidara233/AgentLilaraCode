using Lilara.Core.Protocol;

namespace Lilara.Core.Hosting;

/// <summary>
/// **增量的攒批器**：把模型吐的碎片按时间窗并成少数几条事件再发。
///
/// <para>⚠️ <b>为什么不一次 delta 打一帧</b>：上游可能一秒几十片，而每一片走一趟
/// 「分配 seq → fan-out → 唤醒订阅者 → 序列化 → 写 socket」，代价全是常数级的。
/// 逐片发会把连接带宽和订阅队列一起占满，而人眼在一个 50ms 窗口里看不出差别。</para>
///
/// <para>⚠️ <b>它不是事件的事实来源</b>：这些帧只供显示，不进记录、也不能用于重建。
/// 头收到 <c>message.appended</c> 时**必须整块替换**临时气泡——增量攒得对不对，
/// 在那一刻都无关紧要。这正是「不支持断线续传」那条语义在实现里的落点。</para>
///
/// <para>⚠️ <b>为什么是「时间判定」而不是「攒够 N 个字符」</b>：后者在慢速输出时会让最后几个字
/// 停在缓冲区里等下一次凑够——用户看到的是「打字打到一半卡住」。
/// 这里在**每次 Push** 时看表，所以最坏延迟是一次上游切片的间隔（通常几十毫秒）。
/// 而**收尾那次**由调用方在步末显式 <see cref="Flush"/>（不能只靠下一次 Push 顺手带出去：
/// 最后一片之后可能再也不来了）。</para>
///
/// <para>⚠️ <b>没有后台定时器</b>：一个进程级定时器会随对象释放责任一起含糊下去
/// （谁负责停？测试里造了十个怎么办）。用「Push 时看表 + 步末显式收尾」，
/// 生命周期就与步完全对齐了。</para>
/// </summary>
public sealed class DeltaPump(EventBus bus)
{
    /// <summary>攒批窗口。⚠️ 改它要一起想「一次 flush 最多发多大一帧」——长文本会变成一帧大消息。</summary>
    public const int FlushIntervalMs = 50;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>收下一条增量。攒批窗口到了就地发出去（调用方在模型的读流循环里）。</summary>
    public void Push(string sessionId, int turn, ModelDelta delta)
    {
        Pending? ready = null;

        lock (_gate)
        {
            if (!_pending.TryGetValue(sessionId, out var pending))
                _pending[sessionId] = pending = new Pending(turn);

            pending.Turn = turn;
            pending.Append(delta);

            if (pending.Due(FlushIntervalMs))
            {
                _pending.Remove(sessionId);
                ready = pending;
            }
        }

        // ⚠️ 发布在**锁外**：`EventBus.Publish` 自己也拿锁，两把锁套起来只是自找一次
        // 「谁先谁后」的解释负担，而这里没有需要原子性的不变量。
        if (ready is not null) Publish(sessionId, ready);
    }

    /// <summary>
    /// **本步的增量到此为止**：立刻发掉剩下的那一点。
    /// ⚠️ 调用点必须在「最终回复落盘 / `message.appended`」**之前**——否则尾部几个字
    /// 会出现在最终气泡**之后**，而下一秒它又被整块替换掉（闪一下）。
    /// </summary>
    public void Flush(string sessionId)
    {
        Pending? pending;
        lock (_gate)
        {
            if (!_pending.Remove(sessionId, out pending)) return;
        }
        Publish(sessionId, pending);
    }

    private void Publish(string sessionId, Pending pending)
    {
        foreach (var (key, text) in pending.Parts)
        {
            if (text.Length == 0) continue;

            bus.Publish(
                EventBus.SessionTopic(sessionId),
                new WireEvent.TermDelta(sessionId, pending.Turn, key.Index, key.Kind, text.ToString()),
                // ⚠️ **不保留**：这些帧不参与补发。让它们进缓冲会把真正的事件挤掉
                // （`EventBus` 的注释里那条「话痨 topic 挤爆别人」正是这个形状）；
                // 而重连之后重放一段半截正文，只会让头画出两条重叠的气泡。
                retained: false);
        }
    }

    private sealed class Pending(int turn)
    {
        public int Turn { get; set; } = turn;

        private long _lastFlush = Environment.TickCount64;

        /// <summary>按 (块序号, 种类) 分开攒：正文与思考是两块，混着拼会让头分不清要塞哪儿。</summary>
        public Dictionary<(int Index, string Kind), System.Text.StringBuilder> Parts { get; } = [];

        public void Append(ModelDelta delta)
        {
            if (!Parts.TryGetValue((delta.BlockIndex, delta.Kind), out var text))
                Parts[(delta.BlockIndex, delta.Kind)] = text = new System.Text.StringBuilder();
            text.Append(delta.Text);
        }

        /// <summary>攒批窗口到了没有。到点就重置窗口——否则之后每来一片都会立刻再发一帧。</summary>
        public bool Due(int intervalMs)
        {
            if (Environment.TickCount64 - _lastFlush < intervalMs) return false;
            _lastFlush = Environment.TickCount64;
            return true;
        }
    }
}

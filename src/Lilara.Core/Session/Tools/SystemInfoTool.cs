using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

public sealed class SystemInfoTool : ITool
{
    private const int MaxCommandOutputChars = 4096;
    private readonly Func<CancellationToken, Task<NvidiaStatus>> _nvidia;

    public SystemInfoTool() : this(QueryNvidiaAsync) { }

    internal SystemInfoTool(Func<CancellationToken, Task<NvidiaStatus>> nvidia) => _nvidia = nvidia;

    public string Name => "system_info";
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;
    public string Description => "按需读取本设备 CPU、内存、磁盘卷与可选 NVIDIA GPU 的状态快照；不持续监控，不列进程。不可用的指标会明确标记。";
    public string ParametersSchemaJson => """
        {"type":"object","properties":{}}
        """;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var text = new StringBuilder();
        text.AppendLine($"CPU: {Environment.ProcessorCount} 个逻辑处理器；使用率: {await ReadCpuUsageAsync(ct)}");
        AppendMemory(text);
        AppendDisks(text);

        NvidiaStatus gpu;
        try { gpu = await _nvidia(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { gpu = new NvidiaStatus("查询失败", ex.Message); }

        text.Append("NVIDIA GPU: ").AppendLine(gpu.State + (gpu.Detail is null ? "" : $"（{gpu.Detail}）"));
        // ⚠️ 设备快照本身很小，这一句是**保存**上限那道安全阀（同 `ToolLimits`）：
        // 投递截断在渲染层，不在这里。
        if (text.Length > ToolLimits.MaxToolOutputBytes)
            text.Length = ToolLimits.MaxToolOutputBytes;

        return new ToolOutcome { Content = [new Block.TextBlock(text.ToString().TrimEnd())] };
    }

    private static async Task<string> ReadCpuUsageAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var first = ReadLinuxCpuTimes();
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                var second = ReadLinuxCpuTimes();
                var total = second.Total - first.Total;
                var idle = second.Idle - first.Idle;
                return total == 0 ? Unavailable("采样间隔内无计数") : $"{Math.Clamp(100d * (total - idle) / total, 0, 100):F1}%";
            }

            if (OperatingSystem.IsWindows())
            {
                if (!GetSystemTimes(out var idle1, out var kernel1, out var user1)) return Unavailable("GetSystemTimes 失败");
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                if (!GetSystemTimes(out var idle2, out var kernel2, out var user2)) return Unavailable("GetSystemTimes 失败");
                var idle = idle2 - idle1;
                var total = (kernel2 - kernel1) + (user2 - user1);
                return total == 0 ? Unavailable("采样间隔内无计数") : $"{Math.Clamp(100d * (total - idle) / total, 0, 100):F1}%";
            }

            return Unavailable("当前平台暂不支持");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return Unavailable(ex.Message);
        }
    }

    private static (ulong Total, ulong Idle) ReadLinuxCpuTimes()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault() ?? throw new IOException("/proc/stat 无 CPU 行");
        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length < 4) throw new IOException("/proc/stat CPU 行格式无法识别");
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (values.Aggregate(0UL, (sum, value) => sum + value), idle);
    }

    private static void AppendMemory(StringBuilder text)
    {
        try
        {
            var memory = OperatingSystem.IsWindows()
                ? ReadWindowsMemory()
                : OperatingSystem.IsLinux()
                    ? ReadLinuxMemory()
                    : null;
            if (memory is null)
            {
                text.AppendLine($"内存: {Unavailable("当前平台暂不支持")}");
                return;
            }

            text.AppendLine($"内存: 总量 {FormatBytes(memory.Value.Total)}；可用 {FormatBytes(memory.Value.Available)}；已用 {FormatBytes(memory.Value.Total - memory.Value.Available)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            text.AppendLine($"内存: {Unavailable(ex.Message)}");
        }
    }

    private static (ulong Total, ulong Available)? ReadLinuxMemory()
    {
        var values = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(item => item.Item2.Length >= 2 && ulong.TryParse(item.Item2[0], out _))
            .ToDictionary(item => item.Item1, item => ulong.Parse(item.Item2[0], CultureInfo.InvariantCulture) * 1024, StringComparer.Ordinal);

        return values.TryGetValue("MemTotal", out var total) && values.TryGetValue("MemAvailable", out var available)
            ? (total, Math.Min(available, total))
            : null;
    }

    private static (ulong Total, ulong Available)? ReadWindowsMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (status.TotalPhysical, status.AvailablePhysical) : null;
    }

    private static void AppendDisks(StringBuilder text)
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives().OrderBy(d => d.Name, StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            text.AppendLine($"磁盘: {Unavailable(ex.Message)}");
            return;
        }

        text.AppendLine("磁盘卷:");
        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady) { text.AppendLine($"- {drive.Name}: 未就绪"); continue; }
                text.AppendLine($"- {drive.Name}: 总量 {FormatBytes((ulong)drive.TotalSize)}；已用 {FormatBytes((ulong)(drive.TotalSize - drive.AvailableFreeSpace))}；可用 {FormatBytes((ulong)drive.AvailableFreeSpace)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                text.AppendLine($"- {drive.Name}: {Unavailable(ex.Message)}");
            }
        }
    }

    private static async Task<NvidiaStatus> QueryNvidiaAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--query-gpu=name,memory.total,memory.used,utilization.gpu,temperature.gpu,power.draw");
        start.ArgumentList.Add("--format=csv,noheader,nounits");

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new NvidiaStatus("不可用", "无法启动 nvidia-smi");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new NvidiaStatus("不可用", "未检测到 nvidia-smi");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, MaxCommandOutputChars, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, 512, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            _ = await stderr;
            if (process.ExitCode != 0) return new NvidiaStatus("查询失败", $"nvidia-smi 退出码 {process.ExitCode}");
            if (output.Truncated) return new NvidiaStatus("查询失败", "nvidia-smi 输出超过上限");
            var row = output.Text.Trim().Split(',', StringSplitOptions.TrimEntries);
            if (row.Length < 6) return new NvidiaStatus("查询失败", "nvidia-smi 返回格式无法识别");
            return new NvidiaStatus("可用", $"{row[0]}；显存 {row[2]}/{row[1]} MiB；利用率 {row[3]}%；温度 {row[4]} °C；功耗 {row[5]} W");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new NvidiaStatus("查询超时", "超过 2 秒，未能取得 GPU 状态");
        }
        catch (IOException ex)
        {
            TryKill(process);
            return new NvidiaStatus("查询失败", ex.Message);
        }
        finally
        {
            if (!process.HasExited) TryKill(process);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var buffer = new char[512];
        var result = new StringBuilder();
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return (result.ToString(), truncated);
            var keep = Math.Min(count, Math.Max(0, maxChars - result.Length));
            if (keep > 0) result.Append(buffer, 0, keep);
            if (keep < count) truncated = true;
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string FormatBytes(ulong bytes) => $"{bytes / (1024d * 1024 * 1024):F1} GiB";
    private static string Unavailable(string reason) => $"不可用：{reason}";

    internal sealed record NvidiaStatus(string State, string? Detail);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}

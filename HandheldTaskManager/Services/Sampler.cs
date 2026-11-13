using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;

namespace HandheldTaskManager.Services;

/// <summary>All processes that share an executable name, measured over one sampling interval.</summary>
public sealed class GroupData
{
    public string Key = "";
    public string Name = "";
    public string Title = "";
    public string? Path;
    public double Cpu;
    public long Memory;
    public readonly List<int> Pids = new();
    public readonly List<IntPtr> Windows = new();
}

public sealed class DriveStat
{
    public string Label { get; init; } = "";
    public string Sub { get; init; } = "";
    public double Fraction { get; init; }
}

public sealed class Sample
{
    public List<GroupData> Groups = new();
    public int ProcessCount;
    public double CpuTotal;
    public ulong MemTotal, MemAvailable;
    public bool HasBattery, OnAcPower, Charging;
    public int BatteryPercent = -1;
    public int BatterySecondsLeft = -1;
    public double NetDownBytesPerSec, NetUpBytesPerSec;
    public List<DriveStat> Drives = new();
    public TimeSpan Uptime;
}

/// <summary>
/// Collects process and system statistics. Call <see cref="Collect"/> from a background thread,
/// one call at a time; usage figures are averages since the previous call.
/// </summary>
public sealed class Sampler
{
    private readonly Dictionary<int, TimeSpan> _cpuTimes = new();
    private readonly HashSet<int> _cpuDenied = new();
    private readonly Dictionary<int, string?> _paths = new();
    private long _lastStamp;
    private long _lastIdle, _lastKernel, _lastUser;
    private long _lastNetDown, _lastNetUp;
    private bool _hasNetBaseline;

    public Sample Collect()
    {
        var sample = new Sample();
        long stamp = Stopwatch.GetTimestamp();
        double elapsed = _lastStamp == 0 ? 0 : (stamp - _lastStamp) / (double)Stopwatch.Frequency;
        _lastStamp = stamp;

        CollectProcesses(sample, elapsed);
        CollectSystem(sample, elapsed);
        return sample;
    }

    // ---- processes ------------------------------------------------------------------------

    private void CollectProcesses(Sample sample, double elapsed)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        var groups = new Dictionary<string, GroupData>(StringComparer.OrdinalIgnoreCase);
        var byPid = new Dictionary<int, GroupData>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                int pid = process.Id;
                if (pid == 0) continue; // System Idle Process

                string name = process.ProcessName;
                long memory = process.WorkingSet64;
                double cpu = 0;

                // Protected system processes refuse the query; remember them so we don't pay for the exception every tick.
                if (!_cpuDenied.Contains(pid))
                {
                    try
                    {
                        TimeSpan total = process.TotalProcessorTime;
                        if (elapsed > 0 && _cpuTimes.TryGetValue(pid, out var previous))
                        {
                            double used = (total - previous).TotalSeconds;
                            if (used > 0) cpu = Math.Min(100, used / (elapsed * cores) * 100);
                        }
                        _cpuTimes[pid] = total;
                    }
                    catch { _cpuDenied.Add(pid); }
                }

                if (!_paths.TryGetValue(pid, out var path))
                {
                    path = Native.GetProcessPath(pid);
                    _paths[pid] = path;
                }

                if (!groups.TryGetValue(name, out var group))
                {
                    group = new GroupData { Key = name.ToLowerInvariant(), Name = name };
                    groups[name] = group;
                }
                group.Pids.Add(pid);
                group.Cpu += cpu;
                group.Memory += memory;
                group.Path ??= path;
                byPid[pid] = group;
                sample.ProcessCount++;
            }
            catch { /* the process exited while we were looking at it */ }
            finally { process.Dispose(); }
        }

        // Forget processes that are gone (their IDs get reused).
        foreach (var pid in _cpuTimes.Keys.Where(p => !byPid.ContainsKey(p)).ToList()) _cpuTimes.Remove(pid);
        foreach (var pid in _paths.Keys.Where(p => !byPid.ContainsKey(p)).ToList()) _paths.Remove(pid);
        _cpuDenied.RemoveWhere(p => !byPid.ContainsKey(p));

        foreach (var (hwnd, pid, title) in TopLevelWindows())
        {
            if (!byPid.TryGetValue(pid, out var group)) continue;
            group.Windows.Add(hwnd);
            if (group.Title.Length == 0) group.Title = title;
        }

        sample.Groups = groups.Values.ToList();
    }

    /// <summary>The windows a user would think of as "open apps".</summary>
    private static List<(IntPtr Hwnd, int Pid, string Title)> TopLevelWindows()
    {
        var result = new List<(IntPtr, int, string)>();
        var buffer = new StringBuilder(512);

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;                    // dialogs, popups
            if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;                                                                            // hidden Store-app shells

            buffer.Clear();
            if (Native.GetWindowTextW(hwnd, buffer, buffer.Capacity) <= 0) return true;
            string title = buffer.ToString();
            if (title == "Program Manager") return true;                                               // the desktop

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            result.Add((hwnd, (int)pid, title));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // ---- system ---------------------------------------------------------------------------

    private void CollectSystem(Sample sample, double elapsed)
    {
        // CPU: kernel time includes idle time.
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long total = (kernel - _lastKernel) + (user - _lastUser);
            long idleDelta = idle - _lastIdle;
            if (_lastKernel != 0 && total > 0)
                sample.CpuTotal = Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        }

        var memory = new Native.MEMORYSTATUSEX { dwLength = 64 };
        if (Native.GlobalMemoryStatusEx(ref memory))
        {
            sample.MemTotal = memory.ullTotalPhys;
            sample.MemAvailable = memory.ullAvailPhys;
        }

        if (Native.GetSystemPowerStatus(out var power))
        {
            sample.HasBattery = power.BatteryFlag != 128 && power.BatteryFlag != 255;
            sample.OnAcPower = power.ACLineStatus == 1;
            sample.Charging = (power.BatteryFlag & 8) != 0;
            sample.BatteryPercent = power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : -1;
            sample.BatterySecondsLeft = power.BatteryLifeTime;
        }

        try
        {
            long down = 0, up = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
            if (_hasNetBaseline && elapsed > 0)
            {
                sample.NetDownBytesPerSec = Math.Max(0, (down - _lastNetDown) / elapsed);
                sample.NetUpBytesPerSec = Math.Max(0, (up - _lastNetUp) / elapsed);
            }
            _lastNetDown = down; _lastNetUp = up; _hasNetBaseline = true;
        }
        catch { /* network stack unavailable */ }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.Network) continue;
                long total = drive.TotalSize, free = drive.AvailableFreeSpace;
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                sample.Drives.Add(new DriveStat
                {
                    Label = $"{label} ({drive.Name.TrimEnd('\\')})",
                    Sub = $"{Format.Bytes(free)} free of {Format.Bytes(total)}",
                    Fraction = total > 0 ? 1.0 - (double)free / total : 0
                });
            }
            catch { /* drive vanished mid-query */ }
        }

        sample.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
    }
}

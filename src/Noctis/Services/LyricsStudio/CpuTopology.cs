using System.Runtime.InteropServices;

namespace Noctis.Services.LyricsStudio;

/// <summary>
/// How many CPU cores can do full-speed work for the speech model: physical cores, and on
/// hybrid CPUs (Intel P/E cores, Apple Silicon) only the performance ones. whisper.cpp splits
/// every matrix operation evenly over its threads and waits for the slowest, so a thread on a
/// hyper-thread sibling or an efficiency core holds the rest back. Null when the OS does not say.
/// </summary>
internal static class CpuTopology
{
    private static readonly Lazy<int?> Cached = new(Detect);

    public static int? PerformanceCores => Cached.Value;

    private static int? Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Windows();
            if (OperatingSystem.IsLinux()) return Linux();
            if (OperatingSystem.IsMacOS()) return Mac();
        }
        catch
        {
            // Unknown topology: the caller falls back to the logical count.
        }
        return null;
    }

    // ── Windows: GetLogicalProcessorInformationEx(RelationProcessorCore) ──

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

    private static int? Windows()
    {
        const int relationProcessorCore = 0;
        var length = 0;
        GetLogicalProcessorInformationEx(relationProcessorCore, IntPtr.Zero, ref length);
        if (length <= 0) return null;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length)) return null;
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: DWORD Relationship, DWORD Size, then
            // PROCESSOR_RELATIONSHIP { BYTE Flags, BYTE EfficiencyClass, ... }.
            var classes = new List<byte>();
            for (var offset = 0; offset + 10 <= length;)
            {
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                if (Marshal.ReadInt32(buffer, offset) == relationProcessorCore)
                    classes.Add(Marshal.ReadByte(buffer, offset + 9));
                offset += size;
            }
            return PerformanceCount(classes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Cores of the highest efficiency class (all of them on a CPU that is not hybrid).</summary>
    internal static int? PerformanceCount(IReadOnlyCollection<byte> efficiencyClasses)
    {
        if (efficiencyClasses.Count == 0) return null;
        var top = efficiencyClasses.Max();
        return efficiencyClasses.Count(c => c == top);
    }

    // ── Linux: sysfs topology; Intel hybrid parts list their P-cores under cpu_core ──

    private static int? Linux()
    {
        var root = "/sys/devices/system/cpu";
        HashSet<int>? only = null;
        if (File.Exists("/sys/devices/cpu_core/cpus"))
            only = ParseCpuList(File.ReadAllText("/sys/devices/cpu_core/cpus"));
        var cores = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(root, "cpu*"))
        {
            var name = Path.GetFileName(dir);
            if (name.Length <= 3 || !int.TryParse(name.AsSpan(3), out var cpu)) continue;
            if (only is not null && !only.Contains(cpu)) continue;
            var core = Path.Combine(dir, "topology", "core_id");
            var package = Path.Combine(dir, "topology", "physical_package_id");
            if (!File.Exists(core)) continue;
            cores.Add((File.Exists(package) ? File.ReadAllText(package).Trim() : "0") + ":" + File.ReadAllText(core).Trim());
        }
        return cores.Count > 0 ? cores.Count : null;
    }

    /// <summary>"0-3,8,10-11" → {0,1,2,3,8,10,11}.</summary>
    internal static HashSet<int> ParseCpuList(string text)
    {
        var set = new HashSet<int>();
        foreach (var part in text.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0) { if (int.TryParse(part, out var one)) set.Add(one); continue; }
            if (int.TryParse(part[..dash], out var from) && int.TryParse(part[(dash + 1)..], out var to))
                for (var i = from; i <= to; i++) set.Add(i);
        }
        return set;
    }

    // ── macOS: hw.perflevel0.physicalcpu (Apple Silicon P-cores), else hw.physicalcpu ──

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, out int value, ref IntPtr size, IntPtr newValue, IntPtr newSize);

    private static int? Mac()
    {
        foreach (var key in new[] { "hw.perflevel0.physicalcpu", "hw.physicalcpu" })
        {
            var size = (IntPtr)sizeof(int);
            if (SysctlByName(key, out var value, ref size, IntPtr.Zero, IntPtr.Zero) == 0 && value > 0) return value;
        }
        return null;
    }
}

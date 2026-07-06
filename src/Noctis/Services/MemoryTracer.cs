using System.Diagnostics;

namespace Noctis.Services;

/// <summary>
/// TEMPORARY diagnostic. When the environment variable NOCTIS_MEMTRACE=1 is set,
/// appends one memory/CPU snapshot line every 5 seconds to noctis_memtrace.log on
/// the Desktop. Read-only: it changes no application behavior and is a complete
/// no-op unless the env var is set.
///
/// Purpose: localize a reported runtime growth (process sitting at multiple GB and
/// climbing on navigation, with continuous CPU). The key signals:
///   - managedMB vs workingSetMB: if managed is small but working set is huge, the
///     growth is NATIVE (LibVLC / Skia bitmaps); if they track together, it's the
///     managed heap.
///   - gc2 / threads / handles climbing: GC pressure (≈ the continuous CPU) and
///     thread/handle leaks (e.g. per-track decoder sessions).
///   - artCacheMB: confirms the artwork cache stays within its cap.
///
/// Remove this file and its single call site (App.Initialize) once diagnosed.
/// </summary>
internal static class MemoryTracer
{
    private static Timer? _timer;

    public static void StartIfEnabled()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_MEMTRACE") != "1")
            return;

        string path;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop))
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(desktop, "noctis_memtrace.log");
        }
        catch { return; }

        var proc = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();

        try { File.AppendAllText(path, $"=== MemTrace start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); }
        catch { return; }

        // System.Threading.Timer: fires on a thread-pool thread; the 5s cadence and
        // a single AppendAllText are negligible, so this never skews what it measures.
        _timer = new Timer(_ =>
        {
            try
            {
                proc.Refresh();
                var managedMB = GC.GetTotalMemory(false) / 1048576;
                var wsMB = proc.WorkingSet64 / 1048576;
                var privMB = proc.PrivateMemorySize64 / 1048576;
                int threads = 0, handles = 0;
                try { threads = proc.Threads.Count; } catch { }
                try { handles = proc.HandleCount; } catch { }

                var line = string.Format(
                    "{0:HH:mm:ss} t={1,5}s managedMB={2,5} workingSetMB={3,6} privateMB={4,6} " +
                    "gc0={5} gc1={6} gc2={7} threads={8} handles={9} artCacheMB={10} artCount={11}\n",
                    DateTime.Now,
                    (int)sw.Elapsed.TotalSeconds,
                    managedMB, wsMB, privMB,
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                    threads, handles,
                    ArtworkCache.ResidentBytes / 1048576, ArtworkCache.Count);

                File.AppendAllText(path, line);
            }
            catch { /* diagnostic only — never disturb the app */ }
        }, null, 0, 5000);
    }
}

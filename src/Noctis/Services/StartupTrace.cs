using System.Diagnostics;
using System.Text;

namespace Noctis.Services;

/// <summary>
/// Milestone timings for the launch path, from the first line of Program.Main to the
/// first painted page.
///
/// Startup had no instrumentation at all, so every claim about what makes it slow — the
/// native libvlc load, the library JSON parse, the index rebuild, the view-model graph —
/// was a guess. Each <see cref="Mark"/> is a Stopwatch read and a list append, so the
/// trace costs nothing worth measuring and stays on in release builds; the summary lands
/// in <see cref="DebugLog"/>, which Settings → About → Developer Mode can copy.
/// </summary>
public static class StartupTrace
{
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly object Lock = new();
    private static readonly List<(string Name, double Ms)> Marks = new();
    private static bool _flushed;

    /// <summary>Milliseconds since the process reached Program.Main.</summary>
    public static double ElapsedMs =>
        (Stopwatch.GetTimestamp() - Origin) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Records a milestone. Safe from any thread; first call wins per name.</summary>
    public static void Mark(string name)
    {
        var ms = ElapsedMs;
        lock (Lock)
        {
            if (_flushed) return;
            Marks.Add((name, ms));
        }
    }

    /// <summary>
    /// Writes the collected marks to <see cref="DebugLog"/> as a table of cumulative and
    /// per-step times, sorted by the order they happened. Idempotent — later calls no-op,
    /// so it is safe to call from more than one completion path.
    /// </summary>
    public static void Flush()
    {
        List<(string Name, double Ms)> snapshot;
        lock (Lock)
        {
            if (_flushed || Marks.Count == 0) return;
            _flushed = true;
            snapshot = Marks.OrderBy(m => m.Ms).ToList();
        }

        var sb = new StringBuilder();
        sb.AppendLine("startup timings (ms from process start):");

        var previous = 0.0;
        (string Name, double Delta) slowest = ("", 0);
        foreach (var (name, ms) in snapshot)
        {
            var delta = ms - previous;
            if (delta > slowest.Delta) slowest = (name, delta);
            sb.AppendLine($"  {ms,8:F0}  (+{delta,7:F0})  {name}");
            previous = ms;
        }

        if (slowest.Delta > 0)
            sb.Append($"  slowest step: {slowest.Name} (+{slowest.Delta:F0} ms)");

        DebugLog.Write("Startup", sb.ToString());
    }
}

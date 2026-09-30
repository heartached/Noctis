using System.Text;

namespace Noctis.Services;

/// <summary>
/// Rate-limits the Developer Mode VLC log bridge per line <em>shape</em> (the line with
/// every digit run replaced by '#'), so a warning storm leaves a session log that still
/// covers a real repro. The first <see cref="BurstPerWindow"/> lines of a shape in a
/// <see cref="WindowMs"/> window are written verbatim; the rest are counted and reported
/// as one "×K more like this in N s: &lt;last line&gt;" summary when that window closes.
///
/// GitHub #70: the old keep-alive loop logged "pulse: starting late (-6624210 us)",
/// "main: playback way too late (6646416): flushing buffers" and "avformat:
/// DEMUX_SET_POSITION: 0" ~48 times a second. The numbers differ on every line and the
/// three kinds interleave, so collapsing only identical consecutive lines caught none
/// and the 5000-line session log filled in two minutes. Distinct lines (a different
/// shape) are never held back by another shape's storm, and suppressed ones are always
/// counted. Not thread-safe: the bridge calls it under its lock. Time is passed in
/// (ms, any monotonic clock) so the policy is unit-testable.
/// </summary>
internal sealed class VlcLogCollapser
{
    public const int BurstPerWindow = 3;
    public const long WindowMs = 10_000;
    // Shapes are tracked for one window only; the cap just bounds a pathological
    // flood of distinct lines (those are written through untracked).
    private const int MaxTrackedShapes = 256;

    private sealed class Run
    {
        public long WindowStartMs;
        public int Written;
        public int Suppressed;
        public long LastSuppressedMs;
        public string? LastSuppressed;
    }

    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);
    private readonly List<string> _expired = new();

    /// <summary>
    /// Feeds one line seen at <paramref name="nowMs"/>. Returns the lines to write now, in
    /// order — summaries of windows that have closed, then the line itself unless it was
    /// suppressed — or null when there is nothing to write. <paramref name="force"/>
    /// writes the line regardless (it still counts toward its shape's burst).
    /// </summary>
    public List<string>? Accept(string line, long nowMs, bool force = false)
    {
        var output = CloseExpired(nowMs, all: false);

        var shape = Shape(line);
        if (!_runs.TryGetValue(shape, out var run))
        {
            if (_runs.Count >= MaxTrackedShapes)
                return Append(output, line);
            run = new Run { WindowStartMs = nowMs };
            _runs[shape] = run;
        }

        if (force || run.Written < BurstPerWindow)
        {
            run.Written++;
            return Append(output, line);
        }

        run.Suppressed++;
        run.LastSuppressed = line;
        run.LastSuppressedMs = nowMs;
        return output;
    }

    /// <summary>Summaries for every window still holding suppressed lines (bridge detach).</summary>
    public List<string>? Flush(long nowMs) => CloseExpired(nowMs, all: true);

    /// <summary>The line with each run of ASCII digits replaced by '#'. Pure; internal for tests.</summary>
    internal static string Shape(string line)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < line.Length; i++)
        {
            if (!char.IsAsciiDigit(line[i]))
            {
                sb?.Append(line[i]);
                continue;
            }
            sb ??= new StringBuilder(line.Length).Append(line, 0, i);
            sb.Append('#');
            while (i + 1 < line.Length && char.IsAsciiDigit(line[i + 1])) i++;
        }
        return sb?.ToString() ?? line;
    }

    private List<string>? CloseExpired(long nowMs, bool all)
    {
        List<string>? output = null;
        foreach (var (shape, run) in _runs)
        {
            if (!all && nowMs - run.WindowStartMs < WindowMs) continue;
            if (run.Suppressed > 0)
            {
                var spanSec = Math.Max(1, (run.LastSuppressedMs - run.WindowStartMs + 999) / 1000);
                (output ??= new()).Add($"×{run.Suppressed} more like this in {spanSec} s: {run.LastSuppressed}");
            }
            _expired.Add(shape);
        }
        foreach (var shape in _expired) _runs.Remove(shape);
        _expired.Clear();
        return output;
    }

    private static List<string> Append(List<string>? output, string line)
    {
        (output ??= new()).Add(line);
        return output;
    }
}

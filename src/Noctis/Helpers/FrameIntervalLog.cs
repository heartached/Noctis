using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Diagnostic, off unless NOCTIS_FRAME_LOG names a file: asks for every animation frame and
/// writes one line per second with how many the UI thread got and how late the worst ones
/// were. A page that scrolls at 30 frames a second on a 240 Hz screen shows here as ~30
/// frames with intervals over 16 ms; the session log only notes freezes over 100 ms, so
/// that kind of lag left no trace (owner report 11-20). Costs a wake-up per frame while on.
/// </summary>
internal sealed class FrameIntervalLog
{
    private readonly TopLevel _top;
    private readonly StreamWriter _out;
    private readonly List<double> _intervals = new(512);
    private long _last;
    private long _windowStart;
    private bool _stopped;

    private FrameIntervalLog(TopLevel top, StreamWriter output)
    {
        _top = top;
        _out = output;
    }

    /// <summary>Starts logging for <paramref name="top"/> when the environment asks for it.</summary>
    public static void StartIfAsked(TopLevel top)
    {
        var path = Environment.GetEnvironmentVariable("NOCTIS_FRAME_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("time,frames,avg_ms,p95_ms,max_ms,over8ms,over17ms,over34ms");
            var log = new FrameIntervalLog(top, writer);
            top.Closed += (_, _) => log._stopped = true;
            log._last = log._windowStart = Stopwatch.GetTimestamp();
            top.RequestAnimationFrame(log.OnFrame);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A diagnostic that cannot open its file stays off.
        }
    }

    private void OnFrame(TimeSpan _)
    {
        if (_stopped) return;
        var now = Stopwatch.GetTimestamp();
        _intervals.Add(Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds);
        _last = now;
        if (Stopwatch.GetElapsedTime(_windowStart, now).TotalMilliseconds >= 1000)
        {
            WriteWindow();
            _windowStart = now;
        }
        _top.RequestAnimationFrame(OnFrame);
    }

    private void WriteWindow()
    {
        if (_intervals.Count == 0) return;
        _intervals.Sort();
        var count = _intervals.Count;
        double sum = 0;
        int over8 = 0, over17 = 0, over34 = 0;
        foreach (var ms in _intervals)
        {
            sum += ms;
            if (ms > 8) over8++;
            if (ms > 17) over17++;
            if (ms > 34) over34++;
        }
        var p95 = _intervals[Math.Min(count - 1, (int)(count * 0.95))];
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss},{count},{sum / count:0.00},{p95:0.00},{_intervals[count - 1]:0.00},{over8},{over17},{over34}"));
        _intervals.Clear();
    }
}

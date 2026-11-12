using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Evidence harness for the long-transcript report (Discord, Luwi, 11-12: an 8.5 h audiobook
/// whose .lrc/.elrc stops at line 3000): what N line-synced or word-synced lines cost on the
/// lyrics page when handed to the view in one piece — load, heap, visuals, a seek, a line
/// change and a frame inside a line. Builds the lines directly, so the parser cap does not
/// apply. Explicit only: set NOCTIS_LONG_LYRICS_HARNESS=1.
/// </summary>
public class LongLyricsCostHarnessTests
{
    private readonly ITestOutputHelper _o;
    public LongLyricsCostHarnessTests(ITestOutputHelper o) => _o = o;

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // Not GetTotalMemory(true): it waits for finalizers, which hung the headless UI thread.
    private static long Heap() { GC.Collect(); return GC.GetTotalMemory(false); }

    private static string Stamp(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>N lines, 2 s apart. Word-timed lines carry 10 words of 0.18 s each.</summary>
    internal static List<LyricLine> Book(int count, bool words)
    {
        var lines = new List<LyricLine>(count);
        for (int i = 0; i < count; i++)
        {
            var start = TimeSpan.FromSeconds(1 + i * 2.0);
            var text = $"Line {i + 1} at {Stamp(start)} it was a bright cold day in April";
            var line = new LyricLine { Timestamp = start, Text = LrcParser.SoftWrap(text) };
            if (words)
            {
                var parts = text.Split(' ');
                var w = new List<WordTiming>(parts.Length);
                for (int k = 0; k < parts.Length; k++)
                {
                    var ws = start + TimeSpan.FromSeconds(k * 0.13);
                    w.Add(new WordTiming { Text = k < parts.Length - 1 ? parts[k] + " " : parts[k], Start = ws, End = ws + TimeSpan.FromSeconds(0.12) });
                }
                line.EndTimestamp = w[^1].End;
                line.Words = w;
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>The same 15,000-line book through the real load path, windowed (LyricsLineWindow):
    /// load, heap, visuals, a quiet slide, a far seek, line changes.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowedBook_PageCost(bool words)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("NOCTIS_LONG_LYRICS_HARNESS") != "1", "set NOCTIS_LONG_LYRICS_HARNESS=1");
        var only = Environment.GetEnvironmentVariable("NOCTIS_LONG_LYRICS_CASES");
        Assert.SkipWhen(!string.IsNullOrEmpty(only) && !only.Split(',').Contains($"window{(words ? "w" : "l")}"), "not selected");
        var view = new LyricsView();
        var win = new Window { Width = 1280, Height = 800, Content = view };
        win.Show();
        Pump();
        var heap0 = Heap();
        var sw = Stopwatch.StartNew();
        using var l = await LongLyricsWindowTests.LoadSidecar(LongLyricsWindowTests.BookLrc(15000, words), words ? ".elrc" : ".lrc");
        var vm = l.Vm;
        view.DataContext = vm;
        Pump();
        var loadMs = sw.Elapsed.TotalMilliseconds;
        try
        {
            var heap = (Heap() - heap0) / 1048576.0;
            var visuals = view.GetVisualDescendants().Count();
            static TimeSpan At(int n, int ms) => LongLyricsWindowTests.LineStart(n) + TimeSpan.FromMilliseconds(ms);

            var changes = new List<double>();
            var slides = new List<double>();
            for (int n = 2; n <= 700; n++)
            {
                sw.Restart();
                LongLyricsWindowTests.PlayAt(vm, At(n, 500));
                Pump();
                changes.Add(sw.Elapsed.TotalMilliseconds);
                var before = vm.LyricsWindowStart;
                sw.Restart();
                LongLyricsWindowTests.PlayAt(vm, At(n, 1300), quiet: true);
                Pump();
                if (vm.LyricsWindowStart != before) slides.Add(sw.Elapsed.TotalMilliseconds);
            }
            sw.Restart();
            LongLyricsWindowTests.PlayAt(vm, At(12345, 600));
            Pump();
            var seekMs = sw.Elapsed.TotalMilliseconds;
            changes.Sort();
            var report = $"N=15000 windowed {(words ? "word" : "line")}: load {loadMs:0} ms (incl. parse), heap +{heap:0} MB, visuals {visuals}, " +
                         $"far seek {seekMs:0} ms, line change mean {changes.Average():0.00} / p90 {changes[(int)(changes.Count * 0.9)]:0.00} / max {changes[^1]:0.00} ms, " +
                         $"{slides.Count} slides mean {(slides.Count > 0 ? slides.Average() : 0):0} / max {(slides.Count > 0 ? slides.Max() : 0):0} ms";
            _o.WriteLine(report);
            if (Environment.GetEnvironmentVariable("NOCTIS_LONG_LYRICS_OUT") is { Length: > 0 } outFile)
                File.AppendAllText(outFile, report + Environment.NewLine);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(1000, false)]
    [InlineData(3000, false)]
    [InlineData(10000, false)]
    [InlineData(30000, false)]
    [InlineData(1000, true)]
    [InlineData(3000, true)]
    [InlineData(10000, true)]
    [InlineData(30000, true)]
    public void LongLyrics_PageCost(int count, bool words)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("NOCTIS_LONG_LYRICS_HARNESS") != "1", "set NOCTIS_LONG_LYRICS_HARNESS=1");
        // Optional "1000,3000" filter; results also go to NOCTIS_LONG_LYRICS_OUT (the runner
        // process outlives a killed vstest host, and its console with it).
        var only = Environment.GetEnvironmentVariable("NOCTIS_LONG_LYRICS_CASES");
        Assert.SkipWhen(!string.IsNullOrEmpty(only) && !only.Split(',').Contains($"{count}{(words ? "w" : "l")}"), "not selected");
        var lines = Book(count, words);
        var vm = LongLyricsKit.MakeViewModel();
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800 };
        try
        {
            win.Show();
            win.Content = view;
            Pump();
            var heap0 = Heap();
            var sw = Stopwatch.StartNew();
            vm.LyricLines.ReplaceAll(lines);
            Pump();
            var loadMs = sw.Elapsed.TotalMilliseconds;
            var heap = (Heap() - heap0) / 1048576.0;
            var visuals = view.GetVisualDescendants().Count();

            var update = typeof(LyricsViewModel).GetMethod("UpdateActiveLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
            typeof(LyricsViewModel).GetField("_hasSyncedLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);

            // Seek to the middle.
            var mid = count / 2;
            sw.Restart();
            update.Invoke(vm, [vm.LyricLines[mid].Timestamp!.Value + TimeSpan.FromMilliseconds(500)]);
            Pump();
            var seekMs = sw.Elapsed.TotalMilliseconds;

            // 30 consecutive line changes (each = UpdateLineOpacities over all lines + the glide's first frame).
            var changes = new List<double>();
            for (int k = 1; k <= 30; k++)
            {
                var t = vm.LyricLines[mid + k].Timestamp!.Value + TimeSpan.FromMilliseconds(500);
                sw.Restart();
                update.Invoke(vm, [t]);
                Pump();
                changes.Add(sw.Elapsed.TotalMilliseconds);
            }

            // 60 frames inside one line (the word clock / glide frames between line changes).
            var frames = new List<double>();
            var baseT = vm.LyricLines[mid + 31].Timestamp!.Value + TimeSpan.FromMilliseconds(400);
            update.Invoke(vm, [baseT]);
            Pump();
            for (int f = 1; f <= 60; f++)
            {
                var t = baseT + TimeSpan.FromMilliseconds(f * 1000.0 / 60 * 0.2);
                sw.Restart();
                update.Invoke(vm, [t]);
                Pump();
                frames.Add(sw.Elapsed.TotalMilliseconds);
            }

            changes.Sort();
            frames.Sort();
            var report = $"N={count} {(words ? "word" : "line")}: load {loadMs:0} ms, heap +{heap:0} MB, visuals {visuals}, " +
                         $"seek {seekMs:0.0} ms, line change mean {changes.Average():0.00} / p90 {changes[26]:0.00} / max {changes[^1]:0.00} ms, " +
                         $"in-line frame mean {frames.Average():0.00} / max {frames[^1]:0.00} ms";
            _o.WriteLine(report);
            if (Environment.GetEnvironmentVariable("NOCTIS_LONG_LYRICS_OUT") is { Length: > 0 } outFile)
                File.AppendAllText(outFile, report + Environment.NewLine);
        }
        finally
        {
            win.Close();
        }
    }
}

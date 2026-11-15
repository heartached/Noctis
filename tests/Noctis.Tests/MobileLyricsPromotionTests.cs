using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone lyrics, what survives a minor GC (S23, 11-15): on the lyrics page each nursery
/// collection stalled 67-98 ms and the heap grew ~1 MB a GC, while the Songs list, scrolled
/// nonstop, collected as often in under 32 ms. A minor GC's pause follows what it has to copy:
/// young objects still referenced, most of all from long-lived ones (lines, rows, word cells,
/// the page). These play the lyrics, collect the nursery at intervals and read what it promoted;
/// the diagnostics walk the live graph from the window and the statics and name each old→young
/// reference (owner type and field → young type).
/// </summary>
[Collection("Phone card timing")]
public class MobileLyricsPromotionTests
{
    private readonly ITestOutputHelper _o;
    public MobileLyricsPromotionTests(ITestOutputHelper o)
    {
        _o = o;
        Log = o.WriteLine;
    }

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    internal static long PhaseJobs, PhaseTick, PhaseAfter;

    private static void Pump()
    {
        var b0 = GC.GetAllocatedBytesForCurrentThread();
        Dispatcher.UIThread.RunJobs();
        var b1 = GC.GetAllocatedBytesForCurrentThread();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var b2 = GC.GetAllocatedBytesForCurrentThread();
        Dispatcher.UIThread.RunJobs();
        var b3 = GC.GetAllocatedBytesForCurrentThread();
        PhaseJobs += b1 - b0;
        PhaseTick += b2 - b1;
        PhaseAfter += b3 - b2;
    }

    private sealed record Lyrics(ShellViewModel Shell, FakeAudioPlayer Player, ShellView View, Window Window, LyricsPage Page);

    /// <summary>The lyrics page open on a playing song, transport faded, as on the phone.</summary>
    private static async Task<Lyrics> OpenLyrics(string ext, string text)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        files.Sidecars[ext] = text;
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work())));
        var view = new ShellView { DataContext = shell };
        var win = new Window { Width = 360, Height = 780, Content = view };
        win.Show();
        win.UpdateLayout();
        var track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(300) };
        shell.Player.PlayTracks(new[] { track }, 0);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!shell.Lyrics.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(shell.Lyrics.IsSynced);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        Motion.FinishAll();
        win.UpdateLayout();
        player.RaisePositionChanged(TimeSpan.FromMilliseconds(1000));
        Pump();
        var page = view.GetVisualDescendants().OfType<LyricsPage>().Single();
        page.HideControlsIfPlaying();
        return new Lyrics(shell, player, view, win, page);
    }

    internal readonly record struct Survival(int Frames, int Collections, long PromotedPerGc, long MaxPromoted,
        long UiBytesPerFrame, long AllBytesPerFrame)
    {
        public override string ToString() =>
            $"frames={Frames} GCs={Collections} promoted/GC mean={PromotedPerGc / 1024} KB max={MaxPromoted / 1024} KB"
            + $" alloc/frame UI={UiBytesPerFrame} B all threads={AllBytesPerFrame} B";
    }

    /// <summary>
    /// Runs <paramref name="frame"/> at 60 fps for <paramref name="seconds"/> (after a warm-up
    /// of <paramref name="warmSeconds"/>, then a full collection so the page's own objects are
    /// old), collecting the nursery every <paramref name="intervalMs"/> and reading what that
    /// collection promoted.
    /// </summary>
    internal static Survival RunCollecting(Action<double> frame, double seconds, double intervalMs = 1500, double warmSeconds = 1.5)
    {
        var clock = Stopwatch.StartNew();
        double nextFrame = 0;
        void Step(double t)
        {
            frame(t);
            Pump();
        }
        while (clock.Elapsed.TotalMilliseconds < warmSeconds * 1000)
        {
            var t = clock.Elapsed.TotalMilliseconds;
            if (t < nextFrame) { Thread.Sleep(1); continue; }
            nextFrame = t + 1000.0 / 60;
            Step(t);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        HoldNursery();
        var natural0 = GC.CollectionCount(0);
        PhaseJobs = PhaseTick = PhaseAfter = 0;

        var start = clock.Elapsed.TotalMilliseconds;
        var nextGc = start + intervalMs;
        var frames = 0;
        var promoted = new List<long>();
        long uiBytes = 0;
        var all0 = GC.GetTotalAllocatedBytes(precise: true);
        while (clock.Elapsed.TotalMilliseconds < start + seconds * 1000)
        {
            var t = clock.Elapsed.TotalMilliseconds;
            if (t >= nextGc)
            {
                nextGc = t + intervalMs;
                natural0 = GC.CollectionCount(0) - natural0;
                Assert.True(natural0 == 0, $"{natural0} nursery collections ran inside an interval");
                promoted.Add(CollectNursery());
                HoldNursery();
                natural0 = GC.CollectionCount(0);
                continue;
            }
            if (t < nextFrame) { Thread.Sleep(1); continue; }
            nextFrame = t + 1000.0 / 60;
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            Step(t);
            uiBytes += GC.GetAllocatedBytesForCurrentThread() - b0;
            frames++;
        }
        var all = GC.GetTotalAllocatedBytes(precise: true) - all0;
        ReleaseNursery();
        return new Survival(frames, promoted.Count,
            promoted.Count > 0 ? (long)promoted.Average() : 0, promoted.Count > 0 ? promoted.Max() : 0,
            uiBytes / Math.Max(1, frames), all / Math.Max(1, frames));
    }

    /// <summary>The phone's 32 MB nursery fills every 7-9 s; the desktop runtime's gen0 budget is a
    /// few MB, so it would collect on its own several times an interval. No GC runs until
    /// <see cref="CollectNursery"/> (or the region's budget is spent, which the run asserts).</summary>
    internal static void HoldNursery() => Assert.True(GC.TryStartNoGCRegion(200L * 1024 * 1024, disallowFullBlockingGC: true));

    internal static void ReleaseNursery()
    {
        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
    }

    internal static long LastFinalizable, LastPinned;
    internal static Action<string>? Log;

    /// <summary>A gen0 collection; returns the bytes it promoted.</summary>
    internal static long CollectNursery()
    {
        ReleaseNursery();
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        var info = GC.GetGCMemoryInfo(GCKind.Ephemeral);
        LastFinalizable = info.FinalizationPendingCount;
        LastPinned = info.PinnedObjectsCount;
        Log?.Invoke($"  gc#{info.Index} gen{info.Generation} promoted {info.PromotedBytes / 1024} KB, finalizable {info.FinalizationPendingCount}, pinned {info.PinnedObjectsCount}");
        return info.Generation == 0 ? info.PromotedBytes : 0;
    }

    /// <summary>Plays <paramref name="seconds"/> of the song from the page's own frame loop,
    /// polled every 250 ms (the phone's Media3 poll).</summary>
    private static Action<double> Playing(Lyrics l)
    {
        double nextPoll = 0;
        return t =>
        {
            if (t < nextPoll) return;
            nextPoll = t + 250;
            l.Player.RaisePositionChanged(TimeSpan.FromMilliseconds(1000 + t));
        };
    }

    /// <summary>
    /// Under the opaque lyrics the app, its sheets and the tab bar (four glass panels, each a
    /// backdrop snapshot, blur and lens pass) were repainted on every frame the lyrics moved,
    /// unseen. They leave the tree once the lyrics cover them and come back as the lyrics go;
    /// the bar comes back for the Queue, which raises it over the lyrics.
    /// </summary>
    [AvaloniaFact]
    public async Task UnderTheLyrics_TheAppAndTabBarAreNotDrawn()
    {
        var l = await OpenLyrics(".lrc", MobileLyricsAllocationTests.Lrc(3));
        try
        {
            var app = l.View.FindControl<Control>("AppLayer")!;
            var chrome = l.View.FindControl<Control>("ChromeLayer")!;
            var sheets = l.View.FindControl<Control>("SheetHost")!;
            Assert.False(app.IsVisible);
            Assert.False(sheets.IsVisible);
            Assert.False(chrome.IsVisible);
            Assert.Empty(l.Window.GetVisualDescendants().OfType<Noctis.Controls.GlassPanel>().Where(g => g.IsEffectivelyVisible));
            var lens = Noctis.Controls.GlassLens.LensPasses;
            for (var i = 0; i < 30; i++) { Pump(); Thread.Sleep(16); }
            Assert.Equal(lens, Noctis.Controls.GlassLens.LensPasses);

            l.Shell.ToggleQueueCommand.Execute(null);
            Motion.FinishAll();
            l.Window.UpdateLayout();
            Assert.True(chrome.IsVisible);
            Assert.False(app.IsVisible);
            l.Shell.ToggleQueueCommand.Execute(null);
            l.View.FindControl<QueuePage>("QueueSheet")!.FinishClose();   // the slide away (no clock here)
            Assert.False(chrome.IsVisible);

            l.Shell.ToggleLyricsCommand.Execute(null);
            Assert.True(app.IsVisible);   // back before the fade starts
            Assert.True(chrome.IsVisible);
            Assert.True(sheets.IsVisible);
            Motion.FinishAll();
            Assert.True(app.IsVisible);
        }
        finally
        {
            l.Window.Close();
        }
    }

    /// <summary>The lyrics' dither layer draws exactly what Now Playing's dither brush draws.</summary>
    [AvaloniaFact]
    public void TheLyricsDither_MatchesTheBrush()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        byte[] Capture(Control content)
        {
            var window = new Window { Width = 200, Height = 150, Background = Avalonia.Media.Brushes.Black, Content = content };
            window.Show();
            var frame = window.CaptureRenderedFrame()!;
            using var fb = frame.Lock();
            var pixels = new byte[fb.RowBytes * 150];
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, pixels, 0, pixels.Length);
            window.Close();
            return pixels;
        }
        var brush = Capture(new Border { Background = NowPlayingPage.Dither });
        var layer = Capture(new DitherLayer());
        Assert.Contains(brush, b => b != 0 && b != 255);
        Assert.Equal(brush, layer);
    }

    // ── Diagnostics ──

    /// <summary>Diagnostics, one case per process (a window left from an earlier case skews the
    /// next): NOCTIS_PROMOTION_CASE="lrc,3,0" (format, line gap in s, layers under hidden).</summary>
    [AvaloniaFact]
    public async Task Report_WhatSurvivesTheNursery_Case()
    {
        var spec = Environment.GetEnvironmentVariable("NOCTIS_PROMOTION_CASE");
        if (string.IsNullOrEmpty(spec) || !spec.Contains(',')) return;
        var parts = spec.Split(',');
        await Report_WhatSurvivesTheNursery("." + parts[0], double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2] == "1");
    }

    private async Task Report_WhatSurvivesTheNursery(string ext, double gap, bool hideUnder)
    {
        var l = await OpenLyrics(ext, ext == ".lrc" ? MobileLyricsAllocationTests.Lrc(gap) : MobileLyricsAllocationTests.Ttml(gap, 8));
        try
        {
            if (hideUnder)
                foreach (var name in new[] { "AppLayer", "ChromeLayer", "SheetDim" })
                    l.View.FindControl<Control>(name)!.IsVisible = false;
            var play = Playing(l);
            var exp = Environment.GetEnvironmentVariable("NOCTIS_PROMOTION_EXP") ?? "";
            if (exp.Contains("notrans") || exp.Contains("noeffect"))
            {
                var basePlay = play;
                play = t =>
                {
                    basePlay(t);
                    foreach (var b in l.Page.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("lyric-line")))
                    {
                        if (exp.Contains("notrans") && b.Transitions is not { Count: 0 }) b.Transitions = new Avalonia.Animation.Transitions();
                        if (exp.Contains("noeffect") && b.Effect != null) b.Effect = null;
                    }
                };
            }
            foreach (var name in new[] { "WashDither", "Backdrop", "LyricsShade", "TopFade", "BottomFade" })
                if (exp.Contains("no" + name)) l.Page.GetVisualDescendants().OfType<Control>().First(c => c.Name == name).IsVisible = false;
            if (exp.Contains("ditherCache")) l.Page.GetVisualDescendants().OfType<Control>().First(c => c.Name == "WashDither").CacheMode = new Avalonia.Media.BitmapCache();
            var lens0 = Noctis.Controls.GlassLens.LensPasses;
            var glass = l.Window.GetVisualDescendants().OfType<Noctis.Controls.GlassPanel>().Where(g => g.IsEffectivelyVisible).ToList();
            _o.WriteLine($"visible glass panels: {string.Join(", ", glass.Select(g => g.Name ?? g.Classes.FirstOrDefault() ?? g.GetType().Name))}");
            using var sampler = new AllocationSampler();
            var cost = RunCollecting(play, 12, intervalMs: 3000);
            _o.WriteLine($"{ext} gap {gap} hide {hideUnder}: {cost} lens passes {Noctis.Controls.GlassLens.LensPasses - lens0}");
            _o.WriteLine($"phases (UI bytes/frame): jobs={PhaseJobs / Math.Max(1, cost.Frames)} tick={PhaseTick / Math.Max(1, cost.Frames)} after={PhaseAfter / Math.Max(1, cost.Frames)}");
            foreach (var line in sampler.Top(25)) _o.WriteLine(line);
            foreach (var line in sampler.Marks()) _o.WriteLine(line);
            // Then name what the old graph holds young: eight walks, each 400 ms of play after a
            // full collection, so they catch line changes and their transitions too (a first
            // walk runs the statics' initialisers, so they are old by the next).
            _ = YoungGraph.Report(new object[] { l.Window, l.Shell }, 1).ToList();
            var edges = new Dictionary<string, (int Count, int Objects, long Bytes)>();
            var clock = Stopwatch.StartNew();
            for (var walk = 0; walk < 8; walk++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                HoldNursery();
                var until = clock.ElapsedMilliseconds + 400;
                double next = 0;
                while (clock.ElapsedMilliseconds < until)
                {
                    var t = clock.Elapsed.TotalMilliseconds;
                    if (t < next) { Thread.Sleep(1); continue; }
                    next = t + 1000.0 / 60;
                    play(10_000 + t);
                    Pump();
                }
                YoungGraph.Walk(new object[] { l.Window, l.Shell }, edges);
                ReleaseNursery();
            }
            foreach (var line in YoungGraph.Format(edges, 40, 8)) _o.WriteLine(line);
            ReleaseNursery();
        }
        finally
        {
            l.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Report_SongListScroll()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_PROMOTION_CASE") != "songs") return;
        var songs = Enumerable.Range(0, 600).Select(i => MobileFixtures.Song($"Song {i:D3}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view, 360, 780);
        try
        {
            rig.Shell.OpenSongsCommand.Execute(null);
            MobileFixtures.Land(window);
            var scroll = MobileFixtures.Named<ScrollViewer>(view, "SongScroll");
            var y = 0.0;
            var dir = 1;
            Action<double> frame = _ =>
            {
                y += dir * 25;
                if (y > 15000 || y < 0) dir = -dir;
                scroll.Offset = new Vector(0, y);
            };
            using var sampler = new AllocationSampler();
            var cost = RunCollecting(frame, 12, intervalMs: 3000);
            _o.WriteLine($"songs: {cost}");
            foreach (var line in sampler.Top(25)) _o.WriteLine(line);
            foreach (var line in sampler.Marks()) _o.WriteLine(line);
            _ = YoungGraph.Report(new object[] { window, rig.Shell }, 1).ToList();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            HoldNursery();
            for (var i = 0; i < 180; i++) { frame(0); Pump(); Thread.Sleep(16); }
            foreach (var line in YoungGraph.Report(new object[] { window, rig.Shell }, 25)) _o.WriteLine(line);
            ReleaseNursery();
        }
        finally
        {
            window.Close();
        }
    }
}

/// <summary>Test diagnostics: the runtime's allocation sample (one event per ~100 KB allocated),
/// by type.</summary>
internal sealed class AllocationSampler : System.Diagnostics.Tracing.EventListener
{
    private readonly Dictionary<string, (int Samples, long Bytes)> _types = new();
    private volatile bool _on;

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
            _on = true;
        }
    }

    private static readonly string[] MarkKinds = { "stack", "finalize queue", "handles", "older gen (cards)", "sized ref", "overflow", "dependent handles", "new finalize queue", "steal", "bgc roots" };
    private readonly Dictionary<string, long> _marks = new();
    private long _finalizationPromoted;
    private int _gcs;

    /// <summary>What the nursery collections promoted, by the root that held it (GCMarkWithType).</summary>
    public IEnumerable<string> Marks()
    {
        lock (_types)
        {
            yield return $"promoted by root over {_gcs} gen0 GCs (finalization-promoted {_finalizationPromoted / 1024} KB):";
            foreach (var (k, v) in _marks.OrderByDescending(kv => kv.Value).ToList())
                yield return $"  {v / 1024,8} KB  {k}";
        }
    }

    private bool _inGen0;

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (e.EventName is { } n && e.Payload != null && e.PayloadNames != null)
        {
            if (n.StartsWith("GCStart"))
            {
                var depth = Convert.ToInt32(e.Payload[e.PayloadNames.IndexOf("Depth")]);
                _inGen0 = depth == 0;
                if (_inGen0) lock (_types) _gcs++;
                return;
            }
            if (n.StartsWith("GCMarkWithType") && _inGen0)
            {
                var kind = Convert.ToInt32(e.Payload[e.PayloadNames.IndexOf("Type")]);
                var bytes = Convert.ToInt64(e.Payload[e.PayloadNames.IndexOf("Bytes")]);
                var root = kind < MarkKinds.Length ? MarkKinds[kind] : kind.ToString();
                lock (_types) _marks[root] = (_marks.TryGetValue(root, out var m) ? m : 0) + bytes;
                return;
            }
            if (n.StartsWith("GCHeapStats") && _inGen0)
            {
                var at = e.PayloadNames.IndexOf("FinalizationPromotedSize");
                if (at >= 0) lock (_types) _finalizationPromoted += Convert.ToInt64(e.Payload[at]);
                return;
            }
        }
        if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick") || e.Payload == null) return;
        var name = e.PayloadNames!.IndexOf("TypeName") is var i and >= 0 ? e.Payload[i] as string ?? "?" : "?";
        var amount = e.PayloadNames.IndexOf("AllocationAmount64") is var j and >= 0 ? Convert.ToInt64(e.Payload[j]) : 100_000;
        lock (_types)
        {
            _types.TryGetValue(name, out var t);
            _types[name] = (t.Samples + 1, t.Bytes + amount);
        }
    }

    public IEnumerable<string> Top(int n)
    {
        lock (_types)
        {
            var total = _types.Values.Sum(v => v.Bytes);
            yield return $"allocation samples ({(_on ? "on" : "OFF")}): ~{total / 1024} KB";
            foreach (var (k, v) in _types.OrderByDescending(kv => kv.Value.Bytes).Take(n).ToList())
                yield return $"  {v.Bytes * 100.0 / Math.Max(1, total),5:F1}% {v.Samples,4}  {k}";
        }
    }
}

/// <summary>
/// Test diagnostics: walks the object graph from the given roots and every static field of the
/// Avalonia and Noctis assemblies by reflection, and groups each young object (GC generation 0)
/// under the first old object that references it: "OldType.field → YoungType", with the count
/// and the rough size of the young graph hanging there. That is what a nursery collection must
/// copy because an old object holds it.
/// </summary>
internal static class YoungGraph
{
    private static readonly Dictionary<Type, FieldInfo[]> Fields = new();

    private static FieldInfo[] FieldsOf(Type type)
    {
        if (Fields.TryGetValue(type, out var cached)) return cached;
        var list = new List<FieldInfo>();
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var ft = f.FieldType;
                if (ft.IsPrimitive || ft.IsPointer || ft.IsFunctionPointer || ft.IsEnum || ft.IsByRefLike || ft == typeof(IntPtr) || ft == typeof(UIntPtr)
                    || ft == typeof(decimal) || ft == typeof(DateTime) || ft == typeof(TimeSpan)) continue;
                list.Add(f);
            }
        }
        return Fields[type] = list.ToArray();
    }

    private static bool Skip(object o) => o is Type or MemberInfo or Assembly or Module or Pointer || o.GetType().IsCOMObject;

    /// <summary>Calls <paramref name="visit"/>(field label, child) for each reference the object holds
    /// (struct fields flattened into their holder).</summary>
    private static void Children(object o, Action<string, object> visit)
    {
        var type = o.GetType();
        if (type.IsArray)
        {
            var elem = type.GetElementType()!;
            if (elem.IsPrimitive || elem.IsEnum || elem.IsPointer) return;
            var array = (Array)o;
            if (array.Rank != 1) return;
            var label = elem.IsValueType ? "[]." : "[]";
            for (var i = 0; i < array.Length; i++)
            {
                var item = array.GetValue(i);
                if (item == null) continue;
                if (elem.IsValueType) Flatten(item, "[]", visit);
                else visit(label, item);
            }
            return;
        }
        foreach (var f in FieldsOf(type))
        {
            object? v;
            try { v = f.GetValue(o); } catch { continue; }
            if (v == null) continue;
            if (f.FieldType.IsValueType) Flatten(v, f.Name, visit);
            else visit(f.Name, v);
        }
    }

    private static void Flatten(object boxed, string prefix, Action<string, object> visit, int depth = 0)
    {
        if (depth > 4) return;
        foreach (var f in FieldsOf(boxed.GetType()))
        {
            object? v;
            try { v = f.GetValue(boxed); } catch { continue; }
            if (v == null) continue;
            if (f.FieldType.IsValueType) Flatten(v, prefix + "." + f.Name, visit, depth + 1);
            else visit(prefix + "." + f.Name, v);
        }
    }

    private static long SizeOf(object o) => o switch
    {
        string s => 22 + 2L * s.Length,
        Array a => 24 + (long)a.Length * (a.GetType().GetElementType()!.IsPrimitive ? Math.Max(1, Buffer.ByteLength(a) / Math.Max(1, a.Length)) : 8),
        _ => 16 + 8L * Math.Max(1, FieldsOf(o.GetType()).Length),
    };

    private static string Name(Type t)
    {
        var tick = t.Name.IndexOf('`');
        if (!t.IsGenericType || tick < 0) return t.Name;
        return t.Name[..tick] + "<" + string.Join(",", t.GetGenericArguments().Select(Name)) + ">";
    }

    public static IEnumerable<string> Report(IEnumerable<object> roots, int top)
    {
        var edges = new Dictionary<string, (int Count, int Objects, long Bytes)>();
        Walk(roots, edges);
        return Format(edges, top, 1);
    }

    public static IEnumerable<string> Format(Dictionary<string, (int Count, int Objects, long Bytes)> edges, int top, int walks)
    {
        var total = edges.Values.Aggregate((Count: 0, Objects: 0, Bytes: 0L), (a, e) => (a.Count + e.Count, a.Objects + e.Objects, a.Bytes + e.Bytes));
        yield return $"young reachable over {walks} walks: {total.Objects} objects ~{total.Bytes / 1024} KB under {total.Count} old->young refs";
        foreach (var (key, e) in edges.OrderByDescending(kv => kv.Value.Bytes).Take(top))
            yield return $"  {e.Bytes,8} B {e.Objects,6} obj {e.Count,5} refs  {key}";
    }

    public static void Walk(IEnumerable<object> roots, Dictionary<string, (int Count, int Objects, long Bytes)> edges)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        var statics = new List<object>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name ?? "";
            if (name == "Noctis.Tests" || !name.StartsWith("Avalonia") && !name.StartsWith("Noctis") && !name.StartsWith("SkiaSharp") && !name.StartsWith("CommunityToolkit")) continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var type in types)
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral || f.FieldType.IsPrimitive || f.FieldType.IsPointer || f.FieldType.IsFunctionPointer || f.FieldType.IsByRefLike) continue;
                    if (f.GetCustomAttribute<ThreadStaticAttribute>() != null) continue;
                    try
                    {
                        if (f.GetValue(null) is { } v)
                        {
                            if (f.FieldType.IsValueType) Flatten(v, f.Name, (_, c) => statics.Add(c));
                            else statics.Add(v);
                        }
                    }
                    catch { }
                }
            }
        }

        foreach (var r in roots.Concat(statics))
        {
            if (Skip(r) || !visited.Add(r)) continue;
            if (GC.GetGeneration(r) == 0) { Young("(root) -> " + Name(r.GetType()), r); continue; }
            stack.Push(r);
        }
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            var owner = o.GetType();
            Children(o, (field, child) =>
            {
                if (Skip(child) || !visited.Add(child)) return;
                if (GC.GetGeneration(child) == 0)
                    Young($"{Name(owner)}.{field} -> {Name(child.GetType())}", child);
                else
                    stack.Push(child);
            });
        }

        void Young(string key, object first)
        {
            var objects = 0;
            long bytes = 0;
            var young = new Stack<object>();
            young.Push(first);
            while (young.Count > 0)
            {
                var y = young.Pop();
                objects++;
                bytes += SizeOf(y);
                Children(y, (_, c) =>
                {
                    if (Skip(c) || !visited.Add(c)) return;
                    if (GC.GetGeneration(c) == 0) young.Push(c);
                    else stack.Push(c);
                });
            }
            edges.TryGetValue(key, out var e);
            edges[key] = (e.Count + 1, e.Objects + objects, e.Bytes + bytes);
        }

    }
}

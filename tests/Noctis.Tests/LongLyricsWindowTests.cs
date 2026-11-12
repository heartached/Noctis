using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LocalApi;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Long transcripts (Discord, Luwi 11-12: an 8.5 h audiobook whose .lrc stopped at line 3000):
/// every line is parsed and kept, the lists show a window of them that follows playback, seeks
/// and the reader's scrolling, and anything that writes lyrics back reads every line.
/// </summary>
public class LongLyricsWindowTests
{
    private static string Stamp(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

    /// <summary>LRC of <paramref name="count"/> lines 2 s apart from 0:01, each naming itself
    /// ("Line 12345 at 6:51:29"). Word-timed (ELRC) when <paramref name="words"/>.</summary>
    internal static string BookLrc(int count, bool words = false)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            var start = TimeSpan.FromSeconds(1 + i * 2.0);
            var text = $"Line {i + 1} at {(int)start.TotalHours}:{start.Minutes:00}:{start.Seconds:00}";
            sb.Append('[').Append(Stamp(start)).Append(']');
            if (words)
            {
                var parts = text.Split(' ');
                for (int k = 0; k < parts.Length; k++)
                    sb.Append('<').Append(Stamp(start + TimeSpan.FromMilliseconds(k * 250))).Append('>').Append(parts[k]).Append(k < parts.Length - 1 ? " " : "");
                sb.Append('<').Append(Stamp(start + TimeSpan.FromMilliseconds(parts.Length * 250))).Append('>');
            }
            else
                sb.Append(text);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    internal static TimeSpan LineStart(int number) => TimeSpan.FromSeconds(1 + (number - 1) * 2.0);

    // ── Window math ──

    [Fact]
    public void Window_OnlyPastTheListCap()
    {
        Assert.False(LyricsLineWindow.IsWindowed(LrcParser.ListMaxLines));
        Assert.True(LyricsLineWindow.IsWindowed(LrcParser.ListMaxLines + 1));
        Assert.Null(LyricsLineWindow.PlaybackMove(0, LrcParser.ListMaxLines, 2999, quiet: true));
        Assert.Null(LyricsLineWindow.ScrollMove(0, LrcParser.ListMaxLines, 1));
    }

    [Fact]
    public void IndexAt_FindsTheLinePlaybackIsIn()
    {
        var lines = LrcParser.Parse(BookLrc(5000));
        Assert.Equal(0, LyricsLineWindow.IndexAt(lines, TimeSpan.Zero));
        Assert.Equal(0, LyricsLineWindow.IndexAt(lines, TimeSpan.FromSeconds(2.9)));
        Assert.Equal(1, LyricsLineWindow.IndexAt(lines, TimeSpan.FromSeconds(3)));
        Assert.Equal(4321, LyricsLineWindow.IndexAt(lines, LineStart(4322) + TimeSpan.FromSeconds(1)));
        Assert.Equal(4999, LyricsLineWindow.IndexAt(lines, TimeSpan.FromHours(30)));
        // Untimed lines sort last and are never "the line playback is in".
        lines.Add(new LyricLine { Text = "untimed" });
        Assert.Equal(4999, LyricsLineWindow.IndexAt(lines, TimeSpan.FromHours(30)));
    }

    [Fact]
    public void PlaybackMove_SoftStepsWhenQuiet_HardAtTheEdge_JumpsOnASeek()
    {
        const int total = 15000;
        var start = 1000;
        var size = LyricsLineWindow.Size;
        // Comfortably inside: stay.
        Assert.Null(LyricsLineWindow.PlaybackMove(start, total, start + LyricsLineWindow.Behind, quiet: true));
        // Fewer than SoftAhead lines ahead: only at a quiet moment, a step that puts the line Behind from the top.
        var soft = start + size - LyricsLineWindow.SoftAhead;
        Assert.Null(LyricsLineWindow.PlaybackMove(start, total, soft, quiet: false));
        Assert.Equal(soft - LyricsLineWindow.Behind, LyricsLineWindow.PlaybackMove(start, total, soft, quiet: true));
        Assert.True(soft - LyricsLineWindow.Behind - start <= 50, "forward steps stay small");
        // At the hard margin: now, quiet or not.
        var hard = start + size - LyricsLineWindow.HardMargin;
        Assert.Equal(hard - LyricsLineWindow.Behind, LyricsLineWindow.PlaybackMove(start, total, hard, quiet: false));
        // A seek outside the window: there.
        Assert.Equal(12000 - LyricsLineWindow.Behind, LyricsLineWindow.PlaybackMove(start, total, 12000, quiet: false));
        Assert.Equal(0, LyricsLineWindow.PlaybackMove(start, total, 5, quiet: false));
        // Backwards: too little behind the line.
        Assert.Equal(start + 10 - LyricsLineWindow.Behind, LyricsLineWindow.PlaybackMove(start, total, start + 10, quiet: false));
        // The ends of the list never slide past.
        Assert.Null(LyricsLineWindow.PlaybackMove(0, total, 3, quiet: false));
        Assert.Null(LyricsLineWindow.PlaybackMove(total - size, total, total - 1, quiet: false));
        Assert.Equal(total - size, LyricsLineWindow.StartAround(total - 1, total));
    }

    [Fact]
    public void ScrollMove_StepsWithinTheList()
    {
        Assert.Equal(1200, LyricsLineWindow.ScrollMove(1000, 15000, 1));
        Assert.Equal(800, LyricsLineWindow.ScrollMove(1000, 15000, -1));
        Assert.Equal(0, LyricsLineWindow.ScrollMove(100, 15000, -1));
        Assert.Null(LyricsLineWindow.ScrollMove(0, 15000, -1));
        Assert.Equal(15000 - LyricsLineWindow.Size, LyricsLineWindow.ScrollMove(15000 - LyricsLineWindow.Size - 50, 15000, 1));
        Assert.Null(LyricsLineWindow.ScrollMove(15000 - LyricsLineWindow.Size, 15000, 1));
    }

    [Fact]
    public void ShiftTo_KeepsSharedItems_AndOnlyChangesTheEnds()
    {
        var all = Enumerable.Range(0, 100).Select(i => new LyricLine { Text = i.ToString() }).ToList();
        var c = new BulkObservableCollection<LyricLine>();
        c.ReplaceAll(all.GetRange(10, 30));
        var events = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => events.Add(e.Action);

        c.ShiftTo(all.GetRange(25, 30));
        Assert.Equal(all.GetRange(25, 30), c);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);

        events.Clear();
        c.ShiftTo(all.GetRange(5, 30));
        Assert.Equal(all.GetRange(5, 30), c);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);

        events.Clear();
        c.ShiftTo(all.GetRange(60, 30));
        Assert.Equal(all.GetRange(60, 30), c);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
    }

    [AvaloniaFact]
    public void ShiftTo_AnItemsControlKeepsTheContainersOfSharedLines()
    {
        var all = Enumerable.Range(0, 200).Select(i => new LyricLine { Text = $"line {i}" }).ToList();
        var c = new BulkObservableCollection<LyricLine>();
        c.ReplaceAll(all.GetRange(0, 80));
        var items = new ItemsControl { ItemsSource = c, ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LyricLine>((l, _) => new TextBlock { Text = l.Text }) };
        var win = new Window { Content = new ScrollViewer { Content = items }, Width = 400, Height = 300 };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var kept = items.ContainerFromItem(all[50]);
            Assert.NotNull(kept);
            c.ShiftTo(all.GetRange(40, 80));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(kept, items.ContainerFromItem(all[50]));
            Assert.Equal(80, items.ItemsPanelRoot!.Children.Count);
            for (int i = 0; i < 80; i++)
                Assert.Same(all[40 + i], items.ItemFromContainer(items.ItemsPanelRoot.Children[i]));
            c.ShiftTo(all.GetRange(10, 80));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(kept, items.ContainerFromItem(all[50]));
            for (int i = 0; i < 80; i++)
                Assert.Same(all[10 + i], items.ItemFromContainer(items.ItemsPanelRoot.Children[i]));
        }
        finally
        {
            win.Close();
        }
    }

    // ── Parsers: every line is kept; the phone keeps the old cap ──

    [Fact]
    public void Parsers_KeepEveryLineOfALongTranscript()
    {
        Assert.Equal(15000, LrcParser.Parse(BookLrc(15000)).Count);
        Assert.Equal(15000, LrcParser.Parse(BookLrc(15000, words: true)).Count(l => l.HasWords));
        Assert.Equal(LrcParser.ListMaxLines, LrcParser.Parse(BookLrc(15000), LrcParser.ListMaxLines).Count);
        var plain = string.Join("\n", Enumerable.Range(1, 9000).Select(i => $"row {i}"));
        Assert.Equal(9000, LrcParser.SplitPlain(plain).Length);
        Assert.Equal(LrcParser.ListMaxLines, LrcParser.SplitPlain(plain, LrcParser.ListMaxLines).Length);
        // The hard guard still bounds a hostile file.
        var hostile = string.Concat(Enumerable.Range(0, LrcParser.MaxLines + 10).Select(i => $"[{Stamp(TimeSpan.FromSeconds(i * 0.5))}]x\n"));
        Assert.Equal(LrcParser.MaxLines, LrcParser.Parse(hostile).Count);
    }

    [Fact]
    public void PhoneLoader_StillParsesAListsWorth()
    {
        var track = new Track { Title = "Book", FilePath = "book.mp3", SyncedLyrics = BookLrc(5000) };
        var loaded = LyricsLoader.Load(track, new NoFiles(), joinSplitWords: true);
        Assert.True(loaded.IsSynced);
        Assert.Equal(LrcParser.ListMaxLines, loaded.Lines.Count);
    }

    private sealed class NoFiles : ITrackFileAccess
    {
        public SidecarFile? ReadSidecar(string trackPath, IReadOnlyList<string> extensions) => null;
        public Stream? OpenAudio(string trackPath) => null;
    }

    // ── Things that write lyrics back read every line (they were cut at 3000) ──

    private static string BookTtml(int count)
    {
        var sb = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>");
        for (int i = 0; i < count; i++)
        {
            var s = TimeSpan.FromSeconds(1 + i * 2.0);
            sb.Append($"<p begin=\"{s:hh\\:mm\\:ss\\.fff}\" end=\"{s + TimeSpan.FromSeconds(1.5):hh\\:mm\\:ss\\.fff}\">Line {i + 1}</p>");
        }
        return sb.Append("</div></body></tt>").ToString();
    }

    [Fact]
    public void TtmlToLrc_AndTheStudioLoader_KeepEveryLine()
    {
        // TtmlParser.Parse stopped at 3,000 <p>s, so a long .ttml reached Lyrics Studio (and its
        // save), "Save Lyrics to File" and Edit Info's TTML import 3,000 lines long.
        var ttml = BookTtml(5000);
        var lrc = TtmlParser.ToLrc(ttml)!;
        Assert.Equal(5000, lrc.Split('\n').Count(l => l.StartsWith('[')));

        var dir = Path.Combine(Path.GetTempPath(), "noctis-long-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "book.ttml"), ttml);
            var existing = Services.LyricsStudio.ExistingLyricsLoader.Load(new Track { FilePath = Path.Combine(dir, "book.mp3") });
            Assert.NotNull(existing);
            Assert.Equal(5000, existing!.Lines.Count);
            Assert.Equal("Line 5000", existing.Lines[^1].Text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WritingLyricsToTags_PutsEveryLineInTheSylt()
    {
        // EmbeddedLyrics.BuildSylt parsed the LRC with the 3,000-line cap: an MP3's SYLT frame
        // (what other players read) kept the first 3,000 lines of a longer transcript.
        var dir = Path.Combine(Path.GetTempPath(), "noctis-long-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = EmbeddedLyricsTagTests.Copy(dir, "mp3");
            var lrc = BookLrc(5000);
            Assert.True(new MetadataService().WriteLyrics(path, null, lrc));
            using var f = TagLib.File.Create(path);
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false);
            var sylt = Assert.Single(id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>());
            Assert.Equal(5000, sylt.Text.Length);
            Assert.Equal("Line 5000 at 2:46:39", sylt.Text[^1].Text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── The view model: a window that follows playback ──

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition(), $"timed out waiting for: {what}");
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal sealed record Loaded(LyricsViewModel Vm, PlayerViewModel Player, string Dir) : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    internal static async Task<Loaded> LoadSidecar(string lrc, string ext = ".lrc")
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-long-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "book" + ext), lrc);
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = LongLyricsKit.MakeViewModel(player);
        player.Duration = TimeSpan.FromHours(9);
        player.CurrentTrack = new Track { Title = "1984", Artist = "Orwell", FilePath = Path.Combine(dir, "book.mp3") };
        vm.SetLyricsSurfaceVisible(true);
        vm.EnsureLyricsForCurrentTrack();
        await WaitUntil(() => vm.LyricLines.Count > 0 && vm.UnsyncedLines.Count > 0, "sidecar to load");
        typeof(LyricsViewModel).GetField("_hasSyncedLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, vm.IsSynced);
        return new Loaded(vm, player, dir);
    }

    private static readonly MethodInfo UpdateMethod =
        typeof(LyricsViewModel).GetMethod("UpdateActiveLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LastChange =
        typeof(LyricsViewModel).GetField("_lastLineChangeTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Playback at <paramref name="position"/>; <paramref name="quiet"/> as if no line had changed for a while.</summary>
    internal static void PlayAt(LyricsViewModel vm, TimeSpan position, bool quiet = false)
    {
        if (quiet) LastChange.SetValue(vm, 0L);
        UpdateMethod.Invoke(vm, [position]);
    }

    private static string ActiveText(LyricsViewModel vm) => vm.LyricLines[vm.ActiveLineIndex].Text.Replace('\n', ' ');

    [AvaloniaFact]
    public async Task ShortLyrics_AreNeverWindowed()
    {
        using var l = await LoadSidecar(BookLrc(LrcParser.ListMaxLines));
        var vm = l.Vm;
        Assert.False(vm.IsLyricsWindowed);
        Assert.Equal(LrcParser.ListMaxLines, vm.LyricLines.Count);
        Assert.Same(vm.LyricLines, vm.AllLyricLines);
        var moves = 0;
        vm.LyricsWindowMoving += (_, _) => moves++;
        foreach (var n in new[] { 1, 1500, 2999, 3000, 10 })
        {
            PlayAt(vm, LineStart(n) + TimeSpan.FromMilliseconds(900), quiet: true);
            Assert.StartsWith($"Line {n} at", ActiveText(vm));
        }
        Assert.Equal(0, moves);
    }

    [AvaloniaFact]
    public async Task LongTranscript_KeepsEveryLine_ShowsAWindow_AndFollowsPlayback()
    {
        using var l = await LoadSidecar(BookLrc(15000));
        var vm = l.Vm;
        Assert.True(vm.IsLyricsWindowed);
        Assert.Equal(15000, vm.AllLyricLines.Count);
        Assert.Equal(LyricsLineWindow.Size, vm.LyricLines.Count);
        // The Plain tab mirrors the window.
        Assert.Equal(vm.LyricLines.Select(x => x.Text), vm.UnsyncedLines.Select(x => x.Text));

        var moving = 0;
        var moved = 0;
        vm.LyricsWindowMoving += (_, _) => { Assert.Equal(moved, moving); moving++; };
        vm.LyricsWindowMoved += (_, _) => { moved++; Assert.Equal(moving, moved); };
        var resets = 0;
        vm.LyricLines.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };

        // Play the first 2,000 lines in order, with a quiet moment inside each line.
        for (int n = 1; n <= 2000; n++)
        {
            PlayAt(vm, LineStart(n) + TimeSpan.FromMilliseconds(500));
            PlayAt(vm, LineStart(n) + TimeSpan.FromMilliseconds(1200), quiet: true);
            Assert.StartsWith($"Line {n} at", ActiveText(vm));
            Assert.Same(vm.AllLyricLines[vm.LyricsWindowStart + vm.ActiveLineIndex], vm.LyricLines[vm.ActiveLineIndex]);
            if (vm.LyricsWindowStart > 0)
                Assert.InRange(vm.ActiveLineIndex, LyricsLineWindow.HardMargin, LyricsLineWindow.Size - LyricsLineWindow.HardMargin);
        }
        Assert.True(moved >= 15, $"the window slid {moved} times over 2,000 lines");
        Assert.Equal(moving, moved);
        Assert.Equal(0, resets);
        Assert.Equal(vm.LyricLines.Count(x => !x.IsIntroPlaceholder), vm.UnsyncedLines.Count);
        Assert.Equal(vm.LyricLines[0].Text, vm.UnsyncedLines[0].Text);
        Assert.Equal(1, vm.LyricLines.Count(x => x.IsActive));
    }

    [AvaloniaFact]
    public async Task LongTranscript_SeekAnywhere_LandsOnTheRightLine()
    {
        using var l = await LoadSidecar(BookLrc(15000, words: true), ".elrc");
        var vm = l.Vm;
        Assert.Equal(15000, vm.AllLyricLines.Count);
        foreach (var n in new[] { 12345, 14999, 15000, 1, 7000, 6990, 3001, 2 })
        {
            PlayAt(vm, LineStart(n) + TimeSpan.FromMilliseconds(600));
            Assert.StartsWith($"Line {n} at", ActiveText(vm));
            Assert.Equal(1, vm.LyricLines.Count(x => x.IsActive));
        }
        // The lyrics offset moves the line, windowed or not.
        l.Player.CurrentTrack!.LyricsOffsetMs = 4000;
        PlayAt(vm, LineStart(9000) + TimeSpan.FromMilliseconds(600));
        Assert.StartsWith("Line 8998 at", ActiveText(vm));
    }

    [AvaloniaFact]
    public async Task LongTranscript_ScrollingToTheEdge_ExtendsTheWindow_UntilASeek()
    {
        using var l = await LoadSidecar(BookLrc(15000));
        var vm = l.Vm;
        PlayAt(vm, LineStart(5000) + TimeSpan.FromMilliseconds(600));
        var start = vm.LyricsWindowStart;
        vm.ScrollLyricsWindow(1);
        vm.ScrollLyricsWindow(1);
        Assert.Equal(start + 2 * LyricsLineWindow.ScrollStep, vm.LyricsWindowStart);
        // Playback does not pull it back while the reader holds it…
        PlayAt(vm, LineStart(5001) + TimeSpan.FromMilliseconds(600), quiet: true);
        Assert.Equal(start + 2 * LyricsLineWindow.ScrollStep, vm.LyricsWindowStart);
        vm.ScrollLyricsWindow(-1);
        Assert.Equal(start + LyricsLineWindow.ScrollStep, vm.LyricsWindowStart);
        // …until a seek.
        l.Player.SeekTo(LineStart(5002) + TimeSpan.FromMilliseconds(600));
        PlayAt(vm, LineStart(5002) + TimeSpan.FromMilliseconds(600), quiet: true);
        Assert.StartsWith("Line 5002 at", ActiveText(vm));
        Assert.Equal(LyricsLineWindow.Behind, vm.ActiveLineIndex);
    }

    [AvaloniaFact]
    public async Task LongTranscript_EditSeed_LocalApi_AndShare_SeeEveryLine()
    {
        using var l = await LoadSidecar(BookLrc(15000));
        var vm = l.Vm;
        // The LRC editor seed (its save rewrites the file) held the 3,000 lines the parser kept.
        var seed = vm.BuildSeedFromLoadedLines()!;
        Assert.Equal(15000, seed.Split('\n').Count(x => x.StartsWith('[')));
        Assert.Contains("Line 15000 at 8:19:59", seed);

        var snapshot = new LyricsViewModelLyricsSource(() => vm).Snapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(15000, snapshot!.Lines.Count);

        PlayAt(vm, LineStart(9000) + TimeSpan.FromMilliseconds(600));
        var share = (IReadOnlyList<LyricLine>)typeof(LyricsViewModel)
            .GetMethod("ShareSourceLines", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
        Assert.Equal(LrcParser.ListMaxLines, share.Count);
        Assert.Contains(share, x => x.Text.StartsWith("Line 9000 at"));
    }

    [AvaloniaFact]
    public async Task LongPlainLyrics_AreWindowed_AndScrollable()
    {
        var plain = string.Join("\n", Enumerable.Range(1, 8000).Select(i => $"Plain row {i}"));
        using var l = await LoadSidecar(plain);
        var vm = l.Vm;
        Assert.False(vm.IsSynced);
        Assert.True(vm.IsLyricsWindowed);
        Assert.Equal(8000, vm.AllLyricLines.Count);
        Assert.Equal("Plain row 1", vm.UnsyncedLines[0].Text);
        for (int i = 0; i < 40; i++) vm.ScrollLyricsWindow(1);
        Assert.Equal("Plain row 8000", vm.UnsyncedLines[^1].Text);
        Assert.Equal(vm.LyricLines.Count, vm.UnsyncedLines.Count);
    }

    // ── The views: a slide moves nothing on screen ──

    private static double ViewportTop(ItemsControl items, ScrollViewer viewer, LyricLine line) =>
        ((Control)items.ContainerFromItem(line)!).TranslatePoint(default, viewer)!.Value.Y;

    [AvaloniaFact]
    public async Task WindowSlide_KeepsTheLinesOnScreenInPlace_OnPageAndPanel()
    {
        using var l = await LoadSidecar(BookLrc(15000));
        var vm = l.Vm;
        var page = new LyricsView { DataContext = vm };
        var panel = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 900, Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,420"), Children = { page, panel } } };
        Grid.SetColumn(panel, 1);
        try
        {
            win.Show();
            for (int i = 0; i < 20; i++) { Pump(); await Task.Delay(10); }
            var pageItems = page.FindControl<ItemsControl>("LyricsItemsControl")!;
            var pageViewer = page.FindControl<ScrollViewer>("LyricsScrollViewer")!;
            var panelItems = panel.FindControl<ItemsControl>("PanelItemsControl")!;
            var panelViewer = panel.FindControl<ScrollViewer>("PanelScrollViewer")!;

            // Play to the line just before a soft slide, and let the glide settle.
            var startBefore = vm.LyricsWindowStart;
            // 1-based: the line at index Size - SoftAhead, the first a quiet moment slides for.
            var n = startBefore + LyricsLineWindow.Size - LyricsLineWindow.SoftAhead + 1;
            PlayAt(vm, LineStart(n) + TimeSpan.FromMilliseconds(300));
            for (int i = 0; i < 120; i++) { Pump(); await Task.Delay(8); }
            Assert.Equal(startBefore, vm.LyricsWindowStart);
            var active = vm.LyricLines[vm.ActiveLineIndex];
            var pageY = ViewportTop(pageItems, pageViewer, active);
            var panelY = ViewportTop(panelItems, panelViewer, active);
            var pageNeighbour = vm.LyricLines[vm.ActiveLineIndex + 3];
            var pageNeighbourY = ViewportTop(pageItems, pageViewer, pageNeighbour);

            // A quiet moment inside the same line: the window slides.
            PlayAt(vm, LineStart(n) + TimeSpan.FromMilliseconds(1500), quiet: true);
            // The layout pass that precedes any frame (no render tick in between).
            win.UpdateLayout();
            Assert.True(vm.LyricsWindowStart > startBefore, "the window should have slid");
            Assert.Same(active, vm.LyricLines[vm.ActiveLineIndex]);
            // Before any frame is drawn, and once frames have run.
            Assert.Equal(pageY, ViewportTop(pageItems, pageViewer, active), 1.0);
            Assert.Equal(panelY, ViewportTop(panelItems, panelViewer, active), 1.0);
            Assert.Equal(pageNeighbourY, ViewportTop(pageItems, pageViewer, pageNeighbour), 1.0);
            for (int i = 0; i < 60; i++) { Pump(); await Task.Delay(8); }
            Assert.Equal(pageY, ViewportTop(pageItems, pageViewer, active), 1.0);
            Assert.Equal(panelY, ViewportTop(panelItems, panelViewer, active), 1.0);
            Assert.False(vm.IsAutoFollowPaused, "the slide must not read as a user scroll");

            // A seek far away: the page lands on the new line.
            PlayAt(vm, LineStart(11000) + TimeSpan.FromMilliseconds(600));
            for (int i = 0; i < 60; i++) { Pump(); await Task.Delay(8); }
            Assert.StartsWith("Line 11000 at", ActiveText(vm));
            var landed = ViewportTop(pageItems, pageViewer, vm.LyricLines[vm.ActiveLineIndex]);
            Assert.InRange(landed, 0, pageViewer.Viewport.Height * 0.6);
        }
        finally
        {
            win.Close();
        }
    }
}

/// <summary>Shared stubs for the long-lyrics tests.</summary>
internal static class LongLyricsKit
{
    internal sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    internal sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    internal sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    internal static LyricsViewModel MakeViewModel(PlayerViewModel? player = null)
    {
        player ??= new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
    }
}

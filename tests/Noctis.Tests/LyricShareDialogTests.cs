using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08/09: Share lyrics as the rounded pill pop-up, plus the bugs found on the way.
/// No real ffmpeg: the clip tests run a stand-in script that writes a partial file and waits.
/// </summary>
public class LyricShareDialogTests
{
    internal static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    internal static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static Track Song(string title = "TUTU", string artist = "6ix9ine", string? filePath = null) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, FilePath = filePath ?? string.Empty,
    };

    // ── Card renderer ──

    private static readonly string[] LongLines = Enumerable.Range(1, 8)
        .Select(i => $"You can't do this 'cause you ain't me, it drives you crazy every single night, line {i}")
        .ToArray();

    /// <summary>
    /// Bug: eight long lines (the selection cap) ran out of the card. FitLyrics stops shrinking
    /// at 34 px and the rows were drawn whatever their height, past the wordmark, the card's
    /// box and the bottom of the image. Checked on the pixels: everything below the layout's
    /// bottom margin must stay the plain background.
    /// </summary>
    [Theory]
    [InlineData(ShareCardFormat.Square, ShareCardLayout.Panel, 60)]
    [InlineData(ShareCardFormat.Square, ShareCardLayout.Poster, 72)]
    [InlineData(ShareCardFormat.Story, ShareCardLayout.Panel, 260)]
    [InlineData(ShareCardFormat.Story, ShareCardLayout.Poster, 260)]
    public void LongSelection_StaysInsideTheCard(ShareCardFormat format, ShareCardLayout layout, int marginBottom)
    {
        var spec = new LyricCardSpec
        {
            Title = "TUTU", Artist = "6ix9ine", Lines = LongLines, Format = format, Layout = layout,
            Background = ShareBackground.Solid, SolidColorHex = "#000000", TextColor = ShareTextColor.White,
        };
        using var bmp = SKBitmap.Decode(ShareCardRenderer.RenderLyricCardStyled(spec));
        int bandTop = bmp.Height - marginBottom + 4;
        int lit = 0;
        for (int y = bandTop; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red + c.Green + c.Blue > 90) lit++;
        }
        Assert.True(lit == 0, $"{lit} text pixels below y={bandTop} on the {format} {layout} card");
    }

    /// <summary>
    /// Bug: on the Artwork background the Text option did nothing. The renderer forced white
    /// text there whatever was picked, so Black drew the very same card as White. A plain grey
    /// cover makes it countable: only dark text (and the wordmark) can draw near-black pixels.
    /// </summary>
    [Theory]
    [InlineData(ShareCardLayout.Panel)]
    [InlineData(ShareCardLayout.Poster)]
    public void BlackText_AppliesOnTheArtworkBackground(ShareCardLayout layout)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-share-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var cover = Path.Combine(dir, "grey.png");
            using (var grey = new SKBitmap(600, 600))
            {
                grey.Erase(new SKColor(0xC8, 0xC8, 0xC8));
                using var image = SKImage.FromBitmap(grey);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(cover, data.ToArray());
            }
            int Count(ShareTextColor text, Func<int, bool> luma)
            {
                var spec = new LyricCardSpec
                {
                    Title = "TUTU", Artist = "6ix9ine", ArtworkPath = cover, Layout = layout,
                    Lines = new[] { "You can't do this", "it drives you crazy" },
                    Background = ShareBackground.Artwork, TextColor = text,
                };
                using var bmp = SKBitmap.Decode(ShareCardRenderer.RenderLyricCardStyled(spec));
                int n = 0;
                foreach (var c in bmp.Pixels)
                    if (luma((c.Red * 2126 + c.Green * 7152 + c.Blue * 722) / 10000)) n++;
                return n;
            }

            Assert.True(Count(ShareTextColor.Black, l => l < 50) > 2000, "Black drew no dark text on the artwork card");
            Assert.True(Count(ShareTextColor.White, l => l > 240) > 2000, "White no longer draws white text on the artwork card");
            Assert.Equal(0, Count(ShareTextColor.White, l => l < 50));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── Live preview ──

    /// <summary>A busy JPEG cover of <paramref name="size"/>² (a hi-res release's size).</summary>
    private static string WriteCover(string dir, int size)
    {
        var path = Path.Combine(dir, "cover.jpg");
        using var bmp = new SKBitmap(size, size);
        var rnd = new Random(1);
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(new SKColor(30, 60, 120));
            for (int i = 0; i < 400; i++)
            {
                paint.Color = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                canvas.DrawCircle(rnd.Next(size), rnd.Next(size), rnd.Next(size / 20, size / 4), paint);
            }
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>
    /// Owner 10-12: a change to Text, Background, Format or Layout took about a second to
    /// show on the card. Each change waited out the 200 ms typing debounce, then re-decoded
    /// the cover from disk, drew the card at 1080 px, PNG-encoded it and decoded the PNG back
    /// (headless, 3000 px cover: 340-530 ms a change). The card has to follow at once.
    /// </summary>
    [AvaloniaFact]
    public void OptionChanges_ReachThePreview_AtOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-share-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var track = Song();
        track.AlbumArtworkPath = WriteCover(dir, 3000);
        var vm = new LyricShareViewModel(track, new[]
        {
            "You can't do this 'cause you ain't me", "it drives you crazy every single night",
            "third line here", "fourth line here",
        }, 0);
        try
        {
            Assert.True(PumpUntil(() => vm.Preview != null, 10000), "preview never rendered");
            var changes = new (string Name, Action Apply)[]
            {
                ("Text: White", () => vm.TextColorIndex = 1),
                ("Text: Black", () => vm.TextColorIndex = 2),
                ("Background: Artwork", () => vm.SelectedBackground = vm.BackgroundChoices[0]),
                ("Format: 9:16", () => vm.FormatIndex = 1),
                ("Layout: Poster", () => vm.LayoutIndex = 1),
                ("Background: Navy", () => vm.SelectedBackground = vm.BackgroundChoices.First(c => c.Hex == "#0D2137")),
                ("Format: 1:1", () => vm.FormatIndex = 0),
                ("Layout: Card", () => vm.LayoutIndex = 0),
                ("Background: Auto", () => vm.SelectedBackground = vm.BackgroundChoices[1]),
            };
            var times = new List<(string Name, long Ms)>();
            var shapes = new List<(string Name, bool Story, PixelSize Size)>();
            foreach (var (name, apply) in changes)
            {
                var before = vm.Preview;
                var sw = Stopwatch.StartNew();
                apply();
                Assert.True(PumpUntil(() => vm.IsPreviewCurrent && !ReferenceEquals(vm.Preview, before), 5000),
                    $"{name} never reached the preview");
                times.Add((name, sw.ElapsedMilliseconds));
                shapes.Add((name, vm.IsStory, vm.Preview!.PixelSize));
            }
            var report = string.Join(", ", times.Select(t => $"{t.Name} {t.Ms} ms"));
            var median = times.Select(t => t.Ms).OrderBy(ms => ms).ElementAt(times.Count / 2);
            Assert.True(median <= 100, $"median {median} ms: {report}");
            Assert.True(times.Max(t => t.Ms) <= 250, report);
            // The bitmap that landed is the card for the new options (9:16 is tall).
            foreach (var (name, story, size) in shapes)
                Assert.True(story ? size.Height > size.Width : size.Height == size.Width, $"after {name} the preview is {size}");
        }
        finally
        {
            vm.Detach();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── Clip export ──

    /// <summary>Fakes the converter service so ExportClipAsync finds "ffmpeg" (our script).</summary>
    internal sealed class FfmpegOnly(string path) : IAudioConverterService, IServiceProvider
    {
        public string? GetFfmpegPath() => path;
        public Task<string?> ValidateFfmpegAsync(string? p = null, CancellationToken ct = default) => Task.FromResult<string?>("ffmpeg");
        public Task<ConvertSummary> ConvertAsync(IReadOnlyList<Track> tracks, AudioConvertOptions options,
            IProgress<ConvertProgress> progress, CancellationToken ct) => Task.FromResult(new ConvertSummary());
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>A stand-in ffmpeg: writes a few bytes to its last argument (the output, as
    /// ffmpeg opens it at the start) and then works for 30 s.</summary>
    internal static string WriteSlowFfmpeg(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(dir, "ffmpeg.cmd");
            File.WriteAllText(cmd,
                "@echo off\r\nset \"last=\"\r\n:loop\r\nif \"%~1\"==\"\" goto done\r\nset \"last=%~1\"\r\nshift\r\ngoto loop\r\n" +
                ":done\r\n>\"%last%\" echo partial\r\nping -n 31 127.0.0.1 >nul\r\n");
            return cmd;
        }
        var sh = Path.Combine(dir, "ffmpeg");
        File.WriteAllText(sh, "#!/bin/sh\nfor a; do last=\"$a\"; done\necho partial > \"$last\"\nsleep 30\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    /// <summary>
    /// Bug: closing the dialog during Save Video on plain (or line-synced) lyrics did not stop
    /// the export. That path never made a cancellation source (only the karaoke path did), so
    /// Detach's cancel had nothing to cancel: ffmpeg kept writing the clip after the dialog
    /// was gone, and a cut-off file stayed where the user saved it.
    /// </summary>
    // App.Services is process-wide; safe here because the suite runs one class at a time
    // (HeadlessTestApp: DisableTestParallelization), and it is always restored.
    [AvaloniaFact]
    public void ClosingMidClip_StopsFfmpeg_AndRemovesThePartialFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-share-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var previous = App.Services;
        try
        {
            App.Services = new FfmpegOnly(WriteSlowFfmpeg(dir));
            var audio = Path.Combine(dir, "song.flac");
            File.WriteAllText(audio, "audio");
            var vm = new LyricShareViewModel(Song(filePath: audio), new[] { "one", "two", "three" }, 0);
            Assert.True(PumpUntil(() => vm.Preview != null), "preview never rendered");

            var output = Path.Combine(dir, "clip.mp4");
            var before = WorkClips();
            string? work = null;
            var export = vm.ExportClipAsync(output);
            Assert.True(PumpUntil(() => (work = NewWorkClip(before)) != null, 10000), "ffmpeg stand-in never started");

            vm.Detach(); // what closing the dialog does

            Assert.True(PumpUntil(() => export.IsCompleted, 8000), "the export kept running after the dialog closed");
            Assert.False(File.Exists(output), "the cut-off clip was left behind");
            Assert.False(File.Exists(work), "the unfinished work file was left behind");
            Assert.False(vm.IsRendering);
        }
        finally
        {
            App.Services = previous;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Bug: closing the dialog while a line's edit box was emptied rendered the card again
    /// after the dialog was gone. Detach puts the line's text back (EndEdit) while it still
    /// listens to the lines, so the card re-rendered; with word-timed lyrics that render
    /// re-armed the 30 Hz karaoke timer, which went on drawing frames for a closed dialog.
    /// </summary>
    [AvaloniaFact]
    public void Closing_WhileALineIsBlanked_DoesNotRenderAgain()
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var song = Song();
        player.CurrentTrack = song;
        var lines = new[] { "one two", "three four", "five six" };
        var stamps = lines.Select((_, i) => (TimeSpan?)TimeSpan.FromSeconds(i * 5)).ToList();
        var words = lines.Select((l, i) => (IReadOnlyList<WordTiming>?)l.Split(' ')
            .Select((w, k) => new WordTiming { Text = w, Start = TimeSpan.FromSeconds(i * 5 + k) }).ToList()).ToList();
        var vm = new LyricShareViewModel(song, lines, stamps, player, words);
        Assert.True(PumpUntil(() => vm.AnimatedPreview != null, 5000), "the karaoke preview never started");

        vm.BeginEdit(vm.Lines[0]);
        vm.Lines[0].Text = "  ";
        vm.Detach(); // what closing the dialog does

        PumpUntil(() => false, 700);
        Assert.Equal("one two", vm.Lines[0].Text);
        Assert.Null(vm.AnimatedPreview);
    }

    // ── Playback sync ──

    /// <summary>
    /// Bug: with Sync on, the dialog kept following the player after the song changed. It
    /// read Position without checking which track it belongs to, so the next song's clock
    /// re-picked this song's lines (here: 45 s into song B selected song A's lines 5-8).
    /// </summary>
    [AvaloniaFact]
    public void Sync_StopsFollowing_WhenAnotherSongPlays()
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var songA = Song("A");
        player.CurrentTrack = songA;
        var lines = Enumerable.Range(0, 10).Select(i => $"line {i}").ToList();
        var stamps = Enumerable.Range(0, 10).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 10)).ToList();
        var vm = new LyricShareViewModel(songA, lines, stamps, player);
        try
        {
            Assert.True(vm.SyncEnabled);
            Assert.Equal(new[] { 0, 1, 2, 3 }, Selected(vm));

            player.CurrentTrack = Song("B");
            player.Position = TimeSpan.FromSeconds(45);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { 0, 1, 2, 3 }, Selected(vm));
            Assert.DoesNotContain(vm.Lines, l => l.IsCurrent);
        }
        finally { vm.Detach(); }
    }

    /// <summary>
    /// Bug (same root as above): an unsynced clip starts at the player's position, and that
    /// was read from whatever song played by then — B's 45 s became the start of A's clip.
    /// </summary>
    [AvaloniaFact]
    public void ClipStart_IgnoresAnotherSongsClock()
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var songA = Song("A");
        player.CurrentTrack = songA;
        player.Position = TimeSpan.FromSeconds(30);
        var vm = new LyricShareViewModel(songA, new[] { "one", "two" }, null, player);
        try
        {
            Assert.Equal(30, vm.GetClipTiming().StartSeconds);

            player.CurrentTrack = Song("B");
            player.Position = TimeSpan.FromSeconds(45);
            Assert.Equal(0, vm.GetClipTiming().StartSeconds);
        }
        finally { vm.Detach(); }
    }

    /// <summary>
    /// Bug: the startup sweep meant for clip leftovers only enumerated directories, but the
    /// still clip's frame is a file (noctis-clip-{guid}.png in the temp root). A crash or kill
    /// mid-export left that multi-MB PNG behind for good.
    /// </summary>
    [Fact]
    public void StartupSweep_RemovesAStaleClipFrame()
    {
        var stale = Path.Combine(Path.GetTempPath(), $"noctis-clip-{Guid.NewGuid():N}.png");
        var fresh = Path.Combine(Path.GetTempPath(), $"noctis-clip-{Guid.NewGuid():N}.png");
        File.WriteAllText(stale, "frame");
        File.WriteAllText(fresh, "frame");
        File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
        try
        {
            Noctis.Helpers.PngExportHelper.SweepStaleTempDirs();
            Assert.False(File.Exists(stale), "a day-old clip frame survived the sweep");
            Assert.True(File.Exists(fresh), "a clip frame in use was swept");
        }
        finally
        {
            try { File.Delete(stale); } catch { }
            try { File.Delete(fresh); } catch { }
        }
    }

    /// <summary>A finished clip replaces the file picked to replace, and no work file stays.</summary>
    [AvaloniaFact]
    public void FinishedExport_ReplacesTheChosenFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-share-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var previous = App.Services;
        try
        {
            string ffmpeg;
            if (OperatingSystem.IsWindows())
            {
                ffmpeg = Path.Combine(dir, "ffmpeg.cmd");
                File.WriteAllText(ffmpeg,
                    "@echo off\r\nset \"last=\"\r\n:loop\r\nif \"%~1\"==\"\" goto done\r\nset \"last=%~1\"\r\nshift\r\ngoto loop\r\n" +
                    ":done\r\n>\"%last%\" echo new clip\r\n");
            }
            else
            {
                ffmpeg = Path.Combine(dir, "ffmpeg");
                File.WriteAllText(ffmpeg, "#!/bin/sh\nfor a; do last=\"$a\"; done\necho new clip > \"$last\"\n");
                File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            App.Services = new FfmpegOnly(ffmpeg);
            var audio = Path.Combine(dir, "song.flac");
            File.WriteAllText(audio, "audio");
            var output = Path.Combine(dir, "existing.mp4");
            File.WriteAllText(output, "old clip");
            var vm = new LyricShareViewModel(Song(filePath: audio), new[] { "one" }, 0);
            try
            {
                var before = WorkClips();
                var export = vm.ExportClipAsync(output);
                Assert.True(PumpUntil(() => export.IsCompleted, 10000));
                Assert.Equal(Localization.Loc.T("ShareLyrics.Saved"), export.Result);
                Assert.Equal("new clip", File.ReadAllText(output).Trim());
                Assert.Null(NewWorkClip(before));
            }
            finally { vm.Detach(); }
        }
        finally
        {
            App.Services = previous;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>The clip's work files (ffmpeg's output while it runs) in the temp folder.</summary>
    private static HashSet<string> WorkClips() =>
        new(Directory.EnumerateFiles(Path.GetTempPath(), "noctis-clip-*.mp4"), StringComparer.OrdinalIgnoreCase);

    /// <summary>A work file that was not there <paramref name="before"/>: this run's.</summary>
    private static string? NewWorkClip(HashSet<string> before) =>
        Directory.EnumerateFiles(Path.GetTempPath(), "noctis-clip-*.mp4").FirstOrDefault(f => !before.Contains(f));

    private static bool StartsWithPartial(string path)
    {
        try { return File.ReadAllText(path).StartsWith("partial"); }
        catch (IOException) { return false; }
    }

    private static int[] Selected(LyricShareViewModel vm)
        => vm.Lines.Select((l, i) => (l, i)).Where(p => p.l.IsSelected).Select(p => p.i).ToArray();

    // ── Selection (not bugs; guards for the new rows) ──

    /// <summary>
    /// The owner's "lines wrap very narrowly": multi-line LRC lines (GitHub #116) reached the
    /// list with their "\n" breaks (live check 10-09: "(Great John on the\nbeat, by the way)"),
    /// so the full-width rows broke after 2-4 words. Each lyric line is one line in the list,
    /// on the card and in Copy.
    /// </summary>
    [AvaloniaFact]
    public void MultiLineLyricLines_AreOneLine_InTheListAndOnTheCard()
    {
        var lines = new[] { "(Great John on the\nbeat, by the way)", "First **** play,\r\n I bet he die", "plain line" };
        var vm = new LyricShareViewModel(Song(), lines, 0);
        try
        {
            Assert.Equal("(Great John on the beat, by the way)", vm.Lines[0].Text);
            Assert.Equal("First **** play, I bet he die", vm.Lines[1].Text);
            Assert.Equal("plain line", vm.Lines[2].Text);
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[0]);
            Assert.Equal(new[] { "(Great John on the beat, by the way)" }, vm.CardLines);
        }
        finally { vm.Detach(); }
    }

    /// <summary>The card shows the picked lines in lyric order, whatever order they were picked in.</summary>
    [AvaloniaFact]
    public void CardLines_FollowLyricOrder_NotPickOrder()
    {
        var lines = Enumerable.Range(0, 8).Select(i => $"line {i}").ToList();
        var vm = new LyricShareViewModel(Song(), lines, 0);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            Assert.False(vm.HasSelection);
            vm.ToggleLine(vm.Lines[5]);
            vm.ToggleLine(vm.Lines[1]);
            vm.ToggleLine(vm.Lines[3]);
            Assert.Equal(new[] { "line 1", "line 3", "line 5" }, vm.CardLines);
            Assert.Equal(Localization.Loc.T("ShareLyrics.SelectedCount", 3, LyricShareViewModel.MaxLines), vm.SelectionSummary);
        }
        finally { vm.Detach(); }
    }

    /// <summary>The 8-line cap: the ninth pick is undone and the footer says why.</summary>
    [AvaloniaFact]
    public void NinthLine_IsRefused_WithAStatus()
    {
        var vm = new LyricShareViewModel(Song(), Enumerable.Range(0, 12).Select(i => $"line {i}").ToList(), 0);
        try
        {
            for (int i = 0; i < 9; i++)
                if (!vm.Lines[i].IsSelected) vm.ToggleLine(vm.Lines[i]);
            Assert.Equal(LyricShareViewModel.MaxLines, vm.SelectedCount);
            Assert.False(vm.Lines[8].IsSelected);
            Assert.Equal(Localization.Loc.T("ShareLyrics.UpToLines", LyricShareViewModel.MaxLines), vm.StatusText);
        }
        finally { vm.Detach(); }
    }

    /// <summary>
    /// Bug: double-clicking a ninth line opened its edit box although the 8-line cap refused
    /// to pick it. Every keystroke then changed a line that is not on the card, so the edit
    /// silently never showed. With the cap reached the line stays as it is, and the footer
    /// says why.
    /// </summary>
    [AvaloniaFact]
    public void EditingANinthLine_IsRefused_LikePickingIt()
    {
        var vm = new LyricShareViewModel(Song(), Enumerable.Range(0, 12).Select(i => $"line {i}").ToList(), 0);
        try
        {
            for (int i = 0; i < 8; i++)
                if (!vm.Lines[i].IsSelected) vm.ToggleLine(vm.Lines[i]);
            vm.BeginEdit(vm.Lines[9]);
            Assert.False(vm.Lines[9].IsSelected);
            Assert.False(vm.Lines[9].IsEditing, "an edit box opened for a line the card cannot take");
            Assert.Equal(Localization.Loc.T("ShareLyrics.UpToLines", LyricShareViewModel.MaxLines), vm.StatusText);
        }
        finally { vm.Detach(); }
    }

    /// <summary>Owner 10-12: picked neighbours read as one block in the list. Each picked line
    /// knows whether the line above / below is picked too, through taps, Clear and playback
    /// sync, and its row carries that as join-up / join-down.</summary>
    [AvaloniaFact]
    public void PickedNeighbours_JoinIntoOneRun()
    {
        EnsureAppStyles();
        var vm = new LyricShareViewModel(Song(), Enumerable.Range(0, 8).Select(i => $"line {i}").ToList(), 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        try
        {
            // Opens with lines 0-3 picked: one run.
            Assert.Equal(new[] { 1, 2, 3 }, Indexes(vm, l => l.JoinsPrevious));
            Assert.Equal(new[] { 0, 1, 2 }, Indexes(vm, l => l.JoinsNext));

            vm.ToggleLine(vm.Lines[2]); // splits it: 0-1 and 3
            vm.ToggleLine(vm.Lines[5]); // a lone pick
            Assert.Equal(new[] { 1 }, Indexes(vm, l => l.JoinsPrevious));
            Assert.Equal(new[] { 0 }, Indexes(vm, l => l.JoinsNext));

            vm.ToggleLine(vm.Lines[4]); // bridges 3 and 5
            Assert.Equal(new[] { 1, 4, 5 }, Indexes(vm, l => l.JoinsPrevious));
            Assert.Equal(new[] { 0, 3, 4 }, Indexes(vm, l => l.JoinsNext));

            var list = Named<ItemsControl>(win, "LineItems");
            Assert.True(PumpUntil(() => list.Bounds.Width > 0));
            Panel Row(int i) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<Panel>()
                .First(p => p.Classes.Contains("sl-line") && ReferenceEquals(p.DataContext, vm.Lines[i]));
            Assert.Equal(new[] { true, true }, new[] { Row(4).Classes.Contains("join-up"), Row(4).Classes.Contains("join-down") });
            Assert.Equal(new[] { true, false }, new[] { Row(5).Classes.Contains("join-up"), Row(5).Classes.Contains("join-down") });
            Assert.Equal(new[] { false, false }, new[] { Row(6).Classes.Contains("join-up"), Row(6).Classes.Contains("join-down") });

            vm.ClearSelectionCommand.Execute(null);
            Assert.DoesNotContain(vm.Lines, l => l.JoinsPrevious || l.JoinsNext);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }

        // Follow playback picks a window of lines in bulk; the run follows it.
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var song = Song();
        player.CurrentTrack = song;
        var stamps = Enumerable.Range(0, 10).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 10)).ToList();
        var synced = new LyricShareViewModel(song, Enumerable.Range(0, 10).Select(i => $"line {i}").ToList(), stamps, player);
        try
        {
            player.Position = TimeSpan.FromSeconds(45);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { 4, 5, 6, 7 }, Selected(synced));
            Assert.Equal(new[] { 5, 6, 7 }, Indexes(synced, l => l.JoinsPrevious));
            Assert.Equal(new[] { 4, 5, 6 }, Indexes(synced, l => l.JoinsNext));
        }
        finally { synced.Detach(); }
    }

    private static int[] Indexes(LyricShareViewModel vm, Func<SelectableLyricLine, bool> flag)
        => vm.Lines.Select((l, i) => (l, i)).Where(p => flag(p.l)).Select(p => p.i).ToArray();

    /// <summary>Not a bug: names built from titles carry no characters the file system refuses.</summary>
    // AvaloniaFact: the view model posts its first preview render to the UI dispatcher.
    [AvaloniaFact]
    public void SuggestedNames_HaveNoInvalidCharacters()
    {
        var vm = new LyricShareViewModel(Song("Live: \"Who?\" <A|B>*", "AC/DC"), new[] { "x" }, 0);
        try
        {
            Assert.EndsWith(".png", vm.SuggestedFileName);
            Assert.EndsWith(".mp4", vm.SuggestedVideoFileName);
            Assert.Equal(-1, vm.SuggestedFileName.IndexOfAny(Path.GetInvalidFileNameChars()));
            Assert.Equal(-1, vm.SuggestedVideoFileName.IndexOfAny(Path.GetInvalidFileNameChars()));
        }
        finally { vm.Detach(); }
    }

    /// <summary>A song without an artist: the name starts with the title, not " - ".</summary>
    [AvaloniaFact]
    public void SuggestedName_WithoutAnArtist_StartsWithTheTitle()
    {
        var vm = new LyricShareViewModel(Song("Intro", ""), new[] { "x" }, 0);
        try
        {
            Assert.Equal("Intro lyrics.png", vm.SuggestedFileName);
            Assert.Equal("Intro lyrics.mp4", vm.SuggestedVideoFileName);
        }
        finally { vm.Detach(); }
    }

    /// <summary>The export status the dialog shows reads in the app language (it was fixed English).</summary>
    [AvaloniaFact]
    public void ExportStatus_ReadsInTheAppLanguage()
    {
        try
        {
            Noctis.Localization.Loc.Instance.SetCulture("ru");
            Assert.Equal(Noctis.Localization.Loc.T("ShareLyrics.Saved"), Noctis.Helpers.PngExportHelper.SavedStatus);
            Assert.NotEqual("Copied to clipboard", Noctis.Helpers.PngExportHelper.CopiedStatus);
            Assert.StartsWith(Noctis.Localization.Loc.T("ShareLyrics.SaveFailed"), Noctis.Helpers.PngExportHelper.SaveFailedStatus("disk full"));
            Assert.EndsWith("disk full", Noctis.Helpers.PngExportHelper.SaveFailedStatus("disk full"));
        }
        finally { Noctis.Localization.Loc.Instance.SetCulture("en"); }
    }

    /// <summary>Edit in place: Esc puts the text back, an emptied line gets its text back,
    /// Enter keeps the edit; editing a line picks it.</summary>
    [AvaloniaFact]
    public void Editing_KeepsOrRestoresTheText()
    {
        var vm = new LyricShareViewModel(Song(), new[] { "alpha", "beta", "gamma" }, 0);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            var line = vm.Lines[1];
            vm.BeginEdit(line);
            Assert.True(line.IsEditing);
            Assert.True(line.IsSelected);
            line.Text = "BETA";
            line.EndEdit(keep: false);
            Assert.Equal("beta", line.Text);

            vm.BeginEdit(line);
            line.Text = "   ";
            line.EndEdit(keep: true);
            Assert.Equal("beta", line.Text);

            vm.BeginEdit(line);
            line.Text = "beta, edited";
            vm.BeginEdit(vm.Lines[2]); // starting another edit keeps this one
            Assert.False(line.IsEditing);
            Assert.Equal("beta, edited", line.Text);
            Assert.Equal(new[] { "beta, edited", "gamma" }, vm.CardLines);
        }
        finally { vm.Detach(); }
    }

    // ── The dialog ──

    private static T Named<T>(Window w, string name) where T : Control => w.FindControl<T>(name)!;

    [AvaloniaFact]
    public void Dialog_OpensInPillHost_WithPillControls_AndFullWidthRows()
    {
        EnsureAppStyles();
        Noctis.Tests.AccentTestHarness.WithAccent("#E74856", Avalonia.Styling.ThemeVariant.Dark, () =>
        {
            var lines = new[]
            {
                "You can't do this 'cause you ain't me, it drives you **** crazy",
                "short", "third line", "fourth line", "fifth line",
            };
            var vm = new LyricShareViewModel(Song(), lines, 0);
            var win = new Noctis.Views.LyricShareDialog(vm) { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Width = 1280, Height = 860 };
            win.Show();
            var closed = 0;
            win.Closed += (_, _) => closed++;
            try
            {
                var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Noctis.Controls.PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).ToList();

                // The outlined share pills and the per-line check circles are gone.
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("share-pill"));
                Assert.DoesNotContain(all.OfType<CheckBox>(), c => c.DataContext is SelectableLyricLine);

                // Options: filled pill drop-downs carrying every choice (four for the card, plus
                // the Lyrics Page background, shown only in that layout).
                var combos = all.OfType<ComboBox>().ToList();
                Assert.Equal(5, combos.Count);
                Assert.All(combos, c => Assert.Contains("pill-field", c.Classes));
                Assert.Equal(2, Named<ComboBox>(win, "LayoutBox").ItemCount); // Card, Poster (Lyrics Page out, owner 11-08)
                Assert.Equal(2, Named<ComboBox>(win, "FormatBox").ItemCount);
                Assert.Equal(3, Named<ComboBox>(win, "TextColorBox").ItemCount);
                Assert.Equal(vm.SolidSwatches.Count + 1, Named<ComboBox>(win, "BackgroundBox").ItemCount);

                // Footer: quiet Copy + Save Video, solid accent Save Card.
                Assert.Contains("pill-secondary", Named<Button>(win, "CopyButton").Classes);
                Assert.Contains("pill-secondary", Named<Button>(win, "SaveVideoButton").Classes);
                PillDialogHostTests.AssertSolidAccent(Named<Button>(win, "SaveCardButton"));

                // Rows wrap to the column, not to a sliver: the text gets the row's width
                // minus its padding and the tick.
                var list = Named<ItemsControl>(win, "LineItems");
                Assert.True(PumpUntil(() => list.Bounds.Width > 0));
                foreach (var text in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<TextBlock>()
                             .Where(t => t.Classes.Contains("sl-text")))
                    Assert.True(text.Bounds.Width >= list.Bounds.Width - 60,
                        $"'{text.Text}' wraps at {text.Bounds.Width:0} in a {list.Bounds.Width:0} px list");

                // The preview sits in a filled well.
                Assert.Contains("pill-well", Named<Border>(win, "PreviewWell").Classes);

                // Clear: nothing picked → Copy / Save Card / Save Video off.
                vm.ClearSelectionCommand.Execute(null);
                PumpUntil(() => false, 30);
                Assert.False(Named<Button>(win, "SaveCardButton").IsEffectivelyEnabled);
                Assert.False(Named<Button>(win, "CopyButton").IsEffectivelyEnabled);
                Assert.False(Named<Button>(win, "SaveVideoButton").IsEffectivelyEnabled);

                // A tap on a row picks it; Save Card comes on at once (no wait for the preview).
                var row = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<Panel>()
                    .First(p => p.Classes.Contains("sl-line") && ReferenceEquals(p.DataContext, vm.Lines[2]));
                row.RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, null!));
                Assert.True(vm.Lines[2].IsSelected);
                PumpUntil(() => false, 30);
                Assert.True(row.Classes.Contains("selected"));
                Assert.True(Named<Button>(win, "SaveCardButton").IsEffectivelyEnabled);

                // Drop-downs drive the card, and the card's state drives them.
                Named<ComboBox>(win, "FormatBox").SelectedIndex = 1;
                Assert.True(vm.IsStory);
                Named<ComboBox>(win, "LayoutBox").SelectedIndex = 1;
                Assert.True(vm.IsPosterLayout);
                var bg = Named<ComboBox>(win, "BackgroundBox");
                bg.SelectedItem = vm.BackgroundChoices[0];
                Assert.True(vm.IsArtworkBg);
                var navy = vm.BackgroundChoices.First(c => c.Hex == "#0D2137");
                bg.SelectedItem = navy;
                Assert.True(vm.IsSolidBg);
                Assert.False(vm.IsAutoSolid);
                Assert.Equal("#0D2137", vm.SolidColorHex);
                bg.SelectedItem = vm.BackgroundChoices[1];
                Assert.True(vm.IsAutoSolid);
                vm.UseBlackTextCommand.Execute(null);
                Assert.Equal(2, Named<ComboBox>(win, "TextColorBox").SelectedIndex);

                // Esc closes, animated, exactly once.
                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(host.IsClosing);
                Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
                PumpUntil(() => false, 250);
                Assert.Equal(1, closed);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>
    /// Bug (owner 10-12 screenshot): the option lists opened below their pills, which sit at
    /// the card's foot, so the Background list (320 px) hung 233 px under the card, down to
    /// the window's bottom edge, its last row cut off there; in a 910 px window it ran 98 px
    /// past the edge. On Windows the list is its own popup window, bounded by the screen and
    /// not by the dialog, so nothing pulled it back in: the constraint is switched off here
    /// to place it the same way. Every list opens upward now, inside the card.
    /// </summary>
    [AvaloniaFact]
    public void OptionLists_OpenUpward_InsideTheCard()
    {
        EnsureAppStyles();
        var vm = new LyricShareViewModel(Song(), Enumerable.Range(0, 12).Select(i => $"line {i}").ToList(), 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1154, Height = 910 };
        win.Show();
        try
        {
            var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Noctis.Controls.PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "open animation never settled");
            var card = host.Card!;
            var cardTop = card.TranslatePoint(default, win)!.Value.Y;
            var cardBottom = cardTop + card.Bounds.Height;
            foreach (var name in new[] { "LayoutBox", "FormatBox", "TextColorBox", "BackgroundBox" })
            {
                var box = Named<ComboBox>(win, name);
                var popup = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(box)
                    .OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "PART_Popup");
                popup.PlacementConstraintAdjustment = Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.None;
                box.IsDropDownOpen = true;
                Assert.True(PumpUntil(() => popup.IsOpen && popup.Child is Control { Bounds.Height: > 0 }), $"{name}: the list never opened");
                var list = (Control)popup.Child!;
                double top = 0, bottom = 0, boxTop = 0;
                bool Fits()
                {
                    top = list.TranslatePoint(default, win)!.Value.Y;
                    bottom = top + list.Bounds.Height;
                    boxTop = box.TranslatePoint(default, win)!.Value.Y;
                    // + 1.5: the list's edge sits a pixel over its pill, as Fluent places it.
                    return top >= cardTop && bottom <= boxTop + 1.5;
                }
                // The list pops in: on a loaded runner the first frames read it mid-flight.
                Assert.True(PumpUntil(Fits, 1500),
                    $"{name}: list at {top:0}..{bottom:0}, its pill at {boxTop:0}, the card {cardTop:0}..{cardBottom:0}, the window 0..910");
                box.IsDropDownOpen = false;
                PumpUntil(() => !popup.IsOpen, 1000);
            }
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>
    /// Bug: a double-click to edit a line moved its text about 8 px right and grew the row by
    /// about 19 px. TextBox.sl-edit (Padding 12,6 and MinHeight 0, under a -12,-6 margin so the
    /// text stays put) lived in Window.Styles, but PillDialogHost adds PillDialog.axaml to its
    /// own Styles, nearer the box, so pill-area's Padding 18,14 and MinHeight 40 won.
    /// </summary>
    [AvaloniaFact]
    public void EditBox_KeepsTheLineInPlace()
    {
        EnsureAppStyles();
        var vm = new LyricShareViewModel(Song(), new[] { "alpha line", "beta line" }, 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        try
        {
            var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Noctis.Controls.PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "open animation never settled");
            var list = Named<ItemsControl>(win, "LineItems");
            var row = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<Panel>()
                .First(p => p.Classes.Contains("sl-line") && ReferenceEquals(p.DataContext, vm.Lines[1]));
            var text = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(row).OfType<TextBlock>().Single(t => t.Classes.Contains("sl-text"));
            var x0 = text.TranslatePoint(default, row)!.Value.X;
            var h0 = row.Bounds.Height;

            vm.BeginEdit(vm.Lines[1]);
            var box = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(row).OfType<TextBox>().Single();
            Assert.True(PumpUntil(() => box.IsVisible && box.Bounds.Height > 0));
            PumpUntil(() => false, 50);
            var presenter = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(box).OfType<Avalonia.Controls.Presenters.TextPresenter>().First();
            var x1 = presenter.TranslatePoint(default, row)!.Value.X;
            Assert.True(Math.Abs(x1 - x0) <= 2, $"the text moved {x1 - x0:0.0} px (padding {box.Padding})");
            Assert.True(row.Bounds.Height - h0 <= 4, $"the row grew {row.Bounds.Height - h0:0.0} px (min height {box.MinHeight})");
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>
    /// Bug (lead 11-06 screenshot, 1920×1030, Interface Size 100%): with Layout = Lyrics Page
    /// the drop-downs read "Lyrics Pa", "1:1 · Squa", "Artwork c": Choose image or clip, Remove
    /// and Social safe zones sat in the row's Auto column and squeezed the four option columns.
    /// The page-only controls get a row of their own; no selected-item label trims, at 100% or
    /// 150%, and the card layout's row stays one row with its columns as before.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void PageLayout_OptionLabels_DoNotTruncate(double scale)
    {
        EnsureAppStyles();
        Noctis.Helpers.UiScale.Install();
        Noctis.Helpers.UiScale.SetFactor(scale);
        var dir = Directory.CreateTempSubdirectory("noctis-share-row-").FullName;
        var lines = Enumerable.Range(0, 10).Select(i => $"line {i}").ToList();
        var stamps = Enumerable.Range(0, 10).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 2)).ToList();
        var vm = new LyricShareViewModel(Song(), lines, stamps, null);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1920, Height = 1030 };
        win.Show();
        try
        {
            var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Noctis.Controls.PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "open animation never settled");
            var layout = Named<ComboBox>(win, "LayoutBox");
            var options = (Grid)layout.Parent!.Parent!;
            var pageRow = Named<StackPanel>(win, "PageOptionsRow");

            // Card layout: one row, the second collapsed.
            PumpUntil(() => false, 30);
            Assert.False(pageRow.IsVisible);
            Assert.Equal(((Control)layout.Parent).Bounds.Height, options.Bounds.Height, 1);
            Rect[] CardCombos() => new[] { "LayoutBox", "FormatBox", "TextColorBox", "BackgroundBox" }
                .Select(n => Named<ComboBox>(win, n).Bounds).ToArray();
            var cardBefore = CardCombos();

            // Lyrics Page, Story, a custom background (so Remove shows too).
            var png = Path.Combine(dir, "bg.png");
            using (var bmp = new SKBitmap(8, 8))
            {
                bmp.Erase(SKColors.SteelBlue);
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(png, data.ToArray());
            }
            vm.LayoutIndex = 2;
            vm.FormatIndex = 1;
            vm.PageBackgroundPath = png;
            vm.PageBackgroundIndex = 2;
            Assert.True(vm.IsPageLayout);
            Assert.True(PumpUntil(() => pageRow.IsVisible && pageRow.Bounds.Width > 0));
            PumpUntil(() => false, 30);

            foreach (var name in new[] { "LayoutBox", "FormatBox", "PageBackgroundBox" })
            {
                var combo = Named<ComboBox>(win, name);
                // The page controls take no width from the row: every drop-down keeps the
                // width the card layout gives it (no Karaoke pill here, so the full quarter).
                Assert.Equal(cardBefore[0].Width, combo.Bounds.Width, 1);
                // The stub text shaper draws every glyph one em wide (twice a real font), so
                // the label check needs real shaping: NOCTIS_TEST_SKIA=1.
                if (!HeadlessTestApp.RealRendering) continue;
                var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(combo).OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).ToList();
                Assert.NotEmpty(texts);
                foreach (var t in texts)
                {
                    var natural = new TextBlock
                    {
                        Text = t.Text, FontSize = t.FontSize, FontFamily = t.FontFamily,
                        FontWeight = t.FontWeight, FontStyle = t.FontStyle, FontStretch = t.FontStretch,
                        LetterSpacing = t.LetterSpacing,
                    };
                    natural.Measure(Size.Infinity);
                    Assert.True(t.Bounds.Width + 0.5 >= natural.DesiredSize.Width,
                        $"{name} '{t.Text}' trimmed at {scale:0%}: {t.Bounds.Width:0.0} of {natural.DesiredSize.Width:0.0} px");
                }
            }

            // The page controls sit on their own row under the drop-downs and fit the card,
            // Remove included, inside the options' width.
            Assert.True(pageRow.Bounds.Top >= ((Control)layout.Parent).Bounds.Bottom, "page controls share the drop-downs' row");
            var right = options.Bounds.Width;
            foreach (var c in new Control[] { Named<Button>(win, "ChooseBackgroundButton"),
                         Named<Button>(win, "RemoveBackgroundButton"), Named<CheckBox>(win, "SafeZonesBox") })
            {
                Assert.True(c.IsEffectivelyVisible, $"{c.Name} hidden");
                if (!HeadlessTestApp.RealRendering) continue;
                var r = c.Bounds.Translate((Vector)c.TranslatePoint(default, options)!.Value - c.Bounds.Position);
                Assert.True(r.Right <= right + 0.5, $"{c.Name} ends at {r.Right:0} past {right:0}");
                Assert.True(c.DesiredSize.Width <= c.Bounds.Width + 0.5, $"{c.Name} squeezed");
            }

            // Back to the card: the same single row, the same columns.
            vm.LayoutIndex = 0;
            Assert.True(PumpUntil(() => !pageRow.IsVisible));
            PumpUntil(() => false, 30);
            Assert.Equal(((Control)layout.Parent).Bounds.Height, options.Bounds.Height, 1);
            Assert.Equal(cardBefore, CardCombos());
        }
        finally
        {
            if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
            vm.Detach();
            Noctis.Helpers.UiScale.SetFactor(1.0);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Bug: the Background swatches' ring was a fixed white (#40FFFFFF), so on the Light
    /// theme's pale list the pale swatches had no edge: Mist (#E4ECF4) and Cream (#F5E6CC) sat
    /// on the #F2F2F2 list at about 1.07:1 and 1.1:1 and all but vanished. The ring has to
    /// stand off the list on both themes: darker on Light, lighter on Dark.
    /// </summary>
    [AvaloniaFact]
    public void SwatchRings_StandOffTheList_OnBothThemes()
    {
        EnsureAppStyles();
        foreach (var variant in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            var vm = new LyricShareViewModel(Song(), new[] { "one" }, 0);
            var win = new Noctis.Views.LyricShareDialog(vm) { RequestedThemeVariant = variant, Width = 1280, Height = 860 };
            win.Show();
            try
            {
                var box = Named<ComboBox>(win, "BackgroundBox");
                var popup = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(box)
                    .OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "PART_Popup");
                box.IsDropDownOpen = true;
                Assert.True(PumpUntil(() => popup.IsOpen && popup.Child is Control { Bounds.Height: > 0 }), "the list never opened");
                var list = (Border)popup.Child!;
                var under = AccentTestHarness.ColorOf(list.Background);
                var rings = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<Border>()
                    .Where(b => b.DataContext is ShareBackgroundChoice && b.Width == 16).ToList();
                Assert.NotEmpty(rings);
                foreach (var ring in rings)
                {
                    var c = AccentTestHarness.ColorOf(ring.BorderBrush);
                    double a = c.A / 255.0;
                    double Lum(double r, double g, double b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
                    var drawn = Lum(a * c.R + (1 - a) * under.R, a * c.G + (1 - a) * under.G, a * c.B + (1 - a) * under.B);
                    var delta = drawn - Lum(under.R, under.G, under.B);
                    Assert.True(variant == Avalonia.Styling.ThemeVariant.Light ? delta < -0.05 : delta > 0.05,
                        $"{variant}: ring {c} on the {under} list shifts it by {delta:0.000}");
                }
                box.IsDropDownOpen = false;
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        }
    }

    /// <summary>
    /// Bug: Save Card drew the full-size card (2160 px, PNG) before the save picker opened, so
    /// a click sat 0.3-0.9 s before anything showed (measured: 1:1 solid ~300 ms, 9:16 artwork
    /// ~870 ms). The card renders while the picker is up. Headless, the picker cancels at once,
    /// so the click is done long before the card could have rendered.
    /// </summary>
    [AvaloniaFact]
    public void SaveCard_OpensThePicker_WithoutWaitingForTheRender()
    {
        EnsureAppStyles();
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-share-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var track = Song();
        track.AlbumArtworkPath = WriteCover(dir, 3000);
        var vm = new LyricShareViewModel(track, new[] { "one line", "two lines", "three lines" }, 0) { FormatIndex = 1 };
        vm.SelectedBackground = vm.BackgroundChoices[0];
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        try
        {
            Assert.True(PumpUntil(() => vm.Preview != null && vm.IsPreviewCurrent, 10000), "preview never rendered");
            var sw = Stopwatch.StartNew();
            Named<Button>(win, "SaveCardButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(PumpUntil(() => !win.IsBusy, 5000), "Save Card never finished");
            Assert.True(sw.ElapsedMilliseconds < 150, $"the picker waited {sw.ElapsedMilliseconds} ms for the card");
        }
        finally
        {
            if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>The four option drop-downs read as their labels: UIA named all four "" (10-25),
    /// so a screen reader couldn't tell Layout from Format, Text or Background.</summary>
    [AvaloniaFact]
    public void OptionDropDowns_ReadAsTheirLabels()
    {
        EnsureAppStyles();
        var vm = new LyricShareViewModel(Song(), new[] { "alpha", "beta" }, 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        try
        {
            var names = new[] { "LayoutBox", "FormatBox", "TextColorBox", "BackgroundBox" }
                .Select(n => Avalonia.Automation.AutomationProperties.GetName(Named<ComboBox>(win, n)))
                .ToList();
            Assert.Equal(new[]
            {
                Noctis.Localization.Loc.T("LyricShare.Layout"), Noctis.Localization.Loc.T("LyricShare.Format"),
                Noctis.Localization.Loc.T("LyricShare.Text"), Noctis.Localization.Loc.T("LyricShare.Background"),
            }, names);
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        }
        finally
        {
            if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
        }
    }

    /// <summary>Esc in a line's edit box puts the text back and keeps the dialog open.</summary>
    [AvaloniaFact]
    public void EscapeWhileEditing_RestoresTheLine_AndKeepsTheDialog()
    {
        EnsureAppStyles();
        var vm = new LyricShareViewModel(Song(), new[] { "alpha", "beta" }, 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        try
        {
            var list = Named<ItemsControl>(win, "LineItems");
            Assert.True(PumpUntil(() => list.Bounds.Width > 0));
            var row = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<Panel>()
                .First(p => p.Classes.Contains("sl-line") && ReferenceEquals(p.DataContext, vm.Lines[1]));
            row.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
            Assert.True(vm.Lines[1].IsEditing);
            var box = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(row).OfType<TextBox>().Single();
            Assert.True(PumpUntil(() => box.IsFocused), "the edit box never took focus");
            box.Text = "changed";
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            PumpUntil(() => false, 300);
            Assert.False(vm.Lines[1].IsEditing);
            Assert.Equal("beta", vm.Lines[1].Text);
            Assert.Equal(0, closed);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>
    /// Owner 10-24: "when I hold down and move the scrollable bar to the side it disappears".
    /// The Lines list's bar is only shown while the list is hovered; dragging the thumb and
    /// drifting sideways off the list (into the gap or over the card preview) faded it out
    /// mid-drag. While the thumb is held the bar stays shown and keeps scrolling, as an OS
    /// scrollbar does; it fades once the button is released away from the list.
    /// </summary>
    [AvaloniaFact]
    public void DraggingTheLinesThumb_Sideways_KeepsTheBarShown()
    {
        EnsureAppStyles();
        Noctis.Helpers.ScrollBarThumbDrag.Install(); // as App does
        var vm = new LyricShareViewModel(Song(), Enumerable.Range(0, 40).Select(i => $"lyric line number {i}").ToList(), 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        try
        {
            var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Noctis.Controls.PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "open animation never settled");
            var scroller = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<ScrollViewer>()
                .Single(s => s.Classes.Contains("sl-lines"));
            var bar = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(scroller).OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(PumpUntil(() => bar.IsVisible && scroller.Extent.Height > scroller.Viewport.Height), "the list does not scroll");
            var thumb = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(bar).OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            Assert.True(PumpUntil(() => thumb.Bounds.Height > 0));

            // What is drawn: the bar and its thumb (either may carry the fade).
            double Shown() => bar.Opacity * thumb.Opacity;
            Assert.True(PumpUntil(() => Shown() < 0.001), $"the bar shows at rest ({Shown():0.00})");
            var grab = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), win)!.Value;
            win.MouseMove(grab);
            Assert.True(PumpUntil(() => Shown() > 0.999), $"the bar never showed on hover ({Shown():0.00})");
            win.MouseDown(grab, MouseButton.Left);

            // Drift right off the list, over the card preview, and down: still dragging.
            var preview = Named<Border>(win, "PreviewWell");
            var off = new Point(preview.TranslatePoint(default, win)!.Value.X + 60, grab.Y + 120);
            var listTopLeft = scroller.TranslatePoint(default, win)!.Value;
            Assert.False(new Rect(listTopLeft, scroller.Bounds.Size).Contains(off), "the drift point is still inside the list");
            win.MouseMove(off, RawInputModifiers.LeftMouseButton);
            PumpUntil(() => false, 300);
            Assert.True(Shown() > 0.999, $"the bar faded mid-drag ({Shown():0.00}, list :pointerover {scroller.IsPointerOver})");
            Assert.True(scroller.Offset.Y > 0, "the drag stopped scrolling once the pointer left the bar");
            // The track (app-wide style: shown while the bar is hovered) stays for the drag too.
            var track = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(bar).OfType<Avalonia.Controls.Shapes.Rectangle>()
                .Single(r => r.Name == "TrackRect");
            Assert.True(PumpUntil(() => track.Opacity > 0.999), $"the track faded mid-drag ({track.Opacity:0.00})");

            // Released away from the list: it fades as before.
            win.MouseUp(off, MouseButton.Left);
            win.MouseMove(new Point(off.X + 1, off.Y));
            Assert.True(PumpUntil(() => Shown() < 0.001), $"the bar stayed after release ({Shown():0.00})");
            Assert.True(PumpUntil(() => track.Opacity < 0.001), $"the track stayed after release ({track.Opacity:0.00})");
            Assert.DoesNotContain(Noctis.Helpers.ScrollBarThumbDrag.DraggingClass, bar.Classes);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>
    /// Bug: the Lines list was mouse-only. Its rows were plain Panels that could not take
    /// focus, so Tab went from Clear straight past the list and no line could be picked or
    /// edited from the keyboard. Tab now lands on a row; Up/Down move, Space/Enter pick,
    /// F2 edits and Enter hands the focus back to the row.
    /// </summary>
    [AvaloniaFact]
    public void Lines_CanBePickedAndEdited_FromTheKeyboard()
    {
        EnsureAppStyles();
        var vm = new LyricShareViewModel(Song(), new[] { "alpha", "beta", "gamma", "delta", "epsilon" }, 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        try
        {
            var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Noctis.Controls.PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "open animation never settled");
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[4]); // keeps Clear enabled, the Tab stop before the list
            PumpUntil(() => false, 30);
            var clear = Named<Button>(win, "ClearButton");
            Assert.True(clear.Focus(NavigationMethod.Tab));
            SelectableLyricLine? Focused() => (win.FocusManager?.GetFocusedElement() as Control)?.DataContext as SelectableLyricLine;

            win.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            PumpUntil(() => false, 30);
            Assert.True(Focused() == vm.Lines[0], $"Tab after Clear went to {win.FocusManager?.GetFocusedElement()}");

            win.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            PumpUntil(() => false, 30);
            Assert.Same(vm.Lines[1], Focused());
            win.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.True(vm.Lines[1].IsSelected, "Space did not pick the focused line");
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.False(vm.Lines[1].IsSelected, "Enter did not drop the focused line");

            win.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
            Assert.True(vm.Lines[1].IsEditing, "F2 did not edit the focused line");
            Assert.True(PumpUntil(() => win.FocusManager?.GetFocusedElement() is TextBox), "the edit box never took focus");
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            PumpUntil(() => false, 30);
            Assert.False(vm.Lines[1].IsEditing);
            Assert.Same(vm.Lines[1], Focused());

            win.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            PumpUntil(() => false, 30);
            Assert.Same(vm.Lines[4], Focused());

            // One Tab stop for the whole list: the next Tab leaves it.
            win.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            PumpUntil(() => false, 30);
            Assert.Null(Focused());
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>Esc during Save Video stops the export (status Cancelled, no cut-off file) and
    /// keeps the dialog; the next Esc closes it.</summary>
    [AvaloniaFact]
    public void EscapeDuringVideoExport_Cancels_ThenCloses()
    {
        EnsureAppStyles();
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-share-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var previous = App.Services;
        try
        {
            App.Services = new FfmpegOnly(WriteSlowFfmpeg(dir));
            var audio = Path.Combine(dir, "song.flac");
            File.WriteAllText(audio, "audio");
            var vm = new LyricShareViewModel(Song(filePath: audio), new[] { "one", "two" }, 0);
            var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
            win.Show();
            var closed = 0;
            win.Closed += (_, _) => closed++;
            try
            {
                Assert.True(PumpUntil(() => vm.CanExportVideo));
                var output = Path.Combine(dir, "clip.mp4");
                var before = WorkClips();
                string? work = null;
                var export = vm.ExportClipAsync(output);
                Assert.True(PumpUntil(() => (work = NewWorkClip(before)) != null, 10000), "ffmpeg stand-in never started");
                Assert.False(Named<Button>(win, "SaveVideoButton").IsEffectivelyEnabled);

                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(PumpUntil(() => export.IsCompleted, 8000), "Esc did not stop the export");
                Assert.Equal(Localization.Loc.T("ShareLyrics.Cancelled"), export.Result);
                Assert.False(File.Exists(output));
                Assert.False(File.Exists(work));
                Assert.Equal(0, closed);

                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(PumpUntil(() => closed > 0, 2000));
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        }
        finally
        {
            App.Services = previous;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>A clip that finishes is kept; a file the user chose to replace is never
    /// deleted by a cancel (only one this run created is).</summary>
    [AvaloniaFact]
    public void CancelledExport_KeepsAFileThatWasThereBefore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-share-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var previous = App.Services;
        try
        {
            App.Services = new FfmpegOnly(WriteSlowFfmpeg(dir));
            var audio = Path.Combine(dir, "song.flac");
            File.WriteAllText(audio, "audio");
            var output = Path.Combine(dir, "existing.mp4");
            File.WriteAllText(output, "old clip");
            var vm = new LyricShareViewModel(Song(filePath: audio), new[] { "one" }, 0);
            try
            {
                var before = WorkClips();
                string? work = null;
                var export = vm.ExportClipAsync(output);
                Assert.True(PumpUntil(() => vm.IsRendering && (work = NewWorkClip(before)) != null && StartsWithPartial(work), 10000),
                    "ffmpeg stand-in never started");
                vm.CancelExport();
                Assert.True(PumpUntil(() => export.IsCompleted, 8000));
                Assert.Equal(Localization.Loc.T("ShareLyrics.Cancelled"), export.Result);
                Assert.True(File.Exists(output), "a file that was there before the run was deleted");
                // Bug: ffmpeg wrote straight to the chosen path (-y truncates it at open), so a
                // cancelled or failed replace left the cut-off clip where the old one was.
                Assert.Equal("old clip", File.ReadAllText(output).Trim());
            }
            finally { vm.Detach(); }
        }
        finally
        {
            App.Services = previous;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): a PNG of the dialog over a busy owner,
    /// for eyeballing the layout. Saved under the system temp folder.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheShareDialog()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", Avalonia.Styling.ThemeVariant.Dark, () =>
        {
            var stripes = new StackPanel();
            var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
            for (var i = 0; i < 18; i++)
                stripes.Children.Add(new Border { Height = 50, Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
            var owner = new Window { Width = 1280, Height = 860, Content = stripes, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
            owner.Show();
            PumpUntil(() => false, 100);

            var lines = new[]
            {
                "You can't do this 'cause you ain't me, it drives you **** crazy",
                "Tutu, tutu, tutu", "I don't need no sympathy", "Run it up, run it up",
                "Every day I'm on my grind, every day I'm on the line", "Yeah, yeah",
                "Another one, another one", "Last line of the verse right here",
            };
            var vm = new LyricShareViewModel(Song(), lines, 1);
            var win = new Noctis.Views.LyricShareDialog(vm) { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Width = 1280, Height = 860 };
            _ = win.ShowDialog(owner);
            try
            {
                var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Noctis.Controls.PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropBitmap != null && vm.Preview != null, 4000));
                vm.Lines[0].IsCurrent = true;
                PumpUntil(() => false, 150);
                var path = Path.Combine(Path.GetTempPath(), "noctis-share-lyrics-dialog.png");
                win.CaptureRenderedFrame()!.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally
            {
                if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
            }

            // Synced, word-timed lyrics on a 9:16 card: Follow playback and Karaoke video show.
            using var persistence = new TestPersistenceService();
            var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
            var song = Song();
            player.CurrentTrack = song;
            player.Position = TimeSpan.FromSeconds(11);
            var stamps = lines.Select((_, i) => (TimeSpan?)TimeSpan.FromSeconds(i * 5)).ToList();
            var words = lines.Select((l, i) => (IReadOnlyList<WordTiming>?)l.Split(' ')
                .Select((w, k) => new WordTiming { Text = w, Start = TimeSpan.FromSeconds(i * 5 + k * 0.4) }).ToList()).ToList();
            var synced = new LyricShareViewModel(song, lines, stamps, player, words) { FormatIndex = 1 };
            var win2 = new Noctis.Views.LyricShareDialog(synced) { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Width = 1280, Height = 860 };
            _ = win2.ShowDialog(owner);
            try
            {
                var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win2).OfType<Noctis.Controls.PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && synced.Preview != null, 4000));
                PumpUntil(() => false, 300);
                var path = Path.Combine(Path.GetTempPath(), "noctis-share-lyrics-dialog-synced.png");
                win2.CaptureRenderedFrame()!.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally
            {
                if (win2.IsVisible) { win2.Close(); PumpUntil(() => !win2.IsVisible); }
                owner.Close();
            }
        });
    }

    // ── Karaoke timing ──

    /// <summary>
    /// Bug: a line's last word with no end time swept until the next *picked* line began, or
    /// for 2 s when it was the last pick, instead of until the next lyric line, capped at 2 s
    /// as on the lyrics page (KaraokeSweep.ResolveOpenLastWordEnd). Picking lines 0 and 2 here
    /// stretched "two" from 11 s to 30 s, over line 1 (sung at 11.5 s); picking line 0 alone
    /// ran it to 13 s, past line 1's start.
    /// </summary>
    [AvaloniaFact]
    public void OpenLastWord_EndsAtTheNextLyricLine_CappedLikeTheLyricsPage()
    {
        var lines = new[] { "one two", "three", "four five", "six" };
        var stamps = new TimeSpan?[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11.5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(34) };
        IReadOnlyList<WordTiming> Words(params (string Text, double Start)[] w)
            => w.Select(x => new WordTiming { Text = x.Text, Start = TimeSpan.FromSeconds(x.Start) }).ToList();
        var words = new IReadOnlyList<WordTiming>?[]
        {
            Words(("one", 10), ("two", 11)), Words(("three", 11.5)), Words(("four", 30), ("five", 31)), Words(("six", 34)),
        };
        var vm = new LyricShareViewModel(Song(), lines, stamps, null, words);
        try
        {
            double LastEnd(int cardLine) => vm.CardKaraoke[cardLine].Words![^1].EndSeconds;

            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[0]);
            Assert.Equal(11.5, LastEnd(0), 3);

            vm.ToggleLine(vm.Lines[2]);
            Assert.Equal(11.5, LastEnd(0), 3);

            // "five" at 31 s, next line at 34 s: the page's 2 s cap, not the 3 s gap.
            vm.ToggleLine(vm.Lines[3]);
            Assert.Equal(33, LastEnd(1), 3);
        }
        finally { vm.Detach(); }
    }
}

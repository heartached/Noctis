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
    private static void EnsureAppStyles()
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

    // ── Clip export ──

    /// <summary>Fakes the converter service so ExportClipAsync finds "ffmpeg" (our script).</summary>
    private sealed class FfmpegOnly(string path) : IAudioConverterService, IServiceProvider
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
            Assert.True(PumpUntil(() => vm.CurrentPng != null), "preview never rendered");

            var output = Path.Combine(dir, "clip.mp4");
            var export = vm.ExportClipAsync(output);
            Assert.True(PumpUntil(() => File.Exists(output), 10000), "ffmpeg stand-in never started");

            vm.Detach(); // what closing the dialog does

            Assert.True(PumpUntil(() => export.IsCompleted, 8000), "the export kept running after the dialog closed");
            Assert.False(File.Exists(output), "the cut-off clip was left behind");
            Assert.False(vm.IsRendering);
        }
        finally
        {
            App.Services = previous;
            try { Directory.Delete(dir, true); } catch { }
        }
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

    private static bool StartsWithPartial(string path)
    {
        try { return File.ReadAllText(path).StartsWith("partial"); }
        catch (IOException) { return false; }
    }

    private static int[] Selected(LyricShareViewModel vm)
        => vm.Lines.Select((l, i) => (l, i)).Where(p => p.l.IsSelected).Select(p => p.i).ToArray();

    // ── Selection (not bugs; guards for the new rows) ──

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

    /// <summary>Not a bug: names built from titles carry no characters the file system refuses.</summary>
    [Fact]
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

                // Options: four filled pill drop-downs carrying every choice.
                var combos = all.OfType<ComboBox>().ToList();
                Assert.Equal(4, combos.Count);
                Assert.All(combos, c => Assert.Contains("pill-field", c.Classes));
                Assert.Equal(2, Named<ComboBox>(win, "LayoutBox").ItemCount);
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
                var export = vm.ExportClipAsync(output);
                Assert.True(PumpUntil(() => File.Exists(output), 10000), "ffmpeg stand-in never started");
                Assert.False(Named<Button>(win, "SaveVideoButton").IsEffectivelyEnabled);

                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(PumpUntil(() => export.IsCompleted, 8000), "Esc did not stop the export");
                Assert.Equal(Localization.Loc.T("ShareLyrics.Cancelled"), export.Result);
                Assert.False(File.Exists(output));
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
                var export = vm.ExportClipAsync(output);
                Assert.True(PumpUntil(() => vm.IsRendering && StartsWithPartial(output), 10000), "ffmpeg stand-in never started");
                vm.CancelExport();
                Assert.True(PumpUntil(() => export.IsCompleted, 8000));
                Assert.Equal(Localization.Loc.T("ShareLyrics.Cancelled"), export.Result);
                Assert.True(File.Exists(output), "a file that was there before the run was deleted");
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
}

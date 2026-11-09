using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The "Lyrics Page" share style in the share dialog's view-model: what each line brings
/// from the lyrics page, the layout choice and its switching rules, and range picking.
/// </summary>
public class LyricSharePageModeTests
{
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

    [AvaloniaFact]
    public void Source_lines_bring_voice_adlibs_and_original_text()
    {
        var src = new[] { new LyricLine { Text = "a b", Timestamp = TimeSpan.FromSeconds(1), Voice = LyricVoice.Voice2,
            BackgroundEndTimestamp = TimeSpan.FromSeconds(3),
            BackgroundWords = new[] { new WordTiming { Text = "ooh", Start = TimeSpan.FromSeconds(2) } } } };
        var vm = new LyricShareViewModel(Song(), new[] { "a b" }, new TimeSpan?[] { TimeSpan.FromSeconds(1) }, null, sources: src);
        try
        {
            vm.Lines[0].Text = "edited";
            Assert.Equal("a b", vm.Lines[0].OriginalText);
            Assert.Equal(LyricVoice.Voice2, vm.Lines[0].Voice);
            Assert.Equal("ooh", vm.Lines[0].BackgroundWords![0].Text);
        }
        finally { vm.Detach(); }
    }

    /// <summary>Synced lines <paramref name="gapSeconds"/> apart, the first at 0 s.</summary>
    private static LyricShareViewModel Synced(int count, double gapSeconds = 2)
    {
        var lines = Enumerable.Range(0, count).Select(i => $"line {i}").ToList();
        var stamps = Enumerable.Range(0, count).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * gapSeconds)).ToList();
        return new LyricShareViewModel(Song(), lines, stamps, null);
    }

    private static int[] Picked(LyricShareViewModel vm) =>
        Enumerable.Range(0, vm.Lines.Count).Where(i => vm.Lines[i].IsSelected).ToArray();

    private static int[] Span(int first, int last) => Enumerable.Range(first, last - first + 1).ToArray();

    [AvaloniaFact]
    public void Unsynced_lyrics_cannot_pick_the_Lyrics_Page_and_say_why()
    {
        var vm = new LyricShareViewModel(Song(), new[] { "plain one", "plain two" });
        try
        {
            Assert.False(vm.PageLayoutAvailable);
            vm.LayoutIndex = 2;
            Assert.Equal(0, vm.LayoutIndex);
            Assert.False(vm.IsPageLayout);
            vm.SelectPageLayoutCommand.Execute(null);
            Assert.False(vm.IsPageLayout);
            Assert.Equal(Localization.Loc.T("ShareLyrics.PageNeedsSync"), vm.PageLayoutTip);
        }
        finally { vm.Detach(); }
    }

    [AvaloniaFact]
    public void Entering_the_page_fills_the_gaps_and_leaving_keeps_the_first_eight()
    {
        var vm = Synced(20);
        try
        {
            Assert.True(vm.PageLayoutAvailable);
            Assert.Equal(Localization.Loc.T("LyricShare.LyricsPageTip"), vm.PageLayoutTip);
            vm.ClearSelectionCommand.Execute(null);   // the dialog preselects lines 0-3
            vm.ToggleLine(vm.Lines[1]);
            vm.ToggleLine(vm.Lines[3]);
            vm.ToggleLine(vm.Lines[6]);

            vm.LayoutIndex = 2;
            Assert.True(vm.IsPageLayout);
            Assert.False(vm.IsPanelLayout);
            Assert.False(vm.IsPosterLayout);
            Assert.Equal(2, vm.LayoutIndex);
            Assert.Equal(Span(1, 6), Picked(vm));
            Assert.StartsWith(Localization.Loc.T("LyricShare.LyricsPage"), vm.CardOptionsSummary);

            for (int i = 7; i <= 12; i++)
                vm.Lines[i].IsSelected = true;   // no 8-line cap in the page
            Assert.Equal(Span(1, 12), Picked(vm));

            vm.LayoutIndex = 0;
            Assert.True(vm.IsPanelLayout);
            Assert.False(vm.IsPageLayout);
            Assert.Equal(Span(1, 8), Picked(vm));
            Assert.Equal(Localization.Loc.T("ShareLyrics.SelectedCount", 8, LyricShareViewModel.MaxLines), vm.SelectionSummary);
        }
        finally { vm.Detach(); }
    }

    /// <summary>Card picks may lie far apart; the page's range stops where the 60 s clip does.</summary>
    [AvaloniaFact]
    public void Entering_the_page_trims_the_range_to_sixty_seconds()
    {
        var vm = Synced(60);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[0]);
            vm.ToggleLine(vm.Lines[50]);

            vm.LayoutIndex = 2;
            // Line 29 ends where line 30 starts, at 60 s: the last line the clip holds whole.
            Assert.Equal(Span(0, 29), Picked(vm));
            Assert.Equal(Localization.Loc.T("ShareLyrics.PageMaxLength", 60), vm.StatusText);
        }
        finally { vm.Detach(); }
    }

    /// <summary>A page-mode view-model whose range is lines <paramref name="first"/>..<paramref name="last"/>.</summary>
    private static LyricShareViewModel PageRange(int count, int first, int last)
    {
        var vm = Synced(count);
        vm.ClearSelectionCommand.Execute(null);   // the dialog preselects lines 0-3
        vm.ToggleLine(vm.Lines[first]);
        if (last != first) vm.ToggleLine(vm.Lines[last]);
        vm.LayoutIndex = 2;
        Assert.Equal(Span(first, last), Picked(vm));
        return vm;
    }

    [AvaloniaFact]
    public void In_the_page_a_tap_extends_or_trims_the_range()
    {
        var vm = PageRange(30, 4, 6);
        try
        {
            vm.ToggleLine(vm.Lines[10]);
            Assert.Equal(Span(4, 10), Picked(vm));
            vm.ToggleLine(vm.Lines[2]);
            Assert.Equal(Span(2, 10), Picked(vm));
            vm.ToggleLine(vm.Lines[5]);
            Assert.Equal(Span(2, 5), Picked(vm));
            // Lines 2-5 start at 4 s; line 6 starts at 12 s.
            Assert.Equal(Localization.Loc.T("ShareLyrics.PageRangeSummary", 4, 8), vm.SelectionSummary);

            vm.ToggleLine(vm.Lines[2]);
            Assert.Equal(Span(2, 2), Picked(vm));
            vm.ToggleLine(vm.Lines[2]);
            Assert.Empty(Picked(vm));
            Assert.False(vm.HasSelection);

            vm.ToggleLine(vm.Lines[7]);   // nothing picked: the tap starts a new range
            Assert.Equal(Span(7, 7), Picked(vm));
        }
        finally { vm.Detach(); }
    }

    [AvaloniaFact]
    public void The_range_stops_at_sixty_seconds_with_a_status()
    {
        var vm = PageRange(40, 0, 0);
        try
        {
            // Line 30 starts at 60 s and ends at 62 s, where line 31 starts: past the clip.
            vm.ToggleLine(vm.Lines[30]);
            Assert.Equal(Span(0, 0), Picked(vm));
            Assert.Equal(Localization.Loc.T("ShareLyrics.PageMaxLength", 60), vm.StatusText);

            vm.ToggleLine(vm.Lines[29]);
            Assert.Equal(Span(0, 29), Picked(vm));
            Assert.Equal(string.Empty, vm.StatusText);
        }
        finally { vm.Detach(); }
    }

    [AvaloniaFact]
    public void Editing_a_line_outside_the_range_extends_it_first()
    {
        var vm = PageRange(30, 4, 6);
        try
        {
            vm.BeginEdit(vm.Lines[8]);
            Assert.Equal(Span(4, 8), Picked(vm));
            Assert.True(vm.Lines[8].IsEditing);

            vm.Lines[8].EndEdit(keep: true);
            vm.BeginEdit(vm.Lines[5]);   // inside: the range stays as it is
            Assert.Equal(Span(4, 8), Picked(vm));
            Assert.True(vm.Lines[5].IsEditing);
        }
        finally { vm.Detach(); }
    }

    /// <summary>The card keeps its free picking: taps toggle single lines, no range.</summary>
    [AvaloniaFact]
    public void Card_taps_still_toggle_single_lines()
    {
        var vm = Synced(20);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[2]);
            vm.ToggleLine(vm.Lines[9]);
            Assert.Equal(new[] { 2, 9 }, Picked(vm));
            Assert.Equal(Localization.Loc.T("ShareLyrics.SelectedCount", 2, LyricShareViewModel.MaxLines), vm.SelectionSummary);
        }
        finally { vm.Detach(); }
    }

    /// <summary><paramref name="count"/> lines 2 s apart, each two words long ("wNa" at N·2 s,
    /// "wNb" at N·2 + 0.8 s, the second word open-ended).</summary>
    private static LyricShareViewModel WordTimed(int count, Track? track = null)
    {
        var lines = Enumerable.Range(0, count).Select(i => $"w{i}a w{i}b").ToList();
        var stamps = Enumerable.Range(0, count).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 2)).ToList();
        var words = Enumerable.Range(0, count).Select(i => (IReadOnlyList<WordTiming>?)new[]
        {
            new WordTiming { Text = $"w{i}a ", Start = TimeSpan.FromSeconds(i * 2) },
            new WordTiming { Text = $"w{i}b", Start = TimeSpan.FromSeconds(i * 2 + 0.8) },
        }).ToList();
        return new LyricShareViewModel(track ?? Song(), lines, stamps, null, words);
    }

    [AvaloniaFact]
    public void Page_spec_shows_context_lines_and_applies_edits_only_inside_the_range()
    {
        var track = Song("Gummo", "6ix9ine");
        track.IsExplicit = true;
        var vm = WordTimed(40, track);
        try
        {
            vm.Lines[25].Text = "outside edit";   // typed while on the card
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[15]);
            vm.ToggleLine(vm.Lines[17]);
            vm.LayoutIndex = 2;
            Assert.Equal(Span(15, 17), Picked(vm));
            vm.Lines[16].Text = "changed";

            var spec = vm.PageSpec;
            Assert.Equal(10, LyricShareViewModel.PageContextLines);
            Assert.Equal(Span(5, 27).Select(i => (double?)(i * 2)), spec.Lines.Select(l => l.StartSeconds));
            Assert.Equal("w5a w5b", spec.Lines[0].Text);

            var edited = spec.Lines[16 - 5];
            Assert.Equal("changed", edited.Text);
            Assert.Null(edited.Words);

            var outside = spec.Lines[25 - 5];
            Assert.Equal(vm.Lines[25].OriginalText, outside.Text);
            Assert.Equal("w25a w25b", outside.Text);
            Assert.NotNull(outside.Words);

            // Same word ends as the card's karaoke: the open last word runs to the next line.
            Assert.Equal(2, spec.Lines[15 - 5].Words!.Count);
            Assert.Equal(vm.CardKaraoke[0].Words, spec.Lines[15 - 5].Words);

            Assert.Equal(vm.GetClipTiming().StartSeconds, spec.ClipStartSeconds);
            Assert.Equal(30, spec.ClipStartSeconds);
            Assert.Equal(ShareCardFormat.Square, spec.Format);
            Assert.False(spec.SafeZones);
            Assert.True(spec.IsExplicit);
            Assert.Equal("Gummo", spec.Title);
            Assert.Equal(track.ArtistDisplay, spec.Artist);

            vm.FormatIndex = 1;
            spec = vm.PageSpec;
            Assert.Equal(ShareCardFormat.Story, spec.Format);
            Assert.True(spec.SafeZones);
        }
        finally { vm.Detach(); }
    }

    [AvaloniaFact]
    public void Page_spec_window_stops_at_the_song_edges()
    {
        var vm = PageRange(12, 2, 3);
        try
        {
            Assert.Equal(Span(0, 11).Select(i => (double?)(i * 2)), vm.PageSpec.Lines.Select(l => l.StartSeconds));
        }
        finally { vm.Detach(); }
    }

    /// <summary>Lines 2 s apart; the song's last line (index count - 1) ends at <paramref name="lastEnd"/>.</summary>
    private static LyricShareViewModel EndingAt(int count, double? lastEnd)
    {
        var lines = Enumerable.Range(0, count).Select(i => $"line {i}").ToList();
        var stamps = Enumerable.Range(0, count).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 2)).ToList();
        var ends = Enumerable.Range(0, count)
            .Select(i => i == count - 1 && lastEnd is { } e ? (TimeSpan?)TimeSpan.FromSeconds(e) : null).ToList();
        return new LyricShareViewModel(Song(), lines, stamps, null, endTimestamps: ends);
    }

    /// <summary>The song's last line has no next line: the clip ends with it (at least 4 s
    /// after it starts), not 15 s after the range starts.</summary>
    [AvaloniaFact]
    public void A_range_ending_on_the_last_line_ends_the_clip_with_that_line()
    {
        var vm = EndingAt(10, lastEnd: 23);   // line 9 sings 18-23 s
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[5]);
            vm.ToggleLine(vm.Lines[9]);
            vm.LayoutIndex = 2;
            Assert.Equal(Span(5, 9), Picked(vm));
            Assert.Equal(new ShareClipTiming(10, 13), vm.GetClipTiming());
            Assert.Equal(10, vm.PageSpec.ClipStartSeconds);
            Assert.Equal(23, vm.PageSpec.Lines.Last().EndSeconds);

            vm.LayoutIndex = 0;   // the card keeps its flat 15 s
            Assert.Equal(new ShareClipTiming(10, ShareClipTiming.FallbackSeconds), vm.GetClipTiming());
        }
        finally { vm.Detach(); }

        var open = EndingAt(10, lastEnd: null);   // no end, no words: start + 4 s
        try
        {
            open.ClearSelectionCommand.Execute(null);
            open.ToggleLine(open.Lines[5]);
            open.ToggleLine(open.Lines[9]);
            open.LayoutIndex = 2;
            Assert.Equal(new ShareClipTiming(10, 12), open.GetClipTiming());
        }
        finally { open.Detach(); }
    }

    [AvaloniaFact]
    public void A_last_line_ending_past_sixty_seconds_is_refused()
    {
        // Line 25, the last, starts at 50 s. Ending at 55 s it fits (the old 15 s guess refused it).
        var fits = EndingAt(26, lastEnd: 55);
        try
        {
            fits.ClearSelectionCommand.Execute(null);
            fits.ToggleLine(fits.Lines[0]);
            fits.LayoutIndex = 2;
            fits.ToggleLine(fits.Lines[25]);
            Assert.Equal(Span(0, 25), Picked(fits));
            Assert.Equal(new ShareClipTiming(0, 55), fits.GetClipTiming());
        }
        finally { fits.Detach(); }

        var tooLong = EndingAt(26, lastEnd: 62);
        try
        {
            tooLong.ClearSelectionCommand.Execute(null);
            tooLong.ToggleLine(tooLong.Lines[0]);
            tooLong.LayoutIndex = 2;
            tooLong.ToggleLine(tooLong.Lines[25]);
            Assert.Equal(Span(0, 0), Picked(tooLong));
            Assert.Equal(Localization.Loc.T("ShareLyrics.PageMaxLength", 60), tooLong.StatusText);
        }
        finally { tooLong.Detach(); }
    }

    [AvaloniaFact]
    public void Entering_the_page_clears_a_stale_status_and_leaving_clears_the_length_warning()
    {
        var vm = Synced(60);
        try
        {
            vm.ReportStatus("Saved");
            vm.LayoutIndex = 2;   // lines 0-3 fit
            Assert.Equal(string.Empty, vm.StatusText);

            vm.LayoutIndex = 0;
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[0]);
            vm.ToggleLine(vm.Lines[50]);
            vm.LayoutIndex = 2;
            Assert.Equal(Localization.Loc.T("ShareLyrics.PageMaxLength", 60), vm.StatusText);
            vm.LayoutIndex = 0;
            Assert.Equal(string.Empty, vm.StatusText);

            vm.LayoutIndex = 2;
            vm.ReportStatus("Copied");   // any other status stays when leaving
            vm.LayoutIndex = 1;
            Assert.Equal("Copied", vm.StatusText);
        }
        finally { vm.Detach(); }
    }

    [AvaloniaFact]
    public void Safe_zones_default_on_for_story_and_off_for_square()
    {
        var vm = Synced(10);
        try
        {
            Assert.False(vm.SafeZonesEnabled);   // the dialog opens on 1:1
            vm.FormatIndex = 1;
            Assert.True(vm.SafeZonesEnabled);
            vm.FormatIndex = 0;
            Assert.False(vm.SafeZonesEnabled);

            vm.SafeZonesEnabled = true;          // the user's choice holds...
            vm.LayoutIndex = 2;
            vm.TextColorIndex = 1;
            vm.FormatIndex = 0;                  // (already 1:1: no format change)
            Assert.True(vm.SafeZonesEnabled);
            Assert.True(vm.PageSpec.SafeZones);

            vm.FormatIndex = 1;                  // ...until the format changes
            vm.SafeZonesEnabled = false;
            Assert.False(vm.PageSpec.SafeZones);
            vm.FormatIndex = 0;
            vm.FormatIndex = 1;
            Assert.True(vm.SafeZonesEnabled);
        }
        finally { vm.Detach(); }
    }

    private static string WritePng(string dir)
    {
        var path = System.IO.Path.Combine(dir, "bg.png");
        using var bmp = new SkiaSharp.SKBitmap(8, 8);
        bmp.Erase(SkiaSharp.SKColors.SteelBlue);
        using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        System.IO.File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact]
    public async Task Choosing_and_removing_a_background_updates_the_spec()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("noctis-page-bg-").FullName;
        var vm = PageRange(10, 2, 4);
        try
        {
            Assert.Equal(0, vm.PageBackgroundIndex);
            Assert.Equal(Noctis.Services.LyricsPage.PageBackgroundTint.DefaultStrength, vm.PageBackgroundStrength);
            Assert.False(vm.CustomBackgroundAvailable);
            Assert.Equal(Noctis.Services.LyricsPage.LyricsPageBackgroundKind.ArtworkWash, vm.PageSpec.BackgroundKind);

            vm.PageBackgroundIndex = 2;   // nothing chosen yet: stays on the artwork
            Assert.Equal(0, vm.PageBackgroundIndex);

            var png = WritePng(dir);
            await vm.SetPageBackgroundFileAsync(png);
            Assert.Equal(2, vm.PageBackgroundIndex);
            Assert.True(vm.CustomBackgroundAvailable);
            Assert.Equal(png, vm.PageBackgroundPath);
            var spec = vm.PageSpec;
            Assert.Equal(Noctis.Services.LyricsPage.LyricsPageBackgroundKind.Custom, spec.BackgroundKind);
            Assert.Equal(png, spec.BackgroundMediaPath);

            vm.PageBackgroundIndex = 0;   // back to the artwork, the file stays offered
            Assert.True(vm.CustomBackgroundAvailable);
            Assert.Null(vm.PageSpec.BackgroundMediaPath);
            vm.PageBackgroundIndex = 2;

            await vm.RemovePageBackgroundCommand.ExecuteAsync(null);
            Assert.Equal(0, vm.PageBackgroundIndex);
            Assert.Equal("", vm.PageBackgroundPath);
            Assert.False(vm.CustomBackgroundAvailable);
            Assert.Equal(Noctis.Services.LyricsPage.LyricsPageBackgroundKind.ArtworkWash, vm.PageSpec.BackgroundKind);

            // A remembered file that still exists is the pick again; a gone one is dropped.
            vm.ApplyPageSettings(png, 55);
            Assert.Equal(2, vm.PageBackgroundIndex);
            Assert.Equal(png, vm.PageBackgroundPath);
            Assert.Equal(55, vm.PageSpec.BackgroundStrength);

            vm.ApplyPageSettings(System.IO.Path.Combine(dir, "gone.png"), 40);
            Assert.False(vm.CustomBackgroundAvailable);
            Assert.Equal(0, vm.PageBackgroundIndex);
            Assert.Equal(40, vm.PageSpec.BackgroundStrength);

            vm.ApplyPageSettings("", 250);   // settings.json is not clamped
            Assert.Equal(100, vm.PageBackgroundStrength);
            vm.ApplyPageSettings("", -5);
            Assert.Equal(0, vm.PageBackgroundStrength);
        }
        finally
        {
            vm.Detach();
            try { System.IO.Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [AvaloniaFact]
    public void Index_one_is_ignored_without_an_animated_cover()
    {
        var vm = PageRange(10, 2, 4);
        try
        {
            Assert.Null(vm.AnimatedCoverPath);
            Assert.False(vm.AnimatedCoverAvailable);
            vm.PageBackgroundIndex = 1;
            Assert.Equal(0, vm.PageBackgroundIndex);
            Assert.Equal(Noctis.Services.LyricsPage.LyricsPageBackgroundKind.ArtworkWash, vm.PageSpec.BackgroundKind);
        }
        finally { vm.Detach(); }
    }

    private sealed class FixedCover(string? path) : IAnimatedCoverService
    {
        public string? Resolve(Track track) => path;
        public Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope) => Task.FromResult(sourcePath);
        public Task RemoveAsync(Track track, AnimatedCoverScope scope) => Task.CompletedTask;
    }

    /// <summary>Settings kept in memory; counts saves.</summary>
    private sealed class MemorySettings : TestPersistenceService
    {
        public AppSettings Settings { get; } = new();
        public int Saves;
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
        public override Task SaveSettingsAsync(AppSettings settings) { Interlocked.Increment(ref Saves); return Task.CompletedTask; }
    }

    private sealed class PageServices(IAnimatedCoverService cover, IPersistenceService persistence) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IAnimatedCoverService) ? cover
            : serviceType == typeof(IPersistenceService) ? persistence : null;
    }

    /// <summary>The dialog reads the remembered background and readability on open, offers the
    /// track's animated cover, and writes the choices back.</summary>
    [AvaloniaFact]
    public async Task Background_choices_are_remembered_for_the_next_share()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("noctis-page-bg-").FullName;
        var previous = App.Services;
        using var settings = new MemorySettings();
        try
        {
            var png = WritePng(dir);
            var clip = System.IO.Path.Combine(dir, "cover.mp4");
            settings.Settings.ShareLyricsPageBackgroundPath = png;
            settings.Settings.ShareLyricsPageBackgroundStrength = 140;
            App.Services = new PageServices(new FixedCover(clip), settings);

            var vm = Synced(10);
            try
            {
                Assert.True(PumpUntil(() => vm.PageBackgroundIndex == 2));
                Assert.Equal(png, vm.PageBackgroundPath);
                Assert.Equal(100, vm.PageBackgroundStrength);

                Assert.Equal(clip, vm.AnimatedCoverPath);
                Assert.True(vm.AnimatedCoverAvailable);
                vm.PageBackgroundIndex = 1;
                Assert.Equal(1, vm.PageBackgroundIndex);
                Assert.Equal(Noctis.Services.LyricsPage.LyricsPageBackgroundKind.AnimatedCover, vm.PageSpec.BackgroundKind);
                Assert.Equal(clip, vm.PageSpec.BackgroundMediaPath);

                await vm.RemovePageBackgroundCommand.ExecuteAsync(null);
                Assert.Equal("", settings.Settings.ShareLyricsPageBackgroundPath);
                Assert.Equal(1, vm.PageBackgroundIndex);   // the animated cover stays picked

                await vm.SetPageBackgroundFileAsync(png);
                Assert.Equal(png, settings.Settings.ShareLyricsPageBackgroundPath);
                int saves = settings.Saves;

                vm.PageBackgroundStrength = 30;
                Assert.Equal(saves, settings.Saves);   // the slider saves once, on close
            }
            finally { vm.Detach(); }
            Assert.True(PumpUntil(() => settings.Settings.ShareLyricsPageBackgroundStrength == 30));

            // Closing without moving the slider writes nothing.
            var again = Synced(10);
            Assert.True(PumpUntil(() => again.PageBackgroundStrength == 30));
            int before = settings.Saves;
            again.Detach();
            PumpUntil(() => false, 100);
            Assert.Equal(before, settings.Saves);
        }
        finally
        {
            App.Services = previous;
            try { System.IO.Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Lines 2 s apart; the song's last line ends at <paramref name="lastEnd"/> and its
    /// adlib (background vocal) at <paramref name="adlibEnd"/>.</summary>
    private static LyricShareViewModel EndingWithAdlib(int count, double lastEnd, double adlibEnd)
    {
        var lines = Enumerable.Range(0, count).Select(i => $"line {i}").ToList();
        var stamps = Enumerable.Range(0, count).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 2)).ToList();
        var ends = Enumerable.Range(0, count)
            .Select(i => i == count - 1 ? (TimeSpan?)TimeSpan.FromSeconds(lastEnd) : null).ToList();
        var sources = Enumerable.Range(0, count).Select(i => i < count - 1
            ? new LyricLine { Text = lines[i], Timestamp = stamps[i] }
            : new LyricLine
            {
                Text = lines[i], Timestamp = stamps[i], EndTimestamp = ends[i],
                BackgroundEndTimestamp = TimeSpan.FromSeconds(adlibEnd),
                BackgroundWords = new[] { new WordTiming { Text = "yeah", Start = TimeSpan.FromSeconds(lastEnd - 1) } },
            }).ToList();
        return new LyricShareViewModel(Song(), lines, stamps, null, endTimestamps: ends, sources: sources);
    }

    /// <summary>A background vocal that runs past the song's last line holds the clip open.</summary>
    [AvaloniaFact]
    public void An_adlib_past_the_last_line_extends_the_clip()
    {
        var vm = EndingWithAdlib(10, lastEnd: 23, adlibEnd: 27);   // line 9 sings 18-23 s, its adlib to 27 s
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[5]);
            vm.ToggleLine(vm.Lines[9]);
            vm.LayoutIndex = 2;
            Assert.Equal(Span(5, 9), Picked(vm));
            Assert.Equal(17, vm.GetClipTiming().DurationSeconds);
        }
        finally { vm.Detach(); }

        // Line 25, the last, starts at 50 s and ends at 55 s, but its adlib runs to 62 s.
        var tooLong = EndingWithAdlib(26, lastEnd: 55, adlibEnd: 62);
        try
        {
            tooLong.ClearSelectionCommand.Execute(null);
            tooLong.ToggleLine(tooLong.Lines[0]);
            tooLong.LayoutIndex = 2;
            tooLong.ToggleLine(tooLong.Lines[25]);
            Assert.Equal(Span(0, 0), Picked(tooLong));
            Assert.Equal(Localization.Loc.T("ShareLyrics.PageMaxLength", 60), tooLong.StatusText);
        }
        finally { tooLong.Detach(); }
    }

    /// <summary>An open last word (no end, no line end) sweeps 2 s, as on the lyrics page.</summary>
    [AvaloniaFact]
    public void An_open_last_word_ends_the_clip_two_seconds_after_it_starts()
    {
        var lines = Enumerable.Range(0, 10).Select(i => $"line {i}").ToList();
        var stamps = Enumerable.Range(0, 10).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 2)).ToList();
        var words = Enumerable.Range(0, 10).Select(i => i < 9 ? null : (IReadOnlyList<WordTiming>?)new[]
        {
            new WordTiming { Text = "long ", Start = TimeSpan.FromSeconds(18) },
            new WordTiming { Text = "note", Start = TimeSpan.FromSeconds(21.5) },   // open: sweeps to 23.5 s
        }).ToList();
        var vm = new LyricShareViewModel(Song(), lines, stamps, null, words);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[5]);
            vm.ToggleLine(vm.Lines[9]);
            vm.LayoutIndex = 2;
            Assert.Equal(new ShareClipTiming(10, 13.5), vm.GetClipTiming());
        }
        finally { vm.Detach(); }
    }

    [Fact]
    public void Preview_clock_follows_playback_or_loops_the_clip()
    {
        var t = new ShareClipTiming(30, 12);
        Assert.Equal(41.08, Noctis.Services.LyricsPage.PagePreviewClock.TrackTime(true, 41, 999, t), 9);
        Assert.Equal(35.08, Noctis.Services.LyricsPage.PagePreviewClock.TrackTime(false, 0, 5, t), 9);
        Assert.Equal(31.08, Noctis.Services.LyricsPage.PagePreviewClock.TrackTime(false, 0, 25, t), 9);
    }

    /// <summary>One animation timer tick, as EqBarRepaintTests drives its timer: headless
    /// DispatcherTimers barely fire under RunJobs.</summary>
    private static void Tick(LyricShareViewModel vm) =>
        typeof(LyricShareViewModel).GetMethod("OnAnimTick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);

    [AvaloniaFact]
    public void Page_preview_animates_without_a_player()
    {
        var vm = WordTimed(12);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[3]);
            vm.ToggleLine(vm.Lines[5]);
            vm.LayoutIndex = 2;
            int frames = 0;
            vm.AnimatedFrameRendered += () => frames++;

            Assert.True(PumpUntil(() => vm.AnimatedPreview != null && frames > 0, 5000), "page preview never appeared");
            Assert.Null(vm.Preview);
            byte[]? before = null;
            Assert.True(PumpUntil(() => (before = vm.PageFrameSnapshot()) != null, 2000));

            Thread.Sleep(600);
            int seen = frames;
            Tick(vm);
            Assert.True(PumpUntil(() => frames > seen, 2000), "no frame after a tick");
            byte[]? after = null;
            Assert.True(PumpUntil(() => (after = vm.PageFrameSnapshot()) != null, 2000));
            Assert.False(before.AsSpan().SequenceEqual(after), "the page preview did not move");

            vm.LayoutIndex = 0;
            Assert.True(PumpUntil(() => vm.Preview != null && vm.IsPreviewCurrent, 5000), "card preview did not come back");
            Assert.Null(vm.AnimatedPreview);
        }
        finally { vm.Detach(); }
    }

    private static object? Animator(LyricShareViewModel vm) =>
        typeof(LyricShareViewModel).GetField("_animator",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm);

    /// <summary>A dialog on 30 synced lines 2 s apart while its song plays at <paramref name="positionSeconds"/>.</summary>
    private static (LyricShareViewModel Vm, PlayerViewModel Player) WhilePlaying(TestPersistenceService persistence,
        double positionSeconds)
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var song = Song();
        player.CurrentTrack = song;
        player.Position = TimeSpan.FromSeconds(positionSeconds);
        player.State = PlaybackState.Playing;
        var lines = Enumerable.Range(0, 30).Select(i => $"line {i}").ToList();
        var stamps = Enumerable.Range(0, 30).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 2)).ToList();
        return (new LyricShareViewModel(song, lines, stamps, player), player);
    }

    /// <summary>
    /// Bug: Sync stayed on in the Lyrics Page, so each new line re-picked the range and rebuilt
    /// the preview, whose new list snapped at every line change. Entering the page turns it off.
    /// </summary>
    [AvaloniaFact]
    public void Entering_the_page_turns_sync_off_and_keeps_its_picks()
    {
        using var persistence = new TestPersistenceService();
        var (vm, _) = WhilePlaying(persistence, 10);
        try
        {
            Assert.True(vm.SyncEnabled);
            Assert.Equal(Span(5, 8), Picked(vm));
            vm.LayoutIndex = 2;
            Assert.False(vm.SyncEnabled);
            Assert.Equal(Span(5, 8), Picked(vm));
        }
        finally { vm.Detach(); }
    }

    /// <summary>Bug: with Sync off the page preview still showed wherever the song was playing,
    /// not the clip being made. It loops the clip.</summary>
    [AvaloniaFact]
    public void With_sync_off_the_page_preview_shows_the_clip_not_the_playback()
    {
        using var persistence = new TestPersistenceService();
        var (vm, _) = WhilePlaying(persistence, 40);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[3]);
            vm.ToggleLine(vm.Lines[5]);
            vm.LayoutIndex = 2;
            Assert.False(vm.SyncEnabled);
            Assert.True(PumpUntil(() => Animator(vm) != null && vm.IsPreviewCurrent, 5000), "page preview never appeared");

            var timing = vm.GetClipTiming();
            var t = vm.AnimatedPreviewTime();
            Assert.InRange(t, timing.StartSeconds, timing.StartSeconds + timing.DurationSeconds + 0.1);
        }
        finally { vm.Detach(); }
    }

    /// <summary>With Sync turned back on the page preview follows playback inside its clip; the
    /// line steps do not rebuild it (no snap), and playback leaving the clip does.</summary>
    [AvaloniaFact]
    public void With_sync_on_the_page_preview_follows_playback_without_rebuilding_each_line()
    {
        using var persistence = new TestPersistenceService();
        var (vm, player) = WhilePlaying(persistence, 10);
        try
        {
            vm.LayoutIndex = 2;   // range 5-8: the clip 10-18 s
            Assert.True(PumpUntil(() => Animator(vm) != null && vm.IsPreviewCurrent, 5000), "page preview never appeared");
            vm.ToggleSyncCommand.Execute(null);
            Assert.True(vm.SyncEnabled);
            Assert.True(PumpUntil(() => vm.IsPreviewCurrent, 5000));
            var shown = Animator(vm);
            Assert.InRange(vm.AnimatedPreviewTime(), 10.0, 11.5);

            player.Position = TimeSpan.FromSeconds(12.1);   // the next line: Sync re-picks 6-9
            Assert.Equal(Span(6, 9), Picked(vm));
            PumpUntil(() => false, 300);
            Assert.Same(shown, Animator(vm));
            Assert.InRange(vm.AnimatedPreviewTime(), 12.1, 13.5);

            player.Position = TimeSpan.FromSeconds(30);   // out of the clip: redone around playback
            Assert.True(PumpUntil(() => !ReferenceEquals(shown, Animator(vm)) && vm.IsPreviewCurrent, 5000),
                "the preview was not rebuilt once playback left its clip");
            Assert.InRange(vm.AnimatedPreviewTime(), 30.0, 31.5);
        }
        finally { vm.Detach(); }
    }

    /// <summary>Bug: every option change restarted the preview loop at the clip start (and with a
    /// clip background restarted its ffmpeg). The same clip keeps its place.</summary>
    [AvaloniaFact]
    public void An_option_change_on_the_same_clip_keeps_the_loop_going()
    {
        var vm = WordTimed(12);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[3]);
            vm.ToggleLine(vm.Lines[5]);
            vm.LayoutIndex = 2;   // the clip 6-12 s
            Assert.True(PumpUntil(() => Animator(vm) != null && vm.IsPreviewCurrent, 5000), "page preview never appeared");
            var first = Animator(vm);
            Thread.Sleep(800);

            vm.SafeZonesEnabled = !vm.SafeZonesEnabled;   // rebuilds the animator, same clip
            Assert.True(PumpUntil(() => !ReferenceEquals(first, Animator(vm)) && vm.IsPreviewCurrent, 5000), "no rebuild");
            var into = vm.AnimatedPreviewTime() - vm.GetClipTiming().StartSeconds;
            Assert.True(into >= 0.7, $"the loop restarted: {into:F2} s into the clip");
        }
        finally { vm.Detach(); }
    }

    [AvaloniaFact]
    public void Readability_strength_changes_the_page_preview_without_a_rebuild()
    {
        var vm = WordTimed(12);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[3]);
            vm.ToggleLine(vm.Lines[5]);
            vm.LayoutIndex = 2;
            Assert.True(PumpUntil(() => Animator(vm) != null && vm.IsPreviewCurrent, 5000), "page preview never appeared");
            var shown = Animator(vm);

            vm.PageBackgroundStrength = 10;
            PumpUntil(() => false, 300);
            Assert.Same(shown, Animator(vm));
            Assert.True(vm.IsPreviewCurrent);
            Assert.Equal(10, vm.PageSpec.BackgroundStrength);   // the export reads it as it stands
        }
        finally { vm.Detach(); }
    }

    private static HashSet<string> TempEntries(string pattern) =>
        new(System.IO.Directory.EnumerateFileSystemEntries(System.IO.Path.GetTempPath(), pattern), StringComparer.OrdinalIgnoreCase);

    // App.Services is process-wide; safe because the suite runs one class at a time
    // (HeadlessTestApp: DisableTestParallelization), and it is always restored.
    [AvaloniaFact]
    public void Closing_during_the_page_frame_pass_cancels_and_leaves_nothing()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"noctis-share-test-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        var previous = App.Services;
        try
        {
            App.Services = new LyricShareDialogTests.FfmpegOnly(LyricShareDialogTests.WriteSlowFfmpeg(dir));
            var audio = System.IO.Path.Combine(dir, "song.flac");
            System.IO.File.WriteAllText(audio, "audio");
            var lines = Enumerable.Range(0, 30).Select(i => $"line {i}").ToList();
            var stamps = Enumerable.Range(0, 30).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 2)).ToList();
            var vm = new LyricShareViewModel(Song(filePath: audio), lines, stamps, null);
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[0]);
            vm.ToggleLine(vm.Lines[20]);   // a 40 s clip: thousands of frames
            vm.LayoutIndex = 2;
            Assert.True(vm.CanExportVideo);

            var output = System.IO.Path.Combine(dir, "clip.mp4");
            var framesBefore = TempEntries("noctis-karaoke-*");
            var clipsBefore = TempEntries("noctis-clip-*.mp4");
            var export = vm.ExportClipAsync(output);
            Assert.True(PumpUntil(() => Enumerable.Range(1, 100).Any(n => vm.StatusText == Localization.Loc.T("ShareLyrics.Saving", n)), 20000),
                $"the frame pass never reported progress ({vm.StatusText})");
            Assert.NotEmpty(TempEntries("noctis-karaoke-*").Except(framesBefore));

            vm.Detach();   // what closing the dialog does

            Assert.True(PumpUntil(() => export.IsCompleted, 8000), "the export kept running after the dialog closed");
            Assert.Equal(Localization.Loc.T("ShareLyrics.Cancelled"), export.Result);
            Assert.False(System.IO.File.Exists(output), "a clip was left behind");
            Assert.Empty(TempEntries("noctis-karaoke-*").Except(framesBefore));
            Assert.Empty(TempEntries("noctis-clip-*.mp4").Except(clipsBefore));
            Assert.False(vm.IsRendering);
        }
        finally
        {
            App.Services = previous;
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public void Page_save_card_renders_the_mid_line_still()
    {
        var vm = WordTimed(12);
        try
        {
            vm.ClearSelectionCommand.Execute(null);
            vm.ToggleLine(vm.Lines[3]);
            vm.ToggleLine(vm.Lines[5]);
            vm.LayoutIndex = 2;
            vm.FormatIndex = 1;

            var render = vm.RenderExportPngAsync();
            Assert.True(PumpUntil(() => render.IsCompleted, 10000));
            var png = render.Result;
            Assert.NotNull(png);
            using var bitmap = SkiaSharp.SKBitmap.Decode(png);
            Assert.Equal(2160, bitmap.Width);
            Assert.Equal(3840, bitmap.Height);

            // The still is the page's frame halfway through the range's first line, as the
            // renderer draws it (at the same 2x scale).
            var timing = vm.GetClipTiming();
            var expected = Noctis.Services.LyricsPage.LyricsPageRenderer.RenderStillPng(vm.PageSpec,
                Noctis.Services.LyricsPage.LyricsPageRenderer.StillTime(timing, 8), ShareCardRenderer.CardExportScale);
            using var want = SkiaSharp.SKBitmap.Decode(expected);
            Assert.True(want.Bytes.AsSpan().SequenceEqual(bitmap.Bytes), "not the mid-line still");
        }
        finally { vm.Detach(); }
    }

    private static T Named<T>(Avalonia.Controls.Window w, string name) where T : Avalonia.Controls.Control =>
        Avalonia.Controls.ControlExtensions.FindControl<T>(w, name)!;

    [AvaloniaFact]
    public void Picking_the_Lyrics_Page_swaps_the_option_row()
    {
        LyricShareDialogTests.EnsureAppStyles();
        var vm = WordTimed(12);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        try
        {
            var layout = Named<Avalonia.Controls.ComboBox>(win, "LayoutBox");
            Assert.Equal(2, layout.ItemCount); // the picker no longer offers it (owner 11-08)
            Assert.True(Named<Avalonia.Controls.ComboBox>(win, "TextColorBox").IsEffectivelyVisible);
            Assert.False(Named<Avalonia.Controls.ComboBox>(win, "PageBackgroundBox").IsEffectivelyVisible);
            Assert.True(Named<Avalonia.Controls.CheckBox>(win, "KaraokeBox").IsEffectivelyVisible);

            vm.LayoutIndex = 2;
            PumpUntil(() => false, 100);
            Assert.False(Named<Avalonia.Controls.ComboBox>(win, "TextColorBox").IsEffectivelyVisible);
            Assert.False(Named<Avalonia.Controls.ComboBox>(win, "BackgroundBox").IsEffectivelyVisible);
            Assert.False(Named<Avalonia.Controls.CheckBox>(win, "KaraokeBox").IsEffectivelyVisible);
            var background = Named<Avalonia.Controls.ComboBox>(win, "PageBackgroundBox");
            var readability = Named<Avalonia.Controls.Slider>(win, "ReadabilitySlider");
            var zones = Named<Avalonia.Controls.CheckBox>(win, "SafeZonesBox");
            Assert.True(background.IsEffectivelyVisible);
            Assert.True(readability.IsEffectivelyVisible);
            Assert.True(zones.IsEffectivelyVisible);
            Assert.True(Named<Avalonia.Controls.Button>(win, "ChooseBackgroundButton").IsEffectivelyVisible);
            Assert.False(Named<Avalonia.Controls.Button>(win, "RemoveBackgroundButton").IsEffectivelyVisible);
            Assert.Equal(3, background.ItemCount);
            Assert.Equal(Localization.Loc.T("LyricShare.Background"), Avalonia.Automation.AutomationProperties.GetName(background));
            Assert.Equal(Localization.Loc.T("LyricShare.Readability"), Avalonia.Automation.AutomationProperties.GetName(readability));

            // The controls drive the view-model.
            readability.Value = 30;
            Assert.Equal(30, vm.PageBackgroundStrength);
            Assert.False(zones.IsChecked);
            vm.FormatIndex = 1;
            PumpUntil(() => false, 50);
            Assert.True(zones.IsChecked == true);

            vm.LayoutIndex = 0;
            PumpUntil(() => false, 50);
            Assert.True(Named<Avalonia.Controls.ComboBox>(win, "TextColorBox").IsEffectivelyVisible);
            Assert.False(background.IsEffectivelyVisible);
        }
        finally
        {
            if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
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
}

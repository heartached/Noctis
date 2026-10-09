using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Find Duplicates as the rounded pill pop-up (the Metadata editor / Find
/// Metadata look): it mounts inside PillDialogHost with the pill classes resolved, each copy
/// is a filled card that toggles keep/delete (accent ring = kept), the best copy is marked,
/// and the header/footer actions are bound to the view model's commands.
/// </summary>
public class DuplicateFinderDialogTests
{
    private readonly ITestOutputHelper _o;
    public DuplicateFinderDialogTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
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

    private static Track Copy(string path, string codec, int kbps, int rate, int bits, long size) => new()
    {
        Id = Guid.NewGuid(), Title = "YAYA", Artist = "6ix9ine", Album = "Dummy Boy",
        Duration = TimeSpan.FromSeconds(149), FilePath = path, Codec = codec,
        Bitrate = kbps, SampleRate = rate, BitsPerSample = bits, FileSize = size,
    };

    private static List<DuplicateGroup> Groups()
    {
        var best = Copy(@"C:\Music\6ix9ine\Dummy Boy\01 YAYA.m4a", "Apple Lossless", 1391, 44100, 16, 26_000_000);
        var worse = Copy(@"C:\Music\Downloads\YAYA.mp3", "MPEG Layer 3", 320, 44100, 0, 6_000_000);
        var hiRes = Copy(@"C:\Music\Hi-Res\Song.flac", "FLAC", 4600, 96000, 24, 90_000_000);
        hiRes.Title = "Other Song";
        var cd = Copy(@"C:\Music\CD\Song.flac", "FLAC", 900, 44100, 16, 30_000_000);
        cd.Title = "Other Song";
        return new List<DuplicateGroup>
        {
            new(new[] { worse, best }, best.Id),
            new(new[] { cd, hiRes }, hiRes.Id),
        };
    }

    private static (DuplicateFinderViewModel vm, DuplicateFinderDialog win, PillDialogHost host) Open(IReadOnlyList<DuplicateGroup> groups)
    {
        var vm = new DuplicateFinderViewModel(new StubDuplicates(groups));
        var win = new DuplicateFinderDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (vm, win, host);
    }

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    [AvaloniaFact]
    public void OpensInPillHost_WithResolvedStyles_AndBoundActions()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host) = Open(Groups());
            try
            {
                Assert.NotNull(host.Card);
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                var all = win.GetVisualDescendants().ToList();

                // Footer: Delete N is the solid accent pill, Close the quiet pill; Rescan the
                // header pill. Each bound to its command.
                var delete = all.OfType<Button>().Single(b => b.Command == vm.DeleteSelectedCommand);
                PillDialogHostTests.AssertSolidAccent(delete);
                Assert.True(delete.IsEnabled);
                var close = all.OfType<Button>().Single(b => b.Command == vm.CloseCommand);
                Assert.Contains("pill-secondary", close.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(close.Background));
                var rescan = all.OfType<Button>().Single(b => b.Command == vm.RescanCommand);
                Assert.Contains("pill-secondary", rescan.Classes);
                Assert.True(rescan.IsEnabled);
                Assert.Equal(2, vm.SelectedCount); // one non-best copy per group

                // Header icon well: pill-well resolved (filled tone + radius from PillDialog.axaml).
                var well = all.OfType<Border>().First(b => b.Classes.Contains("pill-well"));
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(well.Background));

                // Copy cards: one filled toggle card per copy; the best copy is kept (checked,
                // accent ring), the other is not (no ring) — no checkbox anywhere.
                Assert.Empty(all.OfType<CheckBox>());
                var cards = all.OfType<ToggleButton>().Where(t => t.Classes.Contains("df-copy")).ToList();
                Assert.Equal(4, cards.Count);
                var accent = AccentTestHarness.ResourceColor("AccentColorBrush");
                foreach (var card in cards)
                {
                    var row = (DuplicateFinderViewModel.DupRow)card.DataContext!;
                    Assert.Equal(row.Keep, card.IsChecked);
                    Assert.Equal(row.IsSuggestedKeep, row.Keep);
                    Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(card.Background));
                    var ring = AccentTestHarness.ColorOf(card.BorderBrush);
                    Assert.Equal(row.Keep ? accent : Colors.Transparent, ring);
                    // Fluent's :checked accent fill must not paint over the card.
                    Assert.DoesNotContain(card.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>(), p => p.Name == "PART_ContentPresenter");
                }

                // The "Best" tag shows only on the matcher's pick.
                var bestTags = all.OfType<TextBlock>().Where(t => t.Text == "Best" && t.IsEffectivelyVisible).ToList();
                Assert.Equal(2, bestTags.Count);

                // Group header: title, artist, copy chip; nothing else wordy.
                Assert.Contains(all.OfType<TextBlock>(), t => t.Text == "YAYA");
                Assert.Contains(all.OfType<TextBlock>(), t => t.Text == "6ix9ine");
                Assert.Contains(all.OfType<TextBlock>(), t => t.Text == "2 copies");
                Assert.Equal("2 groups", vm.StatusMessage);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void ClickingACard_TogglesKeepDelete_AndTheDeleteCount()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host) = Open(Groups());
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)));
                var card = win.GetVisualDescendants().OfType<ToggleButton>()
                    .First(t => t.Classes.Contains("df-copy") && t.IsChecked != true);
                var row = (DuplicateFinderViewModel.DupRow)card.DataContext!;
                Assert.True(row.Delete);
                Assert.Equal(2, vm.SelectedCount);

                // A real click anywhere on the card flips it from Delete to Keep.
                var centre = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), win)!.Value;
                win.MouseDown(centre, MouseButton.Left);
                win.MouseUp(centre, MouseButton.Left);
                PumpUntil(() => !row.Delete, 500);

                Assert.False(row.Delete);
                Assert.True(row.Keep);
                Assert.True(card.IsChecked);
                Assert.Equal(1, vm.SelectedCount);
                var accent = AccentTestHarness.ResourceColor("AccentColorBrush");
                Assert.True(PumpUntil(() => AccentTestHarness.ColorOf(card.BorderBrush) == accent, 1000),
                    $"ring {card.BorderBrush} ({AccentTestHarness.ColorOf(card.BorderBrush)}), expected {accent}");

                // Keep every copy of both groups: nothing to delete, Delete greys out.
                foreach (var r in vm.Groups.SelectMany(g => g.Rows)) r.Keep = true;
                Assert.Equal(0, vm.SelectedCount);
                var delete = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.DeleteSelectedCommand);
                PumpUntil(() => !delete.IsEnabled, 300);
                Assert.False(delete.IsEnabled);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void CloseButton_AnimatesThenCloses()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open(Groups());
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        vm.CloseCommand.Execute(null);
        Assert.True(host.IsClosing);
        Assert.True(win.IsVisible);
        Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void Escape_ClosesLikeClose()
    {
        EnsureAppStyles();
        var (_, win, host) = Open(Groups());
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void NoDuplicates_ShowsTheEmptyMessage_AndDeleteIsDisabled()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open(Array.Empty<DuplicateGroup>());
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            Assert.False(vm.HasGroups);
            Assert.Equal("No duplicates found", vm.ListMessage);
            var all = win.GetVisualDescendants().ToList();
            Assert.Contains(all.OfType<TextBlock>(), t => t.Text == "No duplicates found" && t.IsEffectivelyVisible);
            Assert.DoesNotContain(all.OfType<ScrollViewer>(), s => s.IsEffectivelyVisible && s.Content is ItemsControl);
            Assert.False(all.OfType<Button>().Single(b => b.Command == vm.DeleteSelectedCommand).IsEnabled);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [Fact]
    public void CopyRows_AreCompact_AndShowFormatOnlyWhenItDiffers()
    {
        var groups = Groups();
        var vm = new DuplicateFinderViewModel(new StubDuplicates(groups));
        var same = vm.Groups[0].Rows;   // ALAC vs MP3, both 44.1 kHz
        var alac = same.Single(r => r.IsSuggestedKeep);
        Assert.Equal("ALAC · 1391 kbps · 24.8 MB", alac.Quality);
        Assert.Equal("MP3 · 320 kbps · 5.7 MB", same.Single(r => !r.IsSuggestedKeep).Quality);
        Assert.DoesNotContain(same, r => r.Quality.Contains("Lossless"));
        Assert.Equal(@"C:\Music\6ix9ine\Dummy Boy\01 YAYA.m4a", alac.Location);
        Assert.Equal(alac.FilePathFull, alac.Location);

        var differ = vm.Groups[1].Rows; // 24/96 vs 16/44.1: the format is what tells them apart
        Assert.Equal("FLAC · 4600 kbps · 24-bit 96 kHz · 85.8 MB", differ.Single(r => r.IsSuggestedKeep).Quality);
        Assert.Equal("FLAC · 900 kbps · 16-bit 44.1 kHz · 28.6 MB", differ.Single(r => !r.IsSuggestedKeep).Quality);

        // A long path keeps its start and its file name.
        var deep = @"C:\Users\someone\Music\Library\Artists\A very long artist name\An even longer album title (Deluxe Edition)\Disc 1\01 Track.flac";
        var row = new DuplicateFinderViewModel.DupRow(Copy(deep, "FLAC", 900, 44100, 16, 1), suggestedKeep: false);
        Assert.True(row.Location.Length <= 80);
        Assert.StartsWith(@"C:\Users", row.Location);
        Assert.EndsWith("01 Track.flac", row.Location);
        Assert.Contains("…", row.Location);

        // Keep is Delete's inverse, both ways, and the count follows.
        var mp3 = same.Single(r => !r.IsSuggestedKeep);
        var changed = new List<string?>();
        mp3.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        mp3.Keep = true;
        Assert.False(mp3.Delete);
        Assert.Contains(nameof(DuplicateFinderViewModel.DupRow.Keep), changed);
        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal("2 copies", vm.Groups[0].CopiesText);
    }

    /// <summary>Real Skia only: the dialog over a busy owner (blurred behind it), saved as a PNG
    /// under %TEMP%\NoctisShots for a look at the layout.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheDialogOverItsOwner()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoctisShots");
        System.IO.Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var stripes = new StackPanel();
            var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
            for (var i = 0; i < 18; i++)
                stripes.Children.Add(new Border
                {
                    Height = 50,
                    Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])),
                    Child = new TextBlock { Text = $"Library row {i}", FontSize = 22, Margin = new Thickness(24, 8), Foreground = Brushes.White },
                });
            var owner = new Window { Width = 1100, Height = 820, Content = stripes, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            PumpUntil(() => false, 100);

            var groups = Groups();
            for (var i = 0; i < 6; i++)
            {
                var a = Copy($@"C:\Users\me\Music\Artist {i}\Album {i}\0{i} Track {i}.flac", "FLAC", 1000 + i, 44100, 16, 30_000_000 + i);
                var b = Copy($@"D:\Old Library\Downloads\Track {i}.mp3", "MPEG Layer 3", 320, 44100, 0, 8_000_000);
                a.Title = b.Title = $"Track number {i}";
                a.Artist = b.Artist = $"Artist {i}";
                groups.Add(new DuplicateGroup(new[] { b, a }, a.Id));
            }
            var vm = new DuplicateFinderViewModel(new StubDuplicates(groups));
            var win = new DuplicateFinderDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            _ = win.ShowDialog(owner);
            var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host) && host.BackdropBitmap != null, 3000));
                PumpUntil(() => false, 150);
                var path = System.IO.Path.Combine(dir, "duplicates-pill-dialog.png");
                win.CaptureRenderedFrame()!.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                _o.WriteLine(path);
            }
            finally
            {
                if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
                owner.Close();
            }
        });
    }

    /// <summary>A scan that runs until the test lets it finish.</summary>
    private sealed class GatedDuplicates(IReadOnlyList<DuplicateGroup> groups) : IDuplicateFinderService
    {
        public TaskCompletionSource Gate { get; private set; } = new();
        public void Reset() => Gate = new TaskCompletionSource();
        public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(int durationToleranceSeconds = DuplicateMatcher.DefaultDurationToleranceSeconds,
            CancellationToken ct = default)
        {
            await Gate.Task;
            return groups;
        }
        public Task<int> DeleteAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Owner 10-08: the Rescan icon was a fuzzy 12px hairline ring and never animated. It is a
    /// 16px stroked arrow now that turns while a scan runs, finishes its turn with an ease-out
    /// when the scan ends (no snap back), and the pill stays enabled-looking meanwhile.
    /// </summary>
    [AvaloniaFact]
    public void RescanArrow_SpinsWhileScanning_ThenEasesToRest()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var service = new GatedDuplicates(Groups());
            var vm = new DuplicateFinderViewModel(service);
            var win = new DuplicateFinderDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            win.Show();
            try
            {
                var icon = win.GetVisualDescendants().OfType<Viewbox>().Single(v => v.Name == "RescanIcon");
                Assert.Equal(16, icon.Width);
                var path = icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                Assert.True(path.StrokeThickness >= 2, $"stroke {path.StrokeThickness}");
                var rescan = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.RescanCommand);

                // The opening scan: turning, and the pill is not greyed out.
                Assert.True(vm.IsBusy);
                Assert.True(PumpUntil(() => win.IsRescanSpinning && win.RescanAngle > 30), $"never turned (angle {win.RescanAngle})");
                Assert.True(rescan.IsEffectivelyEnabled);
                var before = win.RescanAngle;
                PumpUntil(() => false, 150);
                Assert.True(win.RescanAngle > before, "stopped turning mid-scan");

                // The scan ends: it keeps going forward to the next full turn, then rests at 0.
                service.Gate.SetResult();
                Assert.True(PumpUntil(() => !vm.IsBusy));
                var atEnd = win.RescanAngle;
                PumpUntil(() => false, 40);
                Assert.True(!win.IsRescanSpinning || win.RescanAngle >= atEnd, "went backwards on the way to rest");
                Assert.True(PumpUntil(() => !win.IsRescanSpinning, 3000), "never came to rest");
                Assert.Equal(0, win.RescanAngle, 3);

                // Rescan turns it again.
                service.Reset();
                rescan.Command!.Execute(null);
                Assert.True(PumpUntil(() => win.IsRescanSpinning && win.RescanAngle > 10), "Rescan didn't turn it");
                service.Gate.SetResult();
                Assert.True(PumpUntil(() => !win.IsRescanSpinning, 3000));
            }
            finally { win.Close(); }
        });
    }

    private sealed class StubDuplicates(IReadOnlyList<DuplicateGroup> groups) : IDuplicateFinderService
    {
        public Task<IReadOnlyList<DuplicateGroup>> FindAsync(int durationToleranceSeconds = DuplicateMatcher.DefaultDurationToleranceSeconds,
            CancellationToken ct = default) => Task.FromResult(groups);
        public Task<int> DeleteAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

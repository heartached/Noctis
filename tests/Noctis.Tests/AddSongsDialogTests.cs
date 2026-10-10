using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-09: Add Songs as the rounded pill pop-up of the Lyrics Studio picker (blurred app
/// behind, a filled search pill with its icon, tidy rows with the songs already in the playlist
/// tagged, "N selected", Select all / Clear, Cancel, "Add (N)"). The dialog is wired exactly as
/// SidebarViewModel.OpenAddSongsAsync wires it.
/// </summary>
public class AddSongsDialogTests
{
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

    private static Track Make(string title, string artist = "A") =>
        new() { Id = Guid.NewGuid(), Title = title, Artist = artist, Album = "B" };

    /// <summary>Opens the dialog wired as SidebarViewModel.OpenAddSongsAsync does.</summary>
    private static (AddSongsDialog win, List<IReadOnlyList<Track>> chosen) Open(AddSongsDialogViewModel vm)
    {
        var win = new AddSongsDialog { DataContext = vm, RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        var chosen = new List<IReadOnlyList<Track>>();
        vm.SongsChosen += (_, tracks) => chosen.Add(tracks);
        vm.CloseRequested += (_, _) => _ = win.CloseAnimatedAsync();
        win.Show();
        return (win, chosen);
    }

    private static void CloseQuietly(Window win)
    {
        if (!win.IsVisible) return;
        win.Close();
        PumpUntil(() => !win.IsVisible);
    }

    // ── Bugs ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Escape_Closes_WithoutAdding()
    {
        EnsureAppStyles();
        var vm = new AddSongsDialogViewModel(new[] { Make("One"), Make("Two") }, Array.Empty<Guid>());
        vm.ToggleSelectCommand.Execute(vm.Results[0]);
        var (win, chosen) = Open(vm);
        try
        {
            PumpUntil(() => false, 300);
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => !win.IsVisible, 2000), "Esc should close the dialog like Cancel");
            Assert.Empty(chosen);
        }
        finally { CloseQuietly(win); }
    }

    [AvaloniaFact]
    public void EmptyLibrary_DoesNotSayEverySongIsAlreadyInThePlaylist()
    {
        EnsureAppStyles();
        var vm = new AddSongsDialogViewModel(Array.Empty<Track>(), Array.Empty<Guid>());
        var (win, _) = Open(vm);
        try
        {
            PumpUntil(() => false, 300);
            var shown = win.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                .Select(t => t.Text!)
                .ToList();
            Assert.DoesNotContain(shown, t => t.Contains("already in this playlist", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Your library is empty", shown);
        }
        finally { CloseQuietly(win); }
    }

    // ── The pill pop-up ──────────────────────────────────────────────────

    private static readonly Color PillFill = Color.Parse("#1CFFFFFF");

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private static T Named<T>(Window win, string name) where T : Control =>
        win.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static List<Button> Rows(Window win) =>
        Named<ItemsControl>(win, "ResultsList").GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("as-row")).ToList();

    /// <summary>Search is debounced (250ms); poll rather than sleep a fixed amount.</summary>
    private static bool WaitForResults(AddSongsDialogViewModel vm, int expected)
        => PumpUntil(() => vm.Results.Count == expected, 3000);

    [AvaloniaFact]
    public void OpensInPillHost_TitleNamesThePlaylist_FilledSearch_TaggedRows()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var one = Make("Song One");
            var two = Make("Song Two");
            var three = Make("Song Three");
            var vm = new AddSongsDialogViewModel(new[] { one, two, three }, new[] { two.Id }, "Road Trip");
            var (win, _) = Open(vm);
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                Assert.Equal("Add songs to Road Trip", Named<TextBlock>(win, "TitleText").Text);
                Assert.Equal("1 song in this playlist", Named<TextBlock>(win, "SubtitleText").Text);

                // Filled search pill with its icon, no outline, no caret on open.
                var search = Named<TextBox>(win, "SearchBox");
                Assert.Contains("pill-field", search.Classes);
                Assert.IsType<PathIcon>(search.InnerLeftContent);
                Assert.False(search.IsFocused);
                var chrome = search.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
                Assert.Equal(0, AccentTestHarness.ColorOf(chrome.BorderBrush).A);

                // Shuffled picks leave out what the playlist has; the reshuffle pill is there.
                Assert.Equal(2, vm.Results.Count);
                Assert.True(Named<Button>(win, "ReshuffleButton").IsEffectivelyVisible);
                Assert.IsType<VirtualizingStackPanel>(Named<ItemsControl>(win, "ResultsList").ItemsPanelRoot);

                // A search shows the playlist's own song tagged, not tickable.
                vm.SearchText = "Song";
                Assert.True(WaitForResults(vm, 3));
                PumpUntil(() => Rows(win).Count == 3);
                Assert.False(Named<Button>(win, "ReshuffleButton").IsVisible);
                var inPlaylist = Rows(win).Single(r => ((AddSongItem)r.DataContext!).Track.Id == two.Id);
                Assert.Contains("in-playlist", inPlaylist.Classes);
                Assert.False(inPlaylist.IsHitTestVisible);
                var tag = inPlaylist.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("as-tag"));
                Assert.True(tag.IsVisible);
                Assert.Equal("In playlist", tag.GetVisualDescendants().OfType<TextBlock>().Single().Text);
                Assert.False(inPlaylist.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("selection-circle")).IsVisible);
                var other = Rows(win).First(r => ((AddSongItem)r.DataContext!).Track.Id != two.Id);
                Assert.True(other.IsHitTestVisible);
                Assert.False(other.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("as-tag")).IsVisible);

                // Footer: Add greyed until a tick; pills.
                var add = Named<Button>(win, "AddButton");
                PillDialogHostTests.AssertSolidAccent(add);
                Assert.False(add.IsEffectivelyEnabled);
                Assert.Equal("Add", add.Content);
                Assert.Contains("pill-secondary", Named<Button>(win, "SelectAllButton").Classes);
                Assert.False(Named<TextBlock>(win, "SelectionCount").IsVisible);
            }
            finally { CloseQuietly(win); }
        });
    }

    [AvaloniaFact]
    public void Footer_CountsTheSelection_AddN_SelectAllFlipsToClear()
    {
        EnsureAppStyles();
        var library = new[] { Make("One"), Make("Two"), Make("Three") };
        var vm = new AddSongsDialogViewModel(library, Array.Empty<Guid>(), "Gym");
        var (win, _) = Open(vm);
        try
        {
            PumpUntil(() => false, 300);
            var selectAll = Named<Button>(win, "SelectAllButton");
            var add = Named<Button>(win, "AddButton");
            var count = Named<TextBlock>(win, "SelectionCount");
            Assert.Equal("Select all", selectAll.Content);

            vm.ToggleSelectCommand.Execute(vm.Results[0]);
            PumpUntil(() => false, 30);
            Assert.Equal("1 selected", count.Text);
            Assert.True(count.IsVisible);
            Assert.Equal("Add (1)", add.Content);
            Assert.True(add.IsEffectivelyEnabled);

            vm.ToggleSelectAllCommand.Execute(null);
            PumpUntil(() => false, 30);
            Assert.Equal("3 selected", count.Text);
            Assert.Equal("Add (3)", add.Content);
            Assert.Equal("Clear", selectAll.Content);

            vm.ToggleSelectAllCommand.Execute(null);
            PumpUntil(() => false, 30);
            Assert.False(count.IsVisible);
            Assert.Equal("Add", add.Content);
            Assert.False(add.IsEffectivelyEnabled);
            Assert.Equal("Select all", selectAll.Content);
        }
        finally { CloseQuietly(win); }
    }

    [AvaloniaFact]
    public void Selection_SurvivesSearches_AndAddKeepsTickOrder()
    {
        var one = Make("Alpha");
        var two = Make("Bravo");
        var three = Make("Charlie");
        var vm = new AddSongsDialogViewModel(new[] { one, two, three }, Array.Empty<Guid>());
        IReadOnlyList<Track>? chosen = null;
        vm.SongsChosen += (_, t) => chosen = t;

        vm.SearchText = "Charlie";
        Assert.True(PumpUntil(() => vm.Results.Count == 1 && vm.Results[0].Track.Id == three.Id));
        vm.ToggleSelectCommand.Execute(vm.Results[0]);
        vm.SearchText = "Alpha";
        Assert.True(PumpUntil(() => vm.Results.Count == 1 && vm.Results[0].Track.Id == one.Id));
        vm.ToggleSelectCommand.Execute(vm.Results[0]);
        vm.SearchText = "";
        Assert.True(WaitForResults(vm, 3));
        Assert.Equal(2, vm.Results.Count(r => r.IsSelected));
        Assert.Equal("2 selected", vm.SelectionText);

        // Clear on a search only unticks what that search matches.
        vm.SearchText = "Alpha";
        Assert.True(PumpUntil(() => vm.Results.Count == 1 && vm.Results[0].Track.Id == one.Id));
        Assert.Equal("Clear", vm.SelectAllText);
        vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(1, vm.SelectedCount);
        vm.ToggleSelectAllCommand.Execute(null);

        vm.AddCommand.Execute(null);
        Assert.Equal(new[] { three.Id, one.Id }, chosen!.Select(t => t.Id));
    }

    [AvaloniaFact]
    public void Add_AnimatesOut_PickReachesTheCaller_Once()
    {
        EnsureAppStyles();
        var library = new[] { Make("One"), Make("Two") };
        var vm = new AddSongsDialogViewModel(library, Array.Empty<Guid>());
        var (win, chosen) = Open(vm);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        vm.ToggleSelectAllCommand.Execute(null);
        vm.AddCommand.Execute(null);
        Assert.True(host.IsClosing);
        Assert.True(win.IsVisible);
        vm.AddCommand.Execute(null); // a second Add while it closes
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Single(chosen);
        Assert.Equal(2, chosen[0].Count);
    }

    [AvaloniaFact]
    public void AlreadyInPlaylist_CannotBeTicked_NorAdded()
    {
        var keep = Make("Song One");
        var have = Make("Song Two");
        var vm = new AddSongsDialogViewModel(new[] { keep, have }, new[] { have.Id });
        IReadOnlyList<Track>? chosen = null;
        vm.SongsChosen += (_, t) => chosen = t;
        vm.SearchText = "Song";
        Assert.True(WaitForResults(vm, 2));

        var row = vm.Results.Single(r => r.Track.Id == have.Id);
        vm.ToggleSelectCommand.Execute(row);
        Assert.False(row.IsSelected);
        Assert.Equal(0, vm.SelectedCount);

        vm.ToggleSelectAllCommand.Execute(null);
        vm.AddCommand.Execute(null);
        Assert.Equal(new[] { keep.Id }, chosen!.Select(t => t.Id));
    }

    [AvaloniaFact]
    public void TypingWithTheSearchUnfocused_StartsTheSearch()
    {
        EnsureAppStyles();
        var vm = new AddSongsDialogViewModel(new[] { Make("Adele"), Make("Bravo") }, Array.Empty<Guid>());
        var (win, _) = Open(vm);
        try
        {
            PumpUntil(() => false, 300);
            var search = Named<TextBox>(win, "SearchBox");
            Assert.False(search.IsFocused);
            win.KeyTextInput("ad");
            PumpUntil(() => false, 30);
            Assert.True(search.IsFocused);
            Assert.Equal("ad", vm.SearchText);
            Assert.True(WaitForResults(vm, 1));
        }
        finally { CloseQuietly(win); }
    }
}

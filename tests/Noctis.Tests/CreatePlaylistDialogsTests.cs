using System;
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
/// Owner 10-08: Create New Playlist and Create Smart Playlist as the rounded pill pop-up of
/// the Metadata editor (blurred app behind, filled pill fields instead of white outlines,
/// solid accent Create, quiet Cancel), the smart one with less clutter: Match All / Any
/// segments, plain pill rule rows, the limit as one sentence, the live count in the footer.
/// The dialogs are wired exactly as SidebarViewModel wires them (CloseRequested →
/// CloseAnimatedAsync, the result through the view model's event).
/// </summary>
public class CreatePlaylistDialogsTests
{
    private readonly ITestOutputHelper _o;
    public CreatePlaylistDialogsTests(ITestOutputHelper o) => _o = o;

    private static readonly Color PillFill = Color.Parse("#1CFFFFFF");

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

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private static void AssertPillField(TextBox box)
    {
        Assert.Contains("pill-field", box.Classes);
        var chrome = box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
        Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
        if (box.IsFocused)
        {
            // Focused: the darker fill and the accent ring.
            Assert.Equal(Color.Parse("#14FFFFFF"), AccentTestHarness.ColorOf(chrome.Background)); // Dark PillFieldBackgroundFocused
            Assert.True(box.TryFindResource("AccentColorBrush", box.ActualThemeVariant, out var accent));
            Assert.Equal(AccentTestHarness.ColorOf(accent as IBrush), AccentTestHarness.ColorOf(chrome.BorderBrush));
        }
        else
        {
            // At rest: the filled pill, no outline (the ring is transparent).
            Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
            Assert.Equal(0, AccentTestHarness.ColorOf(chrome.BorderBrush).A);
        }
    }

    /// <summary>Real-time pump that yields between frames, so DispatcherTimers fire
    /// (the rule fold's RunOnce).</summary>
    private static async Task<bool> PumpUntilAsync(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(8);
        }
        return condition();
    }

    private static void AssertPillCombo(ComboBox box)
    {
        Assert.Contains("pill-field", box.Classes);
        var chrome = box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
        Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
        Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
        // No white 1.5px outline any more: the ring is transparent at rest.
        Assert.Equal(0, ((ISolidColorBrush)chrome.BorderBrush!).Color.A);
    }

    // ── Create New Playlist ──────────────────────────────────────────────

    private sealed class PlainCaller
    {
        public int Created;
        public string Name = "", Description = "";
    }

    /// <summary>Opens the dialog wired as SidebarViewModel.CreatePlaylistCoreAsync does.</summary>
    private static (CreatePlaylistDialogViewModel vm, CreatePlaylistDialog win, PillDialogHost host, PlainCaller caller) OpenPlain(Window? owner = null)
    {
        var vm = new CreatePlaylistDialogViewModel();
        var win = new CreatePlaylistDialog { DataContext = vm, RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        var caller = new PlainCaller();
        vm.PlaylistCreated += (_, args) => { caller.Created++; caller.Name = args.Name; caller.Description = args.Description; };
        vm.CloseRequested += (_, _) => _ = win.CloseAnimatedAsync();
        if (owner is null) win.Show();
        else _ = win.ShowDialog(owner);
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (vm, win, host, caller);
    }

    [AvaloniaFact]
    public void CreatePlaylist_OpensInPillHost_WithPillFields_AndFocusInName()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host, _) = OpenPlain();
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                var all = win.GetVisualDescendants().ToList();

                var name = all.OfType<TextBox>().Single(b => b.Name == "NameTextBox");
                AssertPillField(name);
                Assert.True(PumpUntil(() => name.IsFocused), "the name field should have the caret on open");
                name.Text = "Road Trip";
                Assert.Equal("Road Trip", vm.PlaylistName);
                var description = all.OfType<TextBox>().Single(b => b.Name == "DescriptionTextBox");
                AssertPillField(description);
                description.Text = "Summer";
                Assert.Equal("Summer", vm.PlaylistDescription);

                var create = all.OfType<Button>().Single(b => b.Command == vm.CreateCommand);
                PillDialogHostTests.AssertSolidAccent(create);
                var cancel = all.OfType<Button>().Single(b => b.Command == vm.CancelCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(cancel.Background));
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void CreatePlaylist_Create_AnimatesOut_ThenResultReachesCaller_Once()
    {
        EnsureAppStyles();
        var (vm, win, host, caller) = OpenPlain();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        vm.PlaylistName = "  Road Trip  ";
        vm.PlaylistDescription = " Summer ";
        vm.CreateCommand.Execute(null);
        // The close animates: still up, card on its way out, result already with the caller.
        Assert.True(host.IsClosing);
        Assert.True(win.IsVisible);
        // A second Create while it closes (Enter in the name reached it through the
        // KeyBinding and fired PlaylistCreated twice) is ignored, and its close rides along.
        vm.CreateCommand.Execute(null);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(1, caller.Created);
        Assert.Equal(("Road Trip", "Summer"), (caller.Name, caller.Description));
    }

    [AvaloniaFact]
    public void CreatePlaylist_EnterInName_Creates_BlankNameShowsErrorAndStaysOpen()
    {
        EnsureAppStyles();
        var (vm, win, host, caller) = OpenPlain();
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var name = win.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "NameTextBox");
            Assert.True(PumpUntil(() => name.IsFocused));

            // Whitespace only: no playlist, the error shows, the dialog stays.
            vm.PlaylistName = "   ";
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            PumpUntil(() => false, 100);
            Assert.True(vm.ShowNameRequiredError);
            Assert.False(host.IsClosing);
            Assert.Equal(0, caller.Created);

            // Typing clears the error; Enter creates.
            vm.PlaylistName = "Gym";
            Assert.False(vm.ShowNameRequiredError);
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(host.IsClosing);
            Assert.Equal(1, caller.Created);
            Assert.Equal("Gym", caller.Name);
            Assert.True(PumpUntil(() => !win.IsVisible, 2000));
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    [AvaloniaFact]
    public void CreatePlaylist_Escape_ClosesAnimated_ExactlyOnce_WithoutResult()
    {
        EnsureAppStyles();
        var (_, win, host, caller) = OpenPlain();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        // A second Esc and an Alt+F4 meanwhile ride along. Raised directly, without the
        // headless key press's job pumping: under real Skia that pumping can outlast the
        // 180 ms close and hit a disposed window.
        if (closed == 0)
        {
            win.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            win.Close();
        }
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(0, caller.Created);
    }

    [AvaloniaFact]
    public void BothViewModels_Create_FiresOnce_EvenIfInvokedAgainWhileClosing()
    {
        EnsureAppStyles();
        var plain = new CreatePlaylistDialogViewModel { PlaylistName = "A" };
        var plainFired = 0;
        plain.PlaylistCreated += (_, _) => plainFired++;
        plain.CreateCommand.Execute(null);
        plain.CreateCommand.Execute(null);
        Assert.Equal(1, plainFired);

        var smart = new CreateSmartPlaylistDialogViewModel(Library()) { PlaylistName = "B" };
        var smartFired = 0;
        smart.SmartPlaylistCreated += (_, _) => smartFired++;
        smart.CreateCommand.Execute(null);
        smart.CreateCommand.Execute(null);
        Assert.Equal(1, smartFired);
    }

    // ── Create Smart Playlist ────────────────────────────────────────────

    private sealed class SmartCaller
    {
        public int Created;
        public Playlist? Playlist;
    }

    private static FakeLibraryService Library(int bunny = 2, int adele = 1)
    {
        var lib = new FakeLibraryService();
        for (var i = 0; i < bunny; i++)
            lib.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = $"Bunny {i}", Artist = "Bad Bunny", Album = "A" });
        for (var i = 0; i < adele; i++)
            lib.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = $"Adele {i}", Artist = "Adele", Album = "25" });
        return lib;
    }

    /// <summary>Opens the dialog wired as SidebarViewModel.CreateSmartPlaylistAsync does.</summary>
    private static (CreateSmartPlaylistDialogViewModel vm, CreateSmartPlaylistDialog win, PillDialogHost host, SmartCaller caller) OpenSmart(
        FakeLibraryService? lib = null, Window? owner = null)
    {
        var vm = new CreateSmartPlaylistDialogViewModel(lib ?? Library());
        var win = new CreateSmartPlaylistDialog { DataContext = vm, RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        var caller = new SmartCaller();
        vm.SmartPlaylistCreated += (_, p) => { caller.Created++; caller.Playlist = p; };
        vm.CloseRequested += (_, _) => _ = win.CloseAnimatedAsync();
        if (owner is null) win.Show();
        else _ = win.ShowDialog(owner);
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (vm, win, host, caller);
    }

    [AvaloniaFact]
    public void SmartPlaylist_OpensInPillHost_WithPillRows_SegmentsAndLimitSentence()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host, _) = OpenSmart();
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                var all = win.GetVisualDescendants().ToList();

                var name = all.OfType<TextBox>().Single(b => b.Name == "NameTextBox");
                AssertPillField(name);
                Assert.True(PumpUntil(() => name.IsFocused), "the name field should have the caret on open");

                // The first rule: field + operator pill drop-downs, a pill value box, a quiet ✕.
                var rules = all.OfType<ItemsControl>().Single(i => i.Name == "RulesList");
                var row = rules.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 7);
                var combos = row.GetVisualDescendants().OfType<ComboBox>().ToList();
                Assert.Equal(2, combos.Count);
                combos.ForEach(AssertPillCombo);
                Assert.Equal(RuleField.Artist, combos[0].SelectedItem);
                var value = row.GetVisualDescendants().OfType<TextBox>().First(t => t.IsVisible);
                AssertPillField(value);
                var remove = row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("sp-remove"));
                Assert.Same(vm.Rules[0], remove.CommandParameter);
                Assert.Same(vm.RemoveRuleCommand, remove.Command);

                // Match All / Any: two segments on one setting.
                var allSeg = all.OfType<RadioButton>().Single(r => r.Name == "MatchAllSegment");
                var anySeg = all.OfType<RadioButton>().Single(r => r.Name == "MatchAnySegment");
                Assert.True(allSeg.IsChecked);
                Assert.False(anySeg.IsChecked);
                anySeg.IsChecked = true;
                PumpUntil(() => false, 30);
                Assert.False(vm.MatchAll);
                Assert.False(allSeg.IsChecked);
                vm.MatchAll = true;
                PumpUntil(() => false, 30);
                Assert.True(allSeg.IsChecked);
                Assert.False(anySeg.IsChecked);

                // Limit off: number and sort greyed via the pill classes, words dimmed too.
                var limit = all.OfType<NumericUpDown>().Single(n => n.Name == "LimitBox");
                var sort = all.OfType<ComboBox>().Single(c => c.Name == "SortBox");
                AssertPillCombo(sort);
                Assert.False(limit.IsEnabled);
                Assert.False(sort.IsEnabled);
                Assert.Equal(0.4, limit.Opacity, 3);
                Assert.Equal(0.4, sort.Opacity, 3);
                var spinner = limit.GetVisualDescendants().OfType<ButtonSpinner>().Single();
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(spinner.Background));
                vm.HasLimit = true;
                PumpUntil(() => false, 30);
                Assert.True(limit.IsEnabled && sort.IsEnabled);
                Assert.Equal(1.0, limit.Opacity, 3);

                // Footer: the live count, Cancel quiet, Create solid accent.
                Assert.Equal("Matches 3 tracks", all.OfType<TextBlock>().Single(t => t.Name == "MatchCount").Text);
                var create = all.OfType<Button>().Single(b => b.Command == vm.CreateCommand);
                PillDialogHostTests.AssertSolidAccent(create);
                var cancel = all.OfType<Button>().Single(b => b.Command == vm.CancelCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void SmartPlaylist_Count_RefreshesLive_Localized_WithSingularAndThousands()
    {
        EnsureAppStyles();
        var lib = Library(bunny: 1234, adele: 1);
        var vm = new CreateSmartPlaylistDialogViewModel(lib);
        Assert.Equal("Matches 1,235 tracks", vm.MatchCountText);

        var changes = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.MatchCountText)) changes++; };
        vm.Rules[0].Value = "adele";
        Assert.Equal(1, vm.MatchingTrackCount);
        Assert.Equal("Matches 1 track", vm.MatchCountText);
        Assert.True(changes > 0);

        // Any of two rules: both artists.
        vm.AddRuleCommand.Execute(null);
        vm.Rules[1].Value = "bunny";
        Assert.Equal(0, vm.MatchingTrackCount); // All: nobody is both
        vm.MatchAny = true;
        Assert.Equal(1235, vm.MatchingTrackCount);

        // The limit caps the count (as the saved playlist does).
        vm.HasLimit = true;
        vm.LimitCount = 25;
        Assert.Equal("Matches 25 tracks", vm.MatchCountText);
    }

    [AvaloniaFact]
    public async Task SmartPlaylist_RemovedRule_LeavesCountAndCreate_AtOnce_EvenWhileFolding()
    {
        EnsureAppStyles();
        var vm = new CreateSmartPlaylistDialogViewModel(Library());
        Playlist? created = null;
        vm.SmartPlaylistCreated += (_, p) => created = p;

        vm.Rules[0].Value = "adele";
        vm.AddRuleCommand.Execute(null);
        var second = vm.Rules[1];
        second.Value = "bunny";
        vm.MatchAll = false;
        Assert.Equal(3, vm.MatchingTrackCount);

        // ✕ on the Bunny rule: out of the count straight away while its row folds.
        vm.RemoveRuleCommand.Execute(second);
        Assert.Equal(2, vm.Rules.Count); // still folding
        Assert.Equal(1, vm.MatchingTrackCount);
        // Anything else re-counting during the fold used to bring it back (3).
        vm.Rules[0].Value = "ade";
        Assert.Equal(1, vm.MatchingTrackCount);
        // A second ✕ on the folding row does nothing.
        vm.RemoveRuleCommand.Execute(second);

        // Create during the fold used to save the removed rule too.
        vm.PlaylistName = "Adele only";
        vm.CreateCommand.Execute(null);
        Assert.NotNull(created);
        Assert.Single(created!.Rules);
        Assert.Equal("ade", created.Rules[0].Value);

        Assert.True(await PumpUntilAsync(() => vm.Rules.Count == 1, 2000), "the folded row never left the list");
        Assert.Equal(1, vm.MatchingTrackCount);
    }

    [AvaloniaFact]
    public void SmartPlaylist_NoRulesLeft_DisablesCreate_AndSaysSo()
    {
        EnsureAppStyles();
        var (vm, win, host, caller) = OpenSmart();
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var create = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.CreateCommand);
            Assert.True(create.IsEnabled);

            vm.PlaylistName = "Empty";
            vm.RemoveRuleCommand.Execute(vm.Rules[0]);
            PumpUntil(() => false, 30);
            Assert.False(vm.HasRules);
            Assert.False(create.IsEnabled);
            Assert.Equal("Add a rule to start", vm.MatchCountText);
            vm.CreateCommand.Execute(null);
            Assert.Equal(0, caller.Created);
            Assert.False(host.IsClosing);

            vm.AddRuleCommand.Execute(null);
            PumpUntil(() => false, 30);
            Assert.True(vm.HasRules);
            Assert.True(create.IsEnabled);
            Assert.Equal("Matches 3 tracks", vm.MatchCountText);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void SmartPlaylist_AddDescription_RevealsTheField_AndItIsSaved()
    {
        EnsureAppStyles();
        var (vm, win, host, caller) = OpenSmart();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        var reveal = win.FindControl<CollapsibleContent>("DescriptionReveal")!;
        var link = win.FindControl<Button>("AddDescriptionButton")!;
        Assert.False(reveal.IsOpen);
        Assert.True(link.IsVisible);
        link.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(reveal.IsOpen);
        Assert.False(link.IsVisible);
        var description = win.FindControl<TextBox>("DescriptionTextBox")!;
        Assert.True(PumpUntil(() => description.IsFocused));
        AssertPillField(description);
        description.Text = "  late nights ";

        vm.PlaylistName = "Night";
        vm.Rules[0].Value = "bunny";
        vm.HasLimit = true;
        vm.LimitCount = 10;
        vm.SortBy = SmartPlaylistSortBy.MostPlayed;
        vm.CreateCommand.Execute(null);
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(1, caller.Created);
        var p = caller.Playlist!;
        Assert.Equal(("Night", "late nights"), (p.Name, p.Description));
        Assert.True(p.IsSmartPlaylist);
        Assert.True(p.MatchAll);
        Assert.Equal(10, p.LimitCount);
        Assert.Equal(SmartPlaylistSortBy.MostPlayed, p.SortBy);
        Assert.Equal(RuleField.Artist, p.Rules.Single().Field);
    }

    [AvaloniaFact]
    public void SmartPlaylist_Escape_ClosesAnimated_ExactlyOnce_WithoutResult()
    {
        EnsureAppStyles();
        var (_, win, host, caller) = OpenSmart();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        if (closed == 0) win.Close();
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(0, caller.Created);
    }

    [AvaloniaFact]
    public void SmartPlaylist_BlankName_ShowsError_StaysOpen()
    {
        EnsureAppStyles();
        var (vm, win, host, caller) = OpenSmart();
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            vm.PlaylistName = "  ";
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            PumpUntil(() => false, 50);
            Assert.True(vm.ShowNameRequiredError);
            Assert.False(host.IsClosing);
            Assert.Equal(0, caller.Created);
            vm.PlaylistName = "x";
            Assert.False(vm.ShowNameRequiredError);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    /// <summary>Real Skia only: both dialogs over a striped owner — the blurred backdrop is
    /// captured — saved as PNGs for a visual check (never sent anywhere).</summary>
    [AvaloniaFact]
    public void Probe_BothDialogs_BlurredBackdrop_Renders()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var dir = Path.Combine(Path.GetTempPath(), "noctis-create-playlist-shots");
        Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var colors = new[] { "#E74856", "#2D7DD2", "#3BB273", "#F2A33A", "#8E44AD", "#16A085" };
            var stripes = new StackPanel();
            for (var i = 0; i < 18; i++)
                stripes.Children.Add(new Border { Height = 50, Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
            var owner = new Window { Width = 1100, Height = 820, Content = stripes, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            PumpUntil(() => false, 100);
            try
            {
                var (_, plain, plainHost, _) = OpenPlain(owner);
                Assert.True(PumpUntil(() => CardSettledOpen(plainHost) && plainHost.BackdropBitmap != null, 3000));
                PumpUntil(() => false, 150);
                plain.CaptureRenderedFrame()!.Save(Path.Combine(dir, "create-playlist.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                plain.Close();
                PumpUntil(() => !plain.IsVisible);

                var (vm, smart, smartHost, _) = OpenSmart(Library(bunny: 4352, adele: 1), owner);
                Assert.True(PumpUntil(() => CardSettledOpen(smartHost) && smartHost.BackdropBitmap != null, 3000));
                PumpUntil(() => false, 150);
                smart.CaptureRenderedFrame()!.Save(Path.Combine(dir, "smart-01-start.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                vm.AddRuleCommand.Execute(null);
                vm.Rules[1].SelectedField = RuleField.Year;
                vm.Rules[1].SelectedOperator = RuleOperator.Between;
                vm.HasLimit = true;
                smart.FindControl<Button>("AddDescriptionButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => false, 500);
                smart.CaptureRenderedFrame()!.Save(Path.Combine(dir, "smart-02-filled.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                smart.Close();
                PumpUntil(() => !smart.IsVisible);
                _o.WriteLine(dir);
            }
            finally { owner.Close(); }
        });
    }
}

using System.Diagnostics;
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
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: the Songs View Options sheet as the rounded pill pop-up of the other dialogs
/// (blurred app behind, shared open/close animation): Sort by as a filled pill drop-down with
/// the direction as a segmented pill beside it, Show as a segmented pill, the columns in three
/// filled wells, Restore Defaults quiet and Done solid accent. Everything still applies live,
/// and Done, Escape and a click on the backdrop all just close.
/// </summary>
public class SongsViewOptionsDialogTests
{
    private static readonly Color PillFill = Color.Parse("#1CFFFFFF"); // Dark PillFieldBackground

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

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

    internal static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    /// <summary>The Songs and Settings view models as MainWindowViewModel wires them.</summary>
    private static (LibrarySongsViewModel songs, SettingsViewModel settings) Shell(IPersistenceService? persistence = null)
    {
        var lib = new FakeLibraryService();
        var testPersistence = new TestPersistenceService();
        persistence ??= testPersistence;
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, testPersistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(testPersistence, lib);
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        var songs = new LibrarySongsViewModel(lib, player, sidebar, testPersistence, settings);
        return (songs, settings);
    }

    internal static (SongsViewOptionsViewModel vm, SongsViewOptionsDialog win, PillDialogHost host, LibrarySongsViewModel songs, SettingsViewModel settings)
        Open(double width = 1100, double height = 820, Window? owner = null)
    {
        var (songs, settings) = Shell();
        var vm = new SongsViewOptionsViewModel(songs, settings);
        var win = new SongsViewOptionsDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = width, Height = height };
        if (owner is null) win.Show();
        else _ = win.ShowDialog(owner);
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (vm, win, host, songs, settings);
    }

    internal static void CloseAndWait(Window win)
    {
        if (!win.IsVisible) return;
        win.Close();
        PumpUntil(() => !win.IsVisible);
    }

    internal static T Named<T>(Window win, string name) where T : Control =>
        win.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    // ── Look ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Opens_InThePillHost_WithPillCombo_Segments_ColumnWells_AndPillFooter()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host, _, _) = Open();
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                Assert.True(host.BlurBackdrop);

                // Sort by: a filled pill drop-down, no white outline any more.
                var combo = Named<ComboBox>(win, "SortCombo");
                Assert.Contains("pill-field", combo.Classes);
                var chrome = combo.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
                Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                Assert.Equal(0, ((ISolidColorBrush)chrome.BorderBrush!).Color.A);
                Assert.Same(vm.SelectedSortOption, combo.SelectedItem);
                Assert.Equal("Date Added", ((SongSortOption)combo.SelectedItem!).Key);

                // Direction and Show: segmented pills (one style), each pair on a pill-well track,
                // the direction as tall as the drop-down beside it.
                foreach (var name in new[] { "AscendingSegment", "DescendingSegment", "AllSongsSegment", "OnlyFavoritesSegment" })
                {
                    var seg = Named<RadioButton>(win, name);
                    Assert.Contains("vo-seg", seg.Classes);
                    var track = seg.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("pill-well"));
                    Assert.Equal(new CornerRadius(999), track.CornerRadius);
                }
                var directionTrack = Named<RadioButton>(win, "AscendingSegment").GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("pill-well"));
                Assert.Equal(combo.Bounds.Height, directionTrack.Bounds.Height, 0);
                Assert.False(Named<RadioButton>(win, "AscendingSegment").IsChecked);
                Assert.True(Named<RadioButton>(win, "DescendingSegment").IsChecked);
                Assert.True(Named<RadioButton>(win, "AllSongsSegment").IsChecked);
                Assert.False(Named<RadioButton>(win, "OnlyFavoritesSegment").IsChecked);

                // Columns: the pill pop-ups' round checkbox, 5 + 3 + 3 in three filled wells.
                var wells = Named<Grid>(win, "ColumnWells").Children.OfType<Border>().ToList();
                Assert.Equal(3, wells.Count);
                Assert.All(wells, w => Assert.Contains("pill-well", w.Classes));
                Assert.Equal(new[] { 5, 3, 3 }, wells.Select(w => w.GetVisualDescendants().OfType<CheckBox>().Count()).ToArray());
                Assert.All(wells.SelectMany(w => w.GetVisualDescendants().OfType<CheckBox>()),
                    c => Assert.Contains("metadata-pill-checkbox", c.Classes));
                // The Artwork toggle is the one bound to the artwork thumbnails.
                Assert.Equal(vm.Settings.ShowArtworkColumn, Named<CheckBox>(win, "ArtworkColumnToggle").IsChecked);
                // The wells share one height, so the set reads as a row.
                Assert.Single(wells.Select(w => Math.Round(w.Bounds.Height)).Distinct());

                // Footer: Restore Defaults quiet, Done solid accent; no header ✕ (no pill dialog has one).
                var restore = Named<Button>(win, "RestoreDefaultsButton");
                Assert.Contains("pill-secondary", restore.Classes);
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(restore.Background));
                Assert.Same(vm.RestoreDefaultsCommand, restore.Command);
                PillDialogHostTests.AssertSolidAccent(Named<Button>(win, "DoneButton"));
                Assert.Equal(2, win.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("pill-primary") || b.Classes.Contains("pill-secondary")));
                Assert.DoesNotContain(win.GetVisualDescendants().OfType<PathIcon>(),
                    p => ReferenceEquals(p.Data, win.FindResource("DismissIcon")));
            }
            finally { CloseAndWait(win); }
        });
    }

    /// <summary>The owner's sheet had a scrollbar. At a normal window size the whole card fits;
    /// only a small window scrolls the body (and the card never outgrows the window).</summary>
    [AvaloniaTheory]
    [InlineData(1100, 820, false)]
    [InlineData(1280, 720, false)]
    [InlineData(1000, 460, true)]
    public void Card_FitsWithoutScrolling_AtNormalSizes_ScrollsOnlyWhenSmall(double width, double height, bool scrolls)
    {
        EnsureAppStyles();
        var (_, win, host, _, _) = Open(width, height);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var scroll = Named<ScrollViewer>(win, "BodyScroll");
            Assert.True(PumpUntil(() => scroll.Viewport.Height > 0));
            Assert.Equal(scrolls, scroll.Extent.Height > scroll.Viewport.Height + 0.5);
            Assert.True(Named<Grid>(win, "OptionsCard").Bounds.Height <= height + 0.5);
        }
        finally { CloseAndWait(win); }
    }

    // ── Behaviour: live apply ────────────────────────────────────────────

    [AvaloniaFact]
    public void EveryControl_AppliesLive_AndFollowsOutsideChanges()
    {
        EnsureAppStyles();
        var (vm, win, host, songs, settings) = Open();
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var combo = Named<ComboBox>(win, "SortCombo");
            var asc = Named<RadioButton>(win, "AscendingSegment");
            var desc = Named<RadioButton>(win, "DescendingSegment");
            var all = Named<RadioButton>(win, "AllSongsSegment");
            var favs = Named<RadioButton>(win, "OnlyFavoritesSegment");
            var artwork = Named<CheckBox>(win, "ArtworkColumnToggle");

            combo.SelectedItem = vm.SortOptions.Single(o => o.Key == "Album Artist");
            Assert.Equal("Album Artist", songs.SortColumn);
            Assert.Equal("Album Artist", settings.SongsSortColumn); // persisted state follows

            asc.IsChecked = true;
            PumpUntil(() => false, 20);
            Assert.True(songs.SortAscending);
            Assert.False(desc.IsChecked);
            desc.IsChecked = true;
            PumpUntil(() => false, 20);
            Assert.False(songs.SortAscending);
            Assert.False(asc.IsChecked);

            favs.IsChecked = true;
            PumpUntil(() => false, 20);
            Assert.True(songs.ShowOnlyFavorites);
            Assert.False(all.IsChecked);

            Assert.True(artwork.IsChecked);
            artwork.IsChecked = false;
            Assert.False(settings.ShowArtworkColumn);

            // Changes from elsewhere (the top bar's Sort menu) show in the open sheet.
            songs.SelectSortCommand.Execute("Year");
            songs.SelectSortCommand.Execute("Ascending");
            songs.SetShowAllItemsCommand.Execute(null);
            PumpUntil(() => false, 20);
            Assert.Equal("Year", ((SongSortOption)combo.SelectedItem!).Key);
            Assert.True(asc.IsChecked);
            Assert.False(desc.IsChecked);
            Assert.True(all.IsChecked);
            Assert.False(favs.IsChecked);
        }
        finally { CloseAndWait(win); }
    }

    /// <summary>Restore Defaults returns every option to what a fresh install has
    /// (AppSettings' initializers), on screen too.</summary>
    [AvaloniaFact]
    public void RestoreDefaults_IsExactlyAFreshInstall_AndTheSheetShowsIt()
    {
        EnsureAppStyles();
        var (vm, win, host, songs, settings) = Open();
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            settings.ShowArtworkColumn = settings.ShowArtistColumn = settings.ShowAlbumColumn = false;
            settings.ShowGenreColumn = settings.ShowTimeColumn = settings.ShowFavoritesColumn = false;
            settings.ShowRatingColumn = settings.ShowPlaysColumn = false;
            settings.ShowBpmColumn = settings.ShowBitrateColumn = settings.ShowSampleRateColumn = true;
            songs.SelectSortCommand.Execute("Bitrate");
            songs.SelectSortCommand.Execute("Ascending");
            songs.SetShowOnlyFavoritesCommand.Execute(null);

            Named<Button>(win, "RestoreDefaultsButton").Command!.Execute(null);
            PumpUntil(() => false, 20);

            var d = new AppSettings();
            Assert.Equal(
                (d.ShowArtworkColumn, d.ShowArtistColumn, d.ShowAlbumColumn, d.ShowGenreColumn, d.ShowTimeColumn,
                 d.ShowFavoritesColumn, d.ShowRatingColumn, d.ShowPlaysColumn, d.ShowBpmColumn, d.ShowBitrateColumn, d.ShowSampleRateColumn),
                (settings.ShowArtworkColumn, settings.ShowArtistColumn, settings.ShowAlbumColumn, settings.ShowGenreColumn, settings.ShowTimeColumn,
                 settings.ShowFavoritesColumn, settings.ShowRatingColumn, settings.ShowPlaysColumn, settings.ShowBpmColumn, settings.ShowBitrateColumn, settings.ShowSampleRateColumn));
            Assert.Equal((d.SongsSortColumn, d.SongsSortAscending, d.SongsShowOnlyFavorites),
                (songs.SortColumn, songs.SortAscending, songs.ShowOnlyFavorites));
            Assert.Equal((d.SongsSortColumn, d.SongsSortAscending, d.SongsShowOnlyFavorites),
                (settings.SongsSortColumn, settings.SongsSortAscending, settings.SongsShowOnlyFavorites));

            Assert.Equal(d.SongsSortColumn, ((SongSortOption)Named<ComboBox>(win, "SortCombo").SelectedItem!).Key);
            Assert.Equal(d.SongsSortAscending, Named<RadioButton>(win, "AscendingSegment").IsChecked);
            Assert.Equal(!d.SongsShowOnlyFavorites, Named<RadioButton>(win, "AllSongsSegment").IsChecked);
            Assert.Equal(d.ShowArtworkColumn, Named<CheckBox>(win, "ArtworkColumnToggle").IsChecked);
            Assert.Same(vm.SelectedSortOption, Named<ComboBox>(win, "SortCombo").SelectedItem);
        }
        finally { CloseAndWait(win); }
    }

    /// <summary>What the sheet sets survives a restart: column flags and sort/filter are saved
    /// through SettingsViewModel and read back by a fresh one.</summary>
    [AvaloniaFact]
    public async Task Changes_Persist_AcrossARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var persistence = new PersistenceService(root);
            var (songs, settings) = Shell(persistence);
            await settings.LoadAsync();
            using (var vm = new SongsViewOptionsViewModel(songs, settings))
            {
                vm.SelectedSortOption = vm.SortOptions.Single(o => o.Key == "Rating");
                vm.SetAscendingCommand.Execute(null);
                vm.SetOnlyFavoritesCommand.Execute(null);
                settings.ShowArtworkColumn = false;
                settings.ShowBpmColumn = true;
            }
            await settings.SaveAsync();

            var (songs2, reloaded) = Shell(new PersistenceService(root));
            await reloaded.LoadAsync();
            Assert.False(reloaded.ShowArtworkColumn);
            Assert.True(reloaded.ShowBpmColumn);
            Assert.Equal(("Rating", true, true), (reloaded.SongsSortColumn, reloaded.SongsSortAscending, reloaded.SongsShowOnlyFavorites));
            // The Songs list adopts it when the settings land.
            Assert.Equal(("Rating", true, true), (songs2.SortColumn, songs2.SortAscending, songs2.ShowOnlyFavorites));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>The direction pills mean something for every field the list offers: each
    /// key has its own arm in BuildFilteredAndSortedTracks and Descending reverses it (none
    /// falls through to the direction-blind title default).</summary>
    [AvaloniaFact]
    public void EverySortField_HonoursTheDirection()
    {
        var (songs, settings) = Shell();
        using var vm = new SongsViewOptionsViewModel(songs, settings);
        var low = new Track
        {
            Id = Guid.NewGuid(), Title = "B low", Artist = "A", AlbumArtist = "A", Album = "A", Genre = "A",
            Duration = TimeSpan.FromMinutes(1), PlayCount = 1, IsFavorite = false, Rating = 1, Year = 1990, Bpm = 90,
            Bitrate = 128, SampleRate = 44100, DateAdded = new DateTime(2020, 1, 1), LastModified = new DateTime(2020, 1, 1),
        };
        var high = new Track
        {
            Id = Guid.NewGuid(), Title = "A high", Artist = "Z", AlbumArtist = "Z", Album = "Z", Genre = "Z",
            Duration = TimeSpan.FromMinutes(9), PlayCount = 9, IsFavorite = true, Rating = 5, Year = 2020, Bpm = 180,
            Bitrate = 1411, SampleRate = 96000, DateAdded = new DateTime(2024, 1, 1), LastModified = new DateTime(2024, 1, 1),
        };
        var tracks = new List<Track> { low, high };
        Assert.Equal(15, vm.SortOptions.Count);
        foreach (var option in vm.SortOptions)
        {
            var ascFirst = LibrarySongsViewModel.BuildFilteredAndSortedTracks(tracks, "", option.Key, true, false, "All")[0];
            var descFirst = LibrarySongsViewModel.BuildFilteredAndSortedTracks(tracks, "", option.Key, false, false, "All")[0];
            Assert.True(!ReferenceEquals(ascFirst, descFirst), $"'{option.Key}' ignores the direction");
        }
    }

    // ── Closing ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Escape_ClosesAnimated_ExactlyOnce()
    {
        EnsureAppStyles();
        var (_, win, host, _, _) = Open();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        if (closed == 0)
        {
            // A second Esc and an Alt+F4 meanwhile ride along.
            win.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            win.Close();
        }
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void Done_ClosesAnimated_KeepingWhatWasSet()
    {
        EnsureAppStyles();
        var (_, win, host, songs, settings) = Open();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        Named<CheckBox>(win, "ArtworkColumnToggle").IsChecked = false;
        Named<RadioButton>(win, "AscendingSegment").IsChecked = true;
        Named<Button>(win, "DoneButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(host.IsClosing);
        Assert.True(win.IsVisible); // still animating out
        Assert.True(PumpUntil(() => closed > 0, 2000));
        Assert.False(settings.ShowArtworkColumn);
        Assert.True(songs.SortAscending);
    }

    [AvaloniaFact]
    public void BackdropClick_Closes_ButAClickOnTheCardDoesNot()
    {
        EnsureAppStyles();
        var (_, win, host, _, _) = Open();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var card = Named<Grid>(win, "OptionsCard");

            // On the card (its header): stays open.
            var onCard = card.TranslatePoint(new Point(card.Bounds.Width / 2, 12), win)!.Value;
            win.MouseDown(onCard, MouseButton.Left);
            win.MouseUp(onCard, MouseButton.Left);
            PumpUntil(() => false, 30);
            Assert.False(host.IsClosing);

            // A right click on the backdrop does nothing either.
            win.MouseDown(new Point(12, 12), MouseButton.Right);
            win.MouseUp(new Point(12, 12), MouseButton.Right);
            PumpUntil(() => false, 30);
            Assert.False(host.IsClosing);

            win.MouseDown(new Point(12, 12), MouseButton.Left);
            Assert.True(host.IsClosing);
            Assert.True(PumpUntil(() => closed > 0, 2000));
            Assert.Equal(1, closed);
        }
        finally { CloseAndWait(win); }
    }

    /// <summary>With the Sort by list open, a click outside only closes the list (its own
    /// light-dismiss takes the press), not the whole sheet.</summary>
    [AvaloniaFact]
    public void BackdropClick_WithTheSortListOpen_OnlyClosesTheList()
    {
        EnsureAppStyles();
        var (_, win, host, _, _) = Open();
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var combo = Named<ComboBox>(win, "SortCombo");
            combo.IsDropDownOpen = true;
            Assert.True(PumpUntil(() => combo.IsDropDownOpen));
            PumpUntil(() => false, 50);

            win.MouseDown(new Point(12, 12), MouseButton.Left);
            win.MouseUp(new Point(12, 12), MouseButton.Left);
            // Wait for the close rather than a fixed pump: once ComboBoxDropDownAnimator is
            // installed (any earlier test that starts the App), the list stays open through
            // its 220 ms fade, so a 50 ms pump only passed when this test ran alone.
            Assert.True(PumpUntil(() => !combo.IsDropDownOpen, 2000), "the Sort list stayed open");
            Assert.False(host.IsClosing);
        }
        finally { CloseAndWait(win); }
    }

    /// <summary>Real Skia only: the sheet over a striped owner, blurred backdrop captured,
    /// saved as a PNG for a visual check (never sent anywhere).</summary>
    [AvaloniaFact]
    public void Probe_Sheet_BlurredBackdrop_Renders()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var dir = Path.Combine(Path.GetTempPath(), "noctis-view-options-shots");
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
                var (_, win, host, _, _) = Open(owner: owner);
                Assert.True(PumpUntil(() => CardSettledOpen(host) && host.BackdropBitmap != null, 3000));
                PumpUntil(() => false, 150);
                win.CaptureRenderedFrame()!.Save(Path.Combine(dir, "view-options.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                CloseAndWait(win);
            }
            finally { owner.Close(); }
        });
    }
}

/// <summary>Sort-field names follow the UI language, word for word with the top bar's Sort
/// menu (the same Main.* keys).</summary>
[Collection("Localization")]
public class SongsViewOptionsLocalizationTests : IDisposable
{
    public SongsViewOptionsLocalizationTests() => Loc.Instance.SetCulture("en");
    public void Dispose() => Loc.Instance.SetCulture("en");

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [AvaloniaFact]
    public void SortFieldNames_FollowTheUiLanguage_LikeTheTopBarSortMenu()
    {
        Loc.Instance.SetCulture("tr");
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        var songs = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence, settings);
        using var vm = new SongsViewOptionsViewModel(songs, settings);

        var menuKeys = new Dictionary<string, string>
        {
            ["Title"] = "Main.Title", ["Artist"] = "Main.Artist", ["Album"] = "Main.Album",
            ["Album Artist"] = "Main.AlbumByArtist", ["Genre"] = "Main.Genre", ["Time"] = "Main.Time",
            ["Plays"] = "Main.Plays", ["IsFavorite"] = "Main.Favorite", ["Rating"] = "Main.Rating",
            ["Year"] = "Main.Year", ["Bpm"] = "Main.BPM", ["Bitrate"] = "Main.Bitrate",
            ["SampleRate"] = "Main.SampleRate", ["Date Added"] = "Main.DateAdded", ["Date Modified"] = "Main.DateModified",
        };
        Assert.Equal(menuKeys.Keys.ToHashSet(), vm.SortOptions.Select(o => o.Key).ToHashSet());
        foreach (var option in vm.SortOptions)
            Assert.Equal(Loc.T(menuKeys[option.Key]), option.Label);
        // Turkish, not the English literal the list used to carry.
        Assert.NotEqual("Date Added", vm.SortOptions.Single(o => o.Key == "Date Added").Label);
    }

    /// <summary>The longest shipped column label (French "Fréquence d'échantillonnage") wraps
    /// inside its well rather than running into the next one.</summary>
    [AvaloniaFact]
    public void LongColumnLabels_WrapInsideTheirWell()
    {
        Loc.Instance.SetCulture("fr");
        SongsViewOptionsDialogTests.EnsureAppStyles();
        var (_, win, host, _, _) = SongsViewOptionsDialogTests.Open();
        try
        {
            Assert.True(SongsViewOptionsDialogTests.PumpUntil(() => SongsViewOptionsDialogTests.CardSettledOpen(host)));
            var wells = SongsViewOptionsDialogTests.Named<Grid>(win, "ColumnWells").Children.OfType<Border>().ToList();
            foreach (var well in wells)
            {
                var inner = well.Bounds.Width - well.Padding.Left - well.Padding.Right;
                foreach (var box in well.GetVisualDescendants().OfType<CheckBox>())
                {
                    var presenter = box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(p => p.Content is string);
                    Assert.Equal(TextWrapping.Wrap, presenter.TextWrapping);
                    Assert.True(box.Bounds.Width <= inner + 0.5, $"'{box.Content}' is {box.Bounds.Width} wide in a {inner} well");
                    // DesiredSize carries the template's 10px label gap (Margin); Bounds doesn't.
                    var wanted = presenter.DesiredSize.Width - presenter.Margin.Left - presenter.Margin.Right;
                    Assert.True(wanted <= presenter.Bounds.Width + 0.5, $"'{box.Content}' wants {wanted} in a {presenter.Bounds.Width} slot");
                }
            }
            var sampleRate = wells[2].GetVisualDescendants().OfType<CheckBox>().Last();
            Assert.Equal(Loc.T("SongsViewOptions.SampleRate"), sampleRate.Content);
            Assert.NotEqual("Sample Rate", sampleRate.Content);
        }
        finally { SongsViewOptionsDialogTests.CloseAndWait(win); }
    }
}

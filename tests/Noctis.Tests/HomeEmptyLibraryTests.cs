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
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 10-17: a brand-new profile opened on a completely blank Home (just the header). Home now
/// shows an empty-library state with Settings' own Add Folder command, a "Scanning your
/// music…" state while the scan after it has found nothing yet, and neither once songs exist.
/// </summary>
public class HomeEmptyLibraryTests
{
    private readonly ITestOutputHelper _o;
    public HomeEmptyLibraryTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    private static Track T(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Artist", AlbumArtist = "Artist", Album = "Album",
        AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(180),
    };

    private static (HomeViewModel Home, SettingsViewModel Settings, FakeLibraryService Library) Build()
    {
        var (home, settings, lib, _) = BuildWithPlayer();
        return (home, settings, lib);
    }

    private static (HomeViewModel Home, SettingsViewModel Settings, FakeLibraryService Library, PlayerViewModel Player) BuildWithPlayer()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new FakeHistoryLog());
        var home = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib), settings: settings);
        return (home, settings, lib, player);
    }

    private static (HomeView View, Window Window) Mount(HomeViewModel vm)
    {
        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1200, Height = 800, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, win);
    }

    [AvaloniaFact]
    public async Task EmptyLibrary_NotScanning_ShowsEmptyStateWithAddFolder()
    {
        EnsureAppStyles();
        var (home, _, _) = Build();
        await home.RefreshAsync();
        var (view, _) = Mount(home);

        Assert.True(home.ShowEmptyLibrary);
        Assert.False(home.ShowLibraryScanning);
        Assert.True(view.FindControl<StackPanel>("EmptyLibraryState")!.IsVisible);
        Assert.False(view.FindControl<StackPanel>("LibraryScanningState")!.IsVisible);
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Your library is empty", texts);
        Assert.Contains("Add Music Folder", texts);
        Assert.Contains("Open Settings", texts);
    }

    // Home is the startup page and mounts before library.json is read: nothing may claim the
    // library is empty until Home has built once (after the load).
    [AvaloniaFact]
    public void EmptyState_WaitsForTheFirstBuild()
    {
        EnsureAppStyles();
        var (home, _, _) = Build();
        var (view, _) = Mount(home);

        Assert.False(home.ShowEmptyLibrary);
        Assert.False(view.FindControl<StackPanel>("EmptyLibraryState")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task LibraryWithSongs_HidesEmptyState_AndTheFirstPublishHidesIt()
    {
        EnsureAppStyles();
        var (home, _, lib) = Build();
        lib.TrackList.Add(T("One"));
        await home.RefreshAsync();
        Assert.False(home.ShowEmptyLibrary);
        Assert.False(home.ShowLibraryScanning);

        // Empty, then the scan's first publish: gone without waiting for a Home rebuild.
        var (home2, _, lib2) = Build();
        await home2.RefreshAsync();
        var (view, _) = Mount(home2);
        Assert.True(home2.ShowEmptyLibrary);
        lib2.TrackList.Add(T("Two"));
        lib2.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();
        Assert.False(home2.ShowEmptyLibrary);
        Assert.False(view.FindControl<StackPanel>("EmptyLibraryState")!.IsVisible);
    }

    // Add Folder lands a new user on a library with songs and no plays. Every Home row is
    // play-driven, so Home was blank there too (headless probe 10-17: 30 songs, no plays,
    // no visible text). It now says Home fills in as you listen and offers Shuffle All.
    [AvaloniaFact]
    public async Task SongsButNothingPlayed_ShowsStartListening_AndShuffleAllPlays()
    {
        EnsureAppStyles();
        var (home, _, lib, player) = BuildWithPlayer();
        for (var i = 0; i < 5; i++) lib.TrackList.Add(T("Song " + i));
        await home.RefreshAsync();
        var (view, _) = Mount(home);

        Assert.False(home.ShowEmptyLibrary);
        Assert.True(home.ShowNothingPlayed);
        Assert.True(view.FindControl<StackPanel>("NothingPlayedState")!.IsVisible);
        var button = view.FindControl<Button>("NothingPlayedShuffleButton")!;
        Assert.Same(home.ShuffleLibraryCommand, button.Command);

        button.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(player.CurrentTrack);
        Assert.Contains(player.CurrentTrack!, lib.TrackList);
    }

    [AvaloniaFact]
    public async Task SongsWithPlays_HideStartListening()
    {
        EnsureAppStyles();
        var (home, _, lib) = Build();
        var played = T("Played");
        played.PlayCount = 3;
        lib.TrackList.Add(played);
        lib.TrackList.Add(T("Unplayed"));
        await home.RefreshAsync();

        Assert.NotEmpty(home.TopSongRows);
        Assert.False(home.ShowNothingPlayed);
        Assert.False(home.ShowEmptyLibrary);
    }

    // The first Home build of an existing library must not flash "Start listening" while its
    // rows are still being ranked off the UI thread.
    [AvaloniaFact]
    public void StartListening_WaitsForTheFirstFullBuild()
    {
        EnsureAppStyles();
        var (home, _, lib) = Build();
        lib.TrackList.Add(T("One"));
        lib.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();
        Assert.False(home.ShowNothingPlayed);
    }

    [AvaloniaFact]
    public async Task ScanRunning_ShowsScanningState_InsteadOfEmpty()
    {
        EnsureAppStyles();
        var (home, settings, lib) = Build();
        await home.RefreshAsync();
        var (view, _) = Mount(home);

        settings.IsScanning = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(home.ShowEmptyLibrary);
        Assert.True(home.ShowLibraryScanning);
        Assert.True(view.FindControl<StackPanel>("LibraryScanningState")!.IsVisible);
        Assert.False(view.FindControl<StackPanel>("EmptyLibraryState")!.IsVisible);

        // A scan that found nothing ends back on the empty state (Add Folder again).
        settings.IsScanning = false;
        Assert.True(home.ShowEmptyLibrary);
        Assert.False(home.ShowLibraryScanning);

        // Songs published mid-scan hide both.
        settings.IsScanning = true;
        lib.TrackList.Add(T("One"));
        lib.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();
        Assert.False(home.ShowEmptyLibrary);
        Assert.False(home.ShowLibraryScanning);
    }

    [AvaloniaFact]
    public async Task AddMusicFolderButton_RunsSettingsAddFolderFlow()
    {
        EnsureAppStyles();
        var (home, settings, _) = Build();
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var picks = 0;
            settings.PickMusicFolder = () => { picks++; return Task.FromResult<string?>(dir); };
            await home.RefreshAsync();
            var (view, _) = Mount(home);

            var button = view.FindControl<Button>("EmptyLibraryAddFolderButton")!;
            Assert.Same(settings.AddMusicFolderCommand, button.Command);
            Assert.True(button.IsEffectivelyVisible);
            button.Command!.Execute(button.CommandParameter);
            await settings.AddMusicFolderCommand.ExecutionTask!;

            Assert.Equal(1, picks);
            Assert.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), settings.MusicFolders);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task AddMusicFolder_CancelledPicker_AddsNothing()
    {
        EnsureAppStyles();
        var (_, settings, _) = Build();
        settings.PickMusicFolder = () => Task.FromResult<string?>(null);
        await settings.AddMusicFolderCommand.ExecuteAsync(null);
        Assert.Empty(settings.MusicFolders);
    }

    // Settings › Library › Add Folder runs the very same command (it used to be a Click
    // handler in SettingsView's code-behind with its own picker).
    [AvaloniaFact]
    public void SettingsAddFolderButton_IsBoundToTheSameCommand()
    {
        EnsureAppStyles();
        var (_, settings, _) = Build();
        settings.SelectedSettingsTab = SettingsViewModel.TabLibrary; // pages join the tree when shown (#139)
        var view = new SettingsView { DataContext = settings };
        var win = new Window { Width = 920, Height = 720, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var button = view.FindControl<Button>("AddFolderButton")!;
        Assert.Same(settings.AddMusicFolderCommand, button.Command);
    }

    [AvaloniaFact]
    public async Task OpenSettingsLink_RequestsSettings()
    {
        EnsureAppStyles();
        var (home, _, _) = Build();
        await home.RefreshAsync();
        var (view, _) = Mount(home);
        var raised = 0;
        home.NavigateToSettingsRequested += (_, _) => raised++;

        var link = view.FindControl<Button>("EmptyLibrarySettingsButton")!;
        Assert.Same(home.OpenMediaFolderSettingsCommand, link.Command);
        link.Command!.Execute(null);
        Assert.Equal(1, raised);
    }

    // Explicit only: real Skia renders of both states for the lead's review. Runs when
    // NOCTIS_TEST_SKIA=1 and NOCTIS_HOME_EMPTY_SHOTS_DIR names the output folder.
    [AvaloniaFact]
    public async Task EmptyAndScanningStates_SavePngs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_HOME_EMPTY_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        EnsureAppStyles();
        var (home, settings, _) = Build();
        await home.RefreshAsync();

        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var view = new HomeView { DataContext = home };
            var win = new Window
            {
                Width = 1100, Height = 700, Content = view,
                RequestedThemeVariant = ThemeVariant.Dark,
                Background = new SolidColorBrush(Color.Parse("#242424")),
            };
            win.Show();
            Pump(400);
            Save(win, Path.Combine(dir, "home-empty.png"));

            settings.IsScanning = true;
            Pump(400);
            Save(win, Path.Combine(dir, "home-scanning.png"));
            settings.IsScanning = false;
        });

        var (home2, _, lib2) = Build();
        for (var i = 0; i < 5; i++) lib2.TrackList.Add(T("Song " + i));
        await home2.RefreshAsync();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var view = new HomeView { DataContext = home2 };
            var win = new Window
            {
                Width = 1100, Height = 700, Content = view,
                RequestedThemeVariant = ThemeVariant.Dark,
                Background = new SolidColorBrush(Color.Parse("#242424")),
            };
            win.Show();
            Pump(400);
            Save(win, Path.Combine(dir, "home-nothing-played.png"));
        });
    }

    private void Save(Window win, string path)
    {
        win.CaptureRenderedFrame()!.Save(path);
        _o.WriteLine(path);
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Phone Settings, the theme that follows the system, the shared accent palette and log export.</summary>
public class MobileSettingsTests
{
    private sealed class FakeThemeHost : IThemeHost
    {
        public (string Appearance, string DarkTheme, string Accent)? Last { get; private set; }
        public void ApplyTheme(string appearance, string darkTheme, string accentHex) => Last = (appearance, darkTheme, accentHex);
    }

    private sealed class FakeLogExporter : ILogExporter
    {
        public string? FileName { get; private set; }
        public string? Text { get; private set; }
        public Exception? Failure { get; set; }
        public Task<bool> ExportAsync(string suggestedFileName, string text)
        {
            FileName = suggestedFileName;
            Text = text;
            return Failure != null ? Task.FromException<bool>(Failure) : Task.FromResult(true);
        }
    }

    private static async Task<SettingsPageViewModel> OpenSettings(ShellViewModel shell)
    {
        shell.OpenSettingsCommand.Execute(null);
        var page = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
        await page.Loaded;
        return page;
    }

    [AvaloniaFact]
    public void AccentPalette_Build_CarriesTheAccentAndItsLightThemeOutline()
    {
        var crimson = Color.Parse("#E74856");
        var dark = AccentPalette.Build(crimson, isLightTheme: false);
        Assert.Equal(crimson, (Color)dark["SystemAccentColor"]!);
        Assert.Equal(crimson, ((ISolidColorBrush)dark["AccentColorBrush"]!).Color);
        Assert.Equal(0, ((ISolidColorBrush)dark["AccentBorderBrush"]!).Color.A);          // no outline on dark

        var white = AccentPalette.Build(Colors.White, isLightTheme: true);
        Assert.NotEqual(0, ((ISolidColorBrush)white["AccentBorderBrush"]!).Color.A);      // a white pill on a light page needs one
        Assert.Equal(Colors.Black, ((ISolidColorBrush)white["AccentForegroundBrush"]!).Color);
    }

    [Theory]
    [InlineData("System", "Smoke", PlatformThemeVariant.Dark, "Smoke")]
    [InlineData("System", "Smoke", PlatformThemeVariant.Light, "Light")]
    [InlineData("Dark", "Midnight", PlatformThemeVariant.Light, "Midnight")]
    [InlineData("Light", "Ink", PlatformThemeVariant.Dark, "Light")]
    [InlineData("System", "Gray", PlatformThemeVariant.Dark, "Ink")]       // a desktop-only name falls back to Ink
    [InlineData(null, null, PlatformThemeVariant.Dark, "Ink")]
    public void MobileTheme_Resolve_FollowsTheSystemUnlessOverridden(string? appearance, string? dark, PlatformThemeVariant system, string expected)
    {
        Assert.Equal(expected, MobileTheme.Resolve(appearance, dark, system));
    }

    [AvaloniaFact]
    public void MobileTheme_Apply_SwapsTheOverlayAndTheAccent_AndSetsTheVariant()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        try
        {
            theme.Apply(app, "Ink", "#12C76F");
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.True(app.Resources.TryGetResource("AppMainBackgroundColor", ThemeVariant.Dark, out var ink));
            Assert.Equal(Color.Parse("#0F0F0F"), (Color)ink!);                                      // Ink's surface
            Assert.True(app.Resources.TryGetResource("AccentColorBrush", ThemeVariant.Dark, out var accent));
            Assert.Equal(Color.Parse("#12C76F"), ((ISolidColorBrush)accent!).Color);               // the accent beats Ink's own

            theme.Apply(app, "Light", "#12C76F");
            Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
            Assert.False(app.Resources.TryGetResource("AppMainBackgroundColor", ThemeVariant.Light, out _));   // overlay gone
            Assert.Equal("Light", theme.ThemeName);
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
    }

    /// <summary>The base Light dictionary is the desktop's legacy Light look and keeps white
    /// (#80FFFFFF) secondary text, which vanished on the phone's white pages (device, 09-23).
    /// The phone's override is Light-variant only, so the always-dark overlays keep theirs.</summary>
    [AvaloniaFact]
    public void MobileTheme_Apply_Light_GivesDarkSecondaryText_OnlyForTheLightVariant()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        try
        {
            theme.Apply(app, "Light", null);
            Assert.True(app.Resources.TryGetResource("SecondaryTextBrush", ThemeVariant.Light, out var text));
            var color = ((ISolidColorBrush)text!).Color;
            Assert.True(color.R < 0x40 && color.G < 0x40 && color.B < 0x40, color.ToString());
            Assert.False(app.Resources.TryGetResource("SecondaryTextBrush", ThemeVariant.Dark, out _));

            theme.Apply(app, "Ink", null);                                                       // the override goes with Light
            Assert.True(app.Resources.TryGetResource("SecondaryTextBrush", ThemeVariant.Dark, out var ink));
            Assert.Equal(Color.Parse("#A3A3A3"), ((ISolidColorBrush)ink!).Color);
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
    }

    [Fact]
    public async Task Settings_AppearanceThemeAndAccent_ApplyAndPersist()
    {
        using var rig = MobileFixtures.MakeRig();
        var host = new FakeThemeHost();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Theme = host };
        var page = await OpenSettings(shell);
        Assert.Equal("System", page.Appearance);
        Assert.Equal("Ink", page.DarkTheme);                       // AppSettings.Theme defaults to the desktop's "Gray"
        Assert.Null(host.Last);                                    // loading applies nothing

        page.Appearance = "Light";
        Assert.Equal(("Light", "Ink", "#E74856"), host.Last);
        page.DarkTheme = "Midnight";
        page.SelectedAccent = SettingsPageViewModel.Accents.Single(a => a.Name == "Emerald");
        Assert.Equal(("Light", "Midnight", "#12C76F"), host.Last);
        await page.PendingSave;

        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("Light", saved.MobileAppearance);
        Assert.Equal("Midnight", saved.Theme);
        Assert.True(saved.ThemeV2Migrated);
        Assert.Equal("#12C76F", saved.AccentColorHex);
        Assert.Equal("Emerald", saved.AccentPresetName);
    }

    [Fact]
    public async Task Settings_LyricsTextSize_AppliesAndPersists()
    {
        using var rig = MobileFixtures.MakeRig();
        var page = await OpenSettings(rig.Shell);

        page.LyricsTextScale = 1.4;
        await page.PendingSave;

        Assert.Equal(LyricsPageViewModel.BaseFontSize * 1.4, rig.Shell.Lyrics.LineFontSize, 6);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal(1.4, saved.MobileLyricsTextScale, 6);

        var fresh = new LyricsPageViewModel(rig.Player, rig.Shell.Player, new FakeTrackFiles(), rig.Persistence, work => Task.FromResult(work()));
        await fresh.InitializeAsync();
        Assert.Equal(1.4, fresh.TextScale, 6);
    }

    /// <summary>Owner 10-11: gapless is always on on the phone. A settings.json that says off
    /// (written by the desktop, or by an older phone build that had the switch) changes nothing:
    /// the player is gapless from the moment the shell is built, as the Android startup builds
    /// it, and stays so through Settings; the saved desktop value is left alone.</summary>
    [Fact]
    public async Task Gapless_IsOnFromStartup_EvenWhenSettingsSayOff()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var persistence = new PersistenceService(root);
            var seeded = await persistence.LoadSettingsAsync();
            seeded.GaplessPlaybackEnabled = false;
            await persistence.SaveSettingsAsync(seeded);

            var tracks = new[] { MobileFixtures.Song("One"), MobileFixtures.Song("Two") };
            var library = new FakeLibraryService();
            library.TrackList.AddRange(tracks);
            var player = new FakeAudioPlayer();
            player.SetGapless(false);                               // whatever the player came up with
            var history = new FakeHistoryLog();
            var nowPlaying = new NowPlayingViewModel(player, library, persistence, history, marshal: a => a());
            var shell = new ShellViewModel(
                new LibraryViewModel(library, persistence, new MobileFixtures.NoPicker(), history, marshal: a => a()),
                nowPlaying,
                new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())));
            await shell.Library.InitializeAsync();
            Assert.True(player.GaplessEnabled);

            var page = await OpenSettings(shell);
            page.LyricsTextScale = 1.2;                             // a save through Settings
            await page.PendingSave;
            Assert.True(player.GaplessEnabled);
            Assert.False((await persistence.LoadSettingsAsync()).GaplessPlaybackEnabled);

            nowPlaying.PlayTracks(tracks, 0);
            Assert.Equal(new[] { tracks[1].FilePath }, player.PreparedPaths);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExportLogs_HandsTheLogToTheExporter()
    {
        using var rig = MobileFixtures.MakeRig();
        var exporter = new FakeLogExporter();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Logs = exporter, VersionText = "Noctis 9.9.9" };
        var page = await OpenSettings(shell);
        DebugLog.Write("Test", "export-marker-5f1c");

        await page.ExportLogsCommand.ExecuteAsync(null);

        Assert.StartsWith("noctis-log-", exporter.FileName);
        Assert.Contains("export-marker-5f1c", exporter.Text);
        Assert.Equal("Log saved", page.ExportStatus);
        Assert.Equal("Noctis 9.9.9", page.VersionText);
    }

    /// <summary>A write that failed (the picked place refused it, no documents app) said "Export
    /// cancelled", as if the user had backed out: the failure was visible only in logcat.</summary>
    [Fact]
    public async Task ExportLogs_AFailedWrite_SaysSo()
    {
        using var rig = MobileFixtures.MakeRig();
        var exporter = new FakeLogExporter { Failure = new IOException("no space") };
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Logs = exporter };
        var page = await OpenSettings(shell);

        await page.ExportLogsCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't save the log", page.ExportStatus);
    }

    /// <summary>S23 2026-10-05: the folder's grant was dropped and every song failed with a bare
    /// "Source error"; Settings now names the folder whose access was lost.</summary>
    [AvaloniaFact]
    public async Task AFolderWithoutAccess_SaysSo()
    {
        const string tones = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones";
        const string beats = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FBeats";
        var picker = new MobileFixtures.NoPicker();
        picker.Lost.Add(beats);
        using var rig = MobileFixtures.MakeRig(picker: picker, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.Add(tones);
            s.MusicFolders.Add(beats);
            await p.SaveSettingsAsync(s);
        });

        rig.Shell.OpenSettingsCommand.Execute(null);
        var page = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await page.Loaded;

        Assert.Equal(new[] { new SettingsFolder(tones, "Music/Tones", false), new SettingsFolder(beats, "Music/Beats", true) }, page.FolderRows);
    }

    /// <summary>Device 10-13: with the SD card taken out, a rescan said "1 folder(s) unavailable"
    /// and no row said which. The scan's unreachable folder is now marked on its row (its grant
    /// is fine, so not "Access lost"), named in the note, and the top level shows the dot; a scan
    /// that reaches it again clears all three.</summary>
    [AvaloniaFact]
    public async Task AFolderTheScanCouldNotReach_IsMarked_UntilItIsBack()
    {
        const string card = "content://com.android.externalstorage.documents/tree/A00C-1602%3AMusic";
        var picker = new MobileFixtures.NoPicker();
        picker.Storage[card] = "SD card";
        using var rig = MobileFixtures.MakeRig(picker: picker, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.AddRange(new[] { Tones, card });
            await p.SaveSettingsAsync(s);
        });
        rig.Shell.OpenSettingsCommand.Execute(null);
        var page = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await page.Loaded;

        rig.Library.AbortScanWithRoots = new[] { card };
        await rig.Shell.Library.RescanCommand.ExecuteAsync(null);

        Assert.Equal(new[] { false, true }, page.FolderRows.Select(f => f.Unavailable));
        Assert.All(page.FolderRows, f => Assert.False(f.AccessLost));
        Assert.True(page.HasLostFolder);
        Assert.Equal("“Music” is unavailable. Reconnect it and rescan.", page.FoldersFootnote);

        rig.Library.AbortScanWithRoots = null;
        await rig.Shell.Library.RescanCommand.ExecuteAsync(null);
        Assert.All(page.FolderRows, f => Assert.False(f.Unavailable));
        Assert.False(page.HasLostFolder);
        Assert.Equal("Noctis can read only the folders you choose here.", page.FoldersFootnote);
    }

    /// <summary>Device 10-13: a virtual SD card's Music read just "Music", the same as the phone's
    /// own Music folder. A folder on a card now says which card, and a whole card is its name.</summary>
    [AvaloniaFact]
    public async Task AFolderOnACard_SaysWhichCard()
    {
        const string music = "content://com.android.externalstorage.documents/tree/primary%3AMusic";
        const string cardMusic = "content://com.android.externalstorage.documents/tree/A00C-1602%3AMusic";
        const string card = "content://com.android.externalstorage.documents/tree/B11D-2713%3A";
        var picker = new MobileFixtures.NoPicker();
        picker.Storage[cardMusic] = "SD card";
        picker.Storage[card] = "USB drive";
        using var rig = MobileFixtures.MakeRig(picker: picker, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.AddRange(new[] { music, cardMusic, card });
            await p.SaveSettingsAsync(s);
        });

        rig.Shell.OpenSettingsCommand.Execute(null);
        var page = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await page.Loaded;

        Assert.Equal(new[] { ("Music", false), ("Music", true), ("USB drive", false) },
            page.FolderRows.Select(f => (f.Title, f.ShowsPath)));
        Assert.Equal(new[] { "Music", "SD card/Music", "USB drive" }, page.FolderRows.Select(f => f.Name));
    }

    [AvaloniaFact]
    public async Task ProfileButton_OpensSettings_WithReadableFolders()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.Add("content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones");
            await p.SaveSettingsAsync(s);
        });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        var profile = MobileFixtures.Named<Button>(view, "ProfileButtonRoot");   // Library's header ProfileButton
        profile.Command!.Execute(profile.CommandParameter);
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).Loaded;
        window.UpdateLayout();

        var page = MobileFixtures.Find<SettingsPage>(view);
        var vm = (SettingsPageViewModel)page.DataContext!;
        Assert.Equal(new[] { new SettingsFolder("content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones", "Music/Tones", false) }, vm.FolderRows);
        Assert.True(MobileFixtures.Named<Button>(page, "ExportLogsButton").IsEffectivelyVisible);

        // Music Folders: the folder's own name, where it is in grey under it.
        vm.OpenPaneCommand.Execute(SettingsPane.Folders);
        window.UpdateLayout();
        var texts = MobileFixtures.Named<ItemsControl>(page, "FolderList").GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(new[] { "Tones", "Music/Tones" }, texts);
        window.Close();
    }

    /// <summary>Now Playing and Lyrics are always dark (ruling 8): under the Light theme they
    /// must still resolve dark resources, or Fluent's slider tracks and theme-brushed icons turn
    /// light-on-dark-scrim. The Queue follows the app theme (MobileQueueSheetTests).</summary>
    [AvaloniaFact]
    public void AlwaysDarkOverlays_ResolveDarkResources_UnderTheLightTheme()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
            var window = MobileFixtures.Mount(rig.Shell, out var view);
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
            rig.Shell.OpenNowPlayingCommand.Execute(null);
            window.UpdateLayout();

            Assert.Equal(ThemeVariant.Light, view.ActualThemeVariant);
            var nowPlaying = MobileFixtures.Find<NowPlayingPage>(view);
            Assert.Equal(ThemeVariant.Dark, nowPlaying.ActualThemeVariant);
            Assert.Equal(ThemeVariant.Dark, MobileFixtures.Named<Control>(nowPlaying, "SeekBar").ActualThemeVariant);
            Assert.Equal(ThemeVariant.Dark, MobileFixtures.Find<LyricsPage>(view).ActualThemeVariant);
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = before;
        }
    }

    /// <summary>Mounts Settings with the desktop icon set merged as the device merges it
    /// (AndroidApp.axaml), so the action rows resolve their icons.</summary>
    private static async Task WithMountedSettings(MobileFixtures.Rig rig, Action<SettingsPage, SettingsPageViewModel, Window> check)
    {
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            window.UpdateLayout();
            check(MobileFixtures.Find<SettingsPage>(view), vm, window);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    /// <summary>Owner 10-13: the Player Bar page switches the mini player's progress
    /// colour and line, both off by default; a switch acts on the shell at once and is saved.</summary>
    [AvaloniaFact]
    public async Task Settings_Appearance_PlayerBarSwitches_DriveTheMiniPlayer()
    {
        using var rig = await RigWithFolders(null, Tones);
        await rig.Shell.InitializeAsync();
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            vm.OpenPaneCommand.Execute(SettingsPane.PlayerBar);
            Settle(window);
            var card = MobileFixtures.Named<Border>(page, "PlayerBarCard");
            Assert.True(card.IsEffectivelyVisible);
            var colour = MobileFixtures.Named<Avalonia.Controls.Primitives.ToggleButton>(page, "ProgressColourSwitch");
            var line = MobileFixtures.Named<Avalonia.Controls.Primitives.ToggleButton>(page, "ProgressLineSwitch");
            Assert.False(colour.IsChecked);
            Assert.False(line.IsChecked);

            colour.IsChecked = true;
            Assert.True(rig.Shell.ShowMiniProgressFill);
            Assert.False(rig.Shell.ShowMiniProgressLine);
            line.IsChecked = true;
            Assert.True(rig.Shell.ShowMiniProgressLine);
        });
        await rig.Shell.PendingPlayerBarSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.True(saved.MobilePlayerBarProgressFill);
        Assert.True(saved.MobilePlayerBarProgressLine);
    }

    /// <summary>Device 10-13: the profile card's avatar sat flush against the card's left edge,
    /// the circle clipped by the card's corner: the row's Button#AccountRow padding lost to
    /// MobileStyles' Button.row (Padding 0). The inset is now the row grid's own margin.</summary>
    [AvaloniaFact]
    public async Task Settings_ProfileCard_InsetsTheAvatar16dp()
    {
        using var rig = MobileFixtures.MakeRig();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs("John Doe") },
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            await ((SettingsPageViewModel)shell.CurrentPage!).Loaded;
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);
            var card = MobileFixtures.Named<Border>(page, "AccountCard");
            var avatar = MobileFixtures.Named<Border>(page, "ProfileAvatar");
            Assert.Equal(16, avatar.TranslatePoint(new Point(0, 0), card)!.Value.X, 1);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Lets a pushed page's slide (and the hidden pane's settle) run to the end.</summary>
    private static void Settle(Window window, int ms = 520)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(5);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        MobileFixtures.Land(window);
    }

    private static Task<MobileFixtures.Rig> RigWithFolders(MobileFixtures.NoPicker? picker = null, params string[] folders) =>
        Task.FromResult(MobileFixtures.MakeRig(picker: picker, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.AddRange(folders);
            await p.SaveSettingsAsync(s);
        }));

    private const string Tones = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones";
    private const string Beats = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FBeats";

    /// <summary>10-28 redesign: the top level is a large title over the profile card (its stats
    /// strip always there), then five sections of rows, each with its round glyph chip; the About
    /// rows have none; the only accent rows are gone (Rescan and Export Logs are chip rows now).
    /// The small header title waits for the large one to scroll away.</summary>
    [AvaloniaFact]
    public async Task Settings_TopLevel_HasTheLargeTitle_ProfileStats_SectionsAndChipRows()
    {
        using var rig = await RigWithFolders(null, Tones);
        await WithMountedSettings(rig, (page, vm, _) =>
        {
            var visible = page.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.DoesNotContain(visible, c => c.Classes.Contains("group-head") || c.Classes.Contains("sheet-glyph"));
            Assert.Equal("Settings", MobileFixtures.Named<TextBlock>(page, "LargeTitle").Text);
            Assert.Equal(0, MobileFixtures.Named<TextBlock>(page, "SettingsTitle").Opacity);
            Assert.False(MobileFixtures.Named<Button>(page, "BackButton").IsVisible);
            Assert.False(MobileFixtures.Named<Button>(page, "AccountRow").IsVisible);     // no account service in this rig
            Assert.True(MobileFixtures.Named<Border>(page, "StatsStrip").IsEffectivelyVisible);
            // No account, no downloads: Albums and Artists stand in for Downloaded and On Phone.
            Assert.True(MobileFixtures.Named<TextBlock>(page, "AlbumsStatLabel").IsEffectivelyVisible);
            Assert.True(MobileFixtures.Named<TextBlock>(page, "ArtistsStatLabel").IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<TextBlock>(page, "DownloadedStatLabel").IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<TextBlock>(page, "OnPhoneStatLabel").IsEffectivelyVisible);
            Assert.Equal(rig.Shell.Library.AlbumCount.ToString("N0"), vm.AlbumsStat);
            Assert.Equal(new[] { "Appearance", "Library", "Listening", "Help", "About" },
                visible.OfType<TextBlock>().Where(t => t.Classes.Contains("group-caption")).Select(t => t.Text));

            foreach (var (name, label) in new[]
                     {
                         ("BackgroundRow", "Background"), ("ThemeRow", "Theme"), ("AccentRow", "Accent"), ("PlayerBarRow", "Player Bar"),
                         ("FoldersRow", "Music Folders"), ("RescanButton", "Rescan Library"),
                         ("LyricsRow", "Lyrics"), ("ScrobblingRow", "Scrobbling"), ("HistoryRow", "Listening History"),
                         ("ReportRow", "Report a Problem"), ("ExportLogsButton", "Export Logs"), ("WebsiteRow", "Noctis Website"), ("PrivacyRow", "Privacy Policy"),
                     })
            {
                var row = MobileFixtures.Named<Button>(page, name);
                Assert.True(row.IsEffectivelyVisible, name);
                Assert.Equal(label, row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("row-label")).Text);
                var chip = row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("row-chip"));
                Assert.Equal(new Size(30, 30), chip.Bounds.Size);
                Assert.Single(chip.GetVisualDescendants().OfType<GridIcon>());
            }
            foreach (var name in new[] { "WhatsNewRow", "LicensesRow" })
                Assert.DoesNotContain(MobileFixtures.Named<Button>(page, name).GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("row-chip"));
            // The computer's rows (Downloads, Pick Up on Computer) show only with an account.
            Assert.False(MobileFixtures.Named<Button>(page, "DownloadsRow").IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<Avalonia.Controls.Primitives.ToggleButton>(page, "PickUpSwitch").IsEffectivelyVisible);
            Assert.DoesNotContain(visible.OfType<Button>(), b => b.Classes.Contains("accent-row"));

            Assert.All(visible.OfType<Border>().Where(b => b.Classes.Contains("group-card")), c => Assert.Equal(24, c.CornerRadius.TopLeft));
            Assert.Equal(vm.VersionValue, MobileFixtures.Named<TextBlock>(page, "VersionLabel").Text);
            var tagline = MobileFixtures.Named<TextBlock>(page, "TaglineLabel");
            Assert.True(tagline.IsEffectivelyVisible);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, tagline.HorizontalAlignment);
            Assert.DoesNotContain(visible, c => c.Name is "GaplessSwitch");
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Gapless", StringComparison.OrdinalIgnoreCase) == true);
        });
    }

    /// <summary>Each row carries its value in grey, and its screen-reader name says it too ("Music
    /// Folders, 2" plus the warning when a folder lost its access). They follow changes.</summary>
    [AvaloniaFact]
    public async Task Settings_Rows_ShowTheirValues_NameThem_AndFollowChanges()
    {
        var picker = new MobileFixtures.NoPicker();
        picker.Lost.Add(Beats);
        using var rig = await RigWithFolders(picker, Tones, Beats);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            string NameOf(string row) => Avalonia.Automation.AutomationProperties.GetName(MobileFixtures.Named<Button>(page, row));
            Assert.Equal("2", MobileFixtures.Named<TextBlock>(page, "FoldersValueLabel").Text);
            Assert.True(MobileFixtures.Named<Avalonia.Controls.Shapes.Ellipse>(page, "FolderWarningDot").IsEffectivelyVisible);
            Assert.Equal("Music Folders, 2, a folder lost its access or can't be reached", NameOf("FoldersRow"));
            Assert.Equal("System, Ink", MobileFixtures.Named<TextBlock>(page, "ThemeValueLabel").Text);
            Assert.Equal("Crimson", MobileFixtures.Named<TextBlock>(page, "AccentValueLabel").Text);
            Assert.Equal("Plain", MobileFixtures.Named<TextBlock>(page, "PlayerBarValueLabel").Text);
            Assert.Equal("Off", MobileFixtures.Named<TextBlock>(page, "BackgroundValueLabel").Text);
            Assert.Equal("Off", MobileFixtures.Named<TextBlock>(page, "ScrobblingValueLabel").Text);
            Assert.Equal("Both on", MobileFixtures.Named<TextBlock>(page, "LyricsValueLabel").Text);
            Assert.Equal("Theme, System, Ink", NameOf("ThemeRow"));
            Assert.Equal("Accent, Crimson", NameOf("AccentRow"));
            Assert.Equal("Player Bar, Plain", NameOf("PlayerBarRow"));
            Assert.Equal("Background, Off", NameOf("BackgroundRow"));
            Assert.Equal("Scrobbling, Off", NameOf("ScrobblingRow"));
            Assert.Equal("Listening History", NameOf("HistoryRow"));               // no summary, no comma
            Assert.Equal("Lyrics, Both on", NameOf("LyricsRow"));
            Assert.Equal("Export Logs, Keys and passwords removed", NameOf("ExportLogsButton"));
            Assert.StartsWith("Rescan Library, ", NameOf("RescanButton"));

            vm.Appearance = "Dark";
            vm.SelectedAccent = SettingsPageViewModel.Accents.Single(a => a.Name == "Emerald");
            rig.Shell.ShowMiniProgressLine = true;
            rig.Shell.Lyrics.ShowRomanization = false;
            window.UpdateLayout();
            Assert.Equal("Ink", MobileFixtures.Named<TextBlock>(page, "ThemeValueLabel").Text);
            Assert.Equal("Emerald", MobileFixtures.Named<TextBlock>(page, "AccentValueLabel").Text);
            Assert.Equal("Progress Line", MobileFixtures.Named<TextBlock>(page, "PlayerBarValueLabel").Text);
            Assert.Equal("Translations on", MobileFixtures.Named<TextBlock>(page, "LyricsValueLabel").Text);
            Assert.Equal("Theme, Ink", NameOf("ThemeRow"));
            Assert.Equal("Accent, Emerald", NameOf("AccentRow"));
            Assert.Equal("Player Bar, Progress Line", NameOf("PlayerBarRow"));
            Assert.Equal("Lyrics, Translations on", NameOf("LyricsRow"));
        });
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).PendingSave;
        await rig.Shell.PendingPlayerBarSave;
    }

    /// <summary>A row opens its page inside the sheet: the header takes the page's name and a back
    /// button, the top level leaves the screen. Back (the header's button, or Android Back)
    /// returns to the top level first; the next Back closes Settings.</summary>
    [AvaloniaFact]
    public async Task Settings_Pages_PushInsideTheSheet_AndBackReturnsToTheTopLevelFirst()
    {
        using var rig = await RigWithFolders(null, Tones);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            var title = MobileFixtures.Named<TextBlock>(page, "SettingsTitle");
            var back = MobileFixtures.Named<Button>(page, "BackButton");
            foreach (var (row, pane, heading) in new[]
                     {
                         ("FoldersRow", "FoldersPane", "Music Folders"),
                         ("AccentRow", "AccentPane", "Accent"),
                         ("PlayerBarRow", "PlayerBarPane", "Player Bar"),
                         ("LyricsRow", "LyricsPane", "Lyrics"),
                         ("BackgroundRow", "BackgroundHost", "Background"),
                         ("ScrobblingRow", "ScrobblingHost", "Scrobbling"),
                         ("HistoryRow", "HistoryHost", "Listening History"),
                         ("WhatsNewRow", "WhatsNewPane", "What's New"),
                         ("LicensesRow", "LicensesPane", "Open-Source Licenses"),
                     })
            {
                var button = MobileFixtures.Named<Button>(page, row);
                button.Command!.Execute(button.CommandParameter);
                Settle(window);
                Assert.Equal(heading, title.Text);
                Assert.True(back.IsEffectivelyVisible);
                Assert.True(MobileFixtures.Named<Control>(page, pane).IsEffectivelyVisible);
                Assert.False(MobileFixtures.Named<ScrollViewer>(page, "MainScroll").IsEffectivelyVisible);
                // The page sits fully in place once its slide ends.
                Assert.Equal(0, MobileFixtures.Named<ScrollViewer>(page, "SubScroll").TranslatePoint(new Point(0, 0), page)!.Value.X, 1);

                back.Command!.Execute(back.CommandParameter);
                Settle(window);
                Assert.Equal("Settings", title.Text);
                Assert.Equal(SettingsPane.Main, vm.Pane);
                Assert.True(MobileFixtures.Named<ScrollViewer>(page, "MainScroll").IsEffectivelyVisible);
                Assert.False(MobileFixtures.Named<ScrollViewer>(page, "SubScroll").IsEffectivelyVisible);
            }

            foreach (var pane in new[] { SettingsPane.Background, SettingsPane.Scrobbling, SettingsPane.History, SettingsPane.Accent,
                         SettingsPane.PlayerBar, SettingsPane.Downloads, SettingsPane.WhatsNew, SettingsPane.Licenses })
            {
                vm.OpenPaneCommand.Execute(pane);
                Assert.True(rig.Shell.TryHandleBack());      // Android Back leaves every new page first
                Assert.Equal(SettingsPane.Main, vm.Pane);
                Assert.Same(vm, rig.Shell.CurrentPage);
            }

            vm.OpenPaneCommand.Execute(SettingsPane.Lyrics);
            Assert.True(rig.Shell.TryHandleBack());          // Android Back: out of Lyrics…
            Assert.Equal(SettingsPane.Main, vm.Pane);
            Assert.Same(vm, rig.Shell.CurrentPage);
            Assert.True(rig.Shell.TryHandleBack());          // …then out of Settings
            Assert.Null(rig.Shell.CurrentPage);
        });
    }

    /// <summary>Rows are touch-sized on every page (each at least 52 dp tall, each button at least
    /// 44 dp); icons lead only the accent action rows, all the same 24 dp box at one left edge,
    /// centred on their rows.</summary>
    [AvaloniaFact]
    public async Task Settings_Rows_AreTouchSized_AndOnlyActionsLeadWithAnIcon()
    {
        using var rig = await RigWithFolders(null, Tones);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            var rows = new List<Control>();
            foreach (var pane in new[] { SettingsPane.Main, SettingsPane.Folders, SettingsPane.Accent, SettingsPane.PlayerBar, SettingsPane.Lyrics, SettingsPane.WhatsNew })
            {
                vm.Pane = pane;
                Settle(window);
                var shown = page.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && (c.Classes.Contains("setting-row") || c.Classes.Contains("switch-row"))).ToList();
                Assert.NotEmpty(shown);
                rows.AddRange(shown);
                Assert.All(page.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.TemplatedParent == null),  // not a slider's parts
                    b => Assert.True(b.Bounds.Height >= 44, $"{b.Name} is {b.Bounds.Height} tall"));
            }
            Assert.True(rows.Count >= 12, $"{rows.Count} rows");
            Assert.All(rows, r => Assert.True(r.Bounds.Height >= 52, $"{r.Name ?? r.GetType().Name} is {r.Bounds.Height} tall"));

            var icons = rows.SelectMany(r => r.GetVisualDescendants().OfType<GridIcon>().Where(i => i.Classes.Contains("row-icon"))).ToList();
            Assert.Equal(1, icons.Count);                    // Add Folder; the top level's glyphs are chips, not row-icons
            Assert.All(icons, i => Assert.Contains("accent-row", i.GetVisualAncestors().OfType<Button>().First().Classes));
            Assert.All(icons, i => Assert.Equal(new Size(24, 24), i.Bounds.Size));
            Assert.All(icons, i =>
            {
                var card = i.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("group-card"));
                Assert.Equal(16, i.TranslatePoint(new Point(0, 0), card)!.Value.X, 1);
                var row = i.GetVisualAncestors().OfType<Grid>().First(g => g.Classes.Contains("setting-row"));
                Assert.InRange(i.TranslatePoint(new Point(0, 12), row)!.Value.Y, row.Bounds.Height / 2 - 1.5, row.Bounds.Height / 2 + 1.5);
            });
        });
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).PendingSave;
    }

    /// <summary>Apple's grouped look: a hairline between plain rows starts under the text, 16 dp in,
    /// and stops 16 dp short of the card's right edge; the round close button closes the sheet.</summary>
    [AvaloniaFact]
    public async Task Settings_Hairlines_AreInsetOnBothSides_AndTheCloseButtonGoesBack()
    {
        using var rig = await RigWithFolders(null, Tones, Beats);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            var lines = new List<Border>();
            foreach (var pane in new[] { SettingsPane.Main, SettingsPane.Folders, SettingsPane.Accent, SettingsPane.PlayerBar, SettingsPane.Lyrics })
            {
                vm.Pane = pane;
                Settle(window);
                lines.AddRange(page.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("row-separator") && b.IsEffectivelyVisible));
            }
            // 17 on the top level (7 Appearance with Now Playing Artwork, Language, Liquid Glass Tab Pill and Smooth Motion, 2 Library with
            // Downloads away and Hidden, 3 Listening with Played Songs in Queue, 3 Help, 2 About), 1 between two folders, none on Accent,
            // 1 between the Player Bar's two switches, 3 in Lyrics (two between its three switches,
            // one between its two sliders): none above a list's first row.
            Assert.Equal(22, lines.Count);
            Assert.All(lines, line =>
            {
                var card = line.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("group-card"));
                var left = line.TranslatePoint(new Point(0, 0), card)!.Value.X;
                Assert.Equal(line.Classes.Contains("chipped") ? 60 : 16, left, 1);   // under the text: after a chip, or 16 in
                Assert.Equal(card.Bounds.Width - 16, left + line.Bounds.Width, 1);
            });

            vm.Pane = SettingsPane.Folders;
            Settle(window);
            var close = MobileFixtures.Named<Button>(page, "CloseButton");
            Assert.True(close.Bounds.Width >= 44 && close.Bounds.Height >= 44);
            close.Command!.Execute(close.CommandParameter);     // from a pushed page too: the whole sheet
            window.UpdateLayout();
            Assert.IsNotType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        });
    }

    /// <summary>Music Folders lists each folder by its name with where it is under it, says in
    /// place when its access was lost, and offers Add Folder as an accent action.</summary>
    [AvaloniaFact]
    public async Task Settings_MusicFolders_ShowsTheLostFolder_AndAddFolder()
    {
        var picker = new MobileFixtures.NoPicker();
        picker.Lost.Add(Beats);
        using var rig = await RigWithFolders(picker, Tones, Beats);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            vm.OpenPaneCommand.Execute(SettingsPane.Folders);
            Settle(window);
            var list = MobileFixtures.Named<ItemsControl>(page, "FolderList");
            var warnings = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("folder-lost")).ToList();
            Assert.Equal(new[] { false, true }, warnings.Select(w => w.IsEffectivelyVisible));
            Assert.Equal(new[] { "Tones", "Music/Tones", "Beats", "Access lost. Add this folder again to play its songs." },
                list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Assert.False(MobileFixtures.Named<Grid>(page, "NoFoldersRow").IsEffectivelyVisible);

            var add = MobileFixtures.Named<Button>(page, "AddFolderButton");
            Assert.True(add.IsEffectivelyVisible);
            Assert.Contains("accent-row", add.Classes);
            Assert.NotNull(add.GetVisualDescendants().OfType<GridIcon>().Single().Data);
            Assert.Contains(add.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Add Folder");
        });
    }

    /// <summary>Desktop parity (Settings › Library's ✕ per folder): each folder row ends in a red
    /// bin, named for the screen reader. A tap asks first; Cancel keeps the folder; Remove Folder
    /// takes it out, and the top level's count and warning dot follow at once. Add Folder is named
    /// too (TalkBack read it as "Avalonia.Controls.Grid"), and a refused pick's note shows under it.</summary>
    [AvaloniaFact]
    public async Task Settings_MusicFolders_RemoveAsksFirst_ThenTheFolderGoes()
    {
        var picker = new MobileFixtures.NoPicker();
        picker.Lost.Add(Beats);
        using var rig = await RigWithFolders(picker, Tones, Beats);
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            // The top level is laid out before a row on it opens a page, as on the phone (and
            // as WithMountedSettings does). Without it the page could be built straight onto
            // Music Folders when the settings read beat the dispatcher's layout pass: the top
            // level then starts hidden, a hidden control is never templated, and its
            // FoldersValueLabel was not in the tree for the count check below.
            window.UpdateLayout();
            vm.OpenPaneCommand.Execute(SettingsPane.Folders);
            Settle(window);
            var page = MobileFixtures.Find<SettingsPage>(view);

            Assert.Equal("Add Folder", Avalonia.Automation.AutomationProperties.GetName(MobileFixtures.Named<Button>(page, "AddFolderButton")));
            Assert.Equal("Noctis can read only the folders you choose here.", MobileFixtures.Named<TextBlock>(page, "FoldersFootnoteLabel").Text);
            var bins = page.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("folder-remove")).ToList();
            Assert.Equal(new[] { "Remove Tones", "Remove Beats" }, bins.Select(Avalonia.Automation.AutomationProperties.GetName));
            Assert.All(bins, b => Assert.True(b.Bounds.Width >= 44 && b.Bounds.Height >= 44));
            // Asked on the glass prompt over the sheet (owner 10-20), not in a card on the page.
            var sheet = view.FindControl<PromptSheet>("Prompt")!;
            Assert.False(sheet.IsVisible);

            bins[1].Command!.Execute(bins[1].CommandParameter);
            window.UpdateLayout();
            Assert.True(sheet.IsVisible);
            Assert.Equal("Remove “Beats”?", MobileFixtures.Named<TextBlock>(sheet, "PromptTitle").Text);
            Assert.Equal("Its songs leave your library on this phone. The files stay where they are.",
                MobileFixtures.Named<TextBlock>(sheet, "PromptMessage").Text);
            Assert.Equal("Remove Folder", Assert.Single(rig.Shell.Prompt!.Actions).Label);

            MobileFixtures.Named<Button>(sheet, "PromptCancel").Command!.Execute(null);
            Assert.False(rig.Shell.IsPromptOpen);
            Assert.False(vm.IsConfirmingRemoveFolder);
            Assert.Equal(2, vm.FolderRows.Count);

            bins[1].Command!.Execute(bins[1].CommandParameter);
            await rig.Shell.Prompt!.Actions[0].RunCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Assert.False(rig.Shell.IsPromptOpen);
            Assert.Equal(new[] { Tones }, (await rig.Persistence.LoadSettingsAsync()).MusicFolders);
            Assert.Equal(new[] { Tones }, rig.Library.ScanPasses.Last());
            Assert.Equal(new[] { "Tones", "Music/Tones" },
                MobileFixtures.Named<ItemsControl>(page, "FolderList").GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Assert.Equal("1", MobileFixtures.Named<TextBlock>(page, "FoldersValueLabel").Text);
            Assert.False(vm.HasLostFolder);

            rig.Shell.Library.StatusText = "Already covered by “Music”.";
            window.UpdateLayout();
            Assert.Equal("Already covered by “Music”.", MobileFixtures.Named<TextBlock>(page, "FoldersFootnoteLabel").Text);

            // Leaving the page drops a question left open.
            vm.AskRemoveFolderCommand.Execute(vm.FolderRows[0]);
            vm.ShowMainCommand.Execute(null);
            Assert.False(vm.IsConfirmingRemoveFolder);
            Assert.False(rig.Shell.IsPromptOpen);

            // Back cancels it, as a tap off the card does.
            vm.OpenPaneCommand.Execute(SettingsPane.Folders);
            vm.AskRemoveFolderCommand.Execute(vm.FolderRows[0]);
            Assert.True(rig.Shell.TryHandleBack());
            Assert.False(rig.Shell.IsPromptOpen);
            Assert.False(vm.IsConfirmingRemoveFolder);
            Assert.Equal(SettingsPane.Folders, vm.Pane);   // Back took the question, not the page
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    /// <summary>
    /// Bug: removing a folder gives up the phone's access to it, but its songs stayed in the queue.
    /// Each one then failed in turn with "Noctis lost access to this song's folder. Add the folder
    /// again", until five in a row stopped playback. They leave the queue with the folder now.
    /// </summary>
    [AvaloniaFact]
    public async Task Settings_RemovingAFolder_TakesItsSongsOutOfTheQueue()
    {
        Track In(string root, string name) => new()
        {
            Id = Guid.NewGuid(), Title = name, Duration = TimeSpan.FromSeconds(90),
            FilePath = root + "/document/" + Uri.EscapeDataString("primary:Music/" + name),
        };
        var keep = new[] { In(Beats, "b1"), In(Beats, "b2") };
        var gone = new[] { In(Tones, "t1"), In(Tones, "t2") };
        using var rig = MobileFixtures.MakeRig(new[] { keep[0], gone[0], keep[1], gone[1] }, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.AddRange(new[] { Tones, Beats });
            await p.SaveSettingsAsync(s);
        });
        var player = rig.Shell.Player;
        player.PlayTracks(new[] { keep[0], gone[0], keep[1], gone[1] }, 0);

        rig.Shell.OpenSettingsCommand.Execute(null);
        var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await vm.Loaded;
        vm.AskRemoveFolderCommand.Execute(vm.FolderRows.Single(r => r.Root == Tones));
        await rig.Shell.Prompt!.Actions[0].RunCommand.ExecuteAsync(null);

        Assert.Same(keep[0], player.CurrentTrack);
        Assert.True(player.IsPlaying);
        Assert.Equal(new[] { keep[1] }, player.UpNext);
    }

    /// <summary>Rescan Library is one accent row on the top level. With no folder it is greyed,
    /// and its note says why (it used to do nothing on a tap); with folders it is live and quiet.</summary>
    [AvaloniaFact]
    public async Task Settings_Rescan_IsGreyedWithANote_UntilThereIsAFolder()
    {
        using (var empty = MobileFixtures.MakeRig())
        {
            await WithMountedSettings(empty, (page, _, _) =>
            {
                var rescan = MobileFixtures.Named<Button>(page, "RescanButton");
                Assert.False(rescan.IsEffectivelyEnabled);
                // Greyed, not grey-filled: Fluent's disabled presenter fill painted the card grey.
                var presenter = rescan.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .First(p => p.Name == "PART_ContentPresenter");
                Assert.True(presenter.Background is null || ((ISolidColorBrush)presenter.Background).Color.A == 0,
                    $"disabled row filled {presenter.Background}");
                Assert.Equal("Add a music folder to scan it.", MobileFixtures.Named<TextBlock>(page, "ScanStatusLabel").Text);
                Assert.True(MobileFixtures.Named<TextBlock>(page, "ScanStatusLabel").IsEffectivelyVisible);
                Assert.Equal("None", MobileFixtures.Named<TextBlock>(page, "FoldersValueLabel").Text);
                Assert.False(MobileFixtures.Named<Avalonia.Controls.Shapes.Ellipse>(page, "FolderWarningDot").IsEffectivelyVisible);
            });
        }

        using var rig = await RigWithFolders(null, Tones);
        await WithMountedSettings(rig, (page, _, _) =>
        {
            var rescan = MobileFixtures.Named<Button>(page, "RescanButton");
            Assert.True(rescan.IsEffectivelyEnabled);
            Assert.Contains(rescan.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Rescan Library");
            Assert.False(MobileFixtures.Named<TextBlock>(page, "ScanStatusLabel").IsEffectivelyVisible);
        });
    }

    /// <summary>The Theme row opens Settings › Theme inside the sheet (beta feedback 11-04): Match
    /// Device, then Light and the four dark styles, then the colour themes, the one in use ticked.
    /// A tap applies at once; Light turns Match Device off, a dark theme becomes the dark side.</summary>
    [AvaloniaFact]
    public async Task Settings_ThemePage_AppliesTheLook_AndTicksTheThemeInUse()
    {
        var host = new FakeThemeHost();
        using var rig = MobileFixtures.MakeRig(theme: host);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            var row = MobileFixtures.Named<Button>(page, "ThemeRow");
            row.Command!.Execute(row.CommandParameter);
            window.UpdateLayout();
            Assert.Equal(SettingsPane.Theme, vm.Pane);
            Assert.Equal("Theme", vm.HeaderTitle);
            Assert.True(MobileFixtures.Named<Avalonia.Controls.Primitives.ToggleButton>(page, "MatchDeviceSwitch").IsChecked);
            Assert.Equal(new[] { "Light", "Ink", "Smoke", "Dark", "Midnight" }, vm.StandardThemes.Select(t => t.Name));
            Assert.Equal(12, vm.ColourThemes.Count);
            Assert.Contains(vm.ColourThemes, t => t.Name == "Forest" && t.HasAccent);
            Assert.Equal(new[] { "Ink" }, vm.StandardThemes.Concat(vm.ColourThemes).Where(t => t.IsSelected).Select(t => t.Id));
            var list = MobileFixtures.Named<ItemsControl>(page, "ColourThemeList");
            Assert.Equal(12, list.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("row")));

            vm.ChooseThemeCommand.Execute(vm.StandardThemes[0]);          // Light
            Assert.Equal(("Light", "Ink", "#E74856"), host.Last);
            Assert.False(vm.MatchDevice);
            Assert.Equal("Theme, Light", vm.ThemeRowName);
            Assert.Equal(new[] { "Light" }, vm.StandardThemes.Concat(vm.ColourThemes).Where(t => t.IsSelected).Select(t => t.Id));

            var forest = vm.ColourThemes.Single(t => t.Id == "forest");
            vm.ChooseThemeCommand.Execute(forest);                        // a dark theme: shows now
            Assert.Equal(("Dark", "forest", "#E74856"), host.Last);
            Assert.Equal("Theme, Forest", vm.ThemeRowName);
            Assert.True(forest.IsSelected);
            Assert.Equal("Match Theme", vm.AccentName);                   // a colour theme's accent, Match Theme on by default

            vm.MatchDevice = true;
            Assert.Equal(("System", "forest", "#E74856"), host.Last);
            Assert.Equal("Theme, System, Forest", vm.ThemeRowName);
            Assert.True(forest.IsSelected);
        });
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).PendingSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("System", saved.MobileAppearance);
        Assert.Equal("forest", saved.MobileThemeName);
    }

    /// <summary>The profile card's grey line says when the phone last synced, in words.</summary>
    [Fact]
    public void SyncedText_ReadsAsRelativeTime()
    {
        var now = new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("Not synced yet", AccountPageViewModel.SyncedText(null, now));
        Assert.Equal("Synced just now", AccountPageViewModel.SyncedText(now.AddSeconds(-20), now));
        Assert.Equal("Synced 5 min ago", AccountPageViewModel.SyncedText(now.AddMinutes(-5), now));
        Assert.Equal("Synced 3 hr ago", AccountPageViewModel.SyncedText(now.AddHours(-3), now));
        Assert.Equal("Synced yesterday", AccountPageViewModel.SyncedText(now.AddHours(-24), now));
        Assert.Equal("Synced 4 days ago", AccountPageViewModel.SyncedText(now.AddDays(-4), now));
        Assert.StartsWith("Synced ", AccountPageViewModel.SyncedText(now.AddDays(-30), now));
        Assert.DoesNotContain("ago", AccountPageViewModel.SyncedText(now.AddDays(-30), now));
    }

    /// <summary>The hairline under the header shows only once the page has scrolled under it.</summary>
    [AvaloniaFact]
    public async Task Settings_HeaderHairline_ShowsOnlyWhenScrolled()
    {
        using var rig = await RigWithFolders(null, Tones);
        await WithMountedSettings(rig, (page, _, window) =>
        {
            var line = MobileFixtures.Named<Border>(page, "HeaderHairline");
            var scroll = MobileFixtures.Named<ScrollViewer>(page, "MainScroll");
            window.Height = 500;                               // short enough to scroll
            window.UpdateLayout();
            Assert.Equal(0, line.Opacity);
            scroll.Offset = new Vector(0, 80);
            Settle(window, 250);
            Assert.Equal(1, line.Opacity);
            scroll.Offset = default;
            Settle(window, 250);
            Assert.Equal(0, line.Opacity);
        });
    }

    private sealed class FakeLinks : IUrlLauncher
    {
        public List<string> Opened { get; } = new();
        public bool Open(string url) { Opened.Add(url); return true; }
    }

    private static ShellViewModel ShellWithAccount(MobileFixtures.Rig rig, MobileAccountTests.FakeAccount? account, IUrlLauncher? links = null) =>
        new(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Links = links,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };

    /// <summary>The stats under the profile: the library's song count, then the downloads' count and
    /// bytes, read off the UI thread; they follow a download and a removal.</summary>
    [Fact]
    public async Task Stats_FillIn_FromTheLibraryAndTheDownloads_AndFollowThem()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("A"), MobileFixtures.Song("B") });
        await rig.Shell.Library.InitializeAsync();
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        for (var i = 0; i < 3; i++) account.DownloadedIds.Add(Guid.NewGuid());   // 5 MB each in the fake
        var page = new SettingsPageViewModel(ShellWithAccount(rig, account));
        await page.LoadAsync();
        await page.StatsLoaded;

        Assert.Equal("2", page.SongsStat);
        Assert.Equal("3", page.DownloadedStat);
        Assert.Equal("15 MB", page.OnPhoneStat);
        Assert.Equal("3 songs · 15 MB", page.StorageUsedText);
        Assert.True(page.CanRemoveDownloads);

        account.DownloadedIds.Add(Guid.NewGuid());
        account.RaiseStateChanged();
        await page.StatsLoaded;
        Assert.Equal("4", page.DownloadedStat);
        Assert.Equal("20 MB", page.OnPhoneStat);

        // No computer link: nothing downloaded, so the numbers are zero, not a dash.
        var bare = new SettingsPageViewModel(ShellWithAccount(rig, null));
        Assert.Equal("0", bare.DownloadedStat);
        Assert.Equal("0 MB", bare.OnPhoneStat);
        Assert.False(bare.CanRemoveDownloads);
        page.OnClosed();
        bare.OnClosed();
    }

    /// <summary>Settings → Downloads: the Wi-Fi switch is on by default, saved when flipped and read
    /// back by the next Settings; Remove All Downloads asks first, then empties the phone.</summary>
    [AvaloniaFact]
    public async Task Settings_DownloadsPage_WifiSwitchPersists_AndRemoveAllAsksFirst()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        account.DownloadedIds.Add(Guid.NewGuid());
        var shell = ShellWithAccount(rig, account);
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)shell.CurrentPage!;
            await vm.Loaded;
            await vm.StatsLoaded;
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);

            var row = MobileFixtures.Named<Button>(page, "DownloadsRow");
            Assert.True(row.IsEffectivelyVisible);
            Assert.Equal("Wi-Fi Only", MobileFixtures.Named<TextBlock>(page, "DownloadsValueLabel").Text);
            Assert.Equal("Downloads, Wi-Fi Only", Avalonia.Automation.AutomationProperties.GetName(row));
            Assert.True(MobileFixtures.Named<Avalonia.Controls.Primitives.ToggleButton>(page, "PickUpSwitch").IsEffectivelyVisible);
            Assert.Equal("Studio PC · Not synced yet", MobileFixtures.Named<TextBlock>(page, "AccountRowSub").Text);

            row.Command!.Execute(row.CommandParameter);
            Settle(window);
            var wifi = MobileFixtures.Named<Avalonia.Controls.Primitives.ToggleButton>(page, "DownloadWifiSwitch");
            Assert.True(wifi.IsEffectivelyVisible);
            Assert.True(wifi.IsChecked);
            Assert.Equal("5 MB", vm.OnPhoneStat);
            Assert.Equal("1 song · 5 MB", MobileFixtures.Named<TextBlock>(page, "StorageUsedLabel").Text);

            wifi.IsChecked = false;
            await vm.PendingWifiSave;
            Assert.False((await rig.Persistence.LoadSettingsAsync()).MobileDownloadWifiOnly);
            Assert.Equal("Any Network", vm.DownloadsValue);

            var remove = MobileFixtures.Named<Button>(page, "RemoveDownloadsButton");
            Assert.True(remove.IsEffectivelyVisible);
            remove.Command!.Execute(remove.CommandParameter);
            var prompt = shell.Prompt!;
            Assert.Equal("Remove all downloads?", prompt.Title);
            Assert.Equal(0, account.RemoveAllCalls);                      // asked, not yet done
            await prompt.Actions[0].RunCommand.ExecuteAsync(null);
            Assert.Equal(1, account.RemoveAllCalls);
            await vm.StatsLoaded;
            Assert.Equal("0", vm.DownloadedStat);
            Assert.False(vm.CanRemoveDownloads);

            // The next Settings reads the saved switch.
            var again = new SettingsPageViewModel(shell);
            await again.LoadAsync();
            Assert.False(again.DownloadWifiOnly);
            again.OnClosed();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Help: Report a Problem opens the GitHub new-issue page and Noctis Website the site,
    /// through the head's launcher.</summary>
    [Fact]
    public async Task HelpRows_OpenTheIssuePageAndTheWebsite()
    {
        using var rig = MobileFixtures.MakeRig();
        var links = new FakeLinks();
        var page = await OpenSettings(ShellWithAccount(rig, null, links));
        page.ReportProblemCommand.Execute(null);
        page.OpenWebsiteCommand.Execute(null);
        page.OpenPrivacyPolicyCommand.Execute(null);
        Assert.Equal(new[] { "https://github.com/heartached/Noctis/issues/new", "https://noctisapp.cc/", "https://noctisapp.cc/privacy/android" }, links.Opened);
    }

    /// <summary>About: What's New and Open-Source Licenses have content; the licences name the
    /// bundled LGPL FFmpeg decoder first, with the licence it comes under.</summary>
    [AvaloniaFact]
    public async Task AboutPages_ListWhatsNew_AndTheOpenSourceLicenses()
    {
        using var rig = MobileFixtures.MakeRig();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { VersionText = "Noctis 1.7.0", TintFactory = rig.Shell.TintFactory };
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)shell.CurrentPage!;
            await vm.Loaded;
            Assert.Equal("1.7.0", vm.VersionValue);
            Assert.Equal("Version, 1.7.0", vm.VersionRowName);
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);

            vm.OpenPaneCommand.Execute(SettingsPane.WhatsNew);
            Settle(window);
            Assert.Equal("Noctis 1.7.0", MobileFixtures.Named<TextBlock>(page, "WhatsNewVersion").Text);
            Assert.Equal(vm.WhatsNewItems.Count, MobileFixtures.Named<ItemsControl>(page, "WhatsNewList").GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible));

            vm.OpenPaneCommand.Execute(SettingsPane.Licenses);
            Settle(window);
            var list = MobileFixtures.Named<ItemsControl>(page, "LicenseList");
            var texts = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("LGPL-2.1-or-later", texts);
            Assert.Contains("FFmpeg (libavcodec, libavutil, libswresample)", texts);
            Assert.Contains("Avalonia", texts);
            Assert.True(vm.Licenses.Count >= 8);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Rescan Library's grey line: when the last scan finished (today, yesterday, a date),
    /// else what the row does.</summary>
    [Fact]
    public void RescanSubtitle_SaysWhenTheLastScanFinished()
    {
        var now = new DateTime(2026, 10, 28, 15, 0, 0, DateTimeKind.Local);
        Assert.StartsWith("today, ", SettingsPageViewModel.ScanTime(now.AddHours(-3), now));
        Assert.StartsWith("yesterday, ", SettingsPageViewModel.ScanTime(now.AddHours(-20), now));
        Assert.StartsWith("4 Oct, ", SettingsPageViewModel.ScanTime(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Local), now));
        using var rig = MobileFixtures.MakeRig();
        var page = new SettingsPageViewModel(rig.Shell) { UtcNow = () => now.ToUniversalTime() };
        Assert.Equal("Looks for new songs in your folders", page.RescanSubtitle);
        rig.Shell.Library.LastScanLocal = now.AddMinutes(-18);
        Assert.StartsWith("Last scan today, ", page.RescanSubtitle);
        Assert.StartsWith("Rescan Library, Last scan today, ", page.RescanRowName);
        page.OnClosed();
    }

    /// <summary>Pick Up on Computer is the History page's switch (a stub property until its page
    /// lands); its note names the computer.</summary>
    [AvaloniaFact]
    public async Task PickUpOnComputer_IsASwitchOnTheListeningCard_NamingTheComputer()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = ShellWithAccount(rig, account);
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)shell.CurrentPage!;
            await vm.Loaded;
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);
            var toggle = MobileFixtures.Named<Avalonia.Controls.Primitives.ToggleButton>(page, "PickUpSwitch");
            Assert.False(toggle.IsChecked);
            toggle.IsChecked = true;
            Assert.True(vm.History.PickUpOnComputer);
            Assert.Equal("Carry on from this song when you open Noctis on Studio PC",
                MobileFixtures.Named<TextBlock>(page, "PickUpFootnoteLabel").Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Live: signed out (the account service there, no account) the switch still showed.</summary>
    [AvaloniaFact]
    public async Task PickUpOnComputer_IsHiddenWhileSignedOut_AndShowsOnSignIn()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new MobileAccountTests.FakeAccount();
        var shell = ShellWithAccount(rig, account);
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)shell.CurrentPage!;
            await vm.Loaded;
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);
            Assert.True(shell.HasAccount);
            Assert.False(MobileFixtures.Named<StackPanel>(page, "PickUpGroup").IsVisible);
            Assert.False(MobileFixtures.Named<TextBlock>(page, "PickUpFootnoteLabel").IsVisible);

            account.Account = MobileAccountTests.FakeAccount.SignedInAs();
            account.RaiseStateChanged();
            window.UpdateLayout();
            Assert.True(MobileFixtures.Named<StackPanel>(page, "PickUpGroup").IsVisible);
            Assert.True(MobileFixtures.Named<TextBlock>(page, "PickUpFootnoteLabel").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The header's small title is invisible on the top level until the large one has
    /// scrolled away; a pushed page names itself at once.</summary>
    [AvaloniaFact]
    public async Task Settings_SmallTitle_FadesInOnceTheLargeOneScrollsAway()
    {
        using var rig = await RigWithFolders(null, Tones);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            var small = MobileFixtures.Named<TextBlock>(page, "SettingsTitle");
            var scroll = MobileFixtures.Named<ScrollViewer>(page, "MainScroll");
            window.Height = 500;
            window.UpdateLayout();
            Assert.Equal(0, small.Opacity);
            scroll.Offset = new Vector(0, SettingsPage.TitleCollapseOffset + 20);
            Settle(window, 250);
            Assert.Equal(1, small.Opacity);
            scroll.Offset = default;
            Settle(window, 250);
            Assert.Equal(0, small.Opacity);
            vm.Pane = SettingsPane.Accent;
            Settle(window);
            Assert.Equal(1, small.Opacity);
        });
    }
}

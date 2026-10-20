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

    /// <summary>Owner 10-13: Appearance's Player Bar card switches the mini player's progress
    /// colour and line, both off by default; a switch acts on the shell at once and is saved.</summary>
    [AvaloniaFact]
    public async Task Settings_Appearance_PlayerBarSwitches_DriveTheMiniPlayer()
    {
        using var rig = await RigWithFolders(null, Tones);
        await rig.Shell.InitializeAsync();
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            vm.OpenPaneCommand.Execute(SettingsPane.Appearance);
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

    /// <summary>Owner 10-13, "cluttered, very AI": the top level is Apple's account sheet. No
    /// section heads, no header badge; three plain rows (no icons) into the pages; the only
    /// accent rows are the two actions; the version is a small footer, not a card. Every card
    /// keeps its 24 dp corners, and the gapless switch stays gone (the phone is always gapless).</summary>
    [AvaloniaFact]
    public async Task Settings_TopLevel_IsCalm_PlainRowsIntoPages_AndOnlyActionsInTheAccent()
    {
        using var rig = await RigWithFolders(null, Tones);
        await WithMountedSettings(rig, (page, _, _) =>
        {
            var visible = page.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.DoesNotContain(visible, c => c.Classes.Contains("group-head") || c.Classes.Contains("sheet-glyph"));
            Assert.Equal("Settings", MobileFixtures.Named<TextBlock>(page, "SettingsTitle").Text);
            Assert.False(MobileFixtures.Named<Button>(page, "BackButton").IsVisible);

            foreach (var (name, label) in new[] { ("FoldersRow", "Music Folders"), ("AppearanceRow", "Appearance"), ("LyricsRow", "Lyrics") })
            {
                var row = MobileFixtures.Named<Button>(page, name);
                Assert.DoesNotContain("accent-row", row.Classes);
                Assert.Equal(label, row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("row-label")).Text);
                // The chevron is the row's only icon.
                Assert.Equal(new[] { "row-chevron" }, row.GetVisualDescendants().OfType<GridIcon>().Select(i => i.Classes.Single()));
            }

            var accentRows = visible.OfType<Button>().Where(b => b.Classes.Contains("accent-row")).Select(b => b.Name).ToList();
            Assert.Equal(new[] { "RescanButton", "ExportLogsButton" }, accentRows);
            // Icons on the top level: the actions' own and the chevrons, nothing else.
            Assert.All(visible.OfType<GridIcon>().Where(i => i.Classes.Contains("row-icon")),
                i => Assert.Contains(i.GetVisualAncestors().OfType<Button>().First().Name, accentRows));

            Assert.All(visible.OfType<Border>().Where(b => b.Classes.Contains("group-card")), c => Assert.Equal(24, c.CornerRadius.TopLeft));
            var version = MobileFixtures.Named<TextBlock>(page, "VersionLabel");
            Assert.True(version.IsEffectivelyVisible);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, version.HorizontalAlignment);
            Assert.DoesNotContain(visible, c => c.Name is "AboutCard" or "GaplessSwitch");
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Gapless", StringComparison.OrdinalIgnoreCase) == true);
        });
    }

    /// <summary>Each row into a page carries its value in grey: how many folders (a red dot when
    /// one lost its access), the dark style and accent, which lyric lines show. They follow changes.</summary>
    [AvaloniaFact]
    public async Task Settings_PageRows_ShowTheirValues_AndFollowChanges()
    {
        var picker = new MobileFixtures.NoPicker();
        picker.Lost.Add(Beats);
        using var rig = await RigWithFolders(picker, Tones, Beats);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            Assert.Equal("2", MobileFixtures.Named<TextBlock>(page, "FoldersValueLabel").Text);
            Assert.True(MobileFixtures.Named<Avalonia.Controls.Shapes.Ellipse>(page, "FolderWarningDot").IsEffectivelyVisible);
            Assert.Equal("Ink · Crimson", MobileFixtures.Named<TextBlock>(page, "AppearanceValueLabel").Text);
            Assert.Equal("Translations, Romanization", MobileFixtures.Named<TextBlock>(page, "LyricsValueLabel").Text);

            vm.DarkTheme = "Midnight";
            vm.SelectedAccent = SettingsPageViewModel.Accents.Single(a => a.Name == "Emerald");
            rig.Shell.Lyrics.ShowRomanization = false;
            window.UpdateLayout();
            Assert.Equal("Midnight · Emerald", MobileFixtures.Named<TextBlock>(page, "AppearanceValueLabel").Text);
            Assert.Equal("Translations on", MobileFixtures.Named<TextBlock>(page, "LyricsValueLabel").Text);

            vm.Appearance = "Light";                         // no dark style under Light: just the accent
            window.UpdateLayout();
            Assert.Equal("Emerald", MobileFixtures.Named<TextBlock>(page, "AppearanceValueLabel").Text);
        });
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).PendingSave;
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
                         ("AppearanceRow", "AppearancePane", "Appearance"),
                         ("LyricsRow", "LyricsPane", "Lyrics"),
                     })
            {
                var button = MobileFixtures.Named<Button>(page, row);
                button.Command!.Execute(button.CommandParameter);
                Settle(window);
                Assert.Equal(heading, title.Text);
                Assert.True(back.IsEffectivelyVisible);
                Assert.True(MobileFixtures.Named<StackPanel>(page, pane).IsEffectivelyVisible);
                Assert.False(MobileFixtures.Named<ScrollViewer>(page, "MainScroll").IsEffectivelyVisible);
                // The page sits fully in place once its slide ends.
                Assert.Equal(0, MobileFixtures.Named<ScrollViewer>(page, "SubScroll").TranslatePoint(new Point(0, 0), page)!.Value.X, 1);

                back.Command!.Execute(back.CommandParameter);
                Settle(window);
                Assert.Equal("Settings", title.Text);
                Assert.True(MobileFixtures.Named<ScrollViewer>(page, "MainScroll").IsEffectivelyVisible);
                Assert.False(MobileFixtures.Named<ScrollViewer>(page, "SubScroll").IsEffectivelyVisible);
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
            foreach (var pane in new[] { SettingsPane.Main, SettingsPane.Folders, SettingsPane.Appearance, SettingsPane.Lyrics })
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
            Assert.Equal(1, icons.Count);                    // Add Folder; Rescan Library and Export Logs are text only
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
            foreach (var pane in new[] { SettingsPane.Main, SettingsPane.Folders, SettingsPane.Appearance, SettingsPane.Lyrics })
            {
                vm.Pane = pane;
                Settle(window);
                lines.AddRange(page.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("row-separator") && b.IsEffectivelyVisible));
            }
            // 2 on the top level, 1 between two folders, 3 between four dark styles and 1 between
            // the Player Bar's two switches, 3 in Lyrics (two between its three switches, one
            // between its two sliders): none above a list's first row.
            Assert.Equal(10, lines.Count);
            Assert.All(lines, line =>
            {
                var card = line.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("group-card"));
                var left = line.TranslatePoint(new Point(0, 0), card)!.Value.X;
                Assert.Equal(16, left, 1);
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

    /// <summary>The Look tiles (System / Light / Dark) apply at a tap and mark the chosen one.
    /// On Appearance, Dark Style is a check list; under Light it is greyed with a note saying
    /// why, rather than vanishing.</summary>
    [AvaloniaFact]
    public async Task Settings_LookTiles_Apply_AndDarkStyleGreysUnderLight()
    {
        var host = new FakeThemeHost();
        using var rig = MobileFixtures.MakeRig(theme: host);
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            var tiles = new[] { "LookSystem", "LookLight", "LookDark" }.Select(n => MobileFixtures.Named<Button>(page, n)).ToList();
            Assert.Equal(new[] { true, false, false }, tiles.Select(t => t.Classes.Contains("selected")));

            // Each preview's page and its two cards sit wholly inside the rim (they were cut at
            // the right and bottom edges when the page was sized as the rim's outside).
            Assert.All(tiles, tile =>
            {
                var rim = tile.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("look-preview"));
                var inside = new Rect(rim.Bounds.Size).Deflate(rim.BorderThickness);
                var shapes = rim.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Background != null).ToList();
                Assert.NotEmpty(shapes);
                Assert.All(shapes, b =>
                {
                    var r = new Rect(b.TranslatePoint(new Point(0, 0), rim)!.Value, b.Bounds.Size);
                    Assert.True(inside.Inflate(0.01).Contains(r), $"{tile.Name}: {r} outside {inside}");
                });
            });

            tiles[2].Command!.Execute(tiles[2].CommandParameter);
            window.UpdateLayout();
            Assert.Equal(("Dark", "Ink", "#E74856"), host.Last);
            Assert.Equal(new[] { false, false, true }, tiles.Select(t => t.Classes.Contains("selected")));

            vm.OpenPaneCommand.Execute(SettingsPane.Appearance);
            Settle(window);
            var styles = MobileFixtures.Named<ListBox>(page, "DarkThemeList");
            Assert.Equal(new[] { "Ink", "Smoke", "Dark", "Midnight" }, styles.Items.Cast<string>());
            Assert.True(styles.IsEffectivelyEnabled);
            styles.SelectedItem = "Smoke";
            Assert.Equal(("Dark", "Smoke", "#E74856"), host.Last);
            // The chosen style's checkmark is the only one showing.
            Assert.Equal(new[] { 0.0, 1.0, 0.0, 0.0 }, styles.GetVisualDescendants().OfType<GridIcon>().Where(i => i.Classes.Contains("check")).Select(i => i.Opacity));

            vm.ShowMainCommand.Execute(null);
            Settle(window);
            tiles[1].Command!.Execute(tiles[1].CommandParameter);
            vm.OpenPaneCommand.Execute(SettingsPane.Appearance);
            Settle(window);
            Assert.Equal(("Light", "Smoke", "#E74856"), host.Last);
            Assert.True(MobileFixtures.Named<StackPanel>(page, "DarkThemeSection").IsEffectivelyVisible);
            Assert.False(styles.IsEffectivelyEnabled);
            Assert.Equal("Dark Style applies when Look is System or Dark.", MobileFixtures.Named<TextBlock>(page, "DarkStyleFootnote").Text);
        });
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).PendingSave;
    }

    /// <summary>The Look tiles' previews paint the theme's own page and card colours: they must
    /// follow MobileTheme.BuildPalette, for Light and each dark style.</summary>
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("Ink")]
    [InlineData("Smoke")]
    [InlineData("Dark")]
    [InlineData("Midnight")]
    public void LookPreview_MatchesThePalette(string? darkTheme)
    {
        var palette = MobileTheme.BuildPalette(darkTheme);
        var variant = darkTheme == null ? ThemeVariant.Light : ThemeVariant.Dark;
        var keys = (ResourceDictionary)palette.ThemeDictionaries[variant];
        var preview = darkTheme == null ? LookPreview.Light : LookPreview.Dark(darkTheme);
        Color Of(string key) => ((ISolidColorBrush)keys[key]!).Color;
        Assert.Equal(Of("PhoneGroupedBackgroundBrush"), ((ISolidColorBrush)preview.Page).Color);
        Assert.Equal(Of("PhoneCardBrush"), ((ISolidColorBrush)preview.Card).Color);
        Assert.Equal(Of("PhoneSeparatorBrush"), ((ISolidColorBrush)preview.Line).Color);
    }

    /// <summary>The profile card's grey line says when the phone last synced, in words.</summary>
    [Fact]
    public void SyncedText_ReadsAsRelativeTime()
    {
        var now = new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("not synced yet", SettingsPageViewModel.SyncedText(null, now));
        Assert.Equal("synced just now", SettingsPageViewModel.SyncedText(now.AddSeconds(-20), now));
        Assert.Equal("synced 5 min ago", SettingsPageViewModel.SyncedText(now.AddMinutes(-5), now));
        Assert.Equal("synced 3 hr ago", SettingsPageViewModel.SyncedText(now.AddHours(-3), now));
        Assert.Equal("synced yesterday", SettingsPageViewModel.SyncedText(now.AddHours(-24), now));
        Assert.Equal("synced 4 days ago", SettingsPageViewModel.SyncedText(now.AddDays(-4), now));
        Assert.StartsWith("synced ", SettingsPageViewModel.SyncedText(now.AddDays(-30), now));
        Assert.DoesNotContain("ago", SettingsPageViewModel.SyncedText(now.AddDays(-30), now));
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
}

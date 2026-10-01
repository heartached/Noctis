using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
        public Task<bool> ExportAsync(string suggestedFileName, string text)
        {
            FileName = suggestedFileName;
            Text = text;
            return Task.FromResult(true);
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
    public async Task Settings_GaplessAndLyricsTextSize_ApplyAndPersist()
    {
        using var rig = MobileFixtures.MakeRig();
        var page = await OpenSettings(rig.Shell);

        page.Gapless = false;
        page.LyricsTextScale = 1.4;
        await page.PendingSave;

        Assert.False(rig.Player.GaplessEnabled);
        Assert.Equal(LyricsPageViewModel.BaseFontSize * 1.4, rig.Shell.Lyrics.LineFontSize, 6);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.False(saved.GaplessPlaybackEnabled);
        Assert.Equal(1.4, saved.MobileLyricsTextScale, 6);

        var fresh = new LyricsPageViewModel(rig.Player, rig.Shell.Player, new FakeTrackFiles(), rig.Persistence, work => Task.FromResult(work()));
        await fresh.InitializeAsync();
        Assert.Equal(1.4, fresh.TextScale, 6);
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

        var profile = MobileFixtures.Named<Button>(view, "LibraryProfileButton");
        profile.Command!.Execute(profile.CommandParameter);
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).Loaded;
        window.UpdateLayout();

        var page = MobileFixtures.Find<SettingsPage>(view);
        Assert.Equal(new[] { "Music/Tones" }, ((SettingsPageViewModel)page.DataContext!).FolderLabels);
        Assert.Contains(MobileFixtures.Named<ItemsControl>(page, "FolderList").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Music/Tones");
        Assert.True(MobileFixtures.Named<Button>(page, "ExportLogsButton").IsEffectivelyVisible);
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
            Assert.Equal(ThemeVariant.Dark, MobileFixtures.Named<Slider>(nowPlaying, "SeekBar").ActualThemeVariant);
            Assert.Equal(ThemeVariant.Dark, MobileFixtures.Find<LyricsPage>(view).ActualThemeVariant);
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = before;
        }
    }
}

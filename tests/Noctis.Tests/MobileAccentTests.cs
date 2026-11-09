using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Accent › Match Artwork (beta feedback 11-07): the accent follows the playing cover's
/// colour, tamed like the desktop's "Accent follows album art" and guarded per theme like any
/// accent, blended on a track change, and the chosen colour when nothing plays or the cover is grey.
/// </summary>
public class MobileAccentTests
{
    private sealed class RecordingHost : IThemeHost
    {
        public (string Appearance, string DarkTheme, string Accent, bool FollowsTheme, bool FollowsArtwork)? Last { get; private set; }
        public List<Color?> Covers { get; } = new();
        public void ApplyTheme(string appearance, string darkTheme, string accentHex) => Last = (appearance, darkTheme, accentHex, true, false);
        public void ApplyTheme(string appearance, string darkTheme, string accentHex, bool accentFollowsTheme, bool accentFollowsArtwork) =>
            Last = (appearance, darkTheme, accentHex, accentFollowsTheme, accentFollowsArtwork);
        public void CoverColourChanged(Color? cover) => Covers.Add(cover);
    }

    private static readonly Color DeepRed = Color.FromRgb(0x7A, 0x12, 0x14);

    [Fact]
    public void EffectiveAccent_TakesTheTamedCover_ElseTheChosenOne()
    {
        // A coloured cover: the desktop's taming (mid lightness, real chroma), not the raw colour.
        var tamed = Noctis.Helpers.ArtworkAccent.TameForAccent("#7A1214");
        Assert.NotNull(tamed);
        Assert.Equal(tamed, PhoneThemes.EffectiveAccent("Ink", "#2A7FCF", false, true, DeepRed));
        // Off, no cover, or a grey cover: the chosen colour.
        Assert.Equal("#2A7FCF", PhoneThemes.EffectiveAccent("Ink", "#2A7FCF", false, false, DeepRed));
        Assert.Equal("#2A7FCF", PhoneThemes.EffectiveAccent("Ink", "#2A7FCF", false, true, null));
        Assert.Equal("#2A7FCF", PhoneThemes.EffectiveAccent("Ink", "#2A7FCF", false, true, Color.FromRgb(0x80, 0x80, 0x82)));
        // Match Theme on a colour theme still wins; on Ink (no accent of its own) the cover does.
        Assert.Equal(PhoneThemes.EffectiveAccent("forest", "#2A7FCF", true), PhoneThemes.EffectiveAccent("forest", "#2A7FCF", true, true, DeepRed));
        Assert.Equal(tamed, PhoneThemes.EffectiveAccent("Ink", "#2A7FCF", true, true, DeepRed));
    }

    /// <summary>The Match Artwork swatch's white note and tick read on every colour of its wheel.</summary>
    [Fact]
    public void ArtworkSwatch_KeepsItsWhiteGlyphReadable_OnEveryStop()
    {
        var wheel = Assert.IsAssignableFrom<IConicGradientBrush>(AccentSwatch.ArtworkBrush);
        Assert.All(wheel.GradientStops, stop =>
            Assert.True(MobileTheme.ContrastRatio(Colors.White, stop.Color) >= MobileTheme.MinAccentContrast, stop.Color.ToString()));
        Assert.True(AccentSwatch.Artwork.IsArtwork);
        Assert.Same(AccentSwatch.ArtworkBrush, AccentSwatch.Artwork.Brush);
    }

    /// <summary>Any cover colour, on every theme: the tamed accent keeps every floor the presets
    /// keep (EveryThemeAndAccent_KeepsItsContrast), through the same guards.</summary>
    [AvaloniaFact]
    public void ArtworkAccents_KeepTheirContrast_OnEveryTheme()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        var failures = new List<string>();
        var covers = new List<Color>();
        for (var hue = 0; hue < 360; hue += 30)
        foreach (var (s, l) in new[] { (0.9, 0.15), (0.6, 0.5), (0.4, 0.85), (1.0, 0.5) })
            covers.Add(new HslColor(1, hue, s, l).ToRgb());
        try
        {
            foreach (var name in PhoneThemes.All.Select(t => t.Id))
            {
                var variant = name == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                foreach (var cover in covers)
                {
                    if (PhoneThemes.ArtworkAccentHex(cover) is not { } hex) continue;
                    theme.Apply(app, name, hex);
                    Color Get(string key)
                    {
                        Assert.True(app.TryGetResource(key, variant, out var value), $"{name}: no {key}");
                        return value is Color c ? c : ((ISolidColorBrush)value!).Color;
                    }
                    foreach (var k in new[] { "PhoneBackgroundBrush", "PhoneGroupedBackgroundBrush", "PhoneCardBrush" })
                    foreach (var fg in new[] { "AccentTextBrush", "AccentTextExactBrush" })
                    {
                        var ratio = MobileTheme.ContrastRatio(Get(fg), Get(k));
                        if (ratio < MobileTheme.MinAccentContrast) failures.Add($"{name}/{hex}: {fg} on {k} {ratio:0.00}");
                    }
                    var onFill = MobileTheme.ContrastRatio(Get("AccentForegroundBrush"), Get("AccentColorBrush"));
                    if (onFill < MobileTheme.MinAccentContrast) failures.Add($"{name}/{hex}: glyph on fill {onFill:0.00}");
                }
            }
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>A track change recolours the accent alone, gliding: the brushes on screen move
    /// from the old colour to the new one in place, then the new overlay takes the old one's slot
    /// (one overlay, at the same place), its resources exactly a fresh Apply's.</summary>
    [AvaloniaFact]
    public void SetAccent_BlendsInPlace_ThenLandsOnTheGuardedOverlay()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        try
        {
            theme.Apply(app, "Light", MobileTheme.DefaultAccent);
            var count = app.Resources.MergedDictionaries.Count;
            Assert.True(app.TryGetResource("AccentColorBrush", ThemeVariant.Light, out var o));
            var onScreen = (SolidColorBrush)o!;
            var text = (SolidColorBrush)Resource(app, "AccentTextExactBrush", ThemeVariant.Light);
            var from = onScreen.Color;
            var textFrom = text.Color;
            var target = Color.Parse("#12C76F");

            theme.SetAccent(app, "#12C76F", TimeSpan.FromSeconds(10));
            Assert.True(theme.IsBlending);
            Assert.Same(onScreen, Resource(app, "AccentColorBrush", ThemeVariant.Light));   // nothing re-resolved yet
            theme.AdvanceBlend(0.5);
            Assert.NotEqual(from, onScreen.Color);
            Assert.NotEqual(target, onScreen.Color);
            Assert.InRange(onScreen.Color.G, Math.Min(from.G, target.G), Math.Max(from.G, target.G));
            // The per-variant (guarded) text brush moves too.
            Assert.NotEqual(textFrom, text.Color);

            theme.FinishBlend();
            Assert.False(theme.IsBlending);
            Assert.Equal(count, app.Resources.MergedDictionaries.Count);
            Assert.Equal(target, ((ISolidColorBrush)Resource(app, "AccentColorBrush", ThemeVariant.Light)).Color);

            // Exactly what a fresh Apply builds, guards included (Emerald text darkened on Light).
            var reference = new MobileTheme();
            var landed = Keys.ToDictionary(k => k, k => Colour(Resource(app, k, ThemeVariant.Light)));
            theme.Remove(app);
            reference.Apply(app, "Light", "#12C76F");
            foreach (var k in Keys) Assert.Equal(Colour(Resource(app, k, ThemeVariant.Light)), landed[k]);
            reference.Remove(app);
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
    }

    /// <summary>A second change mid-blend glides on from where the colours are, and a full re-theme
    /// cancels a blend under way.</summary>
    [AvaloniaFact]
    public void SetAccent_MidBlend_ContinuesFromTheColoursOnScreen()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        try
        {
            theme.Apply(app, "Ink", MobileTheme.DefaultAccent);
            var onScreen = (SolidColorBrush)Resource(app, "AccentColorBrush", ThemeVariant.Dark);
            theme.SetAccent(app, "#2A7FCF", TimeSpan.FromSeconds(10));
            theme.AdvanceBlend(0.5);
            var midway = onScreen.Color;
            theme.SetAccent(app, "#F4D24B", TimeSpan.FromSeconds(10));
            theme.AdvanceBlend(0);
            Assert.Equal(midway, onScreen.Color);   // no jump back to Crimson or on to blue
            theme.Apply(app, "Ink", "#C45CE0");
            Assert.False(theme.IsBlending);
            Assert.Equal(Color.Parse("#C45CE0"), ((ISolidColorBrush)Resource(app, "AccentColorBrush", ThemeVariant.Dark)).Color);
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
    }

    /// <summary>Under Match Artwork the shell hands the host each playing cover's vibrant colour
    /// (the desktop's "Accent follows album art" source), not the mini player's edge tint, and
    /// null when the song has none. Off, no cover is decoded for it; turned on mid-song, the
    /// playing cover's colour goes at once.</summary>
    [AvaloniaFact]
    public void Shell_HandsThePlayingCoversVibrantColour_ToTheThemeHost_OnlyUnderMatchArtwork()
    {
        var cover = Path.Combine(Path.GetTempPath(), $"noctis-accent-cover-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(cover, new byte[] { 1, 2, 3 });
        try
        {
            var withCover = MobileFixtures.Song("Red");
            withCover.AlbumArtworkPath = cover;
            var without = MobileFixtures.Song("Plain");
            var host = new RecordingHost();
            var decodes = new List<string>();
            using var rig = MobileFixtures.MakeRig(new[] { withCover, without }, theme: host,
                // The edge tint (a black frame) differs from the vibrant colour: only the latter may reach the host.
                tint: () => new PageTint(_ => Colors.Black, work => Task.FromResult(work())),
                artworkAccent: path => { decodes.Add(path); return "#1E6BFF"; });
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
            Assert.Empty(decodes);
            Assert.All(host.Covers, c => Assert.Null(c));
            // The mini player (and the Now Playing background) keep the edge tint.
            Assert.Equal(Colors.Black, rig.Shell.MiniTint.TintColor);

            rig.Shell.AccentFollowsArtwork = true;   // mid-song
            Assert.Equal(new[] { cover }, decodes);
            Assert.Equal(Color.Parse("#1E6BFF"), host.Covers.Last());

            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 1);
            Assert.Null(host.Covers.Last());
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
            Assert.Equal(Color.Parse("#1E6BFF"), host.Covers.Last());

            rig.Shell.AccentFollowsArtwork = false;
            Assert.Null(host.Covers.Last());   // no stale cover kept for the next time
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 1);
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
            Assert.Equal(2, decodes.Count);
        }
        finally
        {
            File.Delete(cover);
        }
    }

    /// <summary>A slow decode for an earlier song must not land over a later song's colour.</summary>
    [AvaloniaFact]
    public void Shell_ArtworkAccent_NewestTrackWins()
    {
        var a = MobileFixtures.Song("A");
        a.AlbumArtworkPath = "a.png";
        var b = MobileFixtures.Song("B");
        b.AlbumArtworkPath = "b.png";
        var host = new RecordingHost();
        var pending = new List<(string Hex, TaskCompletionSource<string?> Done)>();
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            TintFactory = () => new PageTint(_ => null, work => Task.FromResult(work())),
            Theme = host,
            ArtworkAccentSource = path => path == "a.png" ? "#E02020" : "#20C040",
            RunArtworkAccent = work =>
            {
                var done = new TaskCompletionSource<string?>();
                pending.Add((work()!, done));
                return done.Task;
            },
        };
        shell.AccentFollowsArtwork = true;
        shell.Player.PlayTracks(rig.Library.TrackList, 0);
        shell.Player.PlayTracks(rig.Library.TrackList, 1);
        Assert.Equal(new[] { "#E02020", "#20C040" }, pending.Select(p => p.Hex));
        var covers = host.Covers.Count;
        pending[1].Done.SetResult(pending[1].Hex);   // B lands first
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        pending[0].Done.SetResult(pending[0].Hex);   // then A's late result
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(covers + 1, host.Covers.Count);
        Assert.Equal(Color.Parse("#20C040"), host.Covers.Last());
    }

    /// <summary>
    /// The S23 bug (11-07): a cover on a black frame (10X &amp; 6ix9ine's "Tutu": a bright disc in a
    /// black case) has a black edge, which the accent's grey check turns down, so Match Artwork
    /// wore the fixed Crimson. The vibrant colour of its saturated centre is the accent, as on the
    /// desktop.
    /// </summary>
    [AvaloniaFact]
    public async Task BlackFramedCover_GivesTheCentresHue_NotTheFallback()
    {
        var centre = new SKColor(0x1E, 0x6B, 0xFF);
        var cover = FramedCover(SKColors.Black, centre);
        try
        {
            // The edge colour (what Match Artwork used to take) is black: no accent from it.
            Assert.Null(PhoneThemes.ArtworkAccentHex(Noctis.Services.DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(cover)));

            var song = MobileFixtures.Song("Framed");
            song.AlbumArtworkPath = cover;
            var host = new RecordingHost();
            using var rig = MobileFixtures.MakeRig(new[] { song }, theme: host,
                artworkAccent: Noctis.Services.ArtworkVibrantColor.GetAccentSourceHex);
            rig.Shell.AccentFollowsArtwork = true;
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
            await rig.Shell.ArtworkAccentReady;

            var accent = PhoneThemes.EffectiveAccent("Ink", MobileTheme.DefaultAccent, false, true, host.Covers.Last());
            Assert.NotEqual(MobileTheme.DefaultAccent, accent, StringComparer.OrdinalIgnoreCase);
            var hue = Color.Parse(accent).ToHsl().H;
            centre.ToHsl(out var centreHue, out _, out _);
            Assert.InRange(hue, centreHue - 8, centreHue + 8);
            // The desktop's "Accent follows album art" wears the same colour.
            Assert.Equal(Noctis.Services.ArtworkVibrantColor.GetAccentHex(cover), accent);
        }
        finally
        {
            File.Delete(cover);
        }
    }

    /// <summary>A grey cover keeps the chosen accent. The share card's background floor (30 %
    /// saturation, at hue 0 for a grey) tinted a grey cover's colour red, and the desktop's accent
    /// wore that red: the accent now tames the cover's own (grey) average.</summary>
    [Fact]
    public void GreyCover_KeepsTheChosenAccent()
    {
        var cover = FramedCover(new SKColor(0x30, 0x30, 0x30), new SKColor(0x9A, 0x9A, 0x9A));
        try
        {
            // The card background keeps its floor (unchanged), which an accent would wear...
            Assert.NotNull(Noctis.Helpers.ArtworkAccent.TameForAccent(Noctis.Services.ArtworkVibrantColor.GetHex(cover)));
            // ...the accent does not take it.
            Assert.Null(Noctis.Services.ArtworkVibrantColor.GetAccentHex(cover));
            var source = Noctis.Services.ArtworkVibrantColor.GetAccentSourceHex(cover);
            Assert.NotNull(source);
            Assert.Equal("#2A7FCF", PhoneThemes.EffectiveAccent("Ink", "#2A7FCF", false, true, Color.Parse(source!)));
            // No readable artwork: nothing to follow either.
            Assert.Null(Noctis.Services.ArtworkVibrantColor.GetAccentSourceHex(cover + ".missing"));
        }
        finally
        {
            File.Delete(cover);
        }
    }

    /// <summary>The share card's vibrant colour is the shared one (the desktop and the phone agree).</summary>
    [Fact]
    public void ShareCardVibrantColour_IsTheSharedOne()
    {
        var cover = FramedCover(SKColors.Black, new SKColor(0xE0, 0x40, 0x30));
        try
        {
            Assert.False(Noctis.Services.ShareCardRenderer.HasVibrantColor(cover));
            Assert.Equal(Noctis.Services.ArtworkVibrantColor.GetHex(cover), Noctis.Services.ShareCardRenderer.GetVibrantColorHex(cover));
            Assert.True(Noctis.Services.ShareCardRenderer.HasVibrantColor(cover));
            Assert.Equal(Noctis.Services.ArtworkVibrantColor.FallbackHex, Noctis.Services.ShareCardRenderer.GetVibrantColorHex(null));
        }
        finally
        {
            File.Delete(cover);
        }
    }

    /// <summary>A 300 px PNG: a 60 px <paramref name="frame"/> border around a <paramref name="centre"/> square.</summary>
    private static string FramedCover(SKColor frame, SKColor centre)
    {
        var path = Path.Combine(Path.GetTempPath(), $"noctis-framed-{Guid.NewGuid():N}.png");
        using var bmp = new SKBitmap(300, 300);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(frame);
            using var paint = new SKPaint { Color = centre };
            canvas.DrawRect(60, 60, 180, 180, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>Picking Match Artwork: the host is told, the chosen colour stays saved as the
    /// fallback, the row and the footnote say so; a colour turns it off; a reopen restores it.</summary>
    [AvaloniaFact]
    public async Task MatchArtwork_IsPicked_Saved_AndTurnedOffByAColour()
    {
        var host = new RecordingHost();
        using var rig = MobileFixtures.MakeRig(theme: host, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileAppearance = "Dark";
            s.MobileThemeName = "Ink";
            s.AccentColorHex = "#2A7FCF";
            s.AccentPresetName = "Cerulean";
            await p.SaveSettingsAsync(s);
        });
        rig.Shell.OpenSettingsCommand.Execute(null);
        var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await vm.Loaded;
        Assert.Equal(18, vm.AccentChoices.Count);
        Assert.Same(AccentSwatch.Artwork, vm.AccentChoices[^1]);

        vm.SelectedAccent = AccentSwatch.Artwork;
        Assert.Equal(("Dark", "Ink", "#2A7FCF", true, true), host.Last);
        Assert.True(vm.UsesArtworkAccent);
        Assert.Equal(Loc.T("Mobile.Accent.MatchArtwork"), vm.AccentName);
        Assert.Equal(Loc.T("Mobile.Accent.Cerulean"), vm.FixedAccentName);
        Assert.Same(AccentSwatch.ArtworkBrush, vm.AccentBrush);
        await vm.PendingSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.True(saved.MobileAccentFollowsArtwork);
        Assert.Equal("#2A7FCF", saved.AccentColorHex);
        Assert.Equal("Cerulean", saved.AccentPresetName);

        // Reopened: Match Artwork is the one ticked.
        var again = new SettingsPageViewModel(rig.Shell);
        await again.LoadAsync();
        Assert.Same(AccentSwatch.Artwork, again.SelectedAccent);
        Assert.True(again.UsesArtworkAccent);

        vm.SelectedAccent = SettingsPageViewModel.Accents.Single(a => a.Name == "Mint");
        Assert.Equal(("Dark", "Ink", "#8FE3C4", true, false), host.Last);
        Assert.False(vm.UsesArtworkAccent);
        await vm.PendingSave;
        saved = await rig.Persistence.LoadSettingsAsync();
        Assert.False(saved.MobileAccentFollowsArtwork);
        Assert.Equal("Mint", saved.AccentPresetName);
    }

    /// <summary>Match Artwork picked while a colour theme wears its own accent turns Match Theme
    /// off, as any swatch does: the pick is what the user wants to see.</summary>
    [AvaloniaFact]
    public async Task MatchArtwork_OnAColourTheme_TurnsMatchThemeOff()
    {
        var host = new RecordingHost();
        using var rig = MobileFixtures.MakeRig(theme: host, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileAppearance = "Dark";
            s.MobileThemeName = "forest";
            s.MobileAccentFollowsTheme = true;
            await p.SaveSettingsAsync(s);
        });
        rig.Shell.OpenSettingsCommand.Execute(null);
        var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await vm.Loaded;
        Assert.True(vm.UsesThemeAccent);
        vm.SelectedAccent = AccentSwatch.Artwork;
        Assert.False(vm.AccentFollowsTheme);
        Assert.True(vm.UsesArtworkAccent);
        Assert.Equal((false, true), (host.Last!.Value.FollowsTheme, host.Last!.Value.FollowsArtwork));
    }

    private static readonly string[] Keys =
    {
        "AccentColorBrush", "AccentForegroundBrush", "AccentTextBrush", "AccentTextExactBrush", "ToggleSwitchFillOn",
        "SystemAccentColor", "SliderThumbBackground",
    };

    private static object Resource(Application app, string key, ThemeVariant variant)
    {
        Assert.True(app.TryGetResource(key, variant, out var value), key);
        return value!;
    }

    private static Color Colour(object value) => value is Color c ? c : ((ISolidColorBrush)value).Color;
}

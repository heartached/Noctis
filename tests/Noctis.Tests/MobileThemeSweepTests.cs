using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-20: "make sure all of the themes are working correctly with all of the pages." Every
/// phone theme (Light and the four dark styles) over every page, sheet, menu and banner, with the
/// real resource stack (Noctis.UI's Styles and Icons, MobileTheme's overlay, palette and accent):
/// each visible label and glyph is read against what it is drawn on (its own and its ancestors'
/// fills, and the solid fills laid under it in its panels, composited over the page), and must
/// keep at least 3:1 - no white on white, no black on black, no accent that disappears. A label
/// over a picture, a gradient or the blurred glass backdrop is not judged here (the menu glass has
/// its own worst-case check in MobilePopMenuTests). Under NOCTIS_TEST_SKIA=1 with
/// NOCTIS_THEME_SHOTS=&lt;dir&gt; each screen is also saved as a PNG to eyeball.
/// </summary>
public class MobileThemeSweepTests
{
    private readonly ITestOutputHelper _o;
    public MobileThemeSweepTests(ITestOutputHelper o) => _o = o;

    /// <summary>Light, the four dark styles, and colour themes from round the hue wheel (Settings ›
    /// Theme; MobileThemePickerTests checks every one's palette).</summary>
    internal static readonly string[] Themes = { "Light", "Ink", "Smoke", "Dark", "Midnight", "rose", "amber", "forest", "cobalt" };

    /// <summary>The floor every label and glyph keeps (WCAG's 3:1 for large text and UI parts).</summary>
    private const double Floor = 3.0;

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
        Motion.FinishAll();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The device's resource and style stack (AndroidApp.axaml), torn down after.</summary>
    internal sealed class PhoneStack : IDisposable
    {
        private readonly Application _app = Application.Current!;
        private readonly ResourceInclude _icons;
        private readonly StyleInclude _styles;
        public MobileTheme Theme { get; } = new();

        public PhoneStack()
        {
            _app.Resources["InterSemiBold"] = FontFamily.Default;
            try { FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection()); }
            catch (ArgumentException) { }
            _icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
            _styles = new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
            _app.Resources.MergedDictionaries.Add(_icons);
            _app.Styles.Add(_styles);
        }

        public void Apply(string theme, string accent = MobileTheme.DefaultAccent) => Theme.Apply(_app, theme, accent);

        public void Dispose()
        {
            Theme.Remove(_app);
            _app.Styles.Remove(_styles);
            _app.Resources.MergedDictionaries.Remove(_icons);
            _app.RequestedThemeVariant = null;
        }
    }

    // ---- The audit -------------------------------------------------------------------------

    internal sealed record Reading(string What, Color Text, Color Under, double Ratio);

    /// <summary>
    /// Every visible, enabled label and glyph under <paramref name="root"/> that sits on solid fills
    /// only, with its colour and the colour under it as drawn (alpha and opacities composited over
    /// <paramref name="page"/>). Artwork placeholders (Border.art, behind the cover) are skipped.
    /// </summary>
    internal static IEnumerable<Reading> Read(Visual root, Color page)
    {
        foreach (var c in root.GetVisualDescendants().OfType<Control>())
        {
            IBrush? brush;
            string what;
            if (c is TextBlock tb && !(tb.GetVisualParent() is TextPresenter))
            {
                if (string.IsNullOrWhiteSpace(tb.Text)) continue;
                brush = tb.Foreground;
                what = $"\"{Short(tb.Text)}\"";
            }
            else if (c is PathIcon icon)
            {
                if (icon.Data is null) continue;
                brush = icon.Foreground;
                what = "glyph";
            }
            else continue;
            if (!c.IsEffectivelyVisible || !c.IsEffectivelyEnabled || c.Bounds.Width < 1 || c.Bounds.Height < 1) continue;
            if (c.GetSelfAndVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("art"))) continue;
            if (c is PathIcon glyph && Decorative(glyph)) continue;
            if (brush is not ISolidColorBrush fg) continue;

            var alpha = fg.Color.A / 255.0 * fg.Opacity * c.Opacity;
            // What lies under the label, top first: each step maps the colour beneath it to what
            // shows through (a fill laid over it, or a glass's tone range lifting or holding it).
            var layers = new List<Func<Color, Color>>();
            var known = true;
            Color? floor = null;
            void Lay(Color fill)
            {
                if (fill.A == 255) floor = fill;
                else layers.Add(u => Over(fill, u));
            }
            Visual child = c;
            for (Visual? v = c; v != null && v != root.GetVisualParent() && floor == null; child = v, v = v.GetVisualParent())
            {
                if (v != c && v is Panel panel)
                {
                    // Fills laid under the path in this panel (earlier siblings covering its centre).
                    var centre = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), panel);
                    var index = panel.Children.IndexOf(child as Control ?? c);
                    for (var i = index - 1; i >= 0 && centre is { } p && floor == null; i--)
                    {
                        var s = panel.Children[i];
                        if (!s.IsVisible || s.Opacity <= 0 || !s.Bounds.Contains(p)) continue;
                        if (s is Image || s.GetVisualDescendants().OfType<Image>().Any(im => im.IsEffectivelyVisible && im.Source != null)) { known = false; break; }
                        if (Fill(s) is { } f)
                        {
                            if (Solid(f) is not { } ss) { known = false; break; }
                            Lay(WithAlpha(ss.Color, ss.Color.A / 255.0 * ss.Opacity * s.Opacity));
                        }
                    }
                    if (!known || floor != null) break;
                }
                if (v != c) alpha *= v.Opacity;
                if (Fill(v) is { } fill)
                {
                    if (Solid(fill) is not { } solid) { known = false; break; }
                    Lay(WithAlpha(solid.Color, solid.Color.A / 255.0 * solid.Opacity));
                }
                // A glass shows what lies beneath it toned into its luminance range (GlassLens.Tone).
                if (floor == null && v is GlassPanel { IsGlassActive: true } glass)
                {
                    var (min, max, curve, lift) = ((float)glass.BackdropMinLuminance, (float)glass.BackdropMaxLuminance,
                        (float)glass.BackdropToneCurve, (float)glass.BackdropLift);
                    layers.Add(u =>
                    {
                        var (r, g, b) = GlassLens.Tone(u.R / 255f, u.G / 255f, u.B / 255f, min, max, curve, lift);
                        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
                    });
                }
            }
            if (!known || alpha < 0.05) continue;   // a hidden tick (Opacity 0) is not drawn
            var under = floor ?? page;
            for (var i = layers.Count - 1; i >= 0; i--) under = layers[i](under);
            var text = Over(WithAlpha(fg.Color, alpha), under);
            yield return new Reading($"{what} ({Path(c)})", text, under, MobileTheme.ContrastRatio(text, under));
        }
    }

    /// <summary>The row chevron and the Queue's grabber: Apple's faint affordances (systemGray3), which
    /// repeat what the row or sheet already says and carry nothing of their own.</summary>
    private static bool Decorative(PathIcon glyph) =>
        glyph.GetSelfAndVisualAncestors().OfType<Control>().Any(a => a.Name == "QueueGrabber")
        || new[] { "PhChevronRight", "LibChevronIcon", "QueueGrabberIcon" }.Any(k => glyph.TryFindResource(k, out var g) && ReferenceEquals(g, glyph.Data));

    private static IBrush? Fill(Visual v) => v switch
    {
        Border b => b.Background,
        Panel p => p.Background,
        ContentPresenter cp => cp.Background,
        GlassPanel g => g.Background,
        TextBlock t => t.Background,
        Avalonia.Controls.Shapes.Shape shape => shape.Fill,
        _ => null,
    };

    /// <summary>A fill as one colour: a solid brush, or the flat end of an opaque vertical ramp (a
    /// tinted page's body, whose rows sit below the ramp on the page colour); null for a picture or a
    /// translucent gradient.</summary>
    private static ISolidColorBrush? Solid(IBrush brush) => brush switch
    {
        ISolidColorBrush s => s,
        IGradientBrush { GradientStops.Count: > 0 } g when g.GradientStops.All(st => st.Color.A == 255) =>
            new ImmutableSolidColorBrush(g.GradientStops.OrderBy(st => st.Offset).Last().Color, g.Opacity),
        _ => null,
    };

    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    private static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static string Short(string? s) => s is null ? "" : s.Length > 24 ? s[..24] + "…" : s;

    private static string Path(Control c) => string.Join("/", c.GetSelfAndVisualAncestors().OfType<Control>()
        .Where(a => !string.IsNullOrEmpty(a.Name) || a is UserControl).Take(3).Select(a => a.Name ?? a.GetType().Name));

    // ---- The screens -----------------------------------------------------------------------

    private static (Track[] Tracks, Album[] Albums) Library()
    {
        var a = MobileFixtures.Song("Midnight City", artist: "M83", favourite: true, plays: 9);
        var b = MobileFixtures.Song("Wait", artist: "M83", plays: 4);
        var c = MobileFixtures.Song("Outro", artist: "M83", plays: 2);
        var d = MobileFixtures.Song("Teardrop", artist: "Massive Attack", favourite: true, plays: 7);
        foreach (var t in new[] { a, b, c, d }) t.Codec = "FLAC";
        a.IsExplicit = true;
        var album = MobileFixtures.MakeAlbum("Hurry Up, We're Dreaming", "M83", a, b, c);
        album.Year = 2011;
        album.Genre = "Electronic";
        var other = MobileFixtures.MakeAlbum("Mezzanine", "Massive Attack", d);
        return (new[] { a, b, c, d }, new[] { album, other });
    }

    /// <summary>Each screen: its name, how to open it from the Home tab, and the view to read.</summary>
    private static IEnumerable<(string Name, Action<ShellViewModel> Open)> Screens(Album album, Playlist playlist) => new (string, Action<ShellViewModel>)[]
    {
        ("Home", s => s.SelectTabCommand.Execute(MobileTab.Home)),
        ("Library", s => s.SelectTabCommand.Execute(MobileTab.Library)),
        ("Favorites", s => s.SelectTabCommand.Execute(MobileTab.Favorites)),
        ("Playlists", s => s.SelectTabCommand.Execute(MobileTab.Playlists)),
        ("SearchEmpty", s => s.SelectTabCommand.Execute(MobileTab.Search)),
        ("Search", s => { s.SelectTabCommand.Execute(MobileTab.Search); s.Search.Query = "M"; }),
        ("Songs", s => s.OpenSongsCommand.Execute(null)),
        ("Albums", s => s.OpenAlbumsCommand.Execute(null)),
        ("Artists", s => s.OpenArtistsCommand.Execute(null)),
        ("PlaylistPage", s => s.OpenPlaylistCommand.Execute(playlist)),
        ("Album", s => s.OpenAlbumCommand.Execute(album)),
        ("Artist", s => s.OpenArtistCommand.Execute("M83")),
        ("AlbumTinted", s => s.OpenAlbumCommand.Execute(album)),
        ("ArtistTinted", s => s.OpenArtistCommand.Execute("M83")),
        ("AlbumTintedMenu", s => { s.OpenAlbumCommand.Execute(album); ((AlbumPageViewModel)s.CurrentPage!).MoreCommand.Execute(null); }),
        ("SongMenu", s => { s.OpenSongsCommand.Execute(null); s.OpenTrackSheetCommand.Execute(s.Library.Songs[0]); }),
        ("AddToPlaylist", s => { s.OpenTrackSheetCommand.Execute(s.Library.Songs[0]); ((ContextSheetViewModel)s.Sheet!).ShowPlaylistsCommand.Execute(null); }),
        ("LibraryMenu", s => { s.SelectTabCommand.Execute(MobileTab.Library); s.OpenLibraryMenuCommand.Execute(null); }),
        ("SortMenu", s => { s.OpenSongsCommand.Execute(null); ((SongListPageViewModel)s.CurrentPage!).ShowSortMenuCommand.Execute(null); }),
        ("AlbumNote", s => s.ShowAlbumNote(new AlbumNoteViewModel(s, album.Name, album.Artist, null, "A long description of the album.", true))),
        ("Notice", s => s.Player.Notice = NowPlayingViewModel.ComputerUnreachableStopped),
        // Playlist editing and the glass prompt (owner 10-20).
        ("PlaylistMenu", s => { s.OpenPlaylistCommand.Execute(playlist); ((SongListPageViewModel)s.CurrentPage!).ShowPlaylistMenuCommand.Execute(null); }),
        ("PlaylistSongMenu", s => { s.OpenPlaylistCommand.Execute(playlist); s.OpenTrackMenu(s.Library.Songs.First(t => playlist.TrackIds.Contains(t.Id)), s.Library.Playlists.Single()); }),
        ("DeletePrompt", s => { s.OpenPlaylistCommand.Execute(playlist); s.AskDeletePlaylist(s.Library.Playlists.Single()); }),
        ("RenamePrompt", s => { s.OpenPlaylistCommand.Execute(playlist); s.AskRenamePlaylist(s.Library.Playlists.Single()); }),
        ("RemoveFolderPrompt", s => { s.OpenSettingsCommand.Execute(null); s.ShowPrompt(new PromptViewModel(s, "Remove “Music”?",
            "Its songs leave your library on this phone. The files stay where they are.",
            new[] { new PromptAction("Remove Folder", () => { }, isDestructive: true) })); }),
        ("AlbumTintedPrompt", s => { s.OpenAlbumCommand.Execute(album); s.AskDeletePlaylist(s.Library.Playlists.Single()); }),
        ("AddedBanner", s => s.Player.Notice = "Added to Night Drive"),
        ("Settings", s => s.OpenSettingsCommand.Execute(null)),
        ("SettingsFolders", s => { s.OpenSettingsCommand.Execute(null); ((SettingsPageViewModel)s.CurrentPage!).Pane = SettingsPane.Folders; }),
        ("SettingsAccent", s => { s.OpenSettingsCommand.Execute(null); ((SettingsPageViewModel)s.CurrentPage!).Pane = SettingsPane.Accent; }),
        ("SettingsLyrics", s => { s.OpenSettingsCommand.Execute(null); ((SettingsPageViewModel)s.CurrentPage!).Pane = SettingsPane.Lyrics; }),
        ("Account", s => s.OpenAccountCommand.Execute(null)),
        ("Queue", s => { s.Player.PlayTracks(s.Library.Songs.ToList(), 0); s.OpenNowPlayingCommand.Execute(null); s.ToggleQueueCommand.Execute(null); }),
    };

    /// <summary>No tint, then Apple's two kinds of cover page: a deep red (J. Cole's) and a pale pink
    /// (Doja Cat's), the page text flipping dark on the second.</summary>
    private static readonly Color?[] Tints = { null, Color.FromRgb(0x7A, 0x12, 0x14), Color.FromRgb(0xE8, 0xB4, 0xC0) };

    /// <summary>Each theme with one of the accents hardest on it (a pale Gold on Light's white, Indigo
    /// on Smoke's slate, the dark neutral Slate on Midnight's blue-black...), Ink with the default Crimson.</summary>
    private static readonly Dictionary<string, string> Accents = new()
    {
        ["Light"] = "#F4D24B", ["Ink"] = MobileTheme.DefaultAccent, ["Smoke"] = "#4338CA", ["Dark"] = "#FF8547", ["Midnight"] = "#5B6C8F",
        // A colour theme wears its own accent (Match Theme), or the user's: Indigo on Cobalt is the hardest.
        ["rose"] = PhoneThemes.EffectiveAccent("rose", MobileTheme.DefaultAccent, true), ["amber"] = "#F4D24B",
        ["forest"] = PhoneThemes.EffectiveAccent("forest", MobileTheme.DefaultAccent, true), ["cobalt"] = "#4338CA",
    };

    [AvaloniaFact]
    public void EveryTheme_EveryScreen_KeepsItsLabelsAndGlyphsReadable()
    {
        var shots = Environment.GetEnvironmentVariable("NOCTIS_THEME_SHOTS");
        var save = HeadlessTestApp.RealRendering && !string.IsNullOrEmpty(shots);
        if (save) Directory.CreateDirectory(shots!);
        var (tracks, albums) = Library();
        var cover = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"noctis-sweep-cover-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(cover, new byte[] { 1, 2, 3 });
        foreach (var a in albums) a.ArtworkPath = cover;
        var playlist = new Playlist { Name = "Night Drive", TrackIds = { tracks[0].Id, tracks[3].Id } };
        var failures = new List<string>();
        var read = 0;
        using var stack = new PhoneStack();
        foreach (var theme in Themes)
        {
            stack.Apply(theme, Accents[theme]);
            foreach (var tint in Tints)
            foreach (var (name, open) in Screens(albums[0], playlist))
            {
                // The cover-tinted screens run over each tint (a deep red page, a pale pink one);
                // the rest once, untinted.
                if (name.Contains("Tinted") != tint.HasValue) continue;
                using var rig = MobileFixtures.MakeRig(tracks, albums, seed: p => p.SavePlaylistsAsync(new List<Playlist> { playlist }),
                    tint: tint is { } t ? () => new PageTint(_ => t, work => Task.FromResult(work())) : null);
                var label = tint is { } tc ? $"{name}#{tc.R:X2}{tc.G:X2}{tc.B:X2}" : name;
                var window = MobileFixtures.Mount(rig.Shell, out var view);
                try
                {
                    MobileFixtures.Land(window);
                    open(rig.Shell);
                    // Rail tiles fade in (Appear) and a menu's rows settle one after another: read them landed.
                    Pump(700);
                    MobileFixtures.Land(window);
                    var page = ((ISolidColorBrush)view.FindResource(view.ActualThemeVariant, "PhoneBackgroundBrush")!).Color;
                    var verbose = Environment.GetEnvironmentVariable("NOCTIS_SWEEP_VERBOSE");
                    foreach (var r in Read(view, page))
                    {
                        read++;
                        if (!string.IsNullOrEmpty(verbose) && label.StartsWith(verbose, StringComparison.Ordinal))
                            _o.WriteLine($"  {theme} {label}: {r.What} {r.Text} on {r.Under} = {r.Ratio:0.00}");
                        if (r.Ratio < Floor)
                            failures.Add($"{theme} {label}: {r.What} {r.Text} on {r.Under} = {r.Ratio:0.00}:1");
                    }
                    if (save) window.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(shots!, $"{label.Replace("#", "-")}-{theme}.png"));
                }
                finally
                {
                    window.Close();
                }
            }
        }
        _o.WriteLine($"{read} labels and glyphs read");
        foreach (var f in failures) _o.WriteLine(f);
        Assert.True(failures.Count == 0, $"{failures.Count} unreadable:\n" + string.Join("\n", failures.Take(80)));
    }
    /// <summary>One size per role (owner 10-20, "consistent"): a song row's ⋯ is the same glyph at
    /// the same size in the Songs list, on an album and on an artist page (the list's drew 22 to
    /// the album's and artist's 24).</summary>
    [AvaloniaFact]
    public void ARowsOverflowGlyph_IsOneSize_OnEveryPage()
    {
        var (tracks, albums) = Library();
        using var rig = MobileFixtures.MakeRig(tracks, albums);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var sizes = new List<(string Page, Size Size)>();
        void Collect(string page)
        {
            MobileFixtures.Land(window);
            var more = view.FindResource("PhMore");
            var top = (Visual?)MobileFixtures.PageView(view, rig.Shell.CurrentPage!) ?? view;
            foreach (var glyph in top.GetVisualDescendants().OfType<PathIcon>()
                         .Where(g => ReferenceEquals(g.Data, more) && g.IsEffectivelyVisible
                                     && !g.GetVisualAncestors().OfType<GlassPanel>().Any()))
                sizes.Add((page, glyph.Bounds.Size));
        }
        rig.Shell.OpenSongsCommand.Execute(null);
        Collect("Songs");
        rig.Shell.OpenAlbumCommand.Execute(albums[0]);
        Collect("Album");
        rig.Shell.OpenArtistCommand.Execute("M83");
        Collect("Artist");
        window.Close();
        Assert.Contains(sizes, s => s.Page == "Songs");
        Assert.Contains(sizes, s => s.Page == "Album");
        Assert.True(sizes.Select(s => s.Size).Distinct().Count() == 1,
            string.Join(", ", sizes.Select(s => $"{s.Page} {s.Size}").Distinct()));
    }
}

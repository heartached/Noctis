using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Eyeball probe for the Library's list pages' headers (Songs, Albums, Artists, Playlists,
/// Favorites, Recently Added, a playlist, a rail grid), in Light and every dark style, and once
/// in Russian with a long title. Runs only with NOCTIS_TEST_SKIA=1 and NOCTIS_HEADER_SHOTS=&lt;dir&gt;.
/// </summary>
public class MobileLibraryHeadersShotsTests
{
    private readonly ITestOutputHelper _o;
    public MobileLibraryHeadersShotsTests(ITestOutputHelper o) => _o = o;

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
        Motion.FinishAll();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A 600 px square cover (a diagonal dusk gradient with a pale sun) in the shots folder.</summary>
    private static string CoverFile(string dir)
    {
        var path = Path.Combine(dir, "cover-source.png");
        if (File.Exists(path)) return path;
        using var bitmap = new SkiaSharp.SKBitmap(600, 600);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            using var sky = new SkiaSharp.SKPaint
            {
                Shader = SkiaSharp.SKShader.CreateLinearGradient(new SkiaSharp.SKPoint(0, 0), new SkiaSharp.SKPoint(600, 600),
                    new[] { new SkiaSharp.SKColor(0x2B, 0x1E, 0x5C), new SkiaSharp.SKColor(0xE0, 0x5A, 0x47) }, SkiaSharp.SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, 600, 600, sky);
            using var sun = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFF, 0xE3, 0xB0), IsAntialias = true };
            canvas.DrawCircle(380, 230, 90, sun);
        }
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact]
    public void Shots_LibraryListHeaders_InEveryTheme()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_HEADER_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        var app = Application.Current!;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        try { FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection()); }
        catch (ArgumentException) { }
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        var theme = new MobileTheme();
        try
        {
            foreach (var name in new[] { "Light", "Ink", "Smoke", "Dark", "Midnight", "ru" })
            {
                theme.Apply(app, name == "ru" ? "Dark" : name, MobileTheme.DefaultAccent);
                if (name == "ru")
                {
                    Loc.Instance.SetCulture("ru");
                    Loc.Instance.SetOverlays(new Dictionary<string, IReadOnlyDictionary<string, string>>
                    {
                        ["ru"] = new Dictionary<string, string> { ["Nav.Songs"] = "Очень-очень длинные песни" },
                    });
                }
                var titles = new[] { "Intro", "Midnight Drive", "Glass Houses", "Neon Rain", "Afterglow", "Northern Lights" };
                var tracks = titles.Select((t, i) =>
                {
                    var s = MobileFixtures.Song(t, i % 2 == 0 ? "Aurora Lane" : "Tycho", favourite: i < 3);
                    s.TrackNumber = i + 1;
                    return s;
                }).ToArray();
                var albums = new[]
                {
                    MobileFixtures.MakeAlbum("Night Signals", "Aurora Lane", tracks[0], tracks[2], tracks[4]),
                    MobileFixtures.MakeAlbum("Dive", "Tycho", tracks[1], tracks[3], tracks[5]),
                };
                // Night Drive has a custom cover (the playlist page's cover), Late Show no art at all
                // (its placeholder).
                var mix = new Playlist { Name = "Night Drive", TrackIds = { tracks[0].Id, tracks[1].Id }, CoverArtPath = CoverFile(dir) };
                var bare = new Playlist { Name = "Late Show", TrackIds = { tracks[2].Id } };
                using var rig = MobileFixtures.MakeRig(tracks, albums, seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix, bare }));
                foreach (var artist in new[] { "Aurora Lane", "Tycho", "Bonobo", "Caribou" })
                    rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = artist, AlbumCount = 1, TrackCount = 3 });
                var window = MobileFixtures.Mount(rig.Shell, out _, 412, 700);
                var shell = rig.Shell;
                try
                {
                    void Shot(string page, Action open)
                    {
                        shell.SelectTabCommand.Execute(MobileTab.Library);
                        open();
                        Pump(400);
                        var path = Path.Combine(dir, $"{page}-{name}.png");
                        window.CaptureRenderedFrame()!.Save(path);
                        _o.WriteLine(path);
                    }

                    Shot("songs", () => shell.OpenSongsCommand.Execute(null));
                    if (name is "ru") continue;
                    Shot("albums", () => shell.OpenAlbumsCommand.Execute(null));
                    Shot("artists", () => shell.OpenArtistsCommand.Execute(null));
                    Shot("playlists", () => shell.OpenPlaylistsCommand.Execute(null));
                    Shot("favorites", () => shell.OpenFavouritesCommand.Execute(null));
                    Shot("recently-added", () => shell.OpenRecentlyAddedCommand.Execute(null));
                    foreach (var (file, playlist) in new[] { ("playlist", "Night Drive"), ("playlist-noart", "Late Show") })
                        Shot(file, () =>
                        {
                            shell.OpenPlaylistsCommand.Execute(null);
                            Pump(300);
                            shell.OpenPlaylistCommand.Execute(shell.Library.Playlists.Single(p => p.Name == playlist));
                        });
                    Shot("pinned", () => shell.OpenPinnedCommand.Execute(null));
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            Loc.Instance.SetOverlays(null);
            Loc.Instance.SetCulture("en");
            theme.Remove(app);
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }
}

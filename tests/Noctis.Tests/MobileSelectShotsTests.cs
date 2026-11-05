using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Eyeball probe for Select mode (an album and the Songs list, with the action bar above the
/// tabs) and the M3U import result, in Dark and Light. Runs only with NOCTIS_TEST_SKIA=1 and
/// NOCTIS_SELECT_SHOTS=&lt;dir&gt;.
/// </summary>
public class MobileSelectShotsTests
{
    private readonly ITestOutputHelper _o;
    public MobileSelectShotsTests(ITestOutputHelper o) => _o = o;

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

    private sealed class Picker : IPlaylistFilePicker
    {
        public PickedPlaylistFile[] Files = Array.Empty<PickedPlaylistFile>();
        public Task<System.Collections.Generic.IReadOnlyList<PickedPlaylistFile>> PickPlaylistFilesAsync() =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<PickedPlaylistFile>>(Files);
    }

    [AvaloniaFact]
    public async Task Shots_SelectMode_And_ImportResult()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_SELECT_SHOTS");
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
            foreach (var name in new[] { "Dark", "Light" })
            {
                theme.Apply(app, name, MobileTheme.DefaultAccent);
                var titles = new[] { "Intro", "Midnight Drive", "Glass Houses", "Neon Rain", "Afterglow", "Northern Lights", "Echoes", "Daybreak", "Outro" };
                var tracks = titles.Select((t, i) =>
                {
                    var s = MobileFixtures.Song(t, "Aurora Lane");
                    s.TrackNumber = i + 1;
                    s.Duration = TimeSpan.FromSeconds(180 + i * 17);
                    return s;
                }).ToArray();
                var album = MobileFixtures.MakeAlbum("Night Signals", "Aurora Lane", tracks);
                using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
                var picker = new Picker();
                var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
                {
                    Marshal = a => a(),
                    TintFactory = rig.Shell.TintFactory,
                    PlaylistFiles = picker,
                };
                shell.Player.PlayTracks(new[] { tracks[0] }, 0);
                var window = MobileFixtures.Mount(shell, out _, 412, 915);
                try
                {
                    shell.OpenAlbumCommand.Execute(album);
                    Pump(300);
                    var page = (AlbumPageViewModel)shell.CurrentPage!;
                    page.OpenSongMenuCommand.Execute(tracks[1]);
                    shell.Sheet!.SelectCommand.Execute(null);
                    page.Selection.Toggle(tracks[3]);
                    page.Selection.Toggle(tracks[4]);
                    Pump(400);
                    Save(window, Path.Combine(dir, $"select-album-{name}.png"));

                    shell.SelectTabCommand.Execute(MobileTab.Library);
                    shell.OpenSongsCommand.Execute(null);
                    Pump(300);
                    var songs = (SongListPageViewModel)shell.CurrentPage!;
                    songs.BeginSelectCommand.Execute(null);
                    songs.Selection.Toggle(songs.Songs[0]);
                    songs.Selection.Toggle(songs.Songs[2]);
                    Pump(400);
                    Save(window, Path.Combine(dir, $"select-songs-{name}.png"));
                    songs.Selection.EndCommand.Execute(null);

                    shell.SelectTabCommand.Execute(MobileTab.Playlists);
                    Pump(300);
                    Save(window, Path.Combine(dir, $"select-playlists-tab-{name}.png"));

                    picker.Files = new[]
                    {
                        new PickedPlaylistFile("Road Trip.m3u8", Encoding.UTF8.GetBytes(
                            "#EXTM3U\n#EXTINF:197,Aurora Lane - Midnight Drive\nC:\\Users\\me\\Music\\Aurora Lane\\Night Signals\\02 Midnight Drive.flac\n" +
                            "#EXTINF:231,Aurora Lane - Neon Rain\nD:/Music/neon rain.mp3\n" +
                            "#EXTINF:248,Aurora Lane - Afterglow\n../Afterglow.mp3\n" +
                            "#EXTINF:301,Led Zeppelin - Kashmir\nC:\\Music\\Led Zeppelin\\Kashmir.mp3\n" +
                            "#EXTINF:254,Daft Punk - Something About Us\nC:\\Music\\Daft Punk\\Something About Us.mp3\n")),
                    };
                    await shell.ImportPlaylistsCommand.ExecuteAsync(null);
                    Pump(500);
                    Save(window, Path.Combine(dir, $"select-import-result-{name}.png"));
                    shell.CloseSheet();
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            theme.Remove(app);
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    private void Save(Window window, string path)
    {
        window.CaptureRenderedFrame()!.Save(path);
        _o.WriteLine(path);
    }
}

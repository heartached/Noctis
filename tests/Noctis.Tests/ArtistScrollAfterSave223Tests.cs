using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #223, the live repro after the first fix (11-11): an artist with 30 one-song releases
/// (all Singles), "Singles &amp; EPs" tab, newest first, scrolled to the bottom; the album-scoped
/// editor on album 05 saves a Composer; the page was back at the top of the tab. Mirrors it end
/// to end: real files, real library, the editor's real save, then the folder watcher's re-import
/// of the rewritten file.
/// </summary>
[Collection("MetadataServiceStatics")]
public class ArtistScrollAfterSave223Tests : IDisposable
{
    private const string Band = "T1111 Scroll Band";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "223b-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public ArtistScrollAfterSave223Tests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

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

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static async Task Settle(int ms = 600)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { Pump(1); await Task.Delay(10); }
        Pump(10);
    }

    private string CreateSingle(int n)
    {
        var music = Path.Combine(_dir, "music");
        Directory.CreateDirectory(music);
        var path = Path.Combine(music, $"Song {n:00}.wav");
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = $"Song {n:00}";
        f.Tag.Album = $"T1111 Scroll Album {n:00}";
        f.Tag.Performers = new[] { Band };
        f.Tag.AlbumArtists = new[] { Band };
        f.Tag.Year = (uint)(2000 + n);
        f.Tag.Track = 1;
        f.Save();
        return path;
    }

    private sealed class NullAuditTrail : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task SinglesTab_AlbumScopedComposerSave_ThenWatcherReimport_KeepsTheScrollPosition()
    {
        EnsureAppStyles();
        var paths = Enumerable.Range(1, 30).Select(CreateSingle).ToList();
        var persistence = new PersistenceService(Path.Combine(_dir, "data"));
        var metadata = new MetadataService();
        var library = new LibraryService(metadata, persistence, new SqliteLibraryIndexService(persistence), new NullAuditTrail());
        await library.ScanAsync(new[] { Path.Combine(_dir, "music") });
        Assert.Equal(30, library.Tracks.Count);

        var updates = 0;
        library.LibraryUpdated += (_, _) => Interlocked.Increment(ref updates);

        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var page = new ArtistDetailViewModel(Band, library, player) { SelectedTab = "singles" };
        var view = new ArtistDetailView { DataContext = page };
        var win = new Window { Width = 2560, Height = 1390, Content = view };
        win.Show();
        await Settle();
        Assert.Equal(30, page.SingleReleases.Count);

        var sv = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        var bottom = sv.Extent.Height - sv.Viewport.Height;
        Assert.True(bottom > 1000, $"page only scrolls {bottom}px");
        sv.Offset = new Vector(0, bottom);
        await Settle(100);
        var before = sv.Offset.Y;
        void Log(string when) => _out.WriteLine($"{when}: offset {sv.Offset.Y:0} extent {sv.Extent.Height:0} singles {page.SingleReleases.Count} tab {page.SelectedTab}");
        sv.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty) _out.WriteLine($"  offset -> {sv.Offset.Y:0} (extent {sv.Extent.Height:0}, singles {page.SingleReleases.Count})");
        };
        Log("scrolled");

        // Right-click album 05 › Metadata: the album-scoped editor, Composer, Save.
        var album05 = library.Albums.Single(a => a.Name == "T1111 Scroll Album 05");
        var track = album05.Tracks[0];
        var albumTracks = library.Tracks.Where(t => t.AlbumId == track.AlbumId).ToList();
        var editor = new MetadataViewModel(track, metadata, library, persistence, new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: albumTracks);
        editor.Composer = "Somebody";
        var seen = updates;
        await editor.SaveCommand.ExecuteAsync(null);
        // WAV's INFO tag has no composer field; the live repro's file was rewritten, so make
        // sure this one counts as changed for the watcher's re-import below.
        File.SetLastWriteTimeUtc(track.FilePath, DateTime.UtcNow.AddMinutes(1));
        var deadline = Environment.TickCount64 + 5000;
        while (updates == seen && Environment.TickCount64 < deadline) { Pump(1); await Task.Delay(10); }
        await Settle();
        Log("after save");

        // The folder watcher then re-imports the rewritten file.
        seen = updates;
        await library.ImportDiscoveredFilesAsync(new[] { paths[4] });
        deadline = Environment.TickCount64 + 5000;
        while (updates == seen && Environment.TickCount64 < deadline) { Pump(1); await Task.Delay(10); }
        await Settle();
        Log("after re-import");

        Assert.Equal(30, page.SingleReleases.Count);
        Assert.Equal(before, sv.Offset.Y, 1);
        win.Close();
    }

    /// <summary>
    /// The part a view-model test cannot see: the editor is a modal window. Clicking the
    /// "Singles &amp; EPs" tab focused its button; the wheel scroll leaves focus there and the
    /// right-click on a tile does not take it. When the modal editor closes, the main window
    /// gets its focus back on that tab button, and the page ScrollViewer (BringIntoViewOnFocusChange,
    /// on by default) scrolls it into view: the top of the tab content, the reported spot.
    /// </summary>
    [AvaloniaFact]
    public async Task SinglesTab_ClosingAModalEditor_KeepsTheScrollPosition()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var n = 1; n <= 30; n++)
        {
            var id = Guid.NewGuid();
            var t = new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {n:00}", Artist = Band, AlbumArtist = Band, Album = $"T1111 Scroll Album {n:00}",
                AlbumId = id, Year = 2000 + n, TrackNumber = 1, Duration = TimeSpan.FromMinutes(3), FilePath = $"C:/m/{n}.flac",
            };
            albums.Add(new Album { Id = id, Name = t.Album, Artist = Band, Year = t.Year, TrackCount = 1, Tracks = new List<Track> { t } });
            lib.TrackList.Add(t);
        }
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var page = new ArtistDetailViewModel(Band, lib, player);
        var view = new ArtistDetailView { DataContext = page };
        var win = new Window { Width = 2560, Height = 1390, Content = view };
        win.Show();
        await Settle();

        var tab = view.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("page-tab") && Avalonia.Automation.AutomationProperties.GetName(b) == "Singles & EPs");
        tab.Focus(Avalonia.Input.NavigationMethod.Pointer);
        tab.Command!.Execute(tab.CommandParameter);
        await Settle();
        Assert.Equal("singles", page.SelectedTab);
        Assert.Equal(30, page.SingleReleases.Count);

        var sv = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        var bottom = sv.Extent.Height - sv.Viewport.Height;
        Assert.True(bottom > 1000, $"page only scrolls {bottom}px");
        sv.Offset = new Vector(0, bottom);
        await Settle(100);
        var before = sv.Offset.Y;

        // Right-click › Metadata: a modal window over the main one, then Save closes it.
        var editor = new Window { Width = 400, Height = 300 };
        var shown = editor.ShowDialog(win);
        await Settle(100);
        editor.Close();
        await shown;
        win.Activate();
        await Settle();
        _out.WriteLine($"focused after close: {TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement()} offset {sv.Offset.Y:0} (was {before:0})");

        Assert.Equal(before, sv.Offset.Y, 1);

        win.Close();
    }

    /// <summary>Keyboard focus still scrolls the page to the focused control.</summary>
    [AvaloniaFact]
    public async Task SinglesTab_KeyboardFocus_StillBringsTheControlIntoView()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var n = 1; n <= 30; n++)
        {
            var id = Guid.NewGuid();
            var t = new Track { Id = Guid.NewGuid(), Title = $"Song {n:00}", Artist = Band, AlbumArtist = Band,
                Album = $"T1111 Scroll Album {n:00}", AlbumId = id, Year = 2000 + n, TrackNumber = 1, Duration = TimeSpan.FromMinutes(3) };
            albums.Add(new Album { Id = id, Name = t.Album, Artist = Band, Year = t.Year, TrackCount = 1, Tracks = new List<Track> { t } });
            lib.TrackList.Add(t);
        }
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var page = new ArtistDetailViewModel(Band, lib, player) { SelectedTab = "singles" };
        var view = new ArtistDetailView { DataContext = page };
        var win = new Window { Width = 2560, Height = 1390, Content = view };
        win.Show();
        await Settle();
        var sv = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        sv.Offset = new Vector(0, sv.Extent.Height - sv.Viewport.Height);
        await Settle(100);
        var before = sv.Offset.Y;

        var tab = view.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("page-tab") && Avalonia.Automation.AutomationProperties.GetName(b) == "Singles & EPs");
        Assert.True(tab.Focus(Avalonia.Input.NavigationMethod.Tab));
        await Settle(100);
        Assert.True(sv.Offset.Y < before - 500, $"Tab focus left the page at {sv.Offset.Y:0}");
        win.Close();
    }

    /// <summary>Same modal round trip on an album page scrolled down its song list, with the
    /// header's Play button holding focus from a click.</summary>
    [AvaloniaFact]
    public async Task AlbumPage_ClosingAModalEditor_KeepsTheScrollPosition()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var id = Guid.NewGuid();
        var songs = Enumerable.Range(1, 80).Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i:000}", Artist = Band, AlbumArtist = Band, Album = "Long",
            AlbumId = id, TrackNumber = i, DiscNumber = 1, Duration = TimeSpan.FromMinutes(3), FilePath = $"C:/m/{i}.flac",
        }).ToList();
        var album = new Album { Id = id, Name = "Long", Artist = Band, Tracks = songs, TrackCount = 80 };
        ((List<Album>)lib.Albums).Add(album);
        lib.TrackList.AddRange(songs);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new RebuildKeepsScroll223Tests.NoLastFm());
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 2560, Height = 1390, Content = view };
        win.Show();
        await Settle();

        var sv = view.FindControl<ScrollViewer>("TrackScrollViewer")!;
        var button = view.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && b.Focusable && b.TranslatePoint(default, sv)!.Value.Y < 400);
        button.Focus(Avalonia.Input.NavigationMethod.Pointer);
        var bottom = sv.Extent.Height - sv.Viewport.Height;
        Assert.True(bottom > 1000, $"page only scrolls {bottom}px");
        sv.Offset = new Vector(0, bottom);
        await Settle(100);
        var before = sv.Offset.Y;

        var editor = new Window { Width = 400, Height = 300 };
        var shown = editor.ShowDialog(win);
        await Settle(100);
        editor.Close();
        await shown;
        win.Activate();
        await Settle();
        _out.WriteLine($"album page: focused {TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement()} offset {sv.Offset.Y:0} (was {before:0})");

        Assert.Equal(before, sv.Offset.Y, 1);
        win.Close();
    }
}

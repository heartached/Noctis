using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin API 1.7 (GitHub #198): library commands (the artist page's "…" menu and the Songs
/// page's top bar) and IPlaybackControl.QueueTracks (Play Next / Add to Queue as the app does
/// them), through the real host and player.
/// </summary>
public class PluginLibraryCommandTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose()
    {
        PluginLibraryMenu.Source = null;
        _box.Dispose();
    }

    private const string Id = "dev.test.plugin";
    private const string Icon = "M0 0L10 10";

    private static string M(params string[] permissions) => PluginSandbox.Manifest(id: Id, apiVersion: "1.5", permissions: permissions);

    private static Track T(string title) => new()
    {
        Title = title, Artist = "A", Album = "B", AlbumArtist = "A", Duration = TimeSpan.FromSeconds(200),
        FilePath = Path.Combine(Path.GetTempPath(), title + ".mp3"),
    };

    private (PluginHost Host, LoadedPlugin Plugin) Load(Action<IPluginHost> init, params string[] permissions)
    {
        _box.Approve((Id, permissions));
        var host = _box.NewHost();
        var plugin = _box.AddInProcess(host, new ScriptedPlugin { OnInit = init }, M(permissions));
        return (host, plugin);
    }

    [Fact]
    public void OlderImplementations_SayApi17IsMissing()
    {
        IPluginHost host = new OldHost();
        Assert.Contains("1.7", Assert.Throws<NotSupportedException>(() => host.RegisterLibraryCommand("x", null, LibraryCommandPlaces.SongsPage, _ => { })).Message);
        IPlaybackControl control = new OldControl();
        Assert.Contains("1.7", Assert.Throws<NotSupportedException>(() => control.QueueTracks(Array.Empty<string>(), true)).Message);
    }

    [AvaloniaFact]
    public void LibraryCommand_ShowsWhereItSaid_GetsTheArtist_AndWithdraws()
    {
        var got = new List<LibraryCommandContext>();
        IDisposable? songsOnly = null;
        var (host, plugin) = Load(h =>
        {
            h.RegisterLibraryCommand(" Both ", Icon, LibraryCommandPlaces.ArtistPage | LibraryCommandPlaces.SongsPage, got.Add);
            songsOnly = h.RegisterLibraryCommand("Songs only", Icon, LibraryCommandPlaces.SongsPage, got.Add);
        }, "menu.commands");

        Assert.Equal(new[] { "Both" }, host.LibraryCommands(LibraryCommandPlaces.ArtistPage).Select(c => c.Label));
        Assert.Equal(new[] { "Both", "Songs only" }, host.LibraryCommands(LibraryCommandPlaces.SongsPage).Select(c => c.Label));
        Assert.Contains("Menu: Both", plugin.Extensions);

        var both = host.LibraryCommands(LibraryCommandPlaces.ArtistPage).Single();
        both.Execute(LibraryCommandPlaces.ArtistPage, "  Nova ");
        both.Execute(LibraryCommandPlaces.SongsPage, "ignored");
        Assert.Equal(new[] { new LibraryCommandContext(LibraryCommandPlaces.ArtistPage, "Nova"), new LibraryCommandContext(LibraryCommandPlaces.SongsPage, null) }, got);

        var changed = 0;
        host.LibraryCommandsChanged += (_, _) => changed++;
        songsOnly!.Dispose();
        Assert.Equal(1, changed);
        Assert.Equal(new[] { "Both" }, host.LibraryCommands(LibraryCommandPlaces.SongsPage).Select(c => c.Label));

        host.UnloadAll();
        Assert.Empty(host.LibraryCommands(LibraryCommandPlaces.ArtistPage | LibraryCommandPlaces.SongsPage));
    }

    [AvaloniaFact]
    public void LibraryCommand_NeedsMenuCommands()
    {
        IPluginHost? h = null;
        Load(x => h = x);
        Assert.Equal("menu.commands", Assert.Throws<PluginPermissionException>(() => h!.RegisterLibraryCommand("x", null, LibraryCommandPlaces.SongsPage, _ => { })).Permission);
    }

    [AvaloniaFact]
    public void LibraryCommand_NeedsALabel_AndAPlace()
    {
        IPluginHost? h = null;
        Load(x => h = x, "menu.commands");
        Assert.Throws<ArgumentException>(() => h!.RegisterLibraryCommand(" ", null, LibraryCommandPlaces.SongsPage, _ => { }));
        Assert.Throws<ArgumentException>(() => h!.RegisterLibraryCommand("x", null, LibraryCommandPlaces.None, _ => { }));
        Assert.Throws<ArgumentException>(() => h!.RegisterLibraryCommand("x", null, (LibraryCommandPlaces)64, _ => { }));
    }

    [AvaloniaFact]
    public void LibraryCommand_AThrowFailsThePlugin_AndTakesItsEntriesAway()
    {
        var (host, plugin) = Load(h => h.RegisterLibraryCommand("Boom", Icon, LibraryCommandPlaces.SongsPage, _ => throw new IOException("disk")), "menu.commands");
        host.LibraryCommands(LibraryCommandPlaces.SongsPage).Single().Execute(LibraryCommandPlaces.SongsPage);
        Assert.Equal(PluginStatus.Failed, plugin.Status);
        Assert.Contains("disk", plugin.Error);
        Assert.Empty(host.LibraryCommands(LibraryCommandPlaces.SongsPage));
    }

    [AvaloniaFact]
    public void ArtistMenu_AddsPluginEntriesAtTheEnd_OncePerOpen()
    {
        var artists = new List<string?>();
        var (host, _) = Load(h => h.RegisterLibraryCommand("Album Roulette", Icon, LibraryCommandPlaces.ArtistPage, c => artists.Add(c.Artist)), "menu.commands");
        PluginLibraryMenu.Source = place => host.LibraryCommands(place);

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = "Play Next" });
        PluginLibraryMenu.BindArtistMenu(menu, "Nova");
        PluginLibraryMenu.BindArtistMenu(menu, "Nova"); // reopening must not duplicate
        Assert.Equal(3, menu.Items.Count);
        Assert.IsType<Separator>(menu.Items[1]);
        var item = Assert.IsType<MenuItem>(menu.Items[2]);
        Assert.Equal("Album Roulette", item.Header);
        Assert.NotNull(item.Icon);
        item.Command!.Execute(null);
        Assert.Equal(new[] { "Nova" }, artists);

        host.UnloadAll();
        PluginLibraryMenu.BindArtistMenu(menu, "Nova");
        Assert.Equal("Play Next", Assert.IsType<MenuItem>(Assert.Single(menu.Items)).Header);
    }

    [AvaloniaFact]
    public void SongsPageButtons_NeedAnIcon_AndRunTheCommand()
    {
        var ran = 0;
        var (host, _) = Load(h =>
        {
            h.RegisterLibraryCommand("Song Roulette", Icon, LibraryCommandPlaces.SongsPage, _ => ran++);
            h.RegisterLibraryCommand("No icon", null, LibraryCommandPlaces.SongsPage, _ => ran++);
            h.RegisterLibraryCommand("Artist only", Icon, LibraryCommandPlaces.ArtistPage, _ => ran++);
        }, "menu.commands");
        PluginLibraryMenu.Source = place => host.LibraryCommands(place);

        var button = Assert.Single(PluginLibraryMenu.SongsPageButtons());
        Assert.Equal(("Song Roulette", "Test Plugin"), (button.Label, button.PluginName));
        button.Command.Execute(null);
        Assert.Equal(1, ran);

        var bar = new TopBarViewModel();
        Assert.False(bar.HasPagePluginButtons);
        bar.PagePluginButtons = PluginLibraryMenu.SongsPageButtons();
        Assert.True(bar.HasPagePluginButtons);
        bar.HidePageActions();
        Assert.False(bar.HasPagePluginButtons);
    }

    [AvaloniaFact]
    public void QueueTracks_PlayNextAndAddToQueue_KeepShuffle_AndRaiseTracksQueued()
    {
        var library = new FakeLibraryService();
        var tracks = Enumerable.Range(0, 6).Select(i => T("t" + i)).ToList();
        library.TrackList.AddRange(tracks);
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        _box.Approve((Id, new[] { "playback.control" }));
        var host = _box.NewHost(player, library, "1.6.8");
        var script = new ScriptedPlugin();
        _box.AddInProcess(host, script, M("playback.control"));
        var playback = script.Host!.Playback;
        var queued = new List<(IReadOnlyList<string> Ids, bool PlayNext)>();
        playback.TracksQueued += (_, e) => queued.Add((e.TrackIds, e.PlayNext));

        player.ReplaceQueueAndPlay(tracks.Take(3).ToList(), 0);
        player.ToggleShuffleCommand.Execute(null);
        Assert.True(player.IsShuffleEnabled);

        playback.QueueTracks(new[] { tracks[4].Id.ToString("D"), "nope", tracks[5].Id.ToString("D") }, playNext: true);
        Assert.Equal(new[] { tracks[4].Id, tracks[5].Id }, player.UpNext.Take(2).Select(t => t.Id));
        playback.QueueTracks(new[] { tracks[3].Id.ToString("D") }, playNext: false);
        Assert.Equal(tracks[3].Id, player.UpNext.Last().Id);
        Assert.True(player.IsShuffleEnabled); // unlike SetUpNext
        Assert.Equal(tracks[0].Id, player.CurrentTrack!.Id);
        Assert.Equal(new[] { true, false }, queued.Select(q => q.PlayNext));

        playback.QueueTracks(new[] { "nope" }, playNext: true); // nothing known: nothing happens
        Assert.Equal(2, queued.Count);
    }

    private sealed class OldControl : IPlaybackControl
    {
        public void PlayPause() { }
        public void Play() { }
        public void Pause() { }
        public void Next() { }
        public void Previous() { }
        public void Seek(TimeSpan position) { }
    }

    private sealed class OldHost : IPluginHost
    {
        public string AppVersion => "1.6.6";
        public string DataDirectory => "";
        public INowPlaying NowPlaying => null!;
        public IBeatSource Beat => null!;
        public ISpectrumSource Spectrum => null!;
        public void Log(string message) { }
        public void RegisterVisualLayer(IVisualLayerProvider provider) { }
    }
}

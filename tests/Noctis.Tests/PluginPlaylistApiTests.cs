using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin API 1.4: <see cref="ILibraryReader.GetPlaylists"/> under "library.read" (copies in
/// sidebar order, missing tracks left out, smart playlists resolved), and playing one through
/// the 1.3 PlayTracks. Older kits keep working: the member has a default body.
/// </summary>
public class PluginPlaylistApiTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Id = "dev.test.playlists";

    private static string M(params string[] permissions) => PluginSandbox.Manifest(id: Id, apiVersion: "1.4", minAppVersion: "1.6.6", permissions: permissions);

    private (ScriptedPlugin Script, LoadedPlugin Plugin, PluginHost Host) Start(FakeLibraryService library, PlayerViewModel? player = null, params string[] permissions)
    {
        _box.Approve((Id, permissions));
        var host = _box.NewHost(player, library, appVersion: "1.6.6");
        var script = new ScriptedPlugin();
        var plugin = _box.AddInProcess(host, script, M(permissions));
        Assert.Equal(PluginStatus.Running, plugin.Status);
        return (script, plugin, host);
    }

    private static List<Track> Tracks(FakeLibraryService library, int n)
    {
        var tracks = Enumerable.Range(0, n).Select(i => new Track
        {
            Title = "t" + i, Artist = i % 2 == 0 ? "Even" : "Odd", Album = "B", Duration = TimeSpan.FromSeconds(100),
            FilePath = Path.Combine(Path.GetTempPath(), $"pl{i}.mp3"),
        }).ToList();
        library.TrackList.AddRange(tracks);
        return tracks;
    }

    [Fact]
    public void Kit_Is14_AndA14PluginRunsOn166()
    {
        Assert.Equal("1.4", PluginApi.Version);
        Assert.Equal((1, 4), (typeof(PluginApi).Assembly.GetName().Version!.Major, typeof(PluginApi).Assembly.GetName().Version!.Minor));
        Assert.Null(PluginManifest.Parse(M()).CheckCompatibility("1.6.6"));
        Assert.NotNull(PluginManifest.Parse(M()).CheckCompatibility("1.6.5"));
    }

    [Fact]
    public void OlderReaders_StillCompile_AndSayWhichApiIsMissing()
    {
        ILibraryReader old = new OldReader();
        var ex = Assert.Throws<NotSupportedException>(() => old.GetPlaylists());
        Assert.Contains("1.4", ex.Message);
    }

    private sealed class OldReader : ILibraryReader
    {
        public int TrackCount => 0;
        public IReadOnlyList<TrackInfo> Search(string query, int limit = 50) => Array.Empty<TrackInfo>();
    }

    [AvaloniaFact]
    public void GetPlaylists_NeedsLibraryRead()
    {
        var library = new FakeLibraryService();
        var (script, _, _) = Start(library);
        Assert.Throws<PluginPermissionException>(() => script.Host!.Library);
    }

    [AvaloniaFact]
    public void GetPlaylists_CopiesInOrder_LeavesOutMissingTracks_AndResolvesSmartOnes()
    {
        var library = new FakeLibraryService();
        var tracks = Tracks(library, 4);
        var (script, _, host) = Start(library, null, "library.read");

        // No source (tests, headless): nothing, not a throw.
        Assert.Empty(script.Host!.Library.GetPlaylists());

        var manual = new Playlist
        {
            Name = "Road trip", Color = "#FF6B6B", CoverArtPath = "C:/covers/road.png",
            TrackIds = new List<Guid> { tracks[2].Id, Guid.NewGuid(), tracks[0].Id },
        };
        var smart = new Playlist
        {
            Name = "Evens", IsSmartPlaylist = true, CoverArtPath = "  ",
            Rules = new List<SmartPlaylistRule> { new() { Field = RuleField.Artist, Operator = RuleOperator.Equals, Value = "Even" } },
        };
        var source = new List<Playlist> { manual, smart };
        host.PlaylistSource = () => source;

        var lists = script.Host.Library.GetPlaylists();

        Assert.Equal(new[] { "Road trip", "Evens" }, lists.Select(p => p.Name));
        var road = lists[0];
        Assert.Equal(manual.Id.ToString("D"), road.Id);
        Assert.Equal("C:/covers/road.png", road.CoverPath);
        Assert.Equal("#FF6B6B", road.Color);
        Assert.False(road.IsSmart);
        // Playlist order, the id the library does not know left out.
        Assert.Equal(new[] { tracks[2].Id.ToString("D"), tracks[0].Id.ToString("D") }, road.TrackIds);

        var evens = lists[1];
        Assert.True(evens.IsSmart);
        Assert.Null(evens.CoverPath); // blank = no cover of its own
        Assert.Equal(new[] { tracks[0].Id.ToString("D"), tracks[2].Id.ToString("D") }, evens.TrackIds.OrderBy(i => tracks.FindIndex(t => t.Id.ToString("D") == i)));

        // Copies: editing the plugin's view never touches Noctis's playlist.
        manual.TrackIds.Clear();
        Assert.Equal(2, road.TrackIds.Count);
        Assert.Empty(script.Host.Library.GetPlaylists()[0].TrackIds);
    }

    [AvaloniaFact]
    public void GetPlaylists_AfterThePluginStops_IsEmpty()
    {
        var library = new FakeLibraryService();
        Tracks(library, 1);
        var (script, plugin, host) = Start(library, null, "library.read");
        host.PlaylistSource = () => new[] { new Playlist { Name = "x" } };
        var reader = script.Host!.Library;
        Assert.Single(reader.GetPlaylists());
        host.SetEnabled(plugin, false);
        Assert.Empty(reader.GetPlaylists());
    }

    [AvaloniaFact]
    public void APlaylist_PlaysThroughPlayTracks()
    {
        var audio = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var tracks = Tracks(library, 3);
        var (script, _, host) = Start(library, player, "library.read", "playback.control");
        host.PlaylistSource = () => new[] { new Playlist { Name = "Mix", TrackIds = new List<Guid> { tracks[1].Id, tracks[2].Id } } };

        var mix = script.Host!.Library.GetPlaylists().Single();
        script.Host.Playback.PlayTracks(mix.TrackIds);

        Assert.Same(tracks[1], player.CurrentTrack);
        Assert.Equal(new[] { tracks[2] }, player.UpNext.ToArray());
    }
}

using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin API 1.5's queue members (IPlaybackControl.GetQueue, SetUpNext, TracksQueued) and the new
/// TrackInfo flags, through the real host and player.
/// </summary>
public class PluginQueueApiTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Id = "dev.test.plugin";

    private static string M(params string[] permissions) => PluginSandbox.Manifest(id: Id, apiVersion: "1.5", permissions: permissions);

    private static Track T(string title) => new()
    {
        Title = title, Artist = "A", Album = "B", AlbumArtist = "A", Duration = TimeSpan.FromSeconds(200),
        FilePath = Path.Combine(Path.GetTempPath(), title + ".mp3"),
    };

    private (PlayerViewModel Player, FakeLibraryService Library, IPluginHost Host, List<Track> Tracks) Rig(string[] permissions)
    {
        var library = new FakeLibraryService();
        var tracks = Enumerable.Range(0, 8).Select(i => T("t" + i)).ToList();
        library.TrackList.AddRange(tracks);
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        _box.Approve((Id, permissions));
        var host = _box.NewHost(player, library, "1.6.6");
        var script = new ScriptedPlugin();
        _box.AddInProcess(host, script, M(permissions));
        return (player, library, script.Host!, tracks);
    }

    private static string S(Track t) => t.Id.ToString("D");

    [Fact]
    public void Kit_Is15()
    {
        Assert.Equal("1.5", PluginApi.Version);
        Assert.Equal((1, 5), (typeof(PluginApi).Assembly.GetName().Version!.Major, typeof(PluginApi).Assembly.GetName().Version!.Minor));
    }

    [Fact]
    public void OlderImplementations_StillCompile_AndSayWhichApiIsMissing()
    {
        IPlaybackControl old = new OldControl();
        Assert.Contains("1.5", Assert.Throws<NotSupportedException>(() => old.GetQueue()).Message);
        Assert.Contains("1.5", Assert.Throws<NotSupportedException>(() => old.SetUpNext(Array.Empty<string>())).Message);
        Assert.Throws<NotSupportedException>(() => old.TracksQueued += (_, _) => { });
        IPluginHost host = new OldHost();
        Assert.Contains("1.5", Assert.Throws<NotSupportedException>(() => host.RegisterLyricsEffect(null!)).Message);
    }

    [AvaloniaFact]
    public void GetQueue_And_SetUpNext_ReplaceWhatIsUpNext_WithoutTouchingThePlayingSong()
    {
        var (player, _, host, tracks) = Rig(new[] { "playback.control" });
        player.ReplaceQueueAndPlay(tracks.Take(4).ToList(), 0);
        player.ToggleShuffleCommand.Execute(null);
        Assert.True(player.IsShuffleEnabled);

        var q = host.Playback.GetQueue();
        Assert.Equal(S(tracks[0]), q.CurrentTrackId);
        Assert.Equal(3, q.UpNext.Count);

        host.Playback.SetUpNext(new[] { S(tracks[7]), "not-an-id", Guid.NewGuid().ToString("D"), S(tracks[5]) });
        Assert.Equal(tracks[0].Id, player.CurrentTrack!.Id);
        Assert.Equal(new[] { tracks[7].Id, tracks[5].Id }, player.UpNext.Select(t => t.Id));
        Assert.False(player.IsShuffleEnabled); // the order is the plugin's now
        player.NextCommand.Execute(null);
        Assert.Equal(tracks[7].Id, player.CurrentTrack!.Id);
    }

    [AvaloniaFact]
    public void SetUpNext_LeavesOutExplicitSongs_WhileSettingsHideThem()
    {
        var (player, _, host, tracks) = Rig(new[] { "playback.control" });
        tracks[6].IsExplicit = true;
        player.ReplaceQueueAndPlay(tracks.Take(1).ToList(), 0);
        player.AllowExplicitContent = false;
        host.Playback.SetUpNext(new[] { S(tracks[6]), S(tracks[2]) });
        Assert.Equal(new[] { tracks[2].Id }, player.UpNext.Select(t => t.Id));
    }

    [AvaloniaFact]
    public void TracksQueued_SaysWhichSongsTheUserQueued_AndHow()
    {
        var (player, _, host, tracks) = Rig(new[] { "playback.control" });
        player.ReplaceQueueAndPlay(tracks.Take(3).ToList(), 0);
        var seen = new List<(string[] Ids, bool PlayNext)>();
        host.Playback.TracksQueued += (_, e) => seen.Add((e.TrackIds.ToArray(), e.PlayNext));

        player.AddToQueue(tracks[5]);
        player.AddNext(tracks[6]);
        player.AddRangeToQueue(new List<Track> { tracks[3], tracks[4] });
        player.AddNextRange(new List<Track> { tracks[7], tracks[1] });
        Assert.Equal(4, seen.Count);
        Assert.Equal(new[] { S(tracks[5]) }, seen[0].Ids);
        Assert.Equal(new[] { S(tracks[6]) }, seen[1].Ids);
        Assert.Equal(new[] { S(tracks[3]), S(tracks[4]) }, seen[2].Ids);
        Assert.Equal(new[] { S(tracks[7]), S(tracks[1]) }, seen[3].Ids);
        Assert.Equal(new[] { false, true, false, true }, seen.Select(s => s.PlayNext));

        // Its own SetUpNext is not the user queueing anything.
        host.Playback.SetUpNext(new[] { S(tracks[2]) });
        Assert.Equal(4, seen.Count);
    }

    [AvaloniaFact]
    public void TheQueueMembers_NeedPlaybackControl()
    {
        var (_, _, host, _) = Rig(Array.Empty<string>());
        Assert.Throws<PluginPermissionException>(() => host.Playback.GetQueue());
    }

    [Fact]
    public void TrackInfo_CarriesTheShuffleFlags()
    {
        var until = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = T("x");
        t.IsDisliked = true;
        t.IsExplicit = true;
        t.SkipWhenShuffling = true;
        t.SnoozedUntil = until;
        var info = PluginHost.ToTrackInfo(t);
        Assert.True(info.IsDisliked && info.IsExplicit && info.SkipWhenShuffling);
        Assert.Equal(new DateTimeOffset(until), info.SnoozedUntil);
        Assert.Null(PluginHost.ToTrackInfo(T("y")).SnoozedUntil);
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
        public string AppVersion => "1.6.5";
        public string DataDirectory => "";
        public INowPlaying NowPlaying => throw new NotImplementedException();
        public IBeatSource Beat => throw new NotImplementedException();
        public ISpectrumSource Spectrum => throw new NotImplementedException();
        public void Log(string message) { }
        public void RegisterVisualLayer(IVisualLayerProvider provider) { }
    }
}

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin lyrics providers in the real search: they fill in when the built-ins miss, rank
/// after them on ties, and a failing one never turns a built-in hit into an error.
/// </summary>
public class PluginLyricsSearchTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    public void Dispose() => _box.Dispose();

    private sealed class Lrc : ILrcLibService
    {
        public LrcLibResult? Answer;
        public int Calls;
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        { Calls++; return Task.FromResult(Answer); }
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class NetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class Meta : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private (LyricsViewModel Vm, Track Track, PluginHostApiTests.FixedLyrics Provider, Lrc LrcLib) Mount(PluginLyrics? pluginAnswer)
    {
        _box.Approve(("dev.test.lyrics", new[] { "lyrics.provider" }));
        var host = _box.NewHost();
        var provider = new PluginHostApiTests.FixedLyrics("Plugin Lyrics", pluginAnswer);
        _box.AddInProcess(host, new ScriptedPlugin { OnInit = h => h.RegisterLyricsProvider(provider) },
            PluginSandbox.Manifest(id: "dev.test.lyrics", permissions: new[] { "lyrics.provider" }));

        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var lrc = new Lrc();
        var vm = new LyricsViewModel(player, lrc, new NetEase(), new Meta(), new TestPersistenceService(), new FakeLibraryService())
        {
            PluginLyricsSources = () => host.LyricsProviders,
        };
        var track = new Track
        {
            Title = "Song", Artist = "Band", Album = "Record", Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(_box.Root, "song.mp3"),
        };
        Directory.CreateDirectory(_box.Root);
        player.CurrentTrack = track;
        return (vm, track, provider, lrc);
    }

    private static async Task SearchAsync(LyricsViewModel vm, Track track, PluginHostApiTests.FixedLyrics provider)
    {
        vm.SearchLyricsForTrack(track);
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && (provider.Last is null || vm.IsSearching))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private void Cleanup(Track track)
    {
        var lrc = Path.ChangeExtension(track.FilePath, ".lrc");
        try { if (File.Exists(lrc)) File.Delete(lrc); } catch { }
        AppWrittenSidecarRegistry.Default.Remove(lrc);
    }

    [AvaloniaFact]
    public async Task PluginProvider_FillsIn_WhenTheBuiltInsMiss()
    {
        var (vm, track, provider, _) = Mount(new PluginLyrics("[00:01.00]from the plugin", null));
        try
        {
            await SearchAsync(vm, track, provider);
            Assert.Equal("Plugin Lyrics", vm.LyricsSourceName);
            Assert.True(vm.IsSynced);
            Assert.Equal(("Band", "Song", "Record"), (provider.Last!.Artist, provider.Last.Title, provider.Last.Album));
        }
        finally { Cleanup(track); }
    }

    [AvaloniaFact]
    public async Task BuiltIn_WinsATie_PluginOfferedAsTheAlternate()
    {
        var (vm, track, provider, lrc) = Mount(new PluginLyrics("[00:01.00]plugin", null));
        lrc.Answer = new LrcLibResult { TrackName = "Song", ArtistName = "Band", Duration = 200, SyncedLyrics = "[00:01.00]lrclib" };
        try
        {
            await SearchAsync(vm, track, provider);
            Assert.Equal("LRCLIB", vm.LyricsSourceName);
            Assert.True(vm.HasAlternateLyrics);
            Assert.Equal("Try Plugin Lyrics", vm.AlternateLyricsLabel);
        }
        finally { Cleanup(track); }
    }

    [AvaloniaFact]
    public async Task FailingPluginProvider_DoesNotSpoilABuiltInHit()
    {
        var (vm, track, provider, lrc) = Mount(null);
        provider.Throw = true;
        lrc.Answer = new LrcLibResult { TrackName = "Song", ArtistName = "Band", Duration = 200, PlainLyrics = "words" };
        try
        {
            await SearchAsync(vm, track, provider);
            Assert.Equal("LRCLIB", vm.LyricsSourceName);
            Assert.Equal(string.Empty, vm.SearchFailedMessage);
        }
        finally { Cleanup(track); }
    }

    [Fact]
    public void PickBestResult_GeneralisesTheTwoWayRule()
    {
        LrcLibResult Synced() => new() { SyncedLyrics = "[00:01.00]x" };
        LrcLibResult Plain() => new() { PlainLyrics = "x" };

        var r = LyricsViewModel.PickBestResult(new List<(LrcLibResult?, string)> { (Plain(), "LRCLIB"), (Synced(), "NetEase"), (Synced(), "P") });
        Assert.Equal(("NetEase", "P"), (r.PrimarySource, r.AlternateSource));

        r = LyricsViewModel.PickBestResult(new List<(LrcLibResult?, string)> { (null, "LRCLIB"), (null, "NetEase"), (Plain(), "P") });
        Assert.Equal(("P", (string?)null), (r.PrimarySource, r.AlternateSource));

        r = LyricsViewModel.PickBestResult(new List<(LrcLibResult?, string)> { (Plain(), "LRCLIB"), (Plain(), "NetEase") });
        Assert.Equal(("LRCLIB", "NetEase"), (r.PrimarySource, r.AlternateSource));

        r = LyricsViewModel.PickBestResult(new List<(LrcLibResult?, string)> { (null, "LRCLIB"), (new LrcLibResult(), "NetEase") });
        Assert.Null(r.Primary);
    }
}

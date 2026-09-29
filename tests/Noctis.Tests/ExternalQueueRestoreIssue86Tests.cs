using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #86: with "Import dropped files" off a drop plays from where it is, as tracks the
/// library does not know. Their Ids are minted per session, so queue.json's Id lists could
/// never resolve them again — the queue, the island and Home's Continue / Last Played came
/// back empty after a restart. queue.json now carries their file paths.
/// </summary>
public class ExternalQueueRestoreIssue86Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Reads any file as a track named after it — enough to prove the re-read.</summary>
    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => new()
        {
            Title = Path.GetFileNameWithoutExtension(filePath),
            Artist = "Dropped",
            FilePath = filePath,
            AlbumId = Guid.NewGuid(),
            Duration = TimeSpan.FromMinutes(3),
        };
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return ReadTrackMetadata(filePath); }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private sealed class FakePlayHistory : IPlayHistoryService
    {
        public List<PlayHistoryEvent> Log { get; } = new();
        public IReadOnlyList<PlayHistoryEvent> Events => Log;
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) => Log.Add(new PlayHistoryEvent { TrackId = track.Id, Title = track.Title, PlayedAtUtc = DateTime.UtcNow });
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private string AudioFile(string name, bool create = true)
    {
        var dir = Path.Combine(_root, "outside");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".mp3");
        if (create) File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static Track External(string path) => new()
    {
        Id = Guid.NewGuid(),
        Title = Path.GetFileNameWithoutExtension(path),
        Artist = "Dropped",
        FilePath = path,
        Duration = TimeSpan.FromMinutes(3),
        IsExternal = true,
    };

    [Fact]
    public async Task QueueState_RoundTripsExternalPaths_AndOldFilesLoadWithoutThem()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var id = Guid.NewGuid();
        await persistence.SaveQueueStateAsync(new QueueState
        {
            CurrentTrackId = id,
            ExternalTrackPaths = new() { [id] = @"D:\Music\Outside\song.flac" },
        });
        var loaded = await persistence.LoadQueueStateAsync();
        Assert.Equal(@"D:\Music\Outside\song.flac", loaded!.ExternalTrackPaths![id]);

        // A queue.json written before #86 has no such field.
        var oldRoot = Path.Combine(_root, "old");
        Directory.CreateDirectory(oldRoot);
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "queue.json"),
            $$"""{ "currentTrackId": "{{id}}", "positionSeconds": 12, "upNextIds": [], "historyIds": [] }""");
        var old = await new PersistenceService(oldRoot).LoadQueueStateAsync();
        Assert.Equal(id, old!.CurrentTrackId);
        Assert.Null(old.ExternalTrackPaths);
    }

    [AvaloniaFact]
    public async Task Restore_ReReadsExternalTracks_KeepingTheirIds_AndPrefersALibraryCopy()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var a = External(AudioFile("a"));
        var b = External(AudioFile("b"));
        var gone = External(AudioFile("gone", create: false));   // deleted since
        var imported = External(AudioFile("imported"));          // scanned into the library since

        var before = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        before.ReplaceQueueAndPlay(new List<Track> { a, b, gone, imported }, 0);
        await before.SaveQueueStateAsync();

        var library = new FakeLibraryService();
        var libraryCopy = new Track { Id = Guid.NewGuid(), Title = "imported (library)", FilePath = imported.FilePath, Duration = TimeSpan.FromMinutes(3) };
        library.TrackList.Add(libraryCopy);
        var after = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService(), new StubMetadata());
        await after.RestoreQueueStateAsync();

        Assert.NotNull(after.CurrentTrack);
        Assert.Equal(a.Id, after.CurrentTrack!.Id);
        Assert.True(after.CurrentTrack.IsExternal);
        Assert.Equal("a", after.CurrentTrack.Title);
        Assert.True(after.HasContent);

        Assert.Equal(new[] { b.Id, libraryCopy.Id }, after.UpNext.Select(t => t.Id));
        Assert.True(after.UpNext[0].IsExternal);
        Assert.Same(libraryCopy, after.UpNext[1]);

        var known = after.GetExternalTracksById();
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(g => g), known.Keys.OrderBy(g => g));
    }

    [AvaloniaFact]
    public async Task HomeLastPlayed_ResolvesTheRestoredExternalTracks()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var a = External(AudioFile("Volví"));
        var b = External(AudioFile("Tití"));
        var before = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        before.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);
        await before.SaveQueueStateAsync();

        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService(), new StubMetadata());
        await player.RestoreQueueStateAsync();

        var log = new FakePlayHistory();
        log.RecordPlay(b);
        log.RecordPlay(a); // newest
        var home = new HomeViewModel(player, library, new SidebarViewModel(new TestPersistenceService(), library), playHistory: log);
        await home.RefreshAsync();

        Assert.Equal(new[] { "Volví", "Tití" }, home.LastPlayed.Select(t => t.Title));
        Assert.Equal(a.Id, home.ContinueTrack?.Id);
    }

    [Fact]
    public void ToggleQueue_DefaultsToCtrlU_AndNoTwoDefaultsCollide()
    {
        Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Control), ShortcutDefaults.For(ShortcutAction.ToggleQueue, isMac: false));
        Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Meta), ShortcutDefaults.For(ShortcutAction.ToggleQueue, isMac: true));
        foreach (var isMac in new[] { false, true })
        {
            var gestures = ShortcutDefaults.All.Select(d => ShortcutDefaults.For(d.Action, isMac)).ToList();
            Assert.Equal(gestures.Count, gestures.Distinct().Count());
        }
    }
}

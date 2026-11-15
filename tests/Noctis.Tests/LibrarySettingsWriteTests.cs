using Noctis.Models;
using Noctis.Services;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// Removing a song writes its path to settings.json's exclusions (and may drop a music folder
/// that is gone). It loaded the settings, pruned the exclusions against the disk and saved the
/// whole file, with only the save inside the settings.json gate: a save from elsewhere that
/// landed in between (a Settings toggle, Reset's defaults) was undone by the stale copy.
/// </summary>
public class LibrarySettingsWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public LibrarySettingsWriteTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(_music);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task RemovingASong_DoesNotUndoASaveFromElsewhere()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var path = Path.Combine(_music, "a.flac");
        File.Move(CreateFlac(_music), path);
        var id = Guid.NewGuid();
        await persistence.SaveLibraryAsync(new List<Track>
        {
            new()
            {
                Id = id,
                FilePath = path,
                Title = "Ivy",
                Artist = "Frank Ocean",
                AlbumArtist = "Frank Ocean",
                Album = "Blonde",
                AlbumId = Track.ComputeAlbumId("Frank Ocean", "Blonde"),
                SourceType = SourceType.Local,
            }
        });
        await persistence.SaveSettingsAsync(new AppSettings { MetadataSchemaVersion = 13, MusicFolders = new List<string> { _music } });
        var library = new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence),
            new FolderMetadataBackfillTests.FakeAuditTrail());
        await library.LoadAsync();
        await library.BackgroundInit;

        // Another writer holds the file while the song is removed.
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var other = Task.Run(() => persistence.UpdateSettingsAsync(s =>
        {
            holding.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            s.Theme = "Ink";
        }));
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var removal = library.RemoveTrackAsync(id);
        // Time for an ungated load to read the file before the other save lands.
        await Task.Delay(300);
        release.Set();
        await other.WaitAsync(TimeSpan.FromSeconds(10));
        await removal.WaitAsync(TimeSpan.FromSeconds(10));

        var onDisk = await persistence.LoadSettingsAsync();
        Assert.Equal("Ink", onDisk.Theme);
        Assert.Contains(path, onDisk.ExcludedFilePaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { _music }, onDisk.MusicFolders);
    }
}

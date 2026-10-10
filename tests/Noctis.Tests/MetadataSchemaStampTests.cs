using Noctis.Models;
using Noctis.Services;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// The metadata-schema migration records its version in settings.json once its backfills
/// are done. Those backfills can run for minutes, so the stamp must not write back the
/// settings snapshot taken before them: that reverted whatever else was saved meanwhile.
/// </summary>
public class MetadataSchemaStampTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public MetadataSchemaStampTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(_music);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // Runs a settings save from inside the v10 cover pass — a track removed (its path
    // excluded) while the migration is still reading files.
    private sealed class SaveDuringBackfill : MetadataService, IMetadataService
    {
        public Action? During;
        private int _ran;

        byte[]? IMetadataService.ExtractEmbeddedArt(string filePath)
        {
            if (Interlocked.Exchange(ref _ran, 1) == 0) During?.Invoke();
            return ExtractEmbeddedArt(filePath);
        }
    }

    [Fact]
    public async Task Stamp_KeepsSettingsSavedWhileTheBackfillsRan()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var path = Path.Combine(_music, "a.flac");
        File.Move(CreateFlac(_music), path);
        await persistence.SaveLibraryAsync(new List<Track>
        {
            new()
            {
                Id = Guid.NewGuid(),
                FilePath = path,
                Title = "Ivy",
                Artist = "Frank Ocean",
                AlbumArtist = "Frank Ocean",
                Album = "Blonde",
                AlbumId = Track.ComputeAlbumId("Frank Ocean", "Blonde"),
                SourceType = SourceType.Local,
                Label = "Boys Don't Cry",
            }
        });
        await persistence.SaveSettingsAsync(new AppSettings { MetadataSchemaVersion = 9 });

        const string removed = @"D:\Music\removed.flac";
        var metadata = new SaveDuringBackfill
        {
            During = () =>
            {
                var s = persistence.LoadSettingsAsync().GetAwaiter().GetResult();
                s.ExcludedFilePaths.Add(removed);
                persistence.SaveSettingsAsync(s).GetAwaiter().GetResult();
            }
        };
        var library = new LibraryService(metadata, persistence, new SqliteLibraryIndexService(persistence),
            new FolderMetadataBackfillTests.FakeAuditTrail());

        await library.LoadAsync();
        await library.BackgroundInit;
        await library.LabelBackfill;

        var onDisk = await persistence.LoadSettingsAsync();
        Assert.Contains(removed, onDisk.ExcludedFilePaths);
        Assert.Equal(11, onDisk.MetadataSchemaVersion);
    }
}

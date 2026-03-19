using Noctis.Models;
using Noctis.Services;

namespace Noctis.Tests;

internal sealed class TestPersistenceService : IPersistenceService, IDisposable
{
    public string DataDirectory { get; }
    private readonly string _root;

    public TestPersistenceService()
    {
        _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        DataDirectory = _root;
        Directory.CreateDirectory(_root);
    }

    public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());
    public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
    public Task<List<Track>?> LoadLibraryAsync() => Task.FromResult<List<Track>?>(new List<Track>());
    public Task SaveLibraryAsync(List<Track> tracks) => Task.CompletedTask;
    public Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist>());
    public Task SavePlaylistsAsync(List<Playlist> playlists) => Task.CompletedTask;
    public Task<QueueState?> LoadQueueStateAsync() => Task.FromResult<QueueState?>(null);
    public Task SaveQueueStateAsync(QueueState state) => Task.CompletedTask;
    public Task<LibraryIndexCache?> LoadIndexCacheAsync() => Task.FromResult<LibraryIndexCache?>(null);
    public Task SaveIndexCacheAsync(LibraryIndexCache cache) => Task.CompletedTask;

    public string GetArtworkPath(Guid albumId) => Path.Combine(_root, "artwork", $"{albumId}.jpg");
    public void SaveArtwork(Guid albumId, byte[] imageData) { }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch
        {
            // Ignore cleanup race locks in tests.
        }
    }
}


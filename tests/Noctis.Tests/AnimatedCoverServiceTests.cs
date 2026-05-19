using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class AnimatedCoverServiceTests : IDisposable
{
    private readonly TestPersistenceService _persistence = new();
    private readonly string _libraryRoot;
    private readonly AnimatedCoverService _svc;

    public AnimatedCoverServiceTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), "NoctisAnim", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryRoot);
        _svc = new AnimatedCoverService(_persistence);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        try { if (Directory.Exists(_libraryRoot)) Directory.Delete(_libraryRoot, true); } catch { }
    }

    private Track NewTrack(out string folder)
    {
        folder = Path.Combine(_libraryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var audioPath = Path.Combine(folder, "song.flac");
        File.WriteAllBytes(audioPath, new byte[] { 0 });
        return new Track
        {
            FilePath = audioPath,
            AlbumId = Guid.NewGuid(),
            Id = Guid.NewGuid(),
            Album = "X",
            AlbumArtist = "Y"
        };
    }

    private static void Touch(string path) => File.WriteAllBytes(path, new byte[] { 1 });

    [Fact]
    public void Resolve_PrefersTrackSidecar_OverAlbumSidecar()
    {
        var t = NewTrack(out var folder);
        Touch(Path.Combine(folder, "song.mp4"));
        Touch(Path.Combine(folder, "cover.mp4"));

        var result = _svc.Resolve(t);

        Assert.Equal(Path.Combine(folder, "song.mp4"), result);
    }

    [Fact]
    public void Resolve_FallsBackToAlbumSidecar()
    {
        var t = NewTrack(out var folder);
        Touch(Path.Combine(folder, "cover.webm"));

        var result = _svc.Resolve(t);

        Assert.Equal(Path.Combine(folder, "cover.webm"), result);
    }

    [Fact]
    public void Resolve_FallsBackToTrackCache()
    {
        var t = NewTrack(out _);
        var cachePath = _persistence.GetAnimatedCoverPath(t.AlbumId, t.Id, ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        Touch(cachePath);

        var result = _svc.Resolve(t);

        Assert.Equal(cachePath, result);
    }

    [Fact]
    public void Resolve_FallsBackToAlbumCache()
    {
        var t = NewTrack(out _);
        var cachePath = _persistence.GetAnimatedCoverPath(t.AlbumId, null, ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        Touch(cachePath);

        var result = _svc.Resolve(t);

        Assert.Equal(cachePath, result);
    }

    [Fact]
    public void Resolve_PrefersTrackCacheOverAlbumCache()
    {
        var t = NewTrack(out _);
        var trackCache = _persistence.GetAnimatedCoverPath(t.AlbumId, t.Id, ".mp4");
        var albumCache = _persistence.GetAnimatedCoverPath(t.AlbumId, null, ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(trackCache)!);
        Touch(trackCache);
        Touch(albumCache);

        var result = _svc.Resolve(t);

        Assert.Equal(trackCache, result);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNothingPresent()
    {
        var t = NewTrack(out _);
        Assert.Null(_svc.Resolve(t));
    }

    [Fact]
    public async Task ImportAsync_Track_WritesTrackScopedCacheFile()
    {
        var t = NewTrack(out var folder);
        var src = Path.Combine(folder, "src.mp4");
        File.WriteAllBytes(src, new byte[] { 9, 9, 9 });

        var dst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Track);

        Assert.Equal(_persistence.GetAnimatedCoverPath(t.AlbumId, t.Id, ".mp4"), dst);
        Assert.True(File.Exists(dst));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(dst));
    }

    [Fact]
    public async Task ImportAsync_Album_WritesAlbumScopedCacheFile()
    {
        var t = NewTrack(out var folder);
        var src = Path.Combine(folder, "src.webm");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });

        var dst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Album);

        Assert.Equal(_persistence.GetAnimatedCoverPath(t.AlbumId, null, ".webm"), dst);
        Assert.True(File.Exists(dst));
    }

    [Fact]
    public async Task ImportAsync_Overwrites_ExistingEntry()
    {
        var t = NewTrack(out var folder);
        var src1 = Path.Combine(folder, "a.mp4"); File.WriteAllBytes(src1, new byte[] { 1 });
        var src2 = Path.Combine(folder, "b.mp4"); File.WriteAllBytes(src2, new byte[] { 2 });

        await _svc.ImportAsync(t, src1, AnimatedCoverScope.Album);
        var dst = await _svc.ImportAsync(t, src2, AnimatedCoverScope.Album);

        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(dst));
    }

    [Fact]
    public async Task Remove_Track_DeletesOnlyTrackEntry()
    {
        var t = NewTrack(out var folder);
        var src = Path.Combine(folder, "src.mp4"); File.WriteAllBytes(src, new byte[] { 1 });
        var trackDst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Track);
        var albumDst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Album);

        _svc.Remove(t, AnimatedCoverScope.Track);

        Assert.False(File.Exists(trackDst));
        Assert.True(File.Exists(albumDst));
    }

    [Fact]
    public async Task Remove_Album_DeletesOnlyAlbumEntry()
    {
        var t = NewTrack(out var folder);
        var src = Path.Combine(folder, "src.mp4"); File.WriteAllBytes(src, new byte[] { 1 });
        var trackDst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Track);
        var albumDst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Album);

        _svc.Remove(t, AnimatedCoverScope.Album);

        Assert.True(File.Exists(trackDst));
        Assert.False(File.Exists(albumDst));
    }

    [Fact]
    public void Remove_DoesNotDeleteSidecars()
    {
        var t = NewTrack(out var folder);
        var sidecar = Path.Combine(folder, "song.mp4");
        Touch(sidecar);

        _svc.Remove(t, AnimatedCoverScope.Track);
        _svc.Remove(t, AnimatedCoverScope.Album);

        Assert.True(File.Exists(sidecar));
    }
}

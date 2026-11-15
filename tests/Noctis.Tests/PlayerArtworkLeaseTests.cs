using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The player decodes a cache-missed cover on a pool thread and hands it to the UI thread in a
/// post. Without a hold the cache could evict and dispose the bitmap in between (other decodes
/// filling the byte budget while the UI thread is busy), and the player then showed a disposed
/// bitmap: the race ArtworkCache's acquire overload exists for, which CachedImage already used.
/// </summary>
[Collection("ArtworkCache")]
public class PlayerArtworkLeaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("noctis-player-art").FullName;

    public PlayerArtworkLeaseTests()
    {
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
        // One 512 px cover (1 MiB) fits, a second one overflows and evicts the oldest.
        ArtworkCache.MaxCacheBytes = 1536L * 1024;
        ArtworkCache.DecoderOverride = (_, width) =>
            new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
        ArtworkCache.MaxCacheBytes = 128L * 1024 * 1024;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static bool IsDisposed(Bitmap bitmap)
    {
        try { _ = bitmap.PixelSize; return false; }
        catch (ObjectDisposedException) { return true; }
        catch (NullReferenceException) { return true; }
    }

    [AvaloniaFact]
    public void CoverEvictedBeforeTheUiThreadTakesIt_IsNotDisposedUnderThePlayer()
    {
        var cover = Path.Combine(_dir, "cover.jpg");
        File.WriteAllBytes(cover, new byte[16]);
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var track = new Track { Title = "t", AlbumId = Guid.NewGuid(), AlbumArtworkPath = cover, FilePath = Path.Combine(_dir, "t.mp3") };

        // Cache miss: the decode runs on a pool thread, its result is posted to this (UI) thread.
        typeof(PlayerViewModel).GetMethod("LoadAlbumArt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(player, [track]);

        // The UI thread is busy (no jobs run) while the decode lands in the cache...
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ArtworkCache.TryGet(cover, 512) == null && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.NotNull(ArtworkCache.TryGet(cover, 512));
        Thread.Sleep(100); // ...and posts it.

        // ...and another cover overflows the budget: the player's (oldest) is evicted.
        var other = Path.Combine(_dir, "other.jpg");
        Assert.NotNull(ArtworkCache.LoadAndCache(other, 512));
        Assert.Null(ArtworkCache.TryGet(cover, 512));

        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(player.AlbumArt);
        Assert.False(IsDisposed(player.AlbumArt!));

        // The player holds it now: the next cover lets it go, and only then is it freed.
        var held = player.AlbumArt!;
        player.AlbumArt = null;
        Assert.True(IsDisposed(held));
    }
}

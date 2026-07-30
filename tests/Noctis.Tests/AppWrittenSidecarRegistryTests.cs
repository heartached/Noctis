using System;
using System.IO;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The app-written sidecar registry must survive a restart: the old in-memory
/// HashSet emptied on every launch, so RemoveLyrics treated the app's own
/// auto-written .lrc as the user's file and never deleted it — the sidecar probe
/// then resurrected the removed lyrics on every subsequent play.
/// A "restart" here is simply a fresh registry instance over the same file.
/// </summary>
public sealed class AppWrittenSidecarRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public AppWrittenSidecarRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "noctis-sidecar-reg-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "app_written_sidecars.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string SidecarPath(string name) => TestPaths.Primary("Music", name);

    [Fact]
    public void Add_PersistsAcrossRestart()
    {
        var path = SidecarPath("Song.lrc");
        new AppWrittenSidecarRegistry(_file).Add(path);

        var reloaded = new AppWrittenSidecarRegistry(_file);
        Assert.True(reloaded.Contains(path));
    }

    [Fact]
    public void Remove_AfterRestart_ReturnsTrue_AndStaysRemoved()
    {
        var path = SidecarPath("Song.lrc");
        new AppWrittenSidecarRegistry(_file).Add(path);

        // The Bug A repro: RemoveLyrics only deletes the sidecar when Remove
        // returns true — which it never could after a restart.
        var afterRestart = new AppWrittenSidecarRegistry(_file);
        Assert.True(afterRestart.Remove(path));

        // The removal itself must persist too, or a later Remove re-deletes a
        // file the user may have replaced with their own.
        var secondRestart = new AppWrittenSidecarRegistry(_file);
        Assert.False(secondRestart.Contains(path));
        Assert.False(secondRestart.Remove(path));
    }

    [Fact]
    public void Remove_UnregisteredPath_ReturnsFalse()
    {
        var registry = new AppWrittenSidecarRegistry(_file);
        Assert.False(registry.Remove(SidecarPath("UsersOwn.lrc")));
    }

    [Fact]
    public void Contains_IsCaseInsensitive_MatchingRemoveLyricsSemantics()
    {
        var registry = new AppWrittenSidecarRegistry(_file);
        registry.Add(SidecarPath("Song.lrc"));
        Assert.True(registry.Contains(SidecarPath("song.LRC")));
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        var registry = new AppWrittenSidecarRegistry(_file);
        Assert.False(registry.Contains(SidecarPath("Song.lrc")));
    }

    [Fact]
    public void CorruptFile_DegradesToEmpty_NotThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "{ not json ]");

        var registry = new AppWrittenSidecarRegistry(_file);
        // Degrading to "nothing is ours" means Remove leaves files behind rather
        // than ever deleting a user's own sidecar.
        Assert.False(registry.Remove(SidecarPath("Song.lrc")));

        // And the registry must still be writable afterwards.
        var path = SidecarPath("Other.lrc");
        registry.Add(path);
        Assert.True(new AppWrittenSidecarRegistry(_file).Contains(path));
    }

    [Fact]
    public void Add_IsIdempotent()
    {
        var path = SidecarPath("Song.lrc");
        var registry = new AppWrittenSidecarRegistry(_file);
        registry.Add(path);
        registry.Add(path);

        var reloaded = new AppWrittenSidecarRegistry(_file);
        Assert.True(reloaded.Remove(path));
        Assert.False(reloaded.Remove(path));
    }
}

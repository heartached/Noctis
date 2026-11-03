using System;
using System.IO;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The bundled libvlc ships no plugins.dat, so every plugin DLL stayed loaded (324 of them
/// at idle). The app has libvlc build the cache once and keeps a fingerprint beside it;
/// these pin when it asks for a rebuild.
/// </summary>
public class VlcPluginCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-vlccache-" + Guid.NewGuid().ToString("N"));

    public VlcPluginCacheTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "codec"));
        File.WriteAllText(Path.Combine(_dir, "codec", "liba_plugin.dll"), "a");
        File.WriteAllText(Path.Combine(_dir, "codec", "libb_plugin.dll"), "bb");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void NoCache_NeedsRebuild()
    {
        var fp = VlcPluginCache.Fingerprint(_dir);
        Assert.True(VlcPluginCache.NeedsRebuild(_dir, fp));
    }

    [Fact]
    public void RecordedCache_IsUpToDate_UntilAPluginChanges()
    {
        var started = DateTime.UtcNow;
        File.WriteAllText(Path.Combine(_dir, VlcPluginCache.CacheFileName), "cache");
        var fp = VlcPluginCache.Fingerprint(_dir);
        Assert.True(VlcPluginCache.RecordBuilt(_dir, fp, started));
        Assert.False(VlcPluginCache.NeedsRebuild(_dir, fp));

        // An update replaced a plugin: new size -> new fingerprint -> rebuild.
        File.WriteAllText(Path.Combine(_dir, "codec", "libb_plugin.dll"), "bbb");
        Assert.True(VlcPluginCache.NeedsRebuild(_dir, VlcPluginCache.Fingerprint(_dir)));

        // So does an added plugin.
        File.WriteAllText(Path.Combine(_dir, "codec", "libc_plugin.dll"), "c");
        Assert.NotEqual(fp, VlcPluginCache.Fingerprint(_dir));
    }

    [Fact]
    public void CacheDeleted_NeedsRebuild_EvenWithAStamp()
    {
        var started = DateTime.UtcNow;
        var cache = Path.Combine(_dir, VlcPluginCache.CacheFileName);
        File.WriteAllText(cache, "cache");
        var fp = VlcPluginCache.Fingerprint(_dir);
        Assert.True(VlcPluginCache.RecordBuilt(_dir, fp, started));
        File.Delete(cache);
        Assert.True(VlcPluginCache.NeedsRebuild(_dir, fp));
    }

    [Fact]
    public void SaveThatDidNotHappen_IsNotRecorded()
    {
        // libvlc could not write the cache (read-only folder): no stamp, so the next start retries.
        var fp = VlcPluginCache.Fingerprint(_dir);
        Assert.False(VlcPluginCache.RecordBuilt(_dir, fp, DateTime.UtcNow));
        Assert.False(File.Exists(Path.Combine(_dir, VlcPluginCache.StampFileName)));

        // An old cache left from before the rebuild started does not count either.
        var cache = Path.Combine(_dir, VlcPluginCache.CacheFileName);
        File.WriteAllText(cache, "old");
        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddHours(-1));
        Assert.False(VlcPluginCache.RecordBuilt(_dir, fp, DateTime.UtcNow));
    }

    [Fact]
    public void Fingerprint_IgnoresTheCacheFilesThemselves()
    {
        var fp = VlcPluginCache.Fingerprint(_dir);
        File.WriteAllText(Path.Combine(_dir, VlcPluginCache.CacheFileName), "cache");
        File.WriteAllText(Path.Combine(_dir, VlcPluginCache.StampFileName), "stamp");
        Assert.Equal(fp, VlcPluginCache.Fingerprint(_dir));
    }
}

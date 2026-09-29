using Noctis.Services.Waveform;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #93 waveform cache: keys follow the file's identity (path + size + last write),
/// disk entries round-trip and reject anything that does not match, the memory tier is a
/// bounded LRU, and the folder is trimmed oldest-first under its size cap.
/// </summary>
public sealed class WaveformCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "waveform-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static WaveformData Sample(byte level, int buckets = 16)
    {
        var peaks = new byte[buckets];
        var rms = new byte[buckets];
        Array.Fill(peaks, level);
        Array.Fill(rms, (byte)(level / 2));
        return new WaveformData(peaks, rms);
    }

    private static WaveformFileIdentity Id(string path, long size = 1000, long ticks = 638000000000000000)
        => new(path, size, ticks);

    [Fact]
    public void Key_ChangesWithSizeOrLastWrite_SoAnEditedFileMisses()
    {
        var a = WaveformCache.KeyFor(Id(@"C:\music\a.flac"));
        Assert.Equal(a, WaveformCache.KeyFor(Id(@"C:\music\a.flac")));
        Assert.NotEqual(a, WaveformCache.KeyFor(Id(@"C:\music\a.flac", size: 1001)));
        Assert.NotEqual(a, WaveformCache.KeyFor(Id(@"C:\music\a.flac", ticks: 638000000000000001)));
        Assert.NotEqual(a, WaveformCache.KeyFor(Id(@"C:\music\b.flac")));
    }

    [Fact]
    public void Key_FollowsThePlatformsPathCaseRules()
    {
        var lower = WaveformCache.KeyFor(Id(Path.Combine(Path.GetTempPath(), "music", "song.flac")));
        var upper = WaveformCache.KeyFor(Id(Path.Combine(Path.GetTempPath(), "MUSIC", "SONG.FLAC")));
        if (OperatingSystem.IsLinux()) Assert.NotEqual(lower, upper);
        else Assert.Equal(lower, upper);
    }

    [Fact]
    public void StoreThenLoad_RoundTripsThroughDisk()
    {
        var id = Id(@"C:\music\a.flac");
        new WaveformCache(_dir).Store(id, Sample(200));

        // A fresh instance has an empty memory tier: this is a disk read.
        var loaded = new WaveformCache(_dir).TryLoad(id);
        Assert.NotNull(loaded);
        Assert.Equal(Sample(200).Peaks.ToArray(), loaded!.Peaks.ToArray());
        Assert.Equal(Sample(200).Rms.ToArray(), loaded.Rms.ToArray());
    }

    [Fact]
    public void Parse_RejectsAnEntryWhoseRecordedIdentityDiffers()
    {
        var id = Id(@"C:\music\a.flac");
        var bytes = WaveformCache.Serialize(Sample(10), id);
        Assert.NotNull(WaveformCache.Parse(bytes, id));
        Assert.Null(WaveformCache.Parse(bytes, id with { Size = id.Size + 1 }));
        Assert.Null(WaveformCache.Parse(bytes, id with { LastWriteUtcTicks = id.LastWriteUtcTicks + 1 }));
        Assert.Null(WaveformCache.Parse(bytes.AsSpan(0, bytes.Length - 1).ToArray(), id)); // truncated
        bytes[0] = (byte)'X';
        Assert.Null(WaveformCache.Parse(bytes, id)); // not ours
    }

    [Fact]
    public void CorruptFile_IsAMissAndIsDeleted()
    {
        var id = Id(@"C:\music\a.flac");
        var cache = new WaveformCache(_dir);
        Directory.CreateDirectory(_dir);
        var file = cache.FilePathFor(WaveformCache.KeyFor(id));
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        Assert.Null(cache.TryLoad(id));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Store_RecreatesTheFolder_AfterClearCacheRemovedIt()
    {
        var cache = new WaveformCache(_dir);
        cache.Store(Id(@"C:\music\a.flac"), Sample(1));
        Directory.Delete(_dir, true);

        cache.Store(Id(@"C:\music\b.flac"), Sample(2));
        Assert.True(File.Exists(cache.FilePathFor(WaveformCache.KeyFor(Id(@"C:\music\b.flac")))));
    }

    [Fact]
    public void MemoryTier_IsABoundedLru()
    {
        var cache = new WaveformCache(_dir, memoryEntries: 2);
        var a = Id(@"C:\music\a.flac");
        var b = Id(@"C:\music\b.flac");
        var c = Id(@"C:\music\c.flac");
        cache.Store(a, Sample(1));
        cache.Store(b, Sample(2));
        Assert.True(cache.TryGetMemory(a, out _)); // a is now most recent
        cache.Store(c, Sample(3));                  // evicts b, the least recent

        Assert.True(cache.TryGetMemory(a, out _));
        Assert.False(cache.TryGetMemory(b, out _));
        Assert.True(cache.TryGetMemory(c, out _));
        Assert.NotNull(cache.TryLoad(b)); // still on disk
    }

    [Fact]
    public void Trim_DeletesTheLeastRecentlyWrittenFiles_UntilUnderTheCap()
    {
        const int buckets = 1000; // ~2 KB per file
        var cache = new WaveformCache(_dir, maxBytes: 10 * 2100);
        var ids = Enumerable.Range(0, 20).Select(i => Id($@"C:\music\{i}.flac")).ToList();
        var baseTime = DateTime.UtcNow.AddDays(-2);
        for (var i = 0; i < ids.Count; i++)
        {
            cache.Store(ids[i], Sample((byte)i, buckets));
            File.SetLastWriteTimeUtc(cache.FilePathFor(WaveformCache.KeyFor(ids[i])), baseTime.AddMinutes(i));
        }

        cache.Trim();

        var remaining = Directory.GetFiles(_dir, "*" + WaveformCache.Extension);
        Assert.True(remaining.Sum(f => new FileInfo(f).Length) <= cache.MaxBytes * 8 / 10);
        // The newest survive, the oldest are gone.
        Assert.True(File.Exists(cache.FilePathFor(WaveformCache.KeyFor(ids[^1]))));
        Assert.False(File.Exists(cache.FilePathFor(WaveformCache.KeyFor(ids[0]))));
    }

    [Fact]
    public void DiskHit_ReStampsAnOldEntry_SoTrimTreatsItAsRecentlyUsed()
    {
        var id = Id(@"C:\music\a.flac");
        new WaveformCache(_dir).Store(id, Sample(5));
        var cache = new WaveformCache(_dir);
        var file = cache.FilePathFor(WaveformCache.KeyFor(id));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-10));

        Assert.NotNull(cache.TryLoad(id));
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Identity_ResolvesRealFiles_AndMissesMissingOnes()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "song.bin");
        File.WriteAllBytes(path, new byte[123]);
        var id = WaveformFileIdentity.TryResolve(path);
        Assert.NotNull(id);
        Assert.Equal(123, id!.Value.Size);
        Assert.Null(WaveformFileIdentity.TryResolve(Path.Combine(_dir, "missing.flac")));
    }
}

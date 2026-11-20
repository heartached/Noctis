using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class PlaybackFileWarmerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-warm-" + Guid.NewGuid().ToString("N"));

    public PlaybackFileWarmerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeFile(string name, int bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task ReadsTheWholeFile()
    {
        var path = MakeFile("a.flac", 3_500_000);
        using var warmer = new PlaybackFileWarmer(startDelayMs: 0);

        warmer.Warm(path);
        await warmer.Current.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3_500_000, warmer.BytesRead);
    }

    [Fact]
    public async Task TheSamePathAgain_IsNotReadTwice()
    {
        var path = MakeFile("a.flac", 200_000);
        using var warmer = new PlaybackFileWarmer(startDelayMs: 0);

        warmer.Warm(path);
        var first = warmer.Current;
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        warmer.Warm(path);

        Assert.Same(first, warmer.Current);
    }

    [Fact]
    public async Task ANewSong_DropsTheReadOfTheOldOne()
    {
        var old = MakeFile("old.flac", 200_000);
        var next = MakeFile("next.flac", 300_000);
        using var warmer = new PlaybackFileWarmer(startDelayMs: 5_000);

        warmer.Warm(old);
        var oldRead = warmer.Current;
        using var quick = new PlaybackFileWarmer(startDelayMs: 0);
        warmer.Warm(null);

        // Cancelled inside its start delay: finishes at once and read nothing.
        await oldRead.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, warmer.BytesRead);

        quick.Warm(next);
        await quick.Current.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(300_000, quick.BytesRead);
    }

    [Fact]
    public async Task TheFileStaysFreeForEditsAndDeletes()
    {
        var path = MakeFile("a.flac", 4_000_000);
        using var warmer = new PlaybackFileWarmer(startDelayMs: 0);

        warmer.Warm(path);
        // While (or right after) it is read: a tag edit's write handle and a delete both work.
        using (var write = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
        File.Delete(path);
        await warmer.Current.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AMissingFile_IsIgnored()
    {
        using var warmer = new PlaybackFileWarmer(startDelayMs: 0);

        warmer.Warm(Path.Combine(_dir, "gone.flac"));
        await warmer.Current.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, warmer.BytesRead);
    }
}

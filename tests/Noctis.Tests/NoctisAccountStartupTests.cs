using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Noctis.Mobile.Services.Account;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's cold start with many downloads: the account service is built on the UI thread
/// before the first frame (AndroidApp), and it used to read every download's size and date there,
/// one file at a time. It lists the names only now; a song restored in the queue still plays its
/// file, and sizes and dates are read later, off that thread.
/// </summary>
public class NoctisAccountStartupTests : IDisposable
{
    private const int Downloads = 3000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "acc-start-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public NoctisAccountStartupTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string OfflineDir => Path.Combine(_dir, "offline");

    private Guid[] WriteDownloads()
    {
        Directory.CreateDirectory(OfflineDir);
        var ids = Enumerable.Range(0, Downloads).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) File.WriteAllBytes(Path.Combine(OfflineDir, id.ToString("N") + ".flac"), new byte[100]);
        return ids;
    }

    private NoctisAccountService Start(TestPersistenceService persistence) =>
        new(new FakeLibraryService(), persistence, NoctisHandlers.Sockets, Path.Combine(_dir, "account"), OfflineDir, "Test Phone", marshal: a => a());

    [Fact]
    public void ColdStart_With3000Downloads_ReadsNoFileBeforeTheFirstFrame_AndARestoredSongStillPlaysItsFile()
    {
        var ids = WriteDownloads();
        using var persistence = new TestPersistenceService();
        Start(persistence);                                     // warm the JIT and the file cache

        var clock = Stopwatch.StartNew();
        var svc = Start(persistence);
        clock.Stop();
        _out.WriteLine($"MEASURE cold start, {Downloads} downloads: constructor {clock.Elapsed.TotalMilliseconds:F1} ms, " +
                       $"{svc.DownloadsReadAtStart} files read for size/date");

        Assert.Equal(0, svc.DownloadsReadAtStart);
        Assert.Equal(Downloads, svc.DownloadedCount);
        // The song the queue restores (AndroidApp starts it before any sync) plays its download, not the stream.
        var restored = ids[1234];
        Assert.Equal(Path.GetFullPath(Path.Combine(OfflineDir, restored.ToString("N") + ".flac")),
            svc.ResolvePlaybackUri("noctis-remote://tr-" + restored.ToString("N")));
        // Sizes come in later (or on first ask), and are right.
        Assert.Equal(100L * Downloads, svc.DownloadedBytes);
    }
}

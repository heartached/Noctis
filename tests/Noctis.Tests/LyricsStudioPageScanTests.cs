using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics Studio page on a big library (live 11-25: "Loading" for ~55 s on 7,212 songs on a hard
/// drive). The queue opens once the scan has met its songs; the counts follow when it finishes.
/// </summary>
public class LyricsStudioPageScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-page-scan-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioPageScanTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string Lrc = "[00:05.00]First line here\n[00:12.50]Second line here\n";
    private const string Elrc = "[00:05.00]<00:05.00>First <00:05.40>line\n[00:12.50]<00:12.50>Second <00:13.00>line\n";
    private const string Ttml = "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"00:05.000\" end=\"00:08.000\">First line</p></div></body></tt>";

    /// <summary>A mixed library over several folders: .elrc, .lrc, .ttml, embedded timed, plain, nothing.</summary>
    private List<Track> Fixture(int count)
    {
        var tracks = new List<Track>();
        for (var i = 0; i < count; i++)
        {
            var dir = Path.Combine(_root, "album" + (i % 7));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"song{i}.flac");
            var track = new Track { Title = "song" + i, Artist = "A", FilePath = path };
            switch (i % 6)
            {
                case 0: File.WriteAllText(Path.ChangeExtension(path, ".elrc"), Elrc); break;
                case 1: File.WriteAllText(Path.ChangeExtension(path, ".lrc"), Lrc); break;
                case 2: File.WriteAllText(Path.ChangeExtension(path, ".ttml"), Ttml); break;
                case 3: track.SyncedLyrics = Lrc; break;
                case 4: track.Lyrics = "plain words"; break;
            }
            tracks.Add(track);
        }
        return tracks;
    }

    private static LyricsStudioViewModel Studio(string root, IReadOnlyList<Track> tracks) =>
        new(tracks, new LyricsStudioModelDownloadTests.ModelOnlyEngine(StudioTestModel.Installed(root)),
            new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });

    private LyricsStudioPageViewModel Page(FakeLibraryService library, bool wordTimings, LyricsStudioPageViewModel.DetectFormatsFunc? detect = null) =>
        new(library, () => wordTimings, tracks => Studio(_root, tracks), detect);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition());
    }

    [Fact]
    public void DetectFormats_ReportsEverySongInOrder_SameAsWithoutReports()
    {
        var tracks = Fixture(60);
        var reported = new List<(int, LyricsFormat)>();
        var withReports = ExistingLyricsLoader.DetectFormats(tracks, CancellationToken.None, (i, f) => reported.Add((i, f)));
        Assert.Equal(ExistingLyricsLoader.DetectFormats(tracks), withReports);
        Assert.Equal(withReports.Select((f, i) => (i, f)), reported);
        Assert.Equal(tracks.Select(ExistingLyricsLoader.DetectFormat), withReports);
    }

    [Fact]
    public void DetectFormats_RereadsASidecar_WhenItsSizeOrWriteTimeChanged()
    {
        var path = Path.Combine(_root, "song.flac");
        var lrc = Path.ChangeExtension(path, ".lrc");
        var track = new Track { Title = "song", Artist = "A", FilePath = path };
        File.WriteAllText(lrc, Lrc);
        Assert.Equal(LyricsFormat.Lrc, ExistingLyricsLoader.DetectFormats(new[] { track })[0]);

        File.WriteAllText(lrc, Elrc); // the Studio saved word timings over it
        Assert.Equal(LyricsFormat.Elrc, ExistingLyricsLoader.DetectFormats(new[] { track })[0]);

        // Same length, new text: the write time tells it apart.
        var plain = new string('x', Elrc.Length);
        File.WriteAllText(lrc, plain);
        File.SetLastWriteTimeUtc(lrc, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(LyricsFormat.None, ExistingLyricsLoader.DetectFormats(new[] { track })[0]);

        File.Delete(lrc);
        track.SyncedLyrics = Lrc;
        Assert.Equal(LyricsFormat.Lrc, ExistingLyricsLoader.DetectFormats(new[] { track })[0]);
    }

    [AvaloniaTheory]
    [InlineData(true, 300)]
    [InlineData(false, 300)]
    [InlineData(true, 30)]   // fewer missing songs than the queue cap: the pick waits for the whole scan
    [InlineData(false, 30)]
    public async Task QueueAndCounts_MatchAPickFromTheFinishedScan(bool wordTimings, int songs)
    {
        var library = new FakeLibraryService();
        library.TrackList.AddRange(Fixture(songs));
        var page = Page(library, wordTimings);

        await page.RefreshAsync();

        var formats = ExistingLyricsLoader.DetectFormats(library.TrackList);
        var expected = LyricsStudioPageViewModel.PickMissing(library.TrackList, formats, wordTimings, LyricsStudioPageViewModel.MaxQueue);
        Assert.Equal(expected.Select(t => t.Id), page.Studio!.Queue.Select(q => q.Track.Id));
        Assert.Equal(SettingsViewModel.BuildLyricsStudioCounts(formats).Select(c => (c.Count, c.Tag)),
            page.Counts.Select(c => (c.Count, c.Tag)));
        Assert.False(page.IsLoading);

        var suggested = await page.SuggestMissingAsync(wordTimings, 80);
        Assert.Equal(LyricsStudioPageViewModel.PickMissing(library.TrackList, formats, wordTimings, 80).Select(t => t.Id),
            suggested.Select(t => t.Id));
    }

    /// <summary>Holds the scan after the first <paramref name="openAfter"/> songs until <see cref="Release"/>.</summary>
    private sealed class GatedDetect(int openAfter)
    {
        private readonly ManualResetEventSlim _gate = new();
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public int Reached;
        public void Release() => _gate.Set();

        public IReadOnlyList<LyricsFormat> Detect(IReadOnlyList<Track> tracks, CancellationToken ct, Action<int, LyricsFormat>? detected)
        {
            Interlocked.Increment(ref _calls);
            return ExistingLyricsLoader.DetectFormats(tracks, ct, (i, f) =>
            {
                detected?.Invoke(i, f);
                Volatile.Write(ref Reached, i + 1);
                if (i + 1 == openAfter) while (!_gate.Wait(20)) ct.ThrowIfCancellationRequested();
            });
        }
    }

    [AvaloniaFact]
    public async Task TheQueueOpens_BeforeTheScanFinishes_AndTheCountsFollow()
    {
        var library = new FakeLibraryService();
        library.TrackList.AddRange(Fixture(300));
        var detect = new GatedDetect(openAfter: 120); // 80 songs without word timings by then
        var page = Page(library, wordTimings: true, detect.Detect);

        var refresh = page.RefreshAsync();
        await Until(() => page.Studio is not null);

        Assert.False(page.IsLoading);
        Assert.False(page.HasCounts);           // still counting
        Assert.False(refresh.IsCompleted);
        Assert.Equal(LyricsStudioPageViewModel.MaxQueue, page.Studio!.Queue.Count);
        var studio = page.Studio;

        detect.Release();
        await refresh;

        Assert.True(page.HasCounts);
        Assert.Same(studio, page.Studio);
        var formats = ExistingLyricsLoader.DetectFormats(library.TrackList);
        Assert.Equal(LyricsStudioPageViewModel.PickMissing(library.TrackList, formats, true, LyricsStudioPageViewModel.MaxQueue).Select(t => t.Id),
            studio.Queue.Select(q => q.Track.Id));
        Assert.Equal(1, detect.Calls);
    }

    [AvaloniaFact]
    public async Task ComingBackMidScan_JoinsTheRunningScan_InsteadOfStartingAnother()
    {
        var library = new FakeLibraryService();
        library.TrackList.AddRange(Fixture(300));
        var detect = new GatedDetect(openAfter: 120);
        var page = Page(library, wordTimings: true, detect.Detect);

        var first = page.RefreshAsync();
        await Until(() => page.Studio is not null);
        var second = page.RefreshAsync();     // the user clicked away and back
        var suggest = page.SuggestMissingAsync(true, 80); // and opened Choose songs
        detect.Release();
        await Task.WhenAll(first, second, suggest);

        Assert.Equal(1, detect.Calls);
        Assert.True(page.HasCounts);
        Assert.Equal(80, suggest.Result.Count);
    }

    [AvaloniaFact]
    public async Task ALibraryChangeMidScan_StartsAFreshScan_AndTheStaleOneStops()
    {
        var library = new FakeLibraryService();
        library.TrackList.AddRange(Fixture(300));
        var detect = new GatedDetect(openAfter: 120);
        var page = Page(library, wordTimings: true, detect.Detect);

        var first = page.RefreshAsync();
        await Until(() => page.Studio is not null);
        library.RaiseLibraryUpdated();
        var second = page.RefreshAsync();
        await Until(() => detect.Calls == 2);
        detect.Release();
        await Task.WhenAll(first, second);

        Assert.True(page.HasCounts);
        var counts = SettingsViewModel.BuildLyricsStudioCounts(ExistingLyricsLoader.DetectFormats(library.TrackList));
        Assert.Equal(counts.Select(c => (c.Count, c.Tag)), page.Counts.Select(c => (c.Count, c.Tag)));
    }
}

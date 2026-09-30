using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// ALAC → FLAC for phones without an ALAC decoder, over real Kestrel on loopback:
/// <c>format=flac</c> on an ALAC song serves a FLAC copy from <see cref="FlacTranscodeCache"/>
/// (one transcode for parallel requests, cached, Range-capable, .flac download name); every
/// other case — no format, another format, a non-ALAC song, no transcoder, a failed transcode —
/// serves the original bytes exactly as before. Plus the cache's own rules and, where ffmpeg is
/// installed, the real <see cref="ServerFlacTranscoder"/>.
/// </summary>
public class NoctisServerFlacTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "server-flac-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private string _apiKey = "";
    private FakeLibrary _lib = null!;
    private Track _alac = null!, _aac = null!, _flacTrack = null!;
    private byte[] _original = Array.Empty<byte>();
    private readonly byte[] _flacBytes = new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C' }
        .Concat(Enumerable.Range(0, 6000).Select(i => (byte)(i % 239))).ToArray();
    private readonly List<NoctisServer> _servers = new();

    private string CacheDir => Path.Combine(_dir, "server", "transcode");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _apiKey = _users.RegenerateApiKey("alice");

        _original = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        var m4a = Path.Combine(_dir, "01. Song.m4a");
        File.WriteAllBytes(m4a, _original);
        var aacPath = Path.Combine(_dir, "02. Other.m4a");
        File.WriteAllBytes(aacPath, _original);
        var flacPath = Path.Combine(_dir, "03. Third.flac");
        File.WriteAllBytes(flacPath, _original);

        // The codec strings the desktop library really stores (TagLib's descriptions).
        _alac = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "X", Album = "A", FilePath = m4a, Codec = "MPEG-4 Audio (alac)", FileSize = _original.Length };
        _aac = new Track { Id = Guid.NewGuid(), Title = "Other", Artist = "X", Album = "A", FilePath = aacPath, Codec = "MPEG-4 Audio (mp4a)", FileSize = _original.Length };
        _flacTrack = new Track { Id = Guid.NewGuid(), Title = "Third", Artist = "X", Album = "A", FilePath = flacPath, Codec = "Flac Audio", FileSize = _original.Length };
        _lib = new FakeLibrary(_alac, _aac, _flacTrack);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _servers) await s.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<HttpClient> StartAsync(Func<Guid, string, CancellationToken, Task<string?>>? flac)
    {
        var server = new NoctisServer(_lib, _users, "test", flacTranscoder: flac) { PrivateClientsOnly = true };
        await server.StartAsync(0, certificate: null);
        _servers.Add(server);
        return new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
    }

    private string Url(string method, Track t, string extra = "") => $"rest/{method}.view?apiKey={_apiKey}&id=tr-{t.Id:N}{extra}";

    /// <summary>A stand-in for ffmpeg: counts runs, can be held open, writes <see cref="_flacBytes"/>.</summary>
    private sealed class FakeTranscoder(byte[] output)
    {
        public int Calls;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Succeed { get; set; } = true;

        public async Task<bool> RunAsync(string source, string target, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!Succeed) { await File.WriteAllBytesAsync(target, new byte[] { 1, 2 }); return false; }
            await File.WriteAllBytesAsync(target, output);
            return true;
        }
    }

    [Fact]
    public async Task FormatFlac_OnAlac_TranscodesOnceForParallelRequests_ServesFlacWithRange_AndCaches()
    {
        var fake = new FakeTranscoder(_flacBytes);
        var cache = new FlacTranscodeCache(CacheDir, fake.RunAsync);
        var asked = 0;
        using var http = await StartAsync((id, path, ct) =>
        {
            Interlocked.Increment(ref asked);
            return cache.GetAsync(id, path, ct);
        });
        var ct = TestContext.Current.CancellationToken;

        // Two requests reach the cache while the first transcode is still running: one transcode.
        var a = http.GetAsync(Url("stream", _alac, "&format=flac"), ct);
        var b = http.GetAsync(Url("stream", _alac, "&format=flac"), ct);
        var start = Environment.TickCount64;
        while (Volatile.Read(ref asked) < 2 && Environment.TickCount64 - start < 10_000) await Task.Delay(10, ct);
        Assert.Equal(2, Volatile.Read(ref asked));
        fake.Release.SetResult();

        foreach (var res in await Task.WhenAll(a, b))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("audio/flac", res.Content.Headers.ContentType!.MediaType);
            Assert.Equal(_flacBytes, await res.Content.ReadAsByteArrayAsync(ct));
        }
        Assert.Equal(1, fake.Calls);

        // Seekable: a Range request gets 206 and the slice of the FLAC copy.
        using (var req = new HttpRequestMessage(HttpMethod.Get, Url("stream", _alac, "&format=flac")))
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
            var partial = await http.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal("audio/flac", partial.Content.Headers.ContentType!.MediaType);
            Assert.Equal(_flacBytes.Skip(1000).Take(1000).ToArray(), await partial.Content.ReadAsByteArrayAsync(ct));
        }

        // Cached on disk, named from the Guid, the source size and mtime only; no .part left.
        var source = new FileInfo(_alac.FilePath);
        var cached = Path.Combine(CacheDir, $"{_alac.Id:N}-{source.Length}-{source.LastWriteTimeUtc.Ticks}.flac");
        Assert.True(File.Exists(cached));
        Assert.Empty(Directory.GetFiles(CacheDir, "*.part"));

        // download=: the same copy, named .flac; still no second transcode.
        var dl = await http.GetAsync(Url("download", _alac, "&format=flac"), ct);
        Assert.Equal("audio/flac", dl.Content.Headers.ContentType!.MediaType);
        Assert.Equal("01. Song.flac", dl.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(_flacBytes, await dl.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task OtherRequests_ServeTheOriginal_AndNeverTranscode()
    {
        var fake = new FakeTranscoder(_flacBytes);
        fake.Release.SetResult();
        using var http = await StartAsync(new FlacTranscodeCache(CacheDir, fake.RunAsync).GetAsync);
        var ct = TestContext.Current.CancellationToken;

        async Task Original(string url, string type)
        {
            var res = await http.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(type, res.Content.Headers.ContentType!.MediaType);
            Assert.Equal(_original, await res.Content.ReadAsByteArrayAsync(ct));
        }

        await Original(Url("stream", _alac), "audio/mp4");                     // no format
        await Original(Url("stream", _alac, "&format=mp3"), "audio/mp4");      // not on the allow-list
        await Original(Url("stream", _alac, "&format=raw"), "audio/mp4");
        await Original(Url("stream", _alac, "&format=flac%00"), "audio/mp4");
        await Original(Url("stream", _aac, "&format=flac"), "audio/mp4");      // AAC in an .m4a
        await Original(Url("stream", _flacTrack, "&format=flac"), "audio/flac"); // already FLAC: the file itself
        var dl = await http.GetAsync(Url("download", _aac, "&format=flac"), ct);
        Assert.Equal("02. Other.m4a", dl.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(0, fake.Calls);
        Assert.False(Directory.Exists(CacheDir) && Directory.EnumerateFileSystemEntries(CacheDir).Any());
    }

    [Fact]
    public async Task NoTranscoder_OrAFailedOne_ServesTheOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var http = await StartAsync(flac: null))
        {
            var res = await http.GetAsync(Url("stream", _alac, "&format=flac"), ct);
            Assert.Equal("audio/mp4", res.Content.Headers.ContentType!.MediaType);
            Assert.Equal(_original, await res.Content.ReadAsByteArrayAsync(ct));
        }

        var failing = new FakeTranscoder(_flacBytes) { Succeed = false };
        failing.Release.SetResult();
        using (var http = await StartAsync(new FlacTranscodeCache(CacheDir, failing.RunAsync).GetAsync))
        {
            var res = await http.GetAsync(Url("stream", _alac, "&format=flac"), ct);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("audio/mp4", res.Content.Headers.ContentType!.MediaType);
            Assert.Equal(_original, await res.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(1, failing.Calls);
            Assert.Empty(Directory.GetFiles(CacheDir)); // the failed run's .part is gone, nothing cached
        }

        // A transcoder that throws is a failed transcode too.
        using (var http = await StartAsync((_, _, _) => throw new InvalidOperationException("boom")))
        {
            var res = await http.GetAsync(Url("stream", _alac, "&format=flac"), ct);
            Assert.Equal(_original, await res.Content.ReadAsByteArrayAsync(ct));
        }
    }

    [Fact]
    public async Task Cache_RemakesAChangedSource_AndTrimsTheLeastRecentlyServed()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeTranscoder(new byte[1000]);
        fake.Release.SetResult();
        var cache = new FlacTranscodeCache(CacheDir, fake.RunAsync, maxBytes: 2500);

        var first = await cache.GetAsync(_alac.Id, _alac.FilePath, ct);
        Assert.NotNull(first);
        Assert.Equal(first, await cache.GetAsync(_alac.Id, _alac.FilePath, ct));
        Assert.Equal(1, fake.Calls);

        // The file changed (new mtime): a new copy, and the old one is deleted.
        File.SetLastWriteTimeUtc(_alac.FilePath, DateTime.UtcNow.AddMinutes(-5));
        var second = await cache.GetAsync(_alac.Id, _alac.FilePath, ct);
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(first));
        Assert.Equal(2, fake.Calls);

        // Over the cap (3 × 1000 > 2500): the least recently served copy goes, never the new one.
        var b = await cache.GetAsync(_aac.Id, _aac.FilePath, ct);
        File.SetLastAccessTimeUtc(second!, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(b!, DateTime.UtcNow.AddHours(-1));
        var c = await cache.GetAsync(_flacTrack.Id, _flacTrack.FilePath, ct);
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(b));
        Assert.True(File.Exists(c));

        // A source that is gone gives no copy.
        Assert.Null(await cache.GetAsync(Guid.NewGuid(), Path.Combine(_dir, "missing.m4a"), ct));
    }

    [Fact]
    public async Task Cache_RefusesNewTranscodesPastThePendingCap_ButStillJoinsPendingOnes()
    {
        // A client asking for every ALAC song at once and hanging up must not queue hours of
        // ffmpeg work: the abandoned transcodes still count, and a new one past the cap gets no
        // copy at once (the server then sends the original).
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeTranscoder(new byte[100]);
        var cache = new FlacTranscodeCache(CacheDir, fake.RunAsync);
        var ids = Enumerable.Range(0, FlacTranscodeCache.MaxPending).Select(_ => Guid.NewGuid()).ToList();
        using (var gone = new CancellationTokenSource())
        {
            var waits = ids.Select(id => cache.GetAsync(id, _alac.FilePath, gone.Token)).ToList();
            gone.Cancel();
            foreach (var w in waits) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w);
        }

        var over = cache.GetAsync(Guid.NewGuid(), _alac.FilePath, ct);
        Assert.True(over.IsCompleted);
        Assert.Null(await over);

        // A request for a transcode already pending joins it; it is not refused.
        var join = cache.GetAsync(ids[0], _alac.FilePath, ct);
        Assert.False(join.IsCompleted);

        fake.Release.SetResult();
        Assert.NotNull(await join);
        Assert.All(await Task.WhenAll(ids.Select(id => cache.GetAsync(id, _alac.FilePath, ct))), Assert.NotNull);
        Assert.Equal(FlacTranscodeCache.MaxPending, fake.Calls);

        // Drained: new transcodes are taken again.
        Assert.NotNull(await cache.GetAsync(Guid.NewGuid(), _alac.FilePath, ct));
        Assert.Equal(FlacTranscodeCache.MaxPending + 1, fake.Calls);
    }

    [Fact]
    public void IsAlac_ReadsTheLibraryCodec()
    {
        Assert.True(NoctisServer.IsAlac(new Track { Codec = "MPEG-4 Audio (alac)" }));
        Assert.True(NoctisServer.IsAlac(new Track { Codec = "Apple Lossless (ALAC)" }));
        Assert.False(NoctisServer.IsAlac(new Track { Codec = "MPEG-4 Audio (mp4a)" }));
        Assert.False(NoctisServer.IsAlac(new Track { Codec = "Flac Audio" }));
        Assert.False(NoctisServer.IsAlac(new Track { Codec = "" }));
    }

    [Fact]
    public void Transcoder_Arguments_AreAFixedListAroundThePaths()
    {
        var args = ServerFlacTranscoder.BuildArgs(@"C:\m\a -y b.m4a", @"C:\cache\x.flac.part");
        Assert.Equal(new[]
        {
            "-nostdin", "-hide_banner", "-v", "error", "-i", @"C:\m\a -y b.m4a", "-map", "0:a:0",
            "-c:a", "flac", "-compression_level", "5", "-f", "flac", "-y", @"C:\cache\x.flac.part",
        }, args);
    }

    [Theory]
    [InlineData(16, "s16p", 44100)]
    [InlineData(24, "s32p", 48000)]
    public async Task RealFfmpeg_TurnsAlacIntoFlac_KeepingRateChannelsAndDepth(int bits, string alacFormat, int rate)
    {
        var ffmpeg = new AudioConverterService(() => string.Empty, new MetadataService()).GetFfmpegPath();
        var ffprobe = ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (ffmpeg is null || !File.Exists(ffprobe)) Assert.Skip("needs ffmpeg and ffprobe");

        var alac = Path.Combine(_dir, $"t{bits}.m4a");
        var made = Run(ffmpeg!, "-nostdin", "-v", "error", "-f", "lavfi", "-i", $"sine=d=1:sample_rate={rate}", "-ac", "2",
            "-c:a", "alac", "-sample_fmt", alacFormat, "-y", alac);
        Assert.Equal(0, made.Exit);
        Assert.Contains("codec_name=alac", Probe(ffprobe!, alac));

        var ct = TestContext.Current.CancellationToken;
        var cache = new FlacTranscodeCache(CacheDir, new ServerFlacTranscoder(() => ffmpeg).TranscodeAsync);
        var flac = await cache.GetAsync(Guid.NewGuid(), alac, ct);
        Assert.NotNull(flac);

        var info = Probe(ffprobe!, flac!);
        Assert.Contains("codec_name=flac", info);
        Assert.Contains($"sample_rate={rate}", info);
        Assert.Contains("channels=2", info);
        Assert.Contains($"bits_per_raw_sample={bits}", info);

        // Missing ffmpeg: no copy (the server then serves the original).
        Assert.False(await new ServerFlacTranscoder(() => null).TranscodeAsync(alac, Path.Combine(_dir, "x.part"), ct));
        Assert.False(await new ServerFlacTranscoder(() => Path.Combine(_dir, "no-ffmpeg.exe")).TranscodeAsync(alac, Path.Combine(_dir, "x.part"), ct));
    }

    private static string Probe(string ffprobe, string file) =>
        Run(ffprobe, "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=codec_name,sample_rate,channels,bits_per_raw_sample", "-of", "default=noprint_wrappers=1", file).Out;

    private static (int Exit, string Out) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { p.Kill(true); return (-1, ""); }
        return (p.ExitCode, stdout.Result.Replace("\r", ""));
    }

    private sealed class FakeLibrary(params Track[] tracks) : IServerLibrary
    {
        public List<Track> Tracks { get; } = tracks.ToList();
        public Task<LibrarySnapshot> SnapshotAsync() => Task.FromResult(new LibrarySnapshot(Tracks.ToList(), new List<Album>(), new List<Artist>(), new List<Playlist>()));
        public string? ArtworkPath(Guid albumId) => null;
        public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred) => Task.CompletedTask;
        public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => Task.FromResult(new Playlist { Name = name });
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(false);
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(false);
        public Task ApplyTrackStateAsync(Guid trackId, Noctis.Services.Sync.TrackSyncState state) => Task.CompletedTask;
        public Task ApplyPlaylistStateAsync(Guid playlistId, Noctis.Services.Sync.PlaylistSyncState state) => Task.CompletedTask;
    }
}

using System.Diagnostics;
using System.Reflection;
using LibVLCSharp.Shared;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Evidence harness for "Trouble Playing Long MP3 File" (Discord, Tangent, 10-28): a one-hour
/// 22.05 kHz MP3 went silent ~31 min in with a storm of "mpg123_decode_frame error: A
/// generic mpg123 error." Plays a file through the real player and splice engine with no
/// device behind it (NOCTIS_AOUT=dummy), replays the user's scrub sequence, then plays across
/// <c>NOCTIS_LONGMP3_AT_S</c> (seconds; default 1873) and reports whether the audible
/// position kept moving and how many mpg123 errors VLC logged. Explicit only:
/// NOCTIS_LONGMP3 names the file; NOCTIS_LONGMP3_EXACT=1 plays the whole first half in real
/// time like the user did instead of seeking to just before the point.
/// </summary>
public class LongMp3PlaybackHarnessTests
{
    private readonly ITestOutputHelper _o;
    public LongMp3PlaybackHarnessTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void LongMp3_PlaysAcrossDamagedRegion_AfterUserScrubSequence()
    {
        var path = Environment.GetEnvironmentVariable("NOCTIS_LONGMP3");
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path), "set NOCTIS_LONGMP3");
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        var atSec = int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_LONGMP3_AT_S"), out var a) ? a : 1873;
        var exact = Environment.GetEnvironmentVariable("NOCTIS_LONGMP3_EXACT") == "1";

        using var player = new VlcAudioPlayer();
        var lib = (LibVLC)typeof(VlcAudioPlayer).GetField("_libVlc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(player)!;
        int mpgErrors = 0;
        var firstLines = new List<string>();
        lib.Log += (_, e) =>
        {
            if (e.Level < LogLevel.Warning) return;
            if (e.Module == "mpg123") Interlocked.Increment(ref mpgErrors);
            lock (firstLines)
                if (firstLines.Count < 60) firstLines.Add($"{DateTime.Now:HH:mm:ss.fff} {e.Level} {e.Module}: {e.Message}");
        };
        var appLines = new List<string>();
        DebugLogger.IsEnabled = true;
        DebugLogger.EntryAdded += en =>
        {
            if (en.Category != DebugLogger.Category.Playback) return;
            if (!en.Action.StartsWith("Decoder.", StringComparison.Ordinal) && !en.Action.StartsWith("Mp3.", StringComparison.Ordinal)
                && !en.Action.StartsWith("Seek.", StringComparison.Ordinal)) return;
            lock (appLines) appLines.Add($"{DateTime.Now:HH:mm:ss.fff} {en.Action} | {en.Metadata}");
        };

        long heard = 0;
        string? error = null;
        var ended = false;
        player.PositionChanged += (_, p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
        player.PlaybackError += (_, e) => error = e;
        player.TrackEnded += (_, _) => ended = true;
        player.Play(path!);
        var start = Stopwatch.StartNew();
        while (Interlocked.Read(ref heard) < 1000 && start.ElapsedMilliseconds < 20000) Thread.Sleep(20);
        Assert.True(Interlocked.Read(ref heard) >= 1000, $"never played (error={error})");
        _o.WriteLine($"started after {start.ElapsedMilliseconds} ms, length={player.Duration}");

        // The user's scrubs (log 11:52:35-42), then back to the start.
        foreach (var (ms, wait) in new[] { (1672268L, 3000), (2578080L, 2000), (3121567L, 1500), (3483891L, 1000), (0L, 1500) })
        {
            player.Seek(TimeSpan.FromMilliseconds(ms));
            Thread.Sleep(wait);
            _o.WriteLine($"seek {ms} -> heard {Interlocked.Read(ref heard)}");
        }

        if (!exact)
        {
            player.Seek(TimeSpan.FromSeconds(atSec - 8));
            Thread.Sleep(1500);
        }
        var deadline = Stopwatch.StartNew();
        var limitMs = exact ? (atSec + 60) * 1000L : 60_000L;
        long lastPos = -1, lastMoveMs = 0, longestStallMs = 0;
        while (Interlocked.Read(ref heard) < (atSec + 20) * 1000L && deadline.ElapsedMilliseconds < limitMs && !ended)
        {
            Thread.Sleep(200);
            var p = Interlocked.Read(ref heard);
            if (p != lastPos) { lastPos = p; lastMoveMs = deadline.ElapsedMilliseconds; }
            else longestStallMs = Math.Max(longestStallMs, deadline.ElapsedMilliseconds - lastMoveMs);
        }
        var final = Interlocked.Read(ref heard);
        _o.WriteLine($"final heard={final} ms after {deadline.ElapsedMilliseconds} ms, longest stall={longestStallMs} ms, mpg123 errors={mpgErrors}, ended={ended}, error={error ?? "none"}");
        lock (firstLines) foreach (var l in firstLines) _o.WriteLine("  vlc " + l);
        lock (appLines) foreach (var l in appLines) _o.WriteLine("  app " + l);
        player.Stop();
        Assert.True(final >= (atSec + 20) * 1000L || ended, $"playback stalled at {final} ms (mpg123 errors={mpgErrors})");
    }
}

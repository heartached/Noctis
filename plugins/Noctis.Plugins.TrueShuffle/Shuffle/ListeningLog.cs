using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctis.Plugins.TrueShuffle.Shuffle;

/// <summary>How a play ended, as far as the player shows it.</summary>
public enum PlayEnd
{
    /// <summary>The song reached its end (or the next began in its last seconds).</summary>
    Finished,
    /// <summary>The next song of the queue started early: Next was pressed.</summary>
    Skipped,
    /// <summary>Anything else (Previous, another song picked, stopped): no opinion of the song.</summary>
    Unknown,
}

/// <summary>What a play says about the song. Only a deliberate early Next is a skip; unknown stays neutral.</summary>
public enum PlayOutcome { Completed, Qualified, Partial, StrongSkip, QuickSkip, Neutral }

/// <summary>One play: which song (its Noctis track id), when, how much of it was heard and how it
/// ended. One line of listens.jsonl. No titles, paths or audio: only ids and numbers.</summary>
public sealed record PlayRecord(string TrackId, DateTime StartUtc, int DurationMs, int ListenedMs, double Coverage, PlayEnd End)
{
    /// <summary>Shorter than this and a Next is a slip of the finger, not a verdict on the song.</summary>
    public const int AudibleMs = 3_000;

    [JsonIgnore]
    public PlayOutcome Outcome
    {
        get
        {
            if (End == PlayEnd.Finished) return PlayOutcome.Completed;
            // The scrobble rule: half the song, or four minutes of it.
            if (Coverage >= 0.5 || ListenedMs >= 240_000) return PlayOutcome.Qualified;
            if (End == PlayEnd.Skipped)
                return ListenedMs < AudibleMs ? PlayOutcome.QuickSkip : Coverage < 0.25 ? PlayOutcome.StrongSkip : PlayOutcome.Partial;
            return ListenedMs >= 30_000 ? PlayOutcome.Partial : PlayOutcome.Neutral;
        }
    }
}

/// <summary>
/// Follows the playing song from samples of the player (position, playing or not) and turns it into a
/// <see cref="PlayRecord"/> (ported from True Shuffle's PlayTracker). Listening time grows only while
/// playing and only by what the position really advanced: a pause adds nothing, a jump forward adds
/// nothing. The clock is injected, so it is all testable.
/// </summary>
public sealed class PlayTracker
{
    private sealed class Active
    {
        public required string TrackId;
        public required DateTime StartUtc;
        public required int DurationMs;
        public long ListenedMs;
        public ulong[]? Covered; // one bit per second of the song, up to four hours
        public int Seconds;
        public TimeSpan LastPosition, LastClock;
        public bool LastPlaying;
    }

    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(1.5);
    private Active? _active;

    public string? TrackId => _active?.TrackId;
    public TimeSpan LastPosition => _active?.LastPosition ?? TimeSpan.Zero;
    public int DurationMs => _active?.DurationMs ?? 0;

    public void Start(string trackId, TimeSpan duration, DateTime utcNow, TimeSpan clock, TimeSpan position, bool playing)
    {
        var durationMs = (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue);
        var seconds = durationMs > 0 ? Math.Min(durationMs / 1000 + 1, 4 * 3600) : 0;
        _active = new Active
        {
            TrackId = trackId, StartUtc = utcNow, DurationMs = durationMs,
            Covered = seconds > 0 ? new ulong[(seconds + 63) / 64] : null, Seconds = seconds,
            LastPosition = position, LastClock = clock, LastPlaying = playing,
        };
    }

    /// <summary>A look at the player: about once a second and at every change of state.</summary>
    public void Sample(TimeSpan position, bool playing, TimeSpan clock)
    {
        var a = _active;
        if (a == null) return;
        if (a.LastPlaying && playing)
        {
            var moved = position - a.LastPosition;
            var elapsed = clock - a.LastClock;
            // Real listening moves the position about as fast as the clock; anything else was a jump.
            if (moved > TimeSpan.Zero && elapsed > TimeSpan.Zero && elapsed <= MaxStep && moved <= elapsed + Slack)
            {
                a.ListenedMs += (long)moved.TotalMilliseconds;
                if (a.Covered != null)
                {
                    int from = (int)Math.Max(0, a.LastPosition.TotalSeconds), to = (int)Math.Min(a.Seconds, Math.Ceiling(position.TotalSeconds));
                    for (int s = from; s < to; s++) a.Covered[s >> 6] |= 1UL << (s & 63);
                }
            }
        }
        a.LastPosition = position;
        a.LastClock = clock;
        a.LastPlaying = playing;
    }

    /// <summary>The play is over.</summary>
    public PlayRecord? End(PlayEnd end, DateTime utcNow, TimeSpan clock)
    {
        var a = _active;
        if (a == null) return null;
        _active = null;
        double coverage = 0;
        if (a.Covered != null)
        {
            var set = 0;
            foreach (var w in a.Covered) set += System.Numerics.BitOperations.PopCount(w);
            coverage = (double)set / a.Seconds;
        }
        return new PlayRecord(a.TrackId, a.StartUtc, a.DurationMs, (int)Math.Min(a.ListenedMs, int.MaxValue),
            Math.Round(Math.Clamp(coverage, 0, 1), 3), end);
    }

    /// <summary>
    /// How the song ended, from what the player shows when the next one starts: <paramref name="advanced"/>
    /// is true when the new song was the first one up next. With crossfade the next starts up to 12 s early.
    /// </summary>
    public static PlayEnd Classify(bool advanced, TimeSpan remaining, double coverage)
    {
        if (!advanced) return PlayEnd.Unknown;
        if (remaining <= TimeSpan.FromSeconds(3) || (remaining <= TimeSpan.FromSeconds(13) && coverage >= 0.8)) return PlayEnd.Finished;
        return PlayEnd.Skipped;
    }

    public double Coverage
    {
        get
        {
            var a = _active;
            if (a?.Covered == null) return 0;
            var set = 0;
            foreach (var w in a.Covered) set += System.Numerics.BitOperations.PopCount(w);
            return (double)set / a.Seconds;
        }
    }
}

/// <summary>
/// listens.jsonl in the plugin's data folder: one play per line, appended in order by one writer.
/// A damaged line is skipped, never the whole file. Nothing leaves the computer. Only the newest
/// <see cref="MaxRecords"/> plays are read (years of listening).
/// </summary>
public sealed class ListenStore
{
    public const int MaxRecords = 100_000;
    private const long MaxReadBytes = 32L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly string _path;
    private readonly object _gate = new();
    private Task _write = Task.CompletedTask;

    public ListenStore(string folder) => _path = Path.Combine(folder, "listens.jsonl");

    public string FilePath => _path;

    public List<PlayRecord> Load()
    {
        var result = new List<PlayRecord>();
        if (!File.Exists(_path) || (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0) return result;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxReadBytes) stream.Seek(-MaxReadBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (stream.Position > 0) reader.ReadLine(); // the first line read is cut in half
        for (string? line; (line = reader.ReadLine()) != null;)
        {
            if (line.Length == 0) continue;
            try { if (JsonSerializer.Deserialize<PlayRecord>(line, Json) is { TrackId.Length: > 0 } record) result.Add(record); }
            catch (JsonException) { } // a line cut by a crash
        }
        if (result.Count > MaxRecords) result.RemoveRange(0, result.Count - MaxRecords);
        return result;
    }

    /// <summary>Appends in the order called, off the calling thread.</summary>
    public Task Append(PlayRecord record, Action<Exception>? failed = null)
    {
        var line = JsonSerializer.Serialize(record, Json) + "\n";
        lock (_gate)
        {
            _write = _write.ContinueWith(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("listens.jsonl is a link; it is not written to.");
                    using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    var bytes = Encoding.UTF8.GetBytes(line);
                    stream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex) { failed?.Invoke(ex); }
            }, TaskScheduler.Default);
            return _write;
        }
    }

    /// <summary>The writes queued so far.</summary>
    public Task Flushed { get { lock (_gate) return _write; } }
}

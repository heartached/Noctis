using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>What an online lookup found for a song: LRC text (synced when the source has it,
/// else plain lines), or an instrumental answer with no text.</summary>
public sealed record OnlineLyrics(string Text, bool Instrumental);

/// <summary>
/// Where the lyrics page goes when a song has nothing of its own (no sidecar, no tags) — the
/// desktop's automatic online lookup. Null when nothing was found, offline or timed out; never
/// throws for a failed lookup (cancelling may throw <see cref="OperationCanceledException"/>).
/// Tests inject a fake; the Android head injects <see cref="LrcLibOnlineLyricsSource"/>.
/// </summary>
public interface IOnlineLyricsSource
{
    Task<OnlineLyrics?> FindAsync(Track track, CancellationToken ct);
}

/// <summary>
/// The desktop's first online source: LRCLIB, asked by title, artist and length only (the
/// shared <see cref="LrcLibLookup"/>: exact /get, then a validated /search). A hit is kept in
/// the phone's data directory (lyrics_cache/{track id}.lrc, the desktop's cache name), so a
/// song looked up once shows its lyrics offline and is not asked again. A miss or a failure is
/// not cached: a later play asks again.
/// </summary>
public sealed class LrcLibOnlineLyricsSource : IOnlineLyricsSource
{
    /// <summary>How long a lookup may take before the page gives up on it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The cache file of an instrumental answer: empty, so nothing is shown as lyrics.</summary>
    internal const string InstrumentalExtension = ".instrumental";

    private readonly ILrcLibService _lrcLib;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _timeout;

    public LrcLibOnlineLyricsSource(ILrcLibService lrcLib, string cacheDirectory, TimeSpan? timeout = null)
    {
        _lrcLib = lrcLib;
        _cacheDirectory = cacheDirectory;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The source over the phone's own cache, lyrics_cache under <paramref name="dataDirectory"/>.</summary>
    public static LrcLibOnlineLyricsSource Create(string dataDirectory, HttpClient http) =>
        new(new LrcLibService(http), Path.Combine(dataDirectory, "lyrics_cache"));

    public async Task<OnlineLyrics?> FindAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return null;
        var cached = Path.Combine(_cacheDirectory, track.Id.ToString("N") + ".lrc");
        var instrumental = Path.Combine(_cacheDirectory, track.Id.ToString("N") + InstrumentalExtension);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            // Off the UI thread throughout: the cache read and write and the JSON parse
            // continue wherever their awaits resume.
            return await Task.Run(async () =>
            {
                if (File.Exists(instrumental)) return new OnlineLyrics(string.Empty, Instrumental: true);
                if (File.Exists(cached))
                {
                    var text = await File.ReadAllTextAsync(cached, timeout.Token);
                    if (!string.IsNullOrWhiteSpace(text)) return new OnlineLyrics(text, Instrumental: false);
                }

                var result = await LrcLibLookup.FetchAsync(_lrcLib, track.Artist ?? string.Empty, track.Title,
                    track.Duration.TotalSeconds, timeout.Token);
                if (result is { Instrumental: true })
                {
                    Write(instrumental, string.Empty);
                    return new OnlineLyrics(string.Empty, Instrumental: true);
                }
                var found = result?.HasSyncedLyrics == true ? result.SyncedLyrics
                    : !string.IsNullOrWhiteSpace(result?.PlainLyrics) ? result!.PlainLyrics
                    : null;
                if (found == null) return null;
                Write(cached, found);
                return new OnlineLyrics(found, Instrumental: false);
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            DebugLog.Write("Lyrics", "Online lookup timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugLog.Write("Lyrics", $"Online lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void Write(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Online lyrics cache write failed: {ex.GetType().Name}");
        }
    }
}

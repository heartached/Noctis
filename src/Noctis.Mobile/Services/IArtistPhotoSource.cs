using System.Security.Cryptography;
using System.Text;
using Noctis.Mobile.ViewModels;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// Where the phone's artist photos come from. Asked lazily: when an artist page opens and for
/// the artist circles on screen, never for the whole library. Pages show the artist's album
/// cover until (and unless) a photo arrives. Tests inject a fake; the Android head injects
/// <see cref="DeezerArtistPhotoSource"/>.
/// </summary>
public interface IArtistPhotoSource
{
    /// <summary>The photo already on the phone for <paramref name="artistName"/>, or null. Never
    /// asks the network; cheap enough for every row of a list (an in-memory index).</summary>
    string? CachedPhoto(string artistName);

    /// <summary>
    /// The artist's photo file: the cached one, else a lookup. Null when there is none, the
    /// artist is unknown to the service, or the lookup failed or was slow; never throws for a
    /// failed lookup. Cancelling (the page closed, the circle scrolled away) may throw
    /// <see cref="OperationCanceledException"/>, and a lookup nobody waits for any more stops.
    /// </summary>
    Task<string?> GetPhotoAsync(string artistName, CancellationToken ct);
}

/// <summary>
/// The desktop's artist photos (Deezer, matched by the shared <see cref="DeezerArtistPhotos"/>),
/// looked up by the phone itself, sending only the artist's name. Photos are kept as
/// <c>artist_images/&lt;hash&gt;.jpg</c> under the data directory (the hash of the name, so a
/// name with a slash or an accent makes a plain file name), so a photo seen once shows offline.
/// A name Deezer answered without a match leaves a <c>&lt;hash&gt;.miss</c> marker and is not
/// asked again for <see cref="DefaultMissRetryAfter"/>; a lookup that got no answer (offline, a
/// server error, slower than <see cref="DefaultTimeout"/>) leaves nothing, so a later visit asks
/// again. At most <see cref="MaxConcurrentLookups"/> lookups run at once, each paced like the
/// desktop's sweep (Deezer allows 50 requests per 5 s; a lookup costs two or more).
/// </summary>
public sealed class DeezerArtistPhotoSource : IArtistPhotoSource
{
    /// <summary>How long one lookup (search and download) may take before it is given up.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long a name Deezer did not know is left alone (artists are added over time).</summary>
    public static readonly TimeSpan DefaultMissRetryAfter = TimeSpan.FromDays(3);

    /// <summary>Lookups in flight at once: a fling through the Artists grid queues the rest.</summary>
    public const int MaxConcurrentLookups = 2;

    /// <summary>The desktop sweep's pause before each artist's requests.</summary>
    public static readonly TimeSpan DefaultPacing = TimeSpan.FromMilliseconds(120);

    private readonly DeezerArtistPhotos _deezer;
    private readonly string _directory;
    private readonly Func<string, IEnumerable<string>>? _libraryTitles;
    private readonly TimeSpan _timeout, _missRetryAfter, _pacing;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(MaxConcurrentLookups, MaxConcurrentLookups);
    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    // The cache directory's contents, read once: photo keys, and miss keys with their time.
    private HashSet<string>? _photos;
    private Dictionary<string, DateTime>? _misses;

    /// <param name="deezer">The shared Deezer lookup.</param>
    /// <param name="directory">The cache folder (created on the first save).</param>
    /// <param name="libraryTitles">The library's song titles for a name, to tell same-name Deezer
    /// accounts apart (asked only then); null skips that check (fan order stands).</param>
    public DeezerArtistPhotoSource(DeezerArtistPhotos deezer, string directory, Func<string, IEnumerable<string>>? libraryTitles = null,
        TimeSpan? timeout = null, TimeSpan? missRetryAfter = null, TimeSpan? pacing = null, Func<DateTime>? utcNow = null)
    {
        _deezer = deezer;
        _directory = directory;
        _libraryTitles = libraryTitles;
        _timeout = timeout ?? DefaultTimeout;
        _missRetryAfter = missRetryAfter ?? DefaultMissRetryAfter;
        _pacing = pacing ?? DefaultPacing;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The source over the phone's own cache, <c>artist_images</c> under
    /// <paramref name="dataDirectory"/>, checking same-name accounts against the artist's songs
    /// in <paramref name="library"/>.</summary>
    public static DeezerArtistPhotoSource Create(string dataDirectory, HttpClient http, ILibraryService library) =>
        new(new DeezerArtistPhotos(http), Path.Combine(dataDirectory, "artist_images"),
            name => MobileLibrary.SongsBy(library, name).Select(t => t.Title));

    /// <summary>The cache file name for an artist: a hash of the trimmed, lower-cased name.</summary>
    internal static string Key(string artistName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    internal string PhotoPath(string key) => Path.Combine(_directory, key + ".jpg");
    internal string MissPath(string key) => Path.Combine(_directory, key + ".miss");

    private static bool IsAskable(string artistName) =>
        !string.IsNullOrWhiteSpace(artistName) && !string.Equals(artistName.Trim(), "Unknown Artist", StringComparison.OrdinalIgnoreCase);

    public string? CachedPhoto(string artistName)
    {
        if (!IsAskable(artistName)) return null;
        var key = Key(artistName);
        lock (_lock)
        {
            EnsureIndex();
            return _photos!.Contains(key) ? PhotoPath(key) : null;
        }
    }

    public async Task<string?> GetPhotoAsync(string artistName, CancellationToken ct)
    {
        if (!IsAskable(artistName)) return null;
        ct.ThrowIfCancellationRequested();
        var key = Key(artistName);
        Pending entry;
        lock (_lock)
        {
            EnsureIndex();
            if (_photos!.Contains(key)) return PhotoPath(key);
            if (_misses!.TryGetValue(key, out var missed) && _utcNow() - missed < _missRetryAfter) return null;
            // One lookup per name, however many circles and pages ask; a lookup everyone left
            // is already stopping, so a new ask starts a fresh one.
            if (!_pending.TryGetValue(key, out entry!) || entry.Cts.IsCancellationRequested)
            {
                entry = new Pending();
                _pending[key] = entry;
                var started = entry;
                entry.Task = Task.Run(() => FetchAsync(artistName.Trim(), key, started));
            }
            entry.Waiters++;
        }

        try
        {
            return await entry.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                // The last one waiting walked away: stop the lookup (it caches nothing).
                if (--entry.Waiters == 0 && !entry.Task.IsCompleted) entry.Cts.Cancel();
            }
        }
    }

    private async Task<string?> FetchAsync(string artistName, string key, Pending entry)
    {
        var token = entry.Cts.Token;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Forget(key, entry);
            return null;
        }

        try
        {
            if (_pacing > TimeSpan.Zero) await Task.Delay(_pacing, token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_timeout);
            var lookup = await _deezer.FindAsync(artistName, () => TitleKeys(artistName), timeout.Token).ConfigureAwait(false);
            if (lookup.Match is not { } match)
            {
                if (lookup.Answered) RecordMiss(key);
                return null;
            }
            // The phone never draws a portrait wider than its screen: Deezer's 1000 px original.
            var bytes = await _deezer.DownloadImageAsync(DeezerArtistPhotos.OriginalSize(match.ImageUrl), timeout.Token).ConfigureAwait(false);
            if (bytes == null) return null;
            return await SaveAsync(key, bytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            DebugLog.Write("Artist", "Artist photo lookup timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;   // nobody is waiting any more
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Artist photo lookup failed: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
            Forget(key, entry);
        }
    }

    private void Forget(string key, Pending entry)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _pending.Remove(key);
        }
    }

    /// <summary>The library's titles for the artist as Deezer title keys; empty when the library
    /// cannot be read (a rescan replacing it underneath), which leaves Deezer's fan order.</summary>
    private IReadOnlySet<string> TitleKeys(string artistName)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (_libraryTitles == null) return keys;
        try
        {
            foreach (var title in _libraryTitles(artistName))
            {
                var key = DeezerArtistPhotos.TitleKey(title);
                if (key.Length >= 3) keys.Add(key);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Reading the artist's titles failed: {ex.Message}");
            keys.Clear();
        }
        return keys;
    }

    /// <summary>Written through a temporary file and a rename, so a circle decoding the photo
    /// never reads half a JPEG.</summary>
    private async Task<string> SaveAsync(string key, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var path = PhotoPath(key);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        TryDelete(MissPath(key));
        lock (_lock)
        {
            EnsureIndex();
            _photos!.Add(key);
            _misses!.Remove(key);
        }
        return path;
    }

    private void RecordMiss(string key)
    {
        var now = _utcNow();
        try
        {
            Directory.CreateDirectory(_directory);
            var path = MissPath(key);
            File.WriteAllText(path, string.Empty);
            File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Recording an artist photo miss failed: {ex.Message}");
        }
        lock (_lock)
        {
            EnsureIndex();
            _misses![key] = now;
        }
    }

    /// <summary>Reads the cache folder once (under <see cref="_lock"/>).</summary>
    private void EnsureIndex()
    {
        if (_photos != null) return;
        var photos = new HashSet<string>(StringComparer.Ordinal);
        var misses = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    switch (Path.GetExtension(file))
                    {
                        case ".jpg": photos.Add(name); break;
                        case ".miss": misses[name] = File.GetLastWriteTimeUtc(file); break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Reading the artist photo cache failed: {ex.Message}");
        }
        _photos = photos;
        _misses = misses;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale marker only delays a retry.
        }
    }

    private sealed class Pending
    {
        public readonly CancellationTokenSource Cts = new();
        public int Waiters;
        public Task<string?> Task = System.Threading.Tasks.Task.FromResult<string?>(null);
    }
}

using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>One album's new songs waiting for their copy to finish (GitHub #148).</summary>
public sealed class PendingRgAlbum
{
    internal PendingRgAlbum(Guid key, string albumName)
    {
        Key = key;
        AlbumName = albumName;
    }

    /// <summary><see cref="ReplayGainScannerService.AlbumGroupKey"/> of its songs.</summary>
    public Guid Key { get; }
    public string AlbumName { get; }
    /// <summary>The new songs, in the order they arrived (one per file).</summary>
    public List<Track> Tracks { get; } = new();
    /// <summary>The folders they sit in (a multi-disc album may span CD1/CD2).</summary>
    public HashSet<string> Directories { get; } = new(PathComparison.Comparer);
    /// <summary>When the last of its songs arrived.</summary>
    public DateTime LastAddedUtc { get; internal set; }
}

/// <summary>
/// Collects new songs per album and hands an album over once its copy has finished: no
/// new song joined the album, or arrived in any of its folders, for <see cref="QuietPeriod"/>,
/// and the caller's settle check (files no longer growing or held by a writer) passes. A
/// file that never settles holds its album at most <see cref="MaxSettleWait"/> past the
/// quiet period. Pure bookkeeping: the clock is passed in, nothing here touches the disk.
/// Not thread-safe; the owner locks.
/// </summary>
public sealed class ReplayGainAutoScanQueue
{
    /// <summary>No new song in the album or its folders for this long = the copy ended.</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(30);

    /// <summary>How long a file that keeps changing (or stays locked) may hold its album.</summary>
    public static readonly TimeSpan DefaultMaxSettleWait = TimeSpan.FromMinutes(5);

    private readonly Dictionary<Guid, PendingRgAlbum> _albums = new();
    // Last arrival per folder: an album whose tags differ per disc, or a second album
    // copied into the same folder, still counts as "the copy is still going".
    private readonly Dictionary<string, DateTime> _dirLastAdded = new(PathComparison.Comparer);

    public ReplayGainAutoScanQueue(TimeSpan? quietPeriod = null, TimeSpan? maxSettleWait = null)
    {
        QuietPeriod = quietPeriod ?? DefaultQuietPeriod;
        MaxSettleWait = maxSettleWait ?? DefaultMaxSettleWait;
    }

    public TimeSpan QuietPeriod { get; }
    public TimeSpan MaxSettleWait { get; }

    public int Count => _albums.Count;
    public bool IsEmpty => _albums.Count == 0;
    public IEnumerable<PendingRgAlbum> Albums => _albums.Values;

    /// <summary>Adds songs, grouped by album. A file already waiting is not added twice,
    /// but its arrival still restarts the album's quiet period.</summary>
    public void Add(IEnumerable<Track> tracks, DateTime nowUtc)
    {
        foreach (var t in tracks)
        {
            if (string.IsNullOrWhiteSpace(t.FilePath)) continue;
            var key = ReplayGainScannerService.AlbumGroupKey(t);
            if (!_albums.TryGetValue(key, out var album))
                _albums[key] = album = new PendingRgAlbum(key, t.Album ?? string.Empty);
            if (!album.Tracks.Any(x => PathComparison.Comparer.Equals(x.FilePath, t.FilePath)))
                album.Tracks.Add(t);
            album.LastAddedUtc = nowUtc;

            var dir = Path.GetDirectoryName(t.FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                album.Directories.Add(dir);
                _dirLastAdded[dir] = nowUtc;
            }
        }
    }

    /// <summary>Last arrival in the album or any of its folders.</summary>
    public DateTime LastActivityUtc(PendingRgAlbum album)
    {
        var last = album.LastAddedUtc;
        foreach (var dir in album.Directories)
            if (_dirLastAdded.TryGetValue(dir, out var d) && d > last) last = d;
        return last;
    }

    /// <summary>
    /// Removes and returns the albums ready to scan. <paramref name="isSettled"/> is only
    /// asked about albums past their quiet period; once <see cref="MaxSettleWait"/> has
    /// also passed the album goes regardless. At most <paramref name="max"/> albums per call,
    /// so a newly added folder of thousands doesn't run every settle check in one go.
    /// </summary>
    public List<PendingRgAlbum> TakeReady(DateTime nowUtc, Func<PendingRgAlbum, bool> isSettled, int max = int.MaxValue)
    {
        var ready = new List<PendingRgAlbum>();
        foreach (var album in _albums.Values.ToList())
        {
            if (ready.Count >= max) break;
            var idle = nowUtc - LastActivityUtc(album);
            if (idle < QuietPeriod) continue;
            if (idle < QuietPeriod + MaxSettleWait && !isSettled(album)) continue;
            ready.Add(album);
            _albums.Remove(album.Key);
        }

        if (ready.Count > 0) PruneDirectories(nowUtc);
        return ready;
    }

    public void Clear()
    {
        _albums.Clear();
        _dirLastAdded.Clear();
    }

    // A folder's last arrival only matters while an album in it waits.
    private void PruneDirectories(DateTime nowUtc)
    {
        foreach (var dir in _dirLastAdded.Keys.ToList())
        {
            if (nowUtc - _dirLastAdded[dir] < QuietPeriod) continue;
            if (_albums.Values.Any(a => a.Directories.Contains(dir))) continue;
            _dirLastAdded.Remove(dir);
        }
    }
}

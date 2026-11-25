using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// Finds the animated cover Now Playing shows for a song (Discord beta 11-23, "no animated cover
/// support ... I downloaded all those animations for nothing"):
/// <list type="number">
/// <item>the phone's own music: <c>cover.mp4</c> / <c>cover.webm</c> beside the song, the desktop's
/// own sidecar names (<see cref="AnimatedCoverLocator.SidecarNames"/>), read through the folder grant;</item>
/// <item>the computer's songs (streamed, downloaded or paired): the computer's loop for the album
/// (getNoctisAnimatedCover), saved in <see cref="AnimatedCoverCache"/> and played from there,
/// offline too. A new loop is downloaded only as <see cref="AnimatedCoversSetting"/> allows (Wi-Fi
/// Only: on an unmetered network), and a saved one is asked about again at most every
/// <see cref="RecheckAfter"/> (an ETag, so an unchanged loop costs one small request).</item>
/// </list>
/// An album the computer has no loop for is not asked about again for <see cref="NoneMemo"/>.
/// Nothing here throws: no loop is the still cover.
/// </summary>
public sealed class AnimatedCoverSource
{
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(12);
    public static readonly TimeSpan NoneMemo = TimeSpan.FromHours(6);

    private readonly AnimatedCoverCache _cache;
    private readonly Func<bool> _isOnWifi;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<Guid, DateTime> _none = new();
    private readonly SemaphoreSlim _downloads = new(1, 1);
    private readonly HashSet<Guid> _rechecking = new();
    private bool _loggedMetered;

    public AnimatedCoverSource(AnimatedCoverCache cache, Func<bool> isOnWifi, Func<DateTime>? now = null)
    {
        _cache = cache;
        _isOnWifi = isOnWifi;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The loop beside one of the phone's own songs (a playable URI or path), or null.
    /// Called on a worker thread. Null: the phone's own music has no loops (tests, no grant).</summary>
    public Func<Track, string?>? FindSidecar { get; init; }

    /// <summary>The song is the computer's (streamed, downloaded, or a phone file paired with one).</summary>
    public Func<Track, bool>? IsComputerSong { get; init; }

    /// <summary>getNoctisAnimatedCover through the signed-in account: album, destination, the saved
    /// version. Null: no computer.</summary>
    public Func<Guid, string, string?, CancellationToken, Task<AnimatedCoverDownload>>? Fetch { get; init; }

    /// <summary>The background re-asks started for saved loops; tests await them.</summary>
    internal Task PendingRecheck { get; private set; } = Task.CompletedTask;

    /// <summary>The source for the signed-in computer's songs (and, with <paramref name="findSidecar"/>, the phone's own).</summary>
    public static AnimatedCoverSource ForAccount(AnimatedCoverCache cache, Func<bool> isOnWifi, INoctisAccountService? account,
        Func<Track, string?>? findSidecar) =>
        new(cache, isOnWifi)
        {
            FindSidecar = findSidecar,
            IsComputerSong = account is null ? null : t => account.IsSignedIn && (account.IsRemote(t) || account.DesktopIdOf(t) is not null),
            Fetch = account is null ? null : account.FetchAnimatedCoverAsync,
        };

    /// <summary>The loop to play for <paramref name="track"/> under <paramref name="setting"/>, or null.</summary>
    public async Task<string?> ResolveAsync(Track track, AnimatedCoversSetting setting, CancellationToken ct)
    {
        if (setting == AnimatedCoversSetting.Off) return null;
        try
        {
            if (FindSidecar is { } find && !string.IsNullOrEmpty(track.FilePath))
            {
                var own = await Task.Run(() => find(track), ct).ConfigureAwait(false);
                if (own is not null)
                {
                    DebugLog.Write("AnimatedCover", "using the loop beside the song");
                    return own;
                }
            }
            if (Fetch is null || IsComputerSong?.Invoke(track) != true || track.AlbumId == Guid.Empty) return null;

            var album = track.AlbumId;
            var network = setting == AnimatedCoversSetting.Always || _isOnWifi();
            if (_cache.Get(album) is { } saved)
            {
                if (network && (_cache.CheckedAt(album) is not { } at || _now() - at > RecheckAfter)) StartRecheck(album);
                DebugLog.Write("AnimatedCover", "using the computer's loop saved on the phone");
                return saved;
            }
            if (!network)
            {
                if (!_loggedMetered) DebugLog.Write("AnimatedCover", "not on Wi-Fi: the computer's loop waits (Animated Covers: Wi-Fi Only)");
                _loggedMetered = true;
                return null;
            }
            lock (_none)
                if (_none.TryGetValue(album, out var when) && _now() - when < NoneMemo) return null;
            return await DownloadAsync(album, etag: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("AnimatedCover", $"no loop: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
            return null;
        }
    }

    /// <summary>One download at a time (a loop is tens of MB); a newer song's ask waits its turn.</summary>
    private async Task<string?> DownloadAsync(Guid album, string? etag, CancellationToken ct)
    {
        await _downloads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Saved while this one waited (the same album asked twice).
            if (etag is null && _cache.Get(album) is { } saved) return saved;
            var part = _cache.PartPathFor(album);
            var answer = await Fetch!(album, part, etag, ct).ConfigureAwait(false);
            switch (answer.Result)
            {
                case AnimatedCoverFetch.Saved:
                    var path = _cache.Commit(album, part, answer.ETag);
                    DebugLog.Write("AnimatedCover", $"saved the computer's loop ({new System.IO.FileInfo(path).Length / 1024} KB)");
                    return path;
                case AnimatedCoverFetch.NotModified:
                    _cache.MarkChecked(album);
                    return _cache.Get(album);
                default:
                    lock (_none) _none[album] = _now();
                    if (etag is not null) _cache.Remove(album);   // the computer's loop is gone
                    return null;
            }
        }
        finally
        {
            // The partial file goes before the next download may start: deleted after, it could be
            // the next one's file (the same album asked again), which then ended with the still cover.
            try { if (System.IO.File.Exists(_cache.PartPathFor(album))) System.IO.File.Delete(_cache.PartPathFor(album)); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
            finally { _downloads.Release(); }
        }
    }

    /// <summary>Asks the computer whether the saved loop is still its loop, in the background: the
    /// saved one plays now, a new one from the next time the song shows.</summary>
    private void StartRecheck(Guid album)
    {
        lock (_rechecking)
            if (!_rechecking.Add(album)) return;
        PendingRecheck = Task.Run(async () =>
        {
            try
            {
                await DownloadAsync(album, _cache.ETagOf(album) ?? "\"none\"", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugLog.Write("AnimatedCover", $"recheck failed: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
            }
            finally
            {
                lock (_rechecking) _rechecking.Remove(album);
            }
        });
    }
}

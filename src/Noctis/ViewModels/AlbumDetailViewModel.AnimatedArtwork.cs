using CommunityToolkit.Mvvm.Input;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// GitHub #167 on the album page: the loop is fetched while the page is shown (Settings ›
/// Artwork › Fetch animated artwork when opening an album), and the page's menu removes a
/// stored loop in one click — which also keeps the fetch from bringing a wrong one back.
/// </summary>
public partial class AlbumDetailViewModel
{
    private CancellationTokenSource? _autoFetchCts;

    /// <summary>The shared fetcher; tests swap it out.</summary>
    internal Func<AnimatedArtworkAutoFetcher?> AutoFetcherProvider { get; set; } = () => MetadataHelper.AlbumArtworkFetcher;

    /// <summary>The running fetch, for tests.</summary>
    internal Task AutoFetchTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The menu's Remove Animated Artwork: shown when a loop plays here that Noctis stored
    /// (saved, per-song or fetched). A cover.mp4 beside the music is the user's file and stays.
    /// </summary>
    public bool CanRemoveAnimatedArtwork =>
        !string.IsNullOrEmpty(AnimatedCoverPath) && _animatedCovers.HasStoredCover(Tracks);

    /// <summary>The page is on screen: fetch its loop when the setting asks for it.</summary>
    internal void StartAutoFetch()
    {
        StopAutoFetch();
        if (_settings is not { EnableAnimatedCovers: true, AnimatedArtworkAutoFetch: true }) return;
        if (Tracks.Count == 0 || AutoFetcherProvider() is not { } fetcher) return;

        var cts = new CancellationTokenSource();
        _autoFetchCts = cts;
        AutoFetchTask = AutoFetchAsync(fetcher, Tracks.ToList(), _settings.AnimatedArtworkCacheBytes,
            _settings.AnimatedArtworkPrefersMax, cts.Token);
    }

    /// <summary>The page left the screen (or closed): stop asking for its loop.</summary>
    internal void StopAutoFetch()
    {
        _autoFetchCts?.Cancel();
        _autoFetchCts = null;
    }

    private async Task AutoFetchAsync(AnimatedArtworkAutoFetcher fetcher, IReadOnlyList<Models.Track> tracks, long capBytes,
        bool preferMax, CancellationToken ct)
    {
        try
        {
            // Off the UI thread: the lookup, the download and the cache's file checks.
            var path = await Task.Run(() => fetcher.FetchAsync(tracks, capBytes, preferMax, ct), ct);
            if (path is null || ct.IsCancellationRequested) return;
            ShowStoredAnimatedCover();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Left the page.
        }
        catch (Exception ex)
        {
            DebugLog.Write(ITunesArtworkService.LogSource, $"auto: album page: {ex.Message}");
        }
    }

    /// <summary>Re-reads the loop for the header and, when this album plays, the player.</summary>
    private void ShowStoredAnimatedCover()
    {
        AnimatedCoverPath = ResolveAlbumAnimatedCover();
        OnPropertyChanged(nameof(CanRemoveAnimatedArtwork));
        if (_player.CurrentTrack?.AlbumId == Album.Id)
            _player.RefreshAnimatedCover();
    }

    /// <summary>
    /// Deletes the album's stored loops (saved, per-song and fetched) and marks the album so
    /// the fetch on opening never brings one back. A loop can still be picked by hand in the
    /// metadata editor.
    /// </summary>
    [RelayCommand]
    private async Task RemoveAnimatedArtwork()
    {
        if (Tracks.Count == 0) return;
        StopAutoFetch();
        var albumId = Album.Id;
        AutoFetcherProvider()?.Cache.Block(albumId);

        // Release the files first: the header and the player hold them open while they play.
        AnimatedCoverPath = null;
        var playing = _player.CurrentTrack?.AlbumId == albumId;
        if (playing) _player.CurrentAnimatedCoverPath = null;

        foreach (var track in Tracks.ToList())
            await _animatedCovers.RemoveAsync(track, AnimatedCoverScope.Album);

        ShowStoredAnimatedCover();
    }
}

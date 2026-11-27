using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Artwork › Artist videos on the artist page: while the page is the current view
/// and both Animated Artwork and Artist videos are on, the artist's Apple Music motion video is
/// fetched (or taken from the cache) and the hero plays it. Leaving the page clears it, so only
/// the page on screen ever holds a video. The "…" menu removes it for good.
/// </summary>
public partial class ArtistDetailViewModel
{
    private CancellationTokenSource? _artistVideoCts;
    private PropertyChangedEventHandler? _artistVideoSettingsHandler;

    /// <summary>The shared fetcher; tests swap it out.</summary>
    internal Func<ArtistVideoAutoFetcher?> ArtistVideoFetcherProvider { get; set; } = () => MetadataHelper.ArtistVideoFetcher;

    /// <summary>The running fetch, for tests.</summary>
    internal Task ArtistVideoTask { get; private set; } = Task.CompletedTask;

    /// <summary>The cached video the hero plays; null keeps today's still hero.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasArtistVideo), nameof(CanRemoveArtistVideo))]
    private string? _artistVideoPath;

    public bool HasArtistVideo => !string.IsNullOrEmpty(ArtistVideoPath);

    /// <summary>
    /// The Settings sheet is over the page (MainWindowViewModel). The hero video holds its
    /// frame meanwhile: under the sheet's live blur every video frame made the blur re-render
    /// the whole app (probe: ~10 stand-in retakes a second for a 24 fps hero).
    /// </summary>
    [ObservableProperty]
    private bool _isCovered;

    /// <summary>The menu's Remove Artist Video: shown while a video plays here.</summary>
    public bool CanRemoveArtistVideo => HasArtistVideo;

    /// <summary>Both switches: Animated Artwork and Artist videos.</summary>
    internal static bool ArtistVideosAllowed(SettingsViewModel? settings)
        => settings is { EnableAnimatedCovers: true, ArtistVideos: true };

    /// <summary>Follows the two switches while the page is open (turning either off stops the video).</summary>
    private void WatchArtistVideoSettings()
    {
        if (_settings == null) return;
        _artistVideoSettingsHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.EnableAnimatedCovers) or nameof(SettingsViewModel.ArtistVideos))
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_isActive) return;
                    if (ArtistVideosAllowed(_settings)) { if (!HasArtistVideo) StartArtistVideo(); }
                    else StopArtistVideo();
                });
        };
        _settings.PropertyChanged += _artistVideoSettingsHandler;
    }

    private void UnwatchArtistVideoSettings()
    {
        if (_settings != null && _artistVideoSettingsHandler != null)
            _settings.PropertyChanged -= _artistVideoSettingsHandler;
        _artistVideoSettingsHandler = null;
    }

    /// <summary>The page is on screen: show the artist's video when the settings ask for it.</summary>
    internal void StartArtistVideo()
    {
        StopArtistVideo();
        if (!ArtistVideosAllowed(_settings) || ArtistName.Length == 0) return;
        if (ArtistVideoFetcherProvider() is not { } fetcher) return;

        var cts = new CancellationTokenSource();
        _artistVideoCts = cts;
        ArtistVideoTask = FetchArtistVideoAsync(fetcher, Artist.Id, ArtistName, _settings!.AnimatedArtworkCacheBytes, cts.Token);
    }

    /// <summary>The page left the screen (or closed): stop asking, and let the hero go of the file.</summary>
    internal void StopArtistVideo()
    {
        _artistVideoCts?.Cancel();
        _artistVideoCts = null;
        ArtistVideoPath = null;
    }

    private async Task FetchArtistVideoAsync(ArtistVideoAutoFetcher fetcher, Guid artistId, string name, long capBytes, CancellationToken ct)
    {
        try
        {
            // Off the UI thread: the cache's file checks, the lookup and the download.
            var path = await Task.Run(() => fetcher.FetchAsync(artistId, name, capBytes, ct), ct);
            // Back on the caller's (UI) thread, as the album page's fetch.
            if (path is null || ct.IsCancellationRequested) return;
            ArtistVideoPath = path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Left the page.
        }
        catch (Exception ex)
        {
            DebugLog.Write(ITunesArtworkService.LogSource, $"artist video: artist page: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the artist's cached video and marks the artist so it is never fetched again
    /// (Remove Animated Artwork on the album page, for artists).
    /// </summary>
    [RelayCommand]
    private async Task RemoveArtistVideo()
    {
        var fetcher = ArtistVideoFetcherProvider();
        // Release the file first: the hero holds it open while it plays.
        StopArtistVideo();
        if (fetcher != null) await fetcher.RemoveAsync(Artist.Id);
    }
}

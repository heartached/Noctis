using Noctis.Models;

namespace Noctis.Services.YouTube;

/// <summary>A resolved music video stream for the lyrics page.</summary>
public sealed record MusicVideoStream(string VideoId, string Url, DateTime ExpiresUtc, bool IsUserChoice)
{
    /// <summary>How long before its expiry a stream is resolved again.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    public bool NeedsRefresh(DateTime nowUtc) => nowUtc >= ExpiresUtc - RefreshMargin;
}

/// <summary>
/// Music videos streamed from YouTube behind the lyrics (Discord #feedback 11-25, "Automatic
/// YouTube Music Video Streaming"). Core, not a plugin: the synced video layer, yt-dlp and its
/// installer are core already, and the lyrics page needs the result in its own layers.
/// <para>
/// Order per song: the user's choice (a video or "no video") always wins; otherwise, with
/// "Find music videos automatically" on, the remembered automatic match, else a YouTube search
/// scored by <see cref="MusicVideoMatcher"/>. The chosen id is persisted per song
/// (<see cref="MusicVideoChoiceStore"/>); the stream URL (it expires after some hours) only in
/// memory. A search that found nothing is remembered for <see cref="NoMatchRetry"/> so a song
/// without a video does not search on every play; a failure (offline, yt-dlp missing) is not
/// remembered at all.
/// </para>
/// </summary>
public sealed class MusicVideoStreamService
{
    public const int SearchCount = 8;
    public static readonly TimeSpan NoMatchRetry = TimeSpan.FromDays(14);
    /// <summary>Longest a resolved URL is reused, whatever its own expiry says.</summary>
    public static readonly TimeSpan MaxUrlLifetime = TimeSpan.FromHours(5);
    /// <summary>A URL without an expiry is reused this long.</summary>
    public static readonly TimeSpan UnknownUrlLifetime = TimeSpan.FromHours(1);
    private const int MaxCachedUrls = 32;

    private readonly IYouTubeVideoSource _source;
    private readonly MusicVideoChoiceStore _store;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (string Url, DateTime Expires)> _urls = new(StringComparer.Ordinal);

    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>A song's choice changed (the dialog): the player re-resolves when it is the current song.</summary>
    public event EventHandler<Guid>? ChoiceChanged;

    public MusicVideoStreamService(IYouTubeVideoSource source, MusicVideoChoiceStore store)
    {
        _source = source;
        _store = store;
    }

    public IYouTubeVideoSource Source => _source;
    public bool IsToolAvailable => _source.IsAvailable;

    public MusicVideoChoice? GetChoice(Track track) => _store.Get(track.Id);

    /// <summary>
    /// The stream for <paramref name="track"/>, or null (no choice and automatic off, "no
    /// video", nothing good enough found, yt-dlp missing or offline). Never throws except
    /// for cancellation.
    /// </summary>
    public async Task<MusicVideoStream?> FindForTrackAsync(Track track, bool autoFind, CancellationToken ct)
    {
        var choice = _store.Get(track.Id);
        if (choice?.Source == MusicVideoChoiceSource.User)
        {
            if (choice.Kind == MusicVideoChoiceKind.NoVideo || string.IsNullOrEmpty(choice.VideoId)) return null;
            return await StreamForAsync(choice.VideoId, isUser: true, track, forget: false, ct).ConfigureAwait(false);
        }

        if (!autoFind || !_source.IsAvailable) return null;

        if (choice?.Source == MusicVideoChoiceSource.Auto)
        {
            if (choice.Kind == MusicVideoChoiceKind.Video && !string.IsNullOrEmpty(choice.VideoId))
            {
                var stream = await StreamForAsync(choice.VideoId, isUser: false, track, forget: true, ct).ConfigureAwait(false);
                if (stream != null || _store.Get(track.Id) != null) return stream;
                // The remembered video is gone: fall through and search again.
            }
            else if (choice.Kind == MusicVideoChoiceKind.NoVideo && Clock() - choice.UpdatedUtc < NoMatchRetry)
            {
                return null;
            }
        }

        IReadOnlyList<YouTubeVideoInfo> results;
        try
        {
            results = await _source.SearchAsync(MusicVideoMatcher.SearchQuery(track.Artist, track.Title), SearchCount, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "MusicVideo.SearchFailed", ex.Message);
            return null; // offline / yt-dlp trouble: not remembered, the next play tries again
        }

        ct.ThrowIfCancellationRequested();
        var best = MusicVideoMatcher.PickBest(results, track.Artist, track.Title, track.Duration.TotalSeconds);
        // The user may have picked meanwhile (the dialog): theirs stays.
        if (_store.Get(track.Id)?.Source == MusicVideoChoiceSource.User) return null;
        _store.Set(NewChoice(track, best is null ? MusicVideoChoiceKind.NoVideo : MusicVideoChoiceKind.Video,
            MusicVideoChoiceSource.Auto, best));
        DebugLogger.Info(DebugLogger.Category.State, "MusicVideo.AutoMatch",
            best is null ? $"none of {results.Count}" : $"{best.Id} of {results.Count}");
        if (best is null) return null;
        return await StreamForAsync(best.Id, isUser: false, track, forget: true, ct).ConfigureAwait(false);
    }

    /// <summary>The same video again with a fresh URL (the old one is about to expire).</summary>
    public async Task<MusicVideoStream?> RefreshAsync(MusicVideoStream stream, CancellationToken ct)
    {
        lock (_cacheLock) _urls.Remove(stream.VideoId);
        try
        {
            var url = await _source.ResolveStreamUrlAsync(stream.VideoId, YtDlpParsing.StreamMaxHeight, ct).ConfigureAwait(false);
            return stream with { Url = url, ExpiresUtc = Remember(stream.VideoId, url) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "MusicVideo.RefreshFailed", ex.Message);
            return null;
        }
    }

    /// <summary>The search the "Wrong video?" dialog lists, best match first.</summary>
    public async Task<IReadOnlyList<YouTubeVideoInfo>> SearchCandidatesAsync(Track track, CancellationToken ct)
    {
        var results = await _source.SearchAsync(MusicVideoMatcher.SearchQuery(track.Artist, track.Title), SearchCount, ct).ConfigureAwait(false);
        return MusicVideoMatcher.Rank(results, track.Artist, track.Title, track.Duration.TotalSeconds);
    }

    /// <summary>The user's video for this song: it wins over any automatic match from now on.</summary>
    public void SetUserVideo(Track track, YouTubeVideoInfo video)
    {
        _store.Set(NewChoice(track, MusicVideoChoiceKind.Video, MusicVideoChoiceSource.User, video));
        ChoiceChanged?.Invoke(this, track.Id);
    }

    /// <summary>"Don't use a video for this song".</summary>
    public void SetNoVideo(Track track)
    {
        _store.Set(NewChoice(track, MusicVideoChoiceKind.NoVideo, MusicVideoChoiceSource.User, null));
        ChoiceChanged?.Invoke(this, track.Id);
    }

    /// <summary>Back to automatic: the user's choice (and a remembered match) is forgotten.</summary>
    public void ClearChoice(Track track)
    {
        _store.Remove(track.Id);
        ChoiceChanged?.Invoke(this, track.Id);
    }

    private MusicVideoChoice NewChoice(Track track, MusicVideoChoiceKind kind, MusicVideoChoiceSource source, YouTubeVideoInfo? video) => new()
    {
        TrackKey = MusicVideoChoiceStore.KeyFor(track.Id),
        MatchKey = MusicVideoMatcher.MatchKey(track.Artist, track.Title),
        Artist = track.Artist ?? string.Empty,
        Title = track.Title ?? string.Empty,
        DurationSeconds = (int)Math.Round(track.Duration.TotalSeconds),
        Kind = kind,
        Source = source,
        VideoId = video?.Id,
        VideoTitle = video?.Title,
        UpdatedUtc = Clock(),
    };

    private async Task<MusicVideoStream?> StreamForAsync(string videoId, bool isUser, Track track, bool forget, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_urls.TryGetValue(videoId, out var hit) && Clock() < hit.Expires - MusicVideoStream.RefreshMargin)
                return new MusicVideoStream(videoId, hit.Url, hit.Expires, isUser);
        }
        try
        {
            var url = await _source.ResolveStreamUrlAsync(videoId, YtDlpParsing.StreamMaxHeight, ct).ConfigureAwait(false);
            return new MusicVideoStream(videoId, url, Remember(videoId, url), isUser);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "MusicVideo.ResolveFailed", ex.Message);
            // An automatic match whose video is gone is dropped, so the next play searches again.
            if (forget && IsGone(ex)) _store.Remove(track.Id);
            return null;
        }
    }

    private static bool IsGone(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is YtDlpRunException run && YtDlpParsing.IsVideoGoneError(run.Stderr)) return true;
        return false;
    }

    private DateTime Remember(string videoId, string url)
    {
        var now = Clock();
        var expires = YtDlpParsing.StreamUrlExpiry(url) ?? now + UnknownUrlLifetime;
        if (expires > now + MaxUrlLifetime) expires = now + MaxUrlLifetime;
        lock (_cacheLock)
        {
            if (_urls.Count >= MaxCachedUrls && !_urls.ContainsKey(videoId))
            {
                var oldest = _urls.OrderBy(kv => kv.Value.Expires).First().Key;
                _urls.Remove(oldest);
            }
            _urls[videoId] = (url, expires);
        }
        return expires;
    }
}

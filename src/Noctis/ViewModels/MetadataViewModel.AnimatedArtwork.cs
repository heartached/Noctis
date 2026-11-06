using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Edit Info → Animated Artwork → Search: the chooser. The old search opened candidates one by
/// one and stopped at the first album with a loop, so the pop-up could only ever show that one
/// album (and it labelled it with the edited track's own album and artist, whatever was found).
/// Now every relevant candidate is scored, the best few are opened at once, and each album with
/// a loop becomes a card: its loop's still, title, artist, edition, and a button per quality —
/// the same grid as the still Artwork chooser, with a search box inside to refine and retry.
/// </summary>
public partial class MetadataViewModel
{
    /// <summary>How many of the best-scored albums are opened per search.</summary>
    internal const int AnimatedCandidatesToOpen = 10;

    /// <summary>How many album pages are fetched at once.</summary>
    private const int AnimatedPageConcurrency = 4;

    /// <summary>The query the chooser's box shows when the search came from the tags.</summary>
    [ObservableProperty] private string _animatedSearchQueryHint = string.Empty;

    /// <summary>Second line of the empty / no-result state: why nothing is offered.</summary>
    [ObservableProperty] private string _animatedSearchDetail = string.Empty;

    /// <summary>Albums with a loop are still being looked for (results may already show).</summary>
    [ObservableProperty] private bool _isProbingAnimatedCandidates;

    // Bumped by every animated-artwork search. Background work (page probes, posters, the
    // preview download) can outlive its search; it may only write while its search is current.
    private int _animatedSearchGeneration;
    private CancellationTokenSource? _animatedSearchCts;
    private AnimatedArtworkSearchResult? _previewingResult;
    private int _animatedPreviewRequest;

    /// <summary>GitHub #185: the Animated Artwork chooser's download bar (percent, speed, time left).</summary>
    public ArtworkDownloadProgress AnimatedArtworkDownload { get; } = new();

    partial void OnIsAnimatedArtworkSearchOpenChanged(bool value)
    {
        // Closed: stop opening album pages, stop decoding the preview and drop its temp file,
        // stop a picked loop still downloading (its partial file is deleted).
        // Whatever was found stays listed; a later search starts over anyway.
        if (value) return;
        _animatedSearchCts?.Cancel();
        AnimatedArtworkDownload.Cancel();
        ClearAnimatedPreview();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchAnimatedCover()
    {
        if (_itunes == null) { AnimatedSearchStatus = Loc.T("Metadata.AnimSearchUnavailable"); return; }

        // A new search replaces the running one (the chooser's box allows re-searching mid-flight).
        _animatedSearchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _animatedSearchCts = cts;
        var ct = cts.Token;
        var generation = ++_animatedSearchGeneration;
        bool Current() => generation == _animatedSearchGeneration;

        IsSearchingAnimatedCover = true;
        IsProbingAnimatedCandidates = false;
        AnimatedSearchStatus = Loc.T("Metadata.AnimSearching");
        AnimatedSearchDetail = string.Empty;
        ClearAnimatedResults();
        IsAnimatedArtworkSearchOpen = true;
        try
        {
            var input = (AnimatedSearchInput ?? string.Empty).Trim();
            var noneDetail = string.Empty;

            if (AppleMusicLink.TryParse(input, out var link))
            {
                // Manual override: a pasted link names the album (or playlist) outright.
                AnimatedSearchQueryHint = input;
                DebugLog.Write(ITunesArtworkService.LogSource, $"search: pasted {link.Kind} link ({link.Storefront}) {link.Id}");
                var album = await ResolvePastedLinkAsync(link, ct);
                if (!Current()) return;
                if (album != null)
                    AddAnimatedResult(album, 100, generation);
                noneDetail = Loc.T("Metadata.AnimLinkHasNone");
            }
            else
            {
                // A typed name steers the search away from the loaded track's tags. It may carry
                // the artist too ("Happier Than Ever Billie Eilish"), which is how people type it;
                // the ranking reads the leftover words as the artist.
                var typed = input.Length > 0;
                var trackArtist = !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : Artist;
                trackArtist = (trackArtist ?? string.Empty).Trim();
                var albumTerm = typed ? input : GetArtworkSearchAlbumTerm();
                var artist = typed ? string.Empty : trackArtist;
                AnimatedSearchQueryHint = typed || artist.Length == 0 ? albumTerm : $"{albumTerm} — {artist}";
                DebugLog.Write(ITunesArtworkService.LogSource,
                    typed ? $"search: typed \"{albumTerm}\"" : $"search: tags \"{albumTerm}\" by \"{artist}\"");

                var ranked = await _itunes.FindAnimatedArtworkCandidatesAsync(
                    new ITunesArtworkService.AnimatedArtworkQuery(albumTerm, artist, trackArtist, typed)
                    {
                        // Tie-breaks between editions only; a typed query is about something else.
                        Year = !typed && int.TryParse(Year, out var year) ? year : 0,
                        TrackCount = !typed && _albumScoped ? SongCount : 0,
                    },
                    AnimatedCandidatesToOpen, ct);
                if (!Current()) return;

                await ProbeAnimatedCandidatesAsync(ranked, generation, ct);
                if (!Current()) return;
                noneDetail = ranked.Count == 0
                    ? Loc.T("Metadata.AnimNoMatches")
                    : Loc.T("Metadata.AnimNoneOfMatches", ranked.Count);
            }

            HasAnimatedArtworkSearchResults = AnimatedArtworkSearchResults.Count > 0;
            if (!HasAnimatedArtworkSearchResults)
            {
                AnimatedSearchStatus = Loc.T("Metadata.AnimNoneFound");
                AnimatedSearchDetail = noneDetail;
                return;
            }

            // No informational status once results show — the cards speak for themselves; the
            // status line stays reserved for errors.
            AnimatedSearchStatus = string.Empty;

            // Play the best match's loop in its card: a small variant, downloaded in the
            // background; the card's still shows until it is ready.
            _ = PreviewAnimatedResultAsync(AnimatedArtworkSearchResults[0], generation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Replaced by a newer search, which owns the dialog now.
        }
        catch (Exception ex)
        {
            DebugLog.Write(ITunesArtworkService.LogSource, $"search failed: {ex}");
            if (Current())
                AnimatedSearchStatus = Loc.T("Metadata.AnimSearchFailed", ex.Message);
        }
        finally
        {
            if (Current())
            {
                IsSearchingAnimatedCover = false;
                IsProbingAnimatedCandidates = false;
            }
        }
    }

    /// <summary>
    /// A pasted link: albums and songs resolve through /lookup in the link's own storefront (a
    /// song names its album), then that album's page is read; playlists are read straight from
    /// their page. An album page is opened at the pasted address even when /lookup does not
    /// know it, so a storefront-only album still works.
    /// </summary>
    private async Task<ITunesArtworkService.AnimatedArtworkAlbum?> ResolvePastedLinkAsync(AppleMusicLink link, CancellationToken ct)
    {
        if (link.Kind == AppleMusicLinkKind.Playlist)
            return link.PageUrl == null ? null : await _itunes!.GetAnimatedArtworkAlbumAsync(link.PageUrl, null, ct);

        var known = await _itunes!.LookupAlbumByIdAsync(link.Id, ct, link.Storefront);
        var page = link.PageUrl ?? known?.ViewUrl;
        return page == null ? null : await _itunes.GetAnimatedArtworkAlbumAsync(page, known, ct);
    }

    /// <summary>Opens the ranked candidates a few at a time; each one with a loop joins the grid
    /// in score order as soon as it is known.</summary>
    private async Task ProbeAnimatedCandidatesAsync(
        IReadOnlyList<ITunesArtworkService.RankedAlbumCandidate> ranked, int generation, CancellationToken ct)
    {
        if (ranked.Count == 0) return;
        IsProbingAnimatedCandidates = true;

        // Not disposed: probes still queued when a newer search cancels this one may yet touch it.
        var gate = new SemaphoreSlim(AnimatedPageConcurrency);
        var pending = ranked.Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return (r.Score, Album: await _itunes!.GetAnimatedArtworkAlbumAsync(r.Candidate.ViewUrl, r.Candidate, ct));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var (score, album) = await done;
            if (generation != _animatedSearchGeneration) return;
            if (album == null) continue;

            AddAnimatedResult(album, score, generation);
            HasAnimatedArtworkSearchResults = true;
            AnimatedSearchStatus = string.Empty;
        }
    }

    private void AddAnimatedResult(ITunesArtworkService.AnimatedArtworkAlbum album, double score, int generation)
    {
        if (album.Variants.Count == 0) return;
        if (album.CollectionId != 0 && AnimatedArtworkSearchResults.Any(r => r.Album.CollectionId == album.CollectionId))
            return;

        var item = new AnimatedArtworkSearchResult(album, score);
        var index = 0;
        while (index < AnimatedArtworkSearchResults.Count && AnimatedArtworkSearchResults[index].Score >= score)
            index++;
        AnimatedArtworkSearchResults.Insert(index, item);
        _ = LoadAnimatedPosterAsync(item, generation);
    }

    private async Task LoadAnimatedPosterAsync(AnimatedArtworkSearchResult item, int generation)
    {
        var url = item.Album.PosterUrl ?? item.Album.CoverThumbUrl;
        if (string.IsNullOrWhiteSpace(url) || _itunes == null) return;

        var data = await _itunes.DownloadAsync(url);
        if (data is not { Length: > 0 } || generation != _animatedSearchGeneration ||
            !AnimatedArtworkSearchResults.Contains(item))
            return;

        try
        {
            using var ms = new MemoryStream(data);
            item.Poster = new Bitmap(ms);
        }
        catch
        {
            // A poster is decoration; the card works without it.
        }
    }

    private void ClearAnimatedResults()
    {
        ClearAnimatedPreview();
        foreach (var result in AnimatedArtworkSearchResults)
            result.Dispose();
        AnimatedArtworkSearchResults.Clear();
        HasAnimatedArtworkSearchResults = false;
    }

    [RelayCommand]
    private Task PreviewAnimatedArtworkResult(AnimatedArtworkSearchResult? result)
        => PreviewAnimatedResultAsync(result, _animatedSearchGeneration);

    /// <summary>
    /// Plays one card's loop in place of its still: the album's smallest variant (a few hundred
    /// kB), so previewing costs next to nothing. One card previews at a time.
    /// </summary>
    private async Task PreviewAnimatedResultAsync(AnimatedArtworkSearchResult? result, int generation)
    {
        if (result?.Album.PreviewVariant is not { } variant || _itunes == null) return;
        if (ReferenceEquals(_previewingResult, result) && (result.PreviewPath != null || result.IsPreviewLoading))
            return;

        ClearAnimatedPreview();
        var request = ++_animatedPreviewRequest;
        _previewingResult = result;
        result.IsPreviewLoading = true;
        try
        {
            var path = await DownloadAnimatedVariantAsync(variant, generation, reportErrors: false,
                ct: _animatedSearchCts?.Token ?? default);
            if (string.IsNullOrWhiteSpace(path)) return;
            if (request != _animatedPreviewRequest || generation != _animatedSearchGeneration ||
                !IsAnimatedArtworkSearchOpen || !AnimatedArtworkSearchResults.Contains(result))
            {
                TryDeleteFile(path);
                return;
            }

            result.PreviewPath = path;
            AnimatedPreviewPath = path;
        }
        catch
        {
            // The preview is optional.
        }
        finally
        {
            if (request == _animatedPreviewRequest)
                result.IsPreviewLoading = false;
        }
    }

    private void ClearAnimatedPreview()
    {
        _animatedPreviewRequest++;
        if (_previewingResult != null)
        {
            _previewingResult.PreviewPath = null;
            _previewingResult.IsPreviewLoading = false;
            _previewingResult = null;
        }

        var old = AnimatedPreviewPath;
        AnimatedPreviewPath = null;
        if (!string.IsNullOrWhiteSpace(old)) TryDeleteFile(old);
    }

    [RelayCommand]
    private async Task SelectAnimatedArtworkResult(AnimatedArtworkChoice? choice)
    {
        if (choice == null || _itunes == null || IsDownloadingAnimatedCover)
            return;

        IsDownloadingAnimatedCover = true;
        // Clear any prior error; the bar carries its own "Downloading…" label.
        AnimatedSearchStatus = string.Empty;
        var ct = AnimatedArtworkDownload.Begin();
        try
        {
            var tempPath = await DownloadAnimatedVariantAsync(choice.Variant, ct: ct, onProgress: AnimatedArtworkDownload.Report);
            if (string.IsNullOrWhiteSpace(tempPath))
                return;
            if (ct.IsCancellationRequested)
            {
                // Closed while the last bytes landed: not applied.
                TryDeleteFile(tempPath);
                return;
            }

            _newAnimatedCoverSource = tempPath;
            _animatedCoverRemoved = false;
            AnimatedCoverPath = string.Empty;
            AnimatedCoverPath = tempPath;
            HasAnimatedCover = true;
            AnimatedSearchStatus = Loc.T("Metadata.AnimApplied");
            IsAnimatedArtworkSearchOpen = false;
        }
        catch (Exception ex)
        {
            AnimatedSearchStatus = Loc.T("Metadata.AnimDownloadFailed", ex.Message);
        }
        finally
        {
            AnimatedArtworkDownload.End();
            IsDownloadingAnimatedCover = false;
        }
    }

    [RelayCommand]
    private void CloseAnimatedArtworkSearch() => IsAnimatedArtworkSearchOpen = false;

    /// <param name="generation">The search a background preview belongs to; its failure is only
    /// reported while that search is still the current one. Null for a user's own pick.</param>
    /// <param name="ct">Cancelled when the pop-up or the window closes; nothing is left on disk then.</param>
    /// <param name="onProgress">Bytes saved and the expected total (0 when unknown), for the bar.</param>
    private async Task<string?> DownloadAnimatedVariantAsync(
        ITunesArtworkService.AnimatedArtworkVariant variant, int? generation = null, bool reportErrors = true,
        CancellationToken ct = default, Action<long, long>? onProgress = null)
    {
        bool Report() => reportErrors && !ct.IsCancellationRequested
                         && (generation == null || generation == _animatedSearchGeneration);

        // Streamed straight to the file (plain or HLS); a failed or cancelled download deletes it.
        var tempPath = Path.Combine(Path.GetTempPath(), $"noctis-animatedcover-{Guid.NewGuid():N}.mp4");
        if (!await _itunes!.DownloadHlsVariantAsMp4Async(variant, tempPath, ct, onProgress))
        {
            TryDeleteFile(tempPath);
            if (Report())
                AnimatedSearchStatus = Loc.T(variant.IsHls ? "Metadata.AnimHlsDownloadFailed" : "Metadata.AnimCoverDownloadFailed");
            return null;
        }

        return tempPath;
    }

    /// <summary>Window closed: stop the search and free the chooser's posters and preview.</summary>
    internal void DisposeAnimatedSearch()
    {
        _animatedSearchCts?.Cancel();
        // A pick still downloading in either chooser stops with the window (GitHub #185).
        AnimatedArtworkDownload.Cancel();
        ArtworkDownload.Cancel();
        _animatedSearchGeneration++;
        ClearAnimatedResults();
    }
}

/// <summary>One album with motion artwork in the chooser grid.</summary>
public sealed partial class AnimatedArtworkSearchResult : ObservableObject, IArtworkChooserItem, IDisposable
{
    public AnimatedArtworkSearchResult(ITunesArtworkService.AnimatedArtworkAlbum album, double score)
    {
        Album = album;
        Score = score;
        var variants = album.Variants;
        Choices = variants
            .Select((v, i) => new AnimatedArtworkChoice(this, v, IsPrimary: i == variants.Count - 1))
            .ToList();
    }

    public ITunesArtworkService.AnimatedArtworkAlbum Album { get; }

    /// <summary>Relevance to the query; the grid is sorted by it, best first.</summary>
    public double Score { get; }

    /// <summary>One button per offered quality, lowest first; the last (largest) is the primary.</summary>
    public IReadOnlyList<AnimatedArtworkChoice> Choices { get; }

    public string CollectionName => Album.Title;
    public string ArtistName => Album.Artist;

    /// <summary>"2024 · Clean", "Portrait" — what tells two same-titled cards apart.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>(3);
            if (Album.ReleaseYear > 0) parts.Add(Album.ReleaseYear.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Album.Explicitness.Equals("cleaned", StringComparison.OrdinalIgnoreCase)) parts.Add(Loc.T("Metadata.AnimClean"));
            else if (Album.Explicitness.Equals("explicit", StringComparison.OrdinalIgnoreCase)) parts.Add(Loc.T("Metadata.AnimExplicit"));
            if (Album.IsTallOnly) parts.Add(Loc.T("Metadata.AnimPortrait"));
            return string.Join(" · ", parts);
        }
    }

    public bool HasDetail => Detail.Length > 0;

    bool IArtworkChooserItem.CanPreview => true;
    string? IArtworkChooserItem.PosterTip => Loc.T("Metadata.AnimPreviewTip");
    IReadOnlyList<IArtworkChooserChoice> IArtworkChooserItem.Choices => Choices;

    [ObservableProperty] private Bitmap? _poster;
    [ObservableProperty] private string? _previewPath;
    [ObservableProperty] private bool _isPreviewLoading;

    public bool IsPreviewing => !string.IsNullOrEmpty(PreviewPath);
    partial void OnPreviewPathChanged(string? value) => OnPropertyChanged(nameof(IsPreviewing));

    /// <summary>The first offered variant (kept for callers that want "the" result's stream).</summary>
    public ITunesArtworkService.AnimatedArtworkVariant Variant => Album.Variants[0];

    public void Dispose()
    {
        var poster = Poster;
        Poster = null;
        poster?.Dispose();
    }
}

/// <summary>One quality button on a chooser card ("1080p", "2160p").</summary>
public sealed record AnimatedArtworkChoice(
    AnimatedArtworkSearchResult Owner,
    ITunesArtworkService.AnimatedArtworkVariant Variant,
    bool IsPrimary) : IArtworkChooserChoice
{
    public string Label => Variant.Label;
    public bool IsSecondary => !IsPrimary;
}

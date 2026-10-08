using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.Services.MetadataSearch;

namespace Noctis.ViewModels.MetadataSearch;

public enum MetadataSearchState { Idle, Unavailable, WaitingForLoad, Searching, Results, Empty, Failed }

/// <summary>
/// The metadata editor's "Find online" panel (owner 10-08: Search metadata revamp — "it is not
/// good … I want the user to be able to see the changes before and after"). The old button ran
/// one silent best guess and listed "old → new" strings. This panel lets the user edit the
/// query, pick sources, choose between ranked candidates, compare every field and the cover
/// side by side, and (album scope) see exactly which local track each release track lands on.
///
/// Apply only writes into the editor's edit fields (and stages the cover / per-track values):
/// nothing reaches a file until the user presses Save, as before. The editor owns the field
/// mapping (<see cref="MetadataViewModel.GetSearchFieldState"/>) and the undo.
/// </summary>
public sealed partial class MetadataSearchPanelViewModel : ObservableObject, IDisposable
{
    private readonly MetadataViewModel _owner;
    private readonly IMetadataSearchService? _service;
    private readonly Func<Uri, CancellationToken, Task<byte[]?>>? _thumbLoader;

    // One token per concern: a re-search must not kill the cover the user is previewing, and
    // picking another result must not kill the search's thumbnails.
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _artworkCts;
    private readonly Dictionary<MetadataCandidate, byte[]> _artworkCache = new(ReferenceEqualityComparer.Instance);
    private bool _prefilled;
    private bool _searchWhenLoaded;
    private bool _ownsNewArtwork;
    private bool _suppressArtworkDownload;

    public MetadataSearchPanelViewModel(MetadataViewModel owner, IMetadataSearchService? service,
        Func<Uri, CancellationToken, Task<byte[]?>>? thumbLoader)
    {
        _owner = owner;
        _service = service;
        _thumbLoader = thumbLoader;
        AlbumScope = owner.IsAlbumScopeForSearch;
        if (service != null)
        {
            foreach (var name in service.Providers)
            {
                var chip = new ProviderChip(name);
                chip.PropertyChanged += OnProviderChipChanged;
                Providers.Add(chip);
            }
        }
        _owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    /// <summary>Album editor: candidates are releases, matched track by track.</summary>
    public bool AlbumScope { get; }
    public bool TrackScope => !AlbumScope;
    public bool IsAvailable => _service != null;

    [ObservableProperty] private bool _isOpen;

    // ── Query ──
    [ObservableProperty] private string _queryTitle = string.Empty;
    [ObservableProperty] private string _queryArtist = string.Empty;
    [ObservableProperty] private string _queryAlbum = string.Empty;
    [ObservableProperty] private string _queryAlbumArtist = string.Empty;

    public ObservableCollection<ProviderChip> Providers { get; } = new();
    public bool HasProviderChoice => Providers.Count > 1;

    // ── Search state ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(HasResults), nameof(IsEmpty), nameof(IsUnavailable),
        nameof(IsWaitingForLoad), nameof(IsFailed), nameof(ShowMessage), nameof(MessageTitle), nameof(MessageBody))]
    private MetadataSearchState _state;

    public bool IsSearching => State == MetadataSearchState.Searching;
    public bool HasResults => State == MetadataSearchState.Results;
    public bool IsEmpty => State == MetadataSearchState.Empty;
    public bool IsUnavailable => State == MetadataSearchState.Unavailable;
    public bool IsWaitingForLoad => State == MetadataSearchState.WaitingForLoad;
    public bool IsFailed => State == MetadataSearchState.Failed;
    /// <summary>The centred message in place of results (empty, unavailable, failed, waiting).</summary>
    public bool ShowMessage => State is MetadataSearchState.Empty or MetadataSearchState.Unavailable
        or MetadataSearchState.Failed or MetadataSearchState.WaitingForLoad or MetadataSearchState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageBody))]
    private string _errorText = string.Empty;

    public string MessageTitle => Localization.Loc.T(State switch
    {
        MetadataSearchState.Unavailable => "MetadataSearch.UnavailableTitle",
        MetadataSearchState.Empty => "MetadataSearch.EmptyTitle",
        MetadataSearchState.Failed => "MetadataSearch.FailedTitle",
        MetadataSearchState.WaitingForLoad => "MetadataSearch.WaitingTitle",
        _ => "MetadataSearch.IdleTitle",
    });

    public string MessageBody => State switch
    {
        MetadataSearchState.Unavailable => Localization.Loc.T("MetadataSearch.UnavailableBody"),
        MetadataSearchState.Empty => Localization.Loc.T("MetadataSearch.EmptyBody"),
        MetadataSearchState.Failed => ErrorText,
        MetadataSearchState.WaitingForLoad => Localization.Loc.T("MetadataSearch.WaitingBody"),
        _ => Localization.Loc.T("MetadataSearch.IdleBody"),
    };

    /// <summary>Per-provider outcome of the last search: "Deezer 5 · MusicBrainz 3 · Apple Music offline".</summary>
    public ObservableCollection<ProviderStatusItem> ProviderStatuses { get; } = new();
    public bool HasProviderStatuses => ProviderStatuses.Count > 0;
    public string ProviderStatusLine => string.Join(" · ", ProviderStatuses.Select(s => s.Text));

    public ObservableCollection<CandidateItem> Candidates { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CandidateItem? _selectedCandidate;

    public bool HasSelection => SelectedCandidate != null;

    // ── Before / after ──
    /// <summary>Every field the selected candidate knows, changed or not.</summary>
    public ObservableCollection<CompareRow> Rows { get; } = new();
    /// <summary>What the table shows: changed rows, plus unchanged ones when asked.</summary>
    public ObservableCollection<CompareRow> VisibleRows { get; } = new();

    [ObservableProperty] private bool _showUnchanged;
    [ObservableProperty] private bool _onlyFillEmpty;

    public int ChangedFieldCount => Rows.Count(r => r.IsChanged);
    public int UnchangedFieldCount => Rows.Count(r => !r.IsChanged);
    public bool HasUnchangedFields => UnchangedFieldCount > 0;
    public bool HasNoFieldChanges => SelectedCandidate != null && ChangedFieldCount == 0;
    public string ShowUnchangedText => Localization.Loc.T("MetadataSearch.ShowUnchanged", UnchangedFieldCount);
    public string FieldSummary => ChangedFieldCount == 0
        ? Localization.Loc.T("MetadataSearch.NoFieldChanges")
        : Localization.Loc.T("MetadataSearch.FieldsDiffer", ChangedFieldCount);

    // Artwork: the editor's current cover beside the candidate's.
    public Bitmap? CurrentArtwork => _owner.ArtworkPreview;
    public bool HasCurrentArtwork => _owner.HasArtwork && _owner.ArtworkPreview != null;
    public string CurrentArtworkSize => HasCurrentArtwork ? _owner.ArtworkDimensions : Localization.Loc.T("MetadataSearch.NoCover");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewArtworkPreview))]
    private Bitmap? _newArtwork;

    public bool HasNewArtworkPreview => NewArtwork != null;

    [ObservableProperty] private bool _hasNewArtwork;
    [ObservableProperty] private string _newArtworkSize = string.Empty;
    [ObservableProperty] private bool _useArtwork;
    [ObservableProperty] private bool _isDownloadingArtwork;
    [ObservableProperty] private string _artworkNote = string.Empty;

    // Album scope: the release's tracks laid onto the local ones.
    public ObservableCollection<TrackMatchRow> TrackRows { get; } = new();
    [ObservableProperty] private string _trackSummary = string.Empty;
    [ObservableProperty] private string _trackExtraNote = string.Empty;

    /// <summary>0 = album fields, 1 = tracks (album scope only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFieldsPane), nameof(IsTracksPane))]
    private int _detailPane;

    public bool IsFieldsPane => DetailPane == 0 || !AlbumScope;
    public bool IsTracksPane => DetailPane == 1 && AlbumScope;
    public string TracksTabText => Localization.Loc.T("MetadataSearch.TracksTab", TrackRows.Count(r => r.IsMatched), TrackRows.Count);

    // ── Footer ──
    public int SelectedChangeCount =>
        Rows.Count(r => r.IsChecked && r.CanToggle)
        + (UseArtwork && HasNewArtwork ? 1 : 0)
        + TrackRows.Count(r => r.IsIncluded && r.CanToggle);

    public string ApplyText => SelectedChangeCount == 0
        ? Localization.Loc.T("MetadataSearch.ApplyNone")
        : SelectedChangeCount == 1
            ? Localization.Loc.T("MetadataSearch.ApplyOne")
            : Localization.Loc.T("MetadataSearch.ApplyMany", SelectedChangeCount);

    public string FooterText => SelectedCandidate == null
        ? Localization.Loc.T("MetadataSearch.FooterIdle")
        : Localization.Loc.T("MetadataSearch.FooterSelected", SelectedCandidate.Provider);

    // ── Open / close ──

    [RelayCommand]
    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        if (!_prefilled) Prefill();
        if (_service == null)
        {
            State = MetadataSearchState.Unavailable;
            return;
        }
        // First open, or a search the last close cancelled: (re)run it. Otherwise keep the
        // results and re-read the editor, which may have been edited since.
        if (State is MetadataSearchState.Idle or MetadataSearchState.Searching or MetadataSearchState.WaitingForLoad)
        {
            _ = SearchAsync();
        }
        else
        {
            RebuildComparison();
            // The close cancelled any thumbnails still on their way; fetch the missing ones.
            var missing = Candidates.Where(c => !c.HasThumbnail).ToList();
            if (missing.Count > 0)
            {
                var cts = new CancellationTokenSource();
                _searchCts = cts;
                _ = LoadThumbnailsAsync(missing, cts.Token);
            }
        }
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        _searchCts?.Cancel();
        _artworkCts?.Cancel();
        _searchWhenLoaded = false;
        IsDownloadingArtwork = false;
        IsOpen = false;
    }

    private void Prefill()
    {
        _prefilled = true;
        var seed = _owner.SearchQuerySeed();
        QueryTitle = seed.Title;
        QueryArtist = seed.Artist;
        QueryAlbum = seed.Album;
        QueryAlbumArtist = seed.AlbumArtist;
    }

    // ── Search ──

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Search() => SearchAsync();

    /// <summary>Runs the query. A newer search (or a close) cancels this one; while the editor
    /// is still loading the file's tags the search waits for them, since the comparison needs
    /// the loaded values (ISRC, label, …) and Save is blocked until then anyway.</summary>
    public async Task SearchAsync()
    {
        if (_service == null) { State = MetadataSearchState.Unavailable; return; }

        _searchCts?.Cancel();
        if (_owner.IsLoading)
        {
            _searchWhenLoaded = true;
            State = MetadataSearchState.WaitingForLoad;
            return;
        }
        _searchWhenLoaded = false;

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _artworkCts?.Cancel();
        SelectedCandidate = null;
        ClearCandidates();
        ProviderStatuses.Clear();
        RaiseStatusLine();
        State = MetadataSearchState.Searching;
        DebugLogger.Info(DebugLogger.Category.UI, "MetadataSearch.Search", $"album={AlbumScope}");

        MetadataSearchResult result;
        try
        {
            result = await _service.SearchAsync(BuildQuery(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded or closed; whoever cancelled owns the state now
        }
        catch (Exception ex)
        {
            // The contract says providers never throw; a broken service still must not
            // leave the panel spinning.
            if (_searchCts != cts) return;
            ErrorText = ex.Message;
            State = MetadataSearchState.Failed;
            return;
        }
        if (_searchCts != cts || cts.IsCancellationRequested) return;

        foreach (var s in result.Providers)
            ProviderStatuses.Add(StatusItem(s));
        RaiseStatusLine();

        // Best first, whatever order the service returned (stable for equal scores).
        var items = result.Candidates
            .OrderByDescending(c => c.Confidence)
            .Select(c => new CandidateItem(c, AlbumScope))
            .ToList();
        foreach (var item in items) Candidates.Add(item);

        // Every queried source down is not "no matches": the user should retry, not reword.
        var queried = result.Providers.Where(p => p.Outcome != ProviderOutcome.Disabled).ToList();
        if (items.Count == 0 && queried.Count > 0
            && queried.All(p => p.Outcome is ProviderOutcome.Failed or ProviderOutcome.TimedOut))
        {
            ErrorText = ProviderStatusLine;
            State = MetadataSearchState.Failed;
            return;
        }
        State = items.Count > 0 ? MetadataSearchState.Results : MetadataSearchState.Empty;
        SelectedCandidate = items.FirstOrDefault();
        _ = LoadThumbnailsAsync(items, cts.Token);
    }

    private MetadataQuery BuildQuery()
    {
        var seed = _owner.SearchQuerySeed();
        var all = Providers.All(p => p.IsSelected);
        return seed with
        {
            Title = AlbumScope ? string.Empty : QueryTitle.Trim(),
            Artist = AlbumScope ? QueryAlbumArtist.Trim() : QueryArtist.Trim(),
            Album = QueryAlbum.Trim(),
            AlbumArtist = AlbumScope ? QueryAlbumArtist.Trim() : seed.AlbumArtist,
            Providers = all ? Array.Empty<string>() : Providers.Where(p => p.IsSelected).Select(p => p.Name).ToArray(),
        };
    }

    private static ProviderStatusItem StatusItem(ProviderStatus s) => s.Outcome switch
    {
        ProviderOutcome.Ok => new($"{s.Provider} {s.ResultCount}", true, false),
        ProviderOutcome.NoResults => new($"{s.Provider} 0", false, false),
        ProviderOutcome.TimedOut => new(Localization.Loc.T("MetadataSearch.ProviderTimedOut", s.Provider), false, true),
        ProviderOutcome.Disabled => new(Localization.Loc.T("MetadataSearch.ProviderOff", s.Provider), false, false),
        _ => new(Localization.Loc.T("MetadataSearch.ProviderOffline", s.Provider), false, true),
    };

    private void RaiseStatusLine()
    {
        OnPropertyChanged(nameof(HasProviderStatuses));
        OnPropertyChanged(nameof(ProviderStatusLine));
    }

    private void OnProviderChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProviderChip.IsSelected) || sender is not ProviderChip chip) return;
        // At least one source stays on: an empty filter would read as "all" to the service
        // while the chips say "none".
        if (!chip.IsSelected && Providers.All(p => !p.IsSelected))
        {
            chip.IsSelected = true;
            return;
        }
        // Re-run what's on screen with the new sources (the previous run is cancelled) — a
        // search still running too, or it would land results from the sources just turned off.
        if (IsOpen && State is MetadataSearchState.Results or MetadataSearchState.Empty or MetadataSearchState.Failed
                or MetadataSearchState.Searching)
            _ = SearchAsync();
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<CandidateItem> items, CancellationToken ct)
    {
        if (_thumbLoader == null) return;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(items.Select(async item =>
        {
            var url = item.Candidate.ArtworkThumbUrl ?? item.Candidate.ArtworkUrl;
            if (url == null) return;
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var data = await _thumbLoader(url, ct);
                    if (data is not { Length: > 0 } || ct.IsCancellationRequested) return;
                    // Decoded small and off the UI thread: a list of 3000 px covers would
                    // otherwise cost ~36 MB each and stall the scroll.
                    var bmp = await Task.Run(() =>
                    {
                        try { using var ms = new MemoryStream(data); return Bitmap.DecodeToWidth(ms, 120); }
                        catch { return null; }
                    }, ct);
                    if (bmp == null) return;
                    if (ct.IsCancellationRequested) { bmp.Dispose(); return; }
                    item.Thumbnail = bmp;
                    if (item == SelectedCandidate && !_ownsNewArtwork) NewArtwork = bmp;
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DebugLogger.Info(DebugLogger.Category.UI, "MetadataSearch.ThumbFailed", ex.Message);
            }
        }));
    }

    private void ClearCandidates()
    {
        ReleaseNewArtwork();
        foreach (var c in Candidates) c.Dispose();
        Candidates.Clear();
        _artworkCache.Clear();
        Rows.Clear();
        VisibleRows.Clear();
        TrackRows.Clear();
        RaiseCounts();
    }

    // ── Selection → comparison ──

    partial void OnSelectedCandidateChanged(CandidateItem? value)
    {
        _artworkCts?.Cancel();
        IsDownloadingArtwork = false;
        RebuildComparison();
        OnPropertyChanged(nameof(FooterText));
    }

    /// <summary>Re-reads the editor and lays the selected candidate over it.</summary>
    public void RebuildComparison()
    {
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        foreach (var r in TrackRows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        TrackRows.Clear();
        ArtworkNote = string.Empty;
        ReleaseNewArtwork();
        RaiseArtworkCurrent();

        var item = SelectedCandidate;
        if (item == null)
        {
            HasNewArtwork = false;
            SetUseArtworkQuietly(false);
            RebuildVisibleRows();
            RaiseCounts();
            return;
        }

        var c = item.Candidate;
        foreach (var field in Enum.GetValues<MetadataSearchField>())
        {
            var row = BuildRow(field, c);
            if (row == null) continue;
            row.IsChecked = DefaultChecked(row);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        // Artwork: the thumbnail stands in until the user asks for the real cover.
        HasNewArtwork = c.ArtworkUrl != null || c.ArtworkThumbUrl != null;
        NewArtworkSize = c.ArtworkSize is > 0 ? $"{c.ArtworkSize} × {c.ArtworkSize}" : string.Empty;
        if (_artworkCache.TryGetValue(c, out var cached)) ShowFullArtwork(cached);
        else NewArtwork = item.Thumbnail;
        UpdateArtworkNote();
        // A track with no cover gets one by default; replacing an existing cover is opt-in.
        SetUseArtworkQuietly(HasNewArtwork && !HasCurrentArtwork);
        if (UseArtwork && !_artworkCache.ContainsKey(c)) _ = EnsureArtworkAsync();

        if (AlbumScope) BuildTrackRows(c);

        RebuildVisibleRows();
        RaiseCounts();
    }

    private CompareRow? BuildRow(MetadataSearchField field, MetadataCandidate c)
    {
        var state = _owner.GetSearchFieldState(field);
        if (!state.Applicable) return null;
        var value = CandidateValue(c, field, AlbumScope);
        if (string.IsNullOrWhiteSpace(value)) return null; // the provider doesn't know: never "clear"
        value = value.Trim();
        var changed = !SameValue(field, state.Raw, value);
        return new CompareRow(field, FieldLabel(field), state.Display, DisplayValue(field, value), value,
            changed, state.IsEmpty, state.BlockedReason);
    }

    private bool DefaultChecked(CompareRow row)
    {
        if (!row.CanToggle) return false;
        if (OnlyFillEmpty) return row.CurrentIsEmpty;
        // Filling a gap is always welcome.
        if (row.CurrentIsEmpty) return true;
        // An album's artist is per track: a release artist laid over "Mixed" would wipe every
        // featured artist on the album, so that one waits for an explicit tick.
        if (AlbumScope && row.Field == MetadataSearchField.Artist && _owner.IsSearchFieldMixed(row.Field))
            return false;
        return IsSafeOverwrite(row.Field, AlbumScope);
    }

    /// <summary>
    /// Whether replacing a value the file already has starts ticked (owner 10-08: declutter
    /// Find online, "fix both": a single-track match pre-ticked Track count 17 → 15 from the
    /// standard edition of a deluxe album, and Composer swapped full legal names for
    /// MusicBrainz credit names). Only facts about the recording itself — the same on every
    /// release it appears on — overwrite by default. Everything that depends on WHICH release
    /// matched (album, dates, numbering, counts, label, copyright, barcode), on how a source
    /// formats credits (composer) or on the user's own taxonomy (genre) waits for a tick.
    /// The album editor is the exception for album / album artist: it renames every track
    /// at once, so nothing is split off; one track renamed alone would leave its album.
    /// </summary>
    internal static bool IsSafeOverwrite(MetadataSearchField field, bool albumScope) => field switch
    {
        MetadataSearchField.Title or MetadataSearchField.Artist or MetadataSearchField.Isrc
            or MetadataSearchField.Explicit or MetadataSearchField.Bpm => true,
        MetadataSearchField.Album or MetadataSearchField.AlbumArtist => albumScope,
        _ => false,
    };

    private void BuildTrackRows(MetadataCandidate c)
    {
        var locals = _owner.SearchAlbumTracks
            .OrderBy(t => Math.Max(1, t.DiscNumber)).ThenBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var remote = c.Tracks.ToList();
        var matches = MatchTracks(locals, remote);
        foreach (var local in locals)
        {
            var row = new TrackMatchRow(local, matches.GetValueOrDefault(local));
            row.PropertyChanged += OnRowChanged;
            TrackRows.Add(row);
        }

        var matched = TrackRows.Count(r => r.IsMatched);
        var changing = TrackRows.Count(r => r.CanToggle);
        TrackSummary = Localization.Loc.T("MetadataSearch.TrackSummary", matched, TrackRows.Count, changing);
        var extra = remote.Count - matches.Count;
        TrackExtraNote = extra > 0 ? Localization.Loc.T("MetadataSearch.TracksNotInLibrary", extra) : string.Empty;
        OnPropertyChanged(nameof(TracksTabText));
    }

    /// <summary>
    /// Pairs local tracks with the release's. The service's own pairing (MatchedLocalTrackId)
    /// wins whenever it gave any; without one, disc + track number, then position when both
    /// sides have the same number of tracks.
    /// </summary>
    internal static Dictionary<Models.Track, CandidateTrack> MatchTracks(
        IReadOnlyList<Models.Track> locals, IReadOnlyList<CandidateTrack> remote)
    {
        var result = new Dictionary<Models.Track, CandidateTrack>();
        if (remote.Count == 0) return result;

        if (remote.Any(r => r.MatchedLocalTrackId != null))
        {
            foreach (var local in locals)
            {
                var hit = remote.FirstOrDefault(r => r.MatchedLocalTrackId == local.Id);
                if (hit != null) result[local] = hit;
            }
            return result;
        }

        var used = new HashSet<CandidateTrack>(ReferenceEqualityComparer.Instance);
        foreach (var local in locals)
        {
            if (local.TrackNumber <= 0) continue;
            var hit = remote.FirstOrDefault(r => !used.Contains(r) && r.TrackNumber == local.TrackNumber
                                                 && (r.DiscNumber ?? 1) == Math.Max(1, local.DiscNumber));
            if (hit == null) continue;
            result[local] = hit;
            used.Add(hit);
        }
        if (locals.Count == remote.Count)
        {
            for (var i = 0; i < locals.Count; i++)
            {
                if (result.ContainsKey(locals[i]) || used.Contains(remote[i])) continue;
                result[locals[i]] = remote[i];
                used.Add(remote[i]);
            }
        }
        return result;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CompareRow.IsChecked) or nameof(TrackMatchRow.IsIncluded))
            RaiseSelectionCounts();
    }

    private void RebuildVisibleRows()
    {
        VisibleRows.Clear();
        foreach (var r in Rows)
            if (r.IsChanged || ShowUnchanged) VisibleRows.Add(r);
    }

    partial void OnShowUnchangedChanged(bool value) => RebuildVisibleRows();

    partial void OnOnlyFillEmptyChanged(bool value)
    {
        foreach (var r in Rows) r.IsChecked = DefaultChecked(r);
        // The cover follows the same rule: filled only when there is none.
        if (value && HasCurrentArtwork) UseArtwork = false;
        RaiseSelectionCounts();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Rows) if (r.CanToggle) r.IsChecked = true;
        foreach (var t in TrackRows) if (t.CanToggle) t.IsIncluded = true;
        if (HasNewArtwork) UseArtwork = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Rows) r.IsChecked = false;
        foreach (var t in TrackRows) t.IsIncluded = false;
        UseArtwork = false;
    }

    [RelayCommand]
    private void ShowFieldsPane() => DetailPane = 0;

    [RelayCommand]
    private void ShowTracksPane() => DetailPane = 1;

    // ── Artwork ──

    partial void OnUseArtworkChanged(bool value)
    {
        if (value && !_suppressArtworkDownload) _ = EnsureArtworkAsync();
        RaiseSelectionCounts();
    }

    private void SetUseArtworkQuietly(bool value)
    {
        _suppressArtworkDownload = true;
        try { UseArtwork = value; }
        finally { _suppressArtworkDownload = false; }
    }

    /// <summary>Downloads the full cover (only once the user wants it, or the track has
    /// none), shows it with its real size, and keeps it for Apply.</summary>
    private async Task EnsureArtworkAsync()
    {
        var item = SelectedCandidate;
        if (item == null || _service == null || !HasNewArtwork) return;
        if (_artworkCache.TryGetValue(item.Candidate, out var cached)) { ShowFullArtwork(cached); return; }

        _artworkCts?.Cancel();
        var cts = new CancellationTokenSource();
        _artworkCts = cts;
        IsDownloadingArtwork = true;
        ArtworkNote = Localization.Loc.T("MetadataSearch.DownloadingCover");
        RaiseSelectionCounts();
        try
        {
            var data = await _service.DownloadArtworkAsync(item.Candidate, cts.Token);
            if (_artworkCts != cts || item != SelectedCandidate) return;
            if (data is not { Length: > 0 } || !HttpSafety.LooksLikeImage(data))
            {
                SetUseArtworkQuietly(false);
                ArtworkNote = Localization.Loc.T("MetadataSearch.CoverFailed");
                return;
            }
            _artworkCache[item.Candidate] = data;
            ShowFullArtwork(data);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_artworkCts == cts)
            {
                IsDownloadingArtwork = false;
                RaiseSelectionCounts();
            }
        }
    }

    private void ShowFullArtwork(byte[] data)
    {
        Bitmap? bmp = null;
        try { using var ms = new MemoryStream(data); bmp = Bitmap.DecodeToWidth(ms, 512); }
        catch { }
        var size = SkiaArtworkDecoder.ReadPixelSize(data);
        if (size is { } s) NewArtworkSize = $"{s.Width} × {s.Height}";
        if (bmp != null)
        {
            ReleaseNewArtwork();
            NewArtwork = bmp;
            _ownsNewArtwork = true;
        }
        UpdateArtworkNote(size?.Width);
    }

    private void UpdateArtworkNote(int? knownNewSide = null)
    {
        var item = SelectedCandidate;
        if (item == null || !HasNewArtwork) { ArtworkNote = string.Empty; return; }
        var newSide = knownNewSide ?? item.Candidate.ArtworkSize;
        var curSide = _owner.CurrentArtworkSide;
        if (!HasCurrentArtwork) ArtworkNote = Localization.Loc.T("MetadataSearch.CoverAdds");
        else if (newSide is not > 0 || curSide is not > 0) ArtworkNote = string.Empty;
        else if (newSide > curSide * 1.2) ArtworkNote = Localization.Loc.T("MetadataSearch.CoverSharper");
        else if (newSide * 1.2 < curSide) ArtworkNote = Localization.Loc.T("MetadataSearch.CoverSmaller");
        else ArtworkNote = Localization.Loc.T("MetadataSearch.CoverSameSize");
    }

    private void ReleaseNewArtwork()
    {
        var old = NewArtwork;
        var owned = _ownsNewArtwork;
        _ownsNewArtwork = false;
        NewArtwork = null;
        if (owned) old?.Dispose();
    }

    private void RaiseArtworkCurrent()
    {
        OnPropertyChanged(nameof(CurrentArtwork));
        OnPropertyChanged(nameof(HasCurrentArtwork));
        OnPropertyChanged(nameof(CurrentArtworkSize));
    }

    // ── Apply ──

    public bool CanApply => SelectedChangeCount > 0 && !(UseArtwork && IsDownloadingArtwork);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        var plan = BuildPlan();
        if (plan.Count == 0) return;
        _owner.ApplySearchPlan(plan);
        Close();
    }

    internal MetadataSearchPlan BuildPlan()
    {
        var item = SelectedCandidate;
        if (item == null) return new MetadataSearchPlan();
        byte[]? art = null;
        if (UseArtwork && HasNewArtwork) _artworkCache.TryGetValue(item.Candidate, out art);
        return new MetadataSearchPlan
        {
            Provider = item.Provider,
            Fields = Rows.Where(r => r.IsChecked && r.CanToggle).Select(r => (r.Field, r.NewValue)).ToList(),
            Artwork = art,
            Tracks = TrackRows.Where(r => r.IsIncluded && r.CanToggle)
                .Select(r => (r.Local, new StagedTrackChange(
                    r.TitleChanges ? r.NewTitle : null,
                    r.NewTrackNumber is { } tn && tn != r.Local.TrackNumber ? tn : null,
                    r.NewDiscNumber is { } dn && dn != Math.Max(1, r.Local.DiscNumber) ? dn : null)))
                .ToList(),
        };
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ChangedFieldCount));
        OnPropertyChanged(nameof(UnchangedFieldCount));
        OnPropertyChanged(nameof(HasUnchangedFields));
        OnPropertyChanged(nameof(HasNoFieldChanges));
        OnPropertyChanged(nameof(ShowUnchangedText));
        OnPropertyChanged(nameof(FieldSummary));
        OnPropertyChanged(nameof(TracksTabText));
        RaiseSelectionCounts();
    }

    private void RaiseSelectionCounts()
    {
        OnPropertyChanged(nameof(SelectedChangeCount));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MetadataViewModel.IsLoading) when !_owner.IsLoading && _searchWhenLoaded && IsOpen:
                _ = SearchAsync();
                break;
            case nameof(MetadataViewModel.ArtworkPreview) or nameof(MetadataViewModel.HasArtwork):
                RaiseArtworkCurrent();
                break;
        }
    }

    // ── Value helpers ──

    internal static int? YearOf(string? date)
        => !string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y) && y > 0 ? y : null;

    internal static string? CandidateValue(MetadataCandidate c, MetadataSearchField field, bool albumScope) => field switch
    {
        MetadataSearchField.Title => c.Title,
        MetadataSearchField.Artist => c.Artist,
        MetadataSearchField.Album => c.Album,
        MetadataSearchField.AlbumArtist => c.AlbumArtist,
        MetadataSearchField.Year => (c.Year ?? YearOf(c.ReleaseDate))?.ToString(),
        MetadataSearchField.ReleaseDate => c.ReleaseDate,
        MetadataSearchField.Genre => c.Genre,
        MetadataSearchField.TrackNumber => Positive(c.TrackNumber),
        MetadataSearchField.TrackCount => Positive(c.TrackCount ?? (albumScope && c.Tracks.Count > 0 ? c.Tracks.Count : null)),
        MetadataSearchField.DiscNumber => Positive(c.DiscNumber),
        MetadataSearchField.DiscCount => Positive(c.DiscCount ?? (albumScope && c.Tracks.Any(t => t.DiscNumber is > 0)
            ? c.Tracks.Max(t => t.DiscNumber ?? 1) : null)),
        MetadataSearchField.Composer => c.Composer,
        MetadataSearchField.Label => c.Label,
        MetadataSearchField.Copyright => c.Copyright,
        MetadataSearchField.Isrc => c.Isrc,
        MetadataSearchField.Barcode => c.Barcode,
        MetadataSearchField.Explicit => c.Explicit is { } e ? (e ? "1" : "0") : null,
        MetadataSearchField.Bpm => Positive(c.Bpm),
        _ => null,
    };

    private static string? Positive(int? v) => v is > 0 ? v.Value.ToString() : null;

    /// <summary>Numbers compare as numbers ("03" is 3), text exactly (a capitalisation fix is a change).</summary>
    internal static bool SameValue(MetadataSearchField field, string current, string proposed)
    {
        current = (current ?? string.Empty).Trim();
        if (int.TryParse(current, out var a) && int.TryParse(proposed, out var b)) return a == b;
        return string.Equals(current, proposed, StringComparison.Ordinal);
    }

    internal static string DisplayValue(MetadataSearchField field, string raw) => field switch
    {
        MetadataSearchField.Explicit => Localization.Loc.T(raw == "1" ? "MetadataSearch.Explicit" : "MetadataSearch.NotExplicit"),
        _ => raw,
    };

    internal static string FieldLabel(MetadataSearchField field) => Localization.Loc.T("MetadataSearch.F." + field);

    public void Dispose()
    {
        _searchCts?.Cancel();
        _artworkCts?.Cancel();
        _owner.PropertyChanged -= OnOwnerPropertyChanged;
        ClearCandidates();
    }
}

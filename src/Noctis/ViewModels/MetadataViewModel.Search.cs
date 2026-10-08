using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.Models;
using Noctis.Services.MetadataSearch;
using Noctis.ViewModels.MetadataSearch;

namespace Noctis.ViewModels;

// ── Find online (owner 10-08: Search metadata revamp) ─────────────────────────
// The editor side of MetadataSearchPanelViewModel: which edit property each search field
// lands in (or why this editor can't store it), the query seed, and Apply / Undo. Apply
// only writes the edit fields and stages the cover and per-track values; Save persists
// them exactly like a hand edit, so the footer's change count and Cancel keep working.
public partial class MetadataViewModel
{
    private readonly IMetadataSearchService? _metadataSearch;
    private MetadataSearchPanelViewModel? _searchPanel;

    /// <summary>Album editor: per-track values (title, track #, disc #) a search apply staged,
    /// written onto each Track on Save. Used to be applied silently on Save with no review.</summary>
    private Dictionary<Track, StagedTrackChange> _stagedTrackChanges = new();

    /// <summary>The Find online panel (created on first use).</summary>
    public MetadataSearchPanelViewModel SearchPanel => _searchPanel ??= new MetadataSearchPanelViewModel(
        this, _metadataSearch, ThumbnailLoader());

    /// <summary>Find online shows for single tracks and whole albums, but not arbitrary multi-select.</summary>
    public bool ShowSearchMetadata => !_multiSelect;

    internal bool IsAlbumScopeForSearch => _albumScoped && _albumTracks is { Count: > 0 };

    internal IReadOnlyList<Track> SearchAlbumTracks => IsAlbumScopeForSearch ? _albumTracks! : Array.Empty<Track>();

    /// <summary>Real pixel side of the cover the editor shows, when known.</summary>
    internal int? CurrentArtworkSide => HasArtwork && ArtworkPreview is { } bmp ? (_artworkSourceSize ?? bmp.PixelSize).Width : null;

    [RelayCommand]
    private void OpenSearchPanel()
    {
        if (!ShowSearchMetadata) return;
        SearchPanel.Open();
    }

    /// <summary>The header pill: opens the panel, or backs out of it when it is open.</summary>
    [RelayCommand]
    private void ToggleSearchPanel()
    {
        if (_searchPanel is { IsOpen: true } open) open.Close();
        else OpenSearchPanel();
    }

    // Result-list thumbnails: the contract only downloads full covers, so small ones go
    // through the plain bounded image download the Artwork tab already uses.
    private Func<Uri, CancellationToken, Task<byte[]?>>? ThumbnailLoader()
    {
        if (_itunes is not { } itunes) return null;
        return (uri, ct) => uri.Scheme is "http" or "https"
            ? itunes.DownloadAsync(uri.AbsoluteUri, ct)
            : Task.FromResult<byte[]?>(null);
    }

    /// <summary>The query the panel starts from: the edit fields as they are now, plus the
    /// track facts that help rank (length, numbers, ISRC, the album's tracks).</summary>
    internal MetadataQuery SearchQuerySeed()
    {
        var album = IsAlbumScopeForSearch;
        var artist = !string.IsNullOrWhiteSpace(Artist) ? Artist : AlbumArtist;
        var albumArtist = !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist
            : !string.IsNullOrWhiteSpace(Artist) ? Artist : _track.AlbumArtist ?? string.Empty;
        return new MetadataQuery
        {
            Title = album ? string.Empty : Title.Trim(),
            Artist = (album ? albumArtist : artist).Trim(),
            Album = Album.Trim(),
            AlbumArtist = albumArtist.Trim(),
            Duration = !album && _track.Duration > TimeSpan.Zero ? _track.Duration : null,
            TrackNumber = !album && int.TryParse(TrackNumber, out var tn) && tn > 0 ? tn : null,
            DiscNumber = !album && int.TryParse(DiscNumber, out var dn) && dn > 0 ? dn : null,
            Isrc = album ? string.Empty : Isrc.Trim(),
            Year = int.TryParse(Year, out var y) && y > 0 ? y : null,
            AlbumScope = album,
            AlbumTracks = SearchAlbumTracks,
        };
    }

    internal bool IsSearchFieldMixed(MetadataSearchField field)
        => SearchFieldProperty(field) is { } prop && _mixedFields.Contains(prop);

    /// <summary>
    /// Where <paramref name="field"/> lives in this editor and what it holds now. Fields the
    /// album editor can't keep (it writes only shared Details fields) come back blocked with
    /// the reason, so the panel shows them greyed instead of dropping them silently.
    /// </summary>
    internal SearchFieldState GetSearchFieldState(MetadataSearchField field)
    {
        var album = IsAlbumScopeForSearch;
        switch (field)
        {
            // Per-track values: the album editor maps them in the track table instead.
            case MetadataSearchField.Title or MetadataSearchField.TrackNumber or MetadataSearchField.DiscNumber when album:
                return SearchFieldState.NotApplicable;
            case MetadataSearchField.ReleaseDate or MetadataSearchField.Label or MetadataSearchField.Copyright
                or MetadataSearchField.Isrc or MetadataSearchField.Barcode or MetadataSearchField.Bpm when album:
                return SearchFieldState.Blocked(Localization.Loc.T("MetadataSearch.BlockedAlbum"));
            // The extended tags (label, ISRC, barcode, release date) are written back only
            // when they were read: an unreadable file keeps them out of Save entirely.
            case MetadataSearchField.ReleaseDate or MetadataSearchField.Label or MetadataSearchField.Isrc
                or MetadataSearchField.Barcode when _originalAdvancedFields == null:
                return SearchFieldState.Blocked(Localization.Loc.T("MetadataSearch.BlockedUnreadable"));
            case MetadataSearchField.Explicit:
            {
                // Album: the "Album is explicit" box. Track: the advisory picker, where Clean
                // also means "not explicit".
                var isExplicit = album ? IsExplicit : SelectedAdvisory == "Explicit";
                var display = album || SelectedAdvisory != "Clean"
                    ? Localization.Loc.T(isExplicit ? "MetadataSearch.Explicit" : "MetadataSearch.NotExplicit")
                    : Localization.Loc.T("MetadataSearch.Clean");
                if (!album && _originalAdvancedFields == null)
                    return SearchFieldState.Blocked(Localization.Loc.T("MetadataSearch.BlockedUnreadable")) with { Raw = isExplicit ? "1" : "0", Display = display };
                return new SearchFieldState(isExplicit ? "1" : "0", display, IsEmpty: !album && SelectedAdvisory == "None", null, true);
            }
        }

        var prop = SearchFieldProperty(field)!;
        var raw = (ReadTracked(prop) as string ?? string.Empty).Trim();
        var mixed = _mixedFields.Contains(prop);
        var shown = raw.Length > 0 ? raw : mixed ? Localization.Loc.T("MetadataSearch.Mixed") : "—";
        return new SearchFieldState(raw, shown, IsEmpty: raw.Length == 0 && !mixed, null, true);
    }

    /// <summary>The edit property a search field writes into (null where none exists).</summary>
    private string? SearchFieldProperty(MetadataSearchField field) => field switch
    {
        MetadataSearchField.Title => nameof(Title),
        MetadataSearchField.Artist => nameof(Artist),
        MetadataSearchField.Album => nameof(Album),
        MetadataSearchField.AlbumArtist => nameof(AlbumArtist),
        MetadataSearchField.Year => nameof(Year),
        MetadataSearchField.ReleaseDate => nameof(AdvReleaseDate),
        MetadataSearchField.Genre => nameof(Genre),
        MetadataSearchField.TrackNumber => nameof(TrackNumber),
        MetadataSearchField.TrackCount => nameof(TrackCount),
        MetadataSearchField.DiscNumber => nameof(DiscNumber),
        MetadataSearchField.DiscCount => nameof(DiscCount),
        MetadataSearchField.Composer => nameof(Composer),
        MetadataSearchField.Label => nameof(Publisher),
        MetadataSearchField.Copyright => nameof(Copyright),
        MetadataSearchField.Isrc => nameof(Isrc),
        MetadataSearchField.Barcode => nameof(Barcode),
        MetadataSearchField.Explicit => IsAlbumScopeForSearch ? nameof(IsExplicit) : nameof(SelectedAdvisory),
        MetadataSearchField.Bpm => nameof(Bpm),
        _ => null,
    };

    // ── Apply / undo ──

    /// <summary>What the last apply changed, so Undo can put it back (until Save).</summary>
    private sealed class SearchUndo
    {
        public readonly List<(string Prop, object? Before, object? After)> Fields = new();
        public Dictionary<Track, StagedTrackChange> StagedBefore = new();
        public bool ArtworkApplied;
        public byte[]? ArtworkData;
        public bool ArtworkRemoved;
        public Bitmap? Preview;
        public Avalonia.PixelSize? SourceSize;
        public bool HadArtwork;
        public bool ShowedOwnArtwork;
        public byte[]? AppliedArtwork;
    }

    private SearchUndo? _searchUndo;

    /// <summary>"Applied 6 changes from Deezer" — the Details banner after an apply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchApplied))]
    private string _searchAppliedText = string.Empty;

    public bool HasSearchApplied => SearchAppliedText.Length > 0;
    public bool CanUndoSearchApply => _searchUndo != null;

    /// <summary>Writes the panel's choices into the edit fields and stages the cover and the
    /// per-track values. Nothing touches a file until Save.</summary>
    public void ApplySearchPlan(MetadataSearchPlan plan)
    {
        if (plan.Count == 0) return;
        var undo = new SearchUndo { StagedBefore = new Dictionary<Track, StagedTrackChange>(_stagedTrackChanges) };

        foreach (var (field, value) in plan.Fields)
        {
            if (GetSearchFieldState(field) is not { Applicable: true, BlockedReason: null }) continue;
            var prop = SearchFieldProperty(field)!;
            var before = ReadTracked(prop);
            object after = field == MetadataSearchField.Explicit
                ? (IsAlbumScopeForSearch ? value == "1" : value == "1" ? "Explicit" : "None")
                : value;
            GetType().GetProperty(prop)!.SetValue(this, after);
            undo.Fields.Add((prop, before, ReadTracked(prop)));
        }

        if (plan.Artwork is { Length: > 0 } art && TryDecodeArtwork(art, out var bmp, out var size))
        {
            undo.ArtworkApplied = true;
            undo.ArtworkData = _newArtworkData;
            undo.ArtworkRemoved = _artworkRemoved;
            undo.Preview = ArtworkPreview; // kept alive (not disposed) for Undo
            undo.SourceSize = _artworkSourceSize;
            undo.HadArtwork = HasArtwork;
            undo.ShowedOwnArtwork = ShowsOwnTrackArtwork;
            undo.AppliedArtwork = art;

            _newArtworkData = art;
            _artworkRemoved = false;
            _artworkSourceSize = size;
            ShowsOwnTrackArtwork = false; // a picked cover applies album-wide
            ArtworkPreview = bmp;
            HasArtwork = true;
        }

        foreach (var (track, change) in plan.Tracks)
            _stagedTrackChanges[track] = change;

        DiscardSearchUndo();
        _searchUndo = undo;
        OnPropertyChanged(nameof(CanUndoSearchApply));
        SearchAppliedText = plan.Count == 1
            ? Localization.Loc.T("MetadataSearch.AppliedOne", plan.Provider)
            : Localization.Loc.T("MetadataSearch.AppliedMany", plan.Count, plan.Provider);
        RecomputeChanges();
        DebugLogger.Info(DebugLogger.Category.UI, "MetadataSearch.Apply",
            $"fields={plan.Fields.Count} art={plan.Artwork != null} tracks={plan.Tracks.Count} from={plan.Provider}");
    }

    /// <summary>Puts back what the last apply changed. A field the user edited again since
    /// keeps the newer edit; so does a cover picked after the apply.</summary>
    [RelayCommand]
    private void UndoSearchApply()
    {
        if (_searchUndo is not { } undo) return;
        _searchUndo = null;

        foreach (var (prop, before, after) in undo.Fields)
            if (Equals(ReadTracked(prop), after))
                GetType().GetProperty(prop)!.SetValue(this, before);

        if (undo.ArtworkApplied && ReferenceEquals(_newArtworkData, undo.AppliedArtwork))
        {
            var applied = ArtworkPreview;
            _newArtworkData = undo.ArtworkData;
            _artworkRemoved = undo.ArtworkRemoved;
            _artworkSourceSize = undo.SourceSize;
            ShowsOwnTrackArtwork = undo.ShowedOwnArtwork;
            ArtworkPreview = undo.Preview;
            HasArtwork = undo.HadArtwork;
            if (!ReferenceEquals(applied, undo.Preview)) applied?.Dispose();
        }
        else if (undo.Preview != null && !ReferenceEquals(undo.Preview, ArtworkPreview))
        {
            undo.Preview.Dispose();
        }

        _stagedTrackChanges = undo.StagedBefore;
        OnPropertyChanged(nameof(CanUndoSearchApply));
        SearchAppliedText = string.Empty;
        RecomputeChanges();
    }

    [RelayCommand]
    private void DismissSearchApplied() => SearchAppliedText = string.Empty;

    /// <summary>Drops the undo snapshot, freeing the cover it was holding for Undo.</summary>
    private void DiscardSearchUndo()
    {
        if (_searchUndo is { Preview: { } old } && !ReferenceEquals(old, ArtworkPreview))
            old.Dispose();
        _searchUndo = null;
    }

    private static bool TryDecodeArtwork(byte[] data, out Bitmap? bitmap, out Avalonia.PixelSize? sourceSize)
    {
        bitmap = null;
        sourceSize = null;
        try
        {
            // Decoded at display size like the loaded cover; the chip reports the real size.
            using var ms = new MemoryStream(data);
            bitmap = Bitmap.DecodeToWidth(ms, 512);
            sourceSize = SkiaArtworkDecoder.ReadPixelSize(data) ?? bitmap.PixelSize;
            return true;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
    }

    /// <summary>Writes the staged per-track values onto each Track so the album Save loop
    /// persists them (after the shared fields, so a reviewed per-track number wins).</summary>
    private void ApplyStagedTrackChanges()
    {
        foreach (var (t, change) in _stagedTrackChanges)
        {
            if (!string.IsNullOrWhiteSpace(change.Title)) t.Title = change.Title;
            if (change.TrackNumber is > 0) t.TrackNumber = change.TrackNumber.Value;
            if (change.DiscNumber is > 0) t.DiscNumber = change.DiscNumber.Value;
        }
    }

    /// <summary>Window closed: stop any search or download and free the panel's bitmaps.</summary>
    public void DisposeSearch()
    {
        _searchPanel?.Dispose();
        DiscardSearchUndo();
    }
}

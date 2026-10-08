using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Identifies poorly-tagged tracks and shows the proposed tags side-by-side with the current
/// ones. Identification runs in batch off the UI thread; tags are written only for the rows
/// the user confirms.
/// </summary>
public partial class MetadataFinderViewModel : ViewModelBase
{
    private readonly IMetadataFinderService _finder;
    private readonly IMetadataService _metadata;
    private readonly ILibraryService _library;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _sourceHint = string.Empty;
    [ObservableProperty] private bool _hasSelection;

    public ObservableCollection<MetaRow> Rows { get; } = new();

    /// <summary>False when nothing is poorly tagged: the dialog shows the status in place of
    /// the list. The rows are fixed at construction.</summary>
    public bool HasRows => Rows.Count > 0;

    public event EventHandler? Closed;

    public MetadataFinderViewModel(IReadOnlyList<Track> candidates, IMetadataFinderService finder,
        IMetadataService metadata, ILibraryService library)
    {
        _finder = finder;
        _metadata = metadata;
        _library = library;

        foreach (var t in candidates)
            Rows.Add(new MetaRow(t, RecomputeSelection));

        // Owner 10-08: same UI + animation for search metadata — the editor's short, localized
        // wording; the sources line is the header's ⓘ tooltip now.
        SourceHint = L("MetadataFinder.SourcesTip");

        StatusMessage = Rows.Count == 0
            ? L("MetadataFinder.NothingToFix")
            : Rows.Count == 1 ? L("MetadataFinder.ToIdentifyOne") : L("MetadataFinder.ToIdentifyMany", Rows.Count);
    }

    [RelayCommand]
    private async Task IdentifyAll()
    {
        if (IsBusy || Rows.Count == 0) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var identified = 0;

        try
        {
            // Rows are updated in place: the command runs on the UI thread and every await
            // resumes there. Posting each result instead queued it behind the summary below,
            // so "Identified N of M" missed the last match — and every match when the finder
            // answered without yielding (a cached lookup): "Identified 0 of 4".
            foreach (var row in Rows.ToList())
            {
                ct.ThrowIfCancellationRequested();
                row.Status = L("MetadataFinder.Identifying");

                var hits = await _finder.IdentifyAsync(row.Track, ct);
                var best = hits.FirstOrDefault();

                if (best is null)
                {
                    row.Status = L("MetadataFinder.NoMatch");
                    continue;
                }
                row.ApplyProposal(best);
                identified++;
            }
            StatusMessage = L("MetadataFinder.Identified", identified, Rows.Count);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L("MetadataFinder.Cancelled");
        }
        finally
        {
            IsBusy = false;
            RecomputeSelection();
        }
    }

    private void RecomputeSelection()
        => HasSelection = Rows.Any(r => r.Apply && r.HasProposal);

    [RelayCommand]
    private async Task ApplySelected()
    {
        if (IsBusy) return;
        var toApply = Rows.Where(r => r.Apply && r.HasProposal).ToList();
        if (toApply.Count == 0) return;

        IsBusy = true;
        StatusMessage = L("MetadataFinder.Writing", toApply.Count);

        // Rows whose tag write failed. The in-memory Track fields were mutated *before*
        // the write and every row was marked "Applied" unconditionally, so a locked or
        // read-only file left the library showing tags the file never received — invisible
        // until the next rescan silently reverted them.
        var failed = new HashSet<MetaRow>();

        var written = await Task.Run(() =>
        {
            var n = 0;
            foreach (var row in toApply)
            {
                // Snapshot so a failed write can be rolled back.
                var prevTitle = row.Track.Title;
                var prevArtist = row.Track.Artist;
                var prevAlbumArtist = row.Track.AlbumArtist;
                var prevAlbum = row.Track.Album;
                var prevYear = row.Track.Year;

                row.Track.Title = row.ProposedTitle;
                row.Track.Artist = row.ProposedArtist;
                row.Track.AlbumArtist = string.IsNullOrWhiteSpace(row.Track.AlbumArtist) || row.Track.AlbumArtist == "Unknown Artist"
                    ? row.ProposedArtist : row.Track.AlbumArtist;
                row.Track.Album = row.ProposedAlbum;
                if (row.ProposedYear is { } y && y > 0) row.Track.Year = y;

                if (_metadata.WriteTrackMetadata(row.Track))
                {
                    n++;
                }
                else
                {
                    row.Track.Title = prevTitle;
                    row.Track.Artist = prevArtist;
                    row.Track.AlbumArtist = prevAlbumArtist;
                    row.Track.Album = prevAlbum;
                    row.Track.Year = prevYear;
                    lock (failed) failed.Add(row);
                }
            }
            return n;
        });

        _library.NotifyMetadataChanged();

        foreach (var row in toApply)
            row.Status = failed.Contains(row) ? L("MetadataFinder.WriteFailed") : L("MetadataFinder.Applied");

        IsBusy = false;
        RecomputeSelection();
        StatusMessage = failed.Count > 0
            ? L("MetadataFinder.AppliedFailed", written, failed.Count)
            : written == 1 ? L("MetadataFinder.AppliedOne") : L("MetadataFinder.AppliedMany", written);
    }

    /// <summary>Stops a running identify. The dialog calls it when it closes: Alt+F4 or the
    /// owner closing never go through Cancel, and the loop kept querying the sources for
    /// every remaining row after the window was gone.</summary>
    public void StopIdentify() => _cts?.Cancel();

    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) { _cts?.Cancel(); return; }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public partial class MetaRow : ObservableObject
    {
        private readonly Action _onChanged;

        public MetaRow(Track track, Action onChanged)
        {
            Track = track;
            _onChanged = onChanged;
            CurrentTitle = track.Title;
            CurrentArtist = track.Artist;
            CurrentAlbum = track.Album;
        }

        public Track Track { get; }

        public string CurrentTitle { get; }
        public string CurrentArtist { get; }
        public string CurrentAlbum { get; }

        [ObservableProperty] private string _proposedTitle = string.Empty;
        [ObservableProperty] private string _proposedArtist = string.Empty;
        [ObservableProperty] private string _proposedAlbum = string.Empty;
        [ObservableProperty] private string _status = L("MetadataFinder.Pending");
        [ObservableProperty] private bool _hasProposal;
        [ObservableProperty] private string _confidenceText = string.Empty;
        [ObservableProperty] private bool _apply;

        public int? ProposedYear { get; private set; }

        // Below this confidence a proposal is shown but NOT pre-checked — the
        // user must opt in, so a wrong-track hit can't overwrite tags in bulk.
        private const double AutoApplyConfidence = 0.70;

        public void ApplyProposal(TagSuggestion s)
        {
            ProposedTitle = s.Title;
            ProposedArtist = s.Artist;
            ProposedAlbum = s.Album;
            ProposedYear = s.Year;
            HasProposal = !string.IsNullOrWhiteSpace(s.Title) || !string.IsNullOrWhiteSpace(s.Artist);
            ConfidenceText = $"{s.Source} · {s.Confidence * 100:0}%";
            Apply = HasProposal && s.Confidence >= AutoApplyConfidence;
            Status = L(!HasProposal ? "MetadataFinder.NoMatch" : Apply ? "MetadataFinder.Matched" : "MetadataFinder.Review");
        }

        partial void OnApplyChanged(bool value) => _onChanged();
    }
}

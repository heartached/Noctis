using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
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

    public event EventHandler? Closed;

    public MetadataFinderViewModel(IReadOnlyList<Track> candidates, IMetadataFinderService finder,
        IMetadataService metadata, ILibraryService library)
    {
        _finder = finder;
        _metadata = metadata;
        _library = library;

        foreach (var t in candidates)
            Rows.Add(new MetaRow(t, RecomputeSelection));

        SourceHint = "Identifying by current tags via Deezer, then MusicBrainz. Toggle sources in Settings.";

        StatusMessage = Rows.Count == 0
            ? "No poorly-tagged tracks found."
            : $"{Rows.Count} track{(Rows.Count == 1 ? string.Empty : "s")} to identify";
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
            foreach (var row in Rows.ToList())
            {
                ct.ThrowIfCancellationRequested();
                Dispatcher.UIThread.Post(() => row.Status = "Identifying…");

                var hits = await _finder.IdentifyAsync(row.Track, ct);
                var best = hits.FirstOrDefault();

                Dispatcher.UIThread.Post(() =>
                {
                    if (best is null)
                    {
                        row.Status = "No match";
                        return;
                    }
                    row.ApplyProposal(best);
                    identified++;
                });
            }
            StatusMessage = $"Identified {identified} of {Rows.Count}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
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
        StatusMessage = $"Writing tags to {toApply.Count}…";

        var written = await Task.Run(() =>
        {
            var n = 0;
            foreach (var row in toApply)
            {
                row.Track.Title = row.ProposedTitle;
                row.Track.Artist = row.ProposedArtist;
                row.Track.AlbumArtist = string.IsNullOrWhiteSpace(row.Track.AlbumArtist) || row.Track.AlbumArtist == "Unknown Artist"
                    ? row.ProposedArtist : row.Track.AlbumArtist;
                row.Track.Album = row.ProposedAlbum;
                if (row.ProposedYear is { } y && y > 0) row.Track.Year = y;
                if (_metadata.WriteTrackMetadata(row.Track)) n++;
            }
            return n;
        });

        _library.NotifyMetadataChanged();

        foreach (var row in toApply)
            row.Status = "Applied";

        IsBusy = false;
        RecomputeSelection();
        StatusMessage = $"Applied {written} track{(written == 1 ? string.Empty : "s")}";
    }

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
        [ObservableProperty] private string _status = "Pending";
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
            Status = !HasProposal ? "No match" : Apply ? "Matched" : "Review";
        }

        partial void OnApplyChanged(bool value) => _onChanged();
    }
}

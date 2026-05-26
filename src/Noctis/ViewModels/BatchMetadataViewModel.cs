using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Batch metadata editor — applies tag changes and an optional rename-by-pattern
/// to N selected tracks. Each field is only written when its Apply flag is set,
/// so untouched fields stay per-track.
/// </summary>
public partial class BatchMetadataViewModel : ViewModelBase
{
    private readonly IMetadataService _metadata;
    private readonly ILibraryService _library;
    private readonly IReadOnlyList<Track> _tracks;

    public string TitleText { get; }
    public int TrackCount => _tracks.Count;

    // ── Apply flags (checked = write this field) ──
    [ObservableProperty] private bool _applyAlbumArtist;
    [ObservableProperty] private bool _applyArtist;
    [ObservableProperty] private bool _applyAlbum;
    [ObservableProperty] private bool _applyGenre;
    [ObservableProperty] private bool _applyYear;
    [ObservableProperty] private bool _applyComposer;
    [ObservableProperty] private bool _applyGrouping;
    [ObservableProperty] private bool _applyTrackCount;
    [ObservableProperty] private bool _applyDiscCount;
    [ObservableProperty] private bool _applyDiscNumber;
    [ObservableProperty] private bool _applyCompilation;

    // ── Values ──
    [ObservableProperty] private string _albumArtist = string.Empty;
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private string _album = string.Empty;
    [ObservableProperty] private string _genre = string.Empty;
    [ObservableProperty] private string _year = string.Empty;
    [ObservableProperty] private string _composer = string.Empty;
    [ObservableProperty] private string _grouping = string.Empty;
    [ObservableProperty] private string _trackCountValue = string.Empty;
    [ObservableProperty] private string _discCountValue = string.Empty;
    [ObservableProperty] private string _discNumberValue = string.Empty;
    [ObservableProperty] private bool _isCompilation;

    // ── Rename ──
    [ObservableProperty] private bool _applyRename;
    [ObservableProperty] private string _renamePattern = "%tracknumber2% - %title%";
    public ObservableCollection<RenamePreview> RenamePreviews { get; } = new();

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isSaving;

    public event EventHandler? Closed;
    public event EventHandler? Saved;

    public BatchMetadataViewModel(IReadOnlyList<Track> tracks, IMetadataService metadata, ILibraryService library)
    {
        _tracks = tracks;
        _metadata = metadata;
        _library = library;

        TitleText = $"Edit {tracks.Count} tracks";

        // Prefill any field where all selected tracks share the same value, so
        // ticking the Apply box doesn't accidentally blank a shared field.
        AlbumArtist = CommonString(t => t.AlbumArtist);
        Artist = CommonString(t => t.Artist);
        Album = CommonString(t => t.Album);
        Genre = CommonString(t => t.Genre);
        Composer = CommonString(t => t.Composer);
        Grouping = CommonString(t => t.Grouping);
        var year = CommonInt(t => t.Year); Year = year > 0 ? year.ToString() : string.Empty;
        var tc = CommonInt(t => t.TrackCount); TrackCountValue = tc > 0 ? tc.ToString() : string.Empty;
        var dc = CommonInt(t => t.DiscCount); DiscCountValue = dc > 0 ? dc.ToString() : string.Empty;
        var dn = CommonInt(t => t.DiscNumber); DiscNumberValue = dn > 0 ? dn.ToString() : string.Empty;
        IsCompilation = _tracks.Count > 0 && _tracks.All(t => t.IsCompilation);

        RebuildRenamePreview();
    }

    partial void OnRenamePatternChanged(string value) => RebuildRenamePreview();

    private string CommonString(Func<Track, string> sel)
    {
        if (_tracks.Count == 0) return string.Empty;
        var first = sel(_tracks[0]) ?? string.Empty;
        return _tracks.All(t => (sel(t) ?? string.Empty) == first) ? first : string.Empty;
    }

    private int CommonInt(Func<Track, int> sel)
    {
        if (_tracks.Count == 0) return 0;
        var first = sel(_tracks[0]);
        return _tracks.All(t => sel(t) == first) ? first : 0;
    }

    private void RebuildRenamePreview()
    {
        RenamePreviews.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _tracks.Take(8))
        {
            var newPath = ComputeRenamedPath(t, out var conflict, seen);
            RenamePreviews.Add(new RenamePreview
            {
                OriginalName = Path.GetFileName(t.FilePath),
                NewName = newPath != null ? Path.GetFileName(newPath) : "(empty pattern)",
                Conflict = conflict,
            });
        }
    }

    private string? ComputeRenamedPath(Track t, out bool conflict, HashSet<string>? seenInBatch = null)
    {
        conflict = false;
        var expanded = TitleFormatter.Expand(RenamePattern, t, sanitizeForFilename: true);
        if (string.IsNullOrWhiteSpace(expanded)) return null;

        var dir = Path.GetDirectoryName(t.FilePath) ?? string.Empty;
        var ext = Path.GetExtension(t.FilePath);
        var newPath = Path.Combine(dir, expanded + ext);

        if (string.Equals(newPath, t.FilePath, StringComparison.OrdinalIgnoreCase)) return newPath;

        if (File.Exists(newPath)) conflict = true;
        if (seenInBatch != null && !seenInBatch.Add(newPath.ToLowerInvariant())) conflict = true;

        return newPath;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (IsSaving) return;
        IsSaving = true;
        try
        {
            int yearVal = int.TryParse(Year, out var y) ? y : 0;
            int trackCountVal = int.TryParse(TrackCountValue, out var tc) ? tc : 0;
            int discCountVal = int.TryParse(DiscCountValue, out var dc) ? dc : 0;
            int discNumberVal = int.TryParse(DiscNumberValue, out var dn) ? dn : 0;

            int tagsWritten = 0, renamed = 0, failed = 0;
            var renameSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in _tracks)
            {
                // Apply tag changes first; the file may move below.
                bool tagDirty = false;
                if (ApplyAlbumArtist) { t.AlbumArtist = AlbumArtist; tagDirty = true; }
                if (ApplyArtist) { t.Artist = Artist; tagDirty = true; }
                if (ApplyAlbum) { t.Album = Album; tagDirty = true; }
                if (ApplyGenre) { t.Genre = Genre; tagDirty = true; }
                if (ApplyYear) { t.Year = yearVal; tagDirty = true; }
                if (ApplyComposer) { t.Composer = Composer; tagDirty = true; }
                if (ApplyGrouping) { t.Grouping = Grouping; tagDirty = true; }
                if (ApplyTrackCount) { t.TrackCount = trackCountVal; tagDirty = true; }
                if (ApplyDiscCount) { t.DiscCount = discCountVal; tagDirty = true; }
                if (ApplyDiscNumber) { t.DiscNumber = discNumberVal; tagDirty = true; }
                if (ApplyCompilation) { t.IsCompilation = IsCompilation; tagDirty = true; }

                if (tagDirty)
                {
                    if (_metadata.WriteTrackMetadata(t)) tagsWritten++;
                    else failed++;
                }

                // Rename file if requested. Done after tag write so the new name
                // can reflect just-applied tags.
                if (ApplyRename)
                {
                    var newPath = ComputeRenamedPath(t, out var conflict, renameSeen);
                    if (newPath != null && !conflict && !string.Equals(newPath, t.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.Move(t.FilePath, newPath);
                            t.FilePath = newPath;
                            renamed++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                }
            }

            await _library.SaveAsync();
            StatusMessage = $"Saved · Tags: {tagsWritten} · Renamed: {renamed}" + (failed > 0 ? $" · Failed: {failed}" : string.Empty);
            Saved?.Invoke(this, EventArgs.Empty);
            // Close the dialog *before* notifying the library so the user
            // doesn't see the underlying songs view re-render through the
            // semi-transparent overlay (the "flash").
            Closed?.Invoke(this, EventArgs.Empty);
            _library.NotifyMetadataChanged();
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    public class RenamePreview
    {
        public string OriginalName { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
        public bool Conflict { get; set; }
    }
}

using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the duplicate-finder dialog: scan into groups, let the user choose which copies
/// to delete (best copy pre-kept), and delete the selected files after explicit confirmation
/// via the Delete button. Nothing is deleted until the user clicks Delete.
/// </summary>
public partial class DuplicateFinderViewModel : ViewModelBase
{
    private readonly IDuplicateFinderService _service;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _hasSelection;
    /// <summary>False while scanning and when nothing was found: the list area then shows <see cref="ListMessage"/>.</summary>
    [ObservableProperty] private bool _hasGroups;
    /// <summary>What the empty list area says: scanning, or no duplicates.</summary>
    [ObservableProperty] private string _listMessage = string.Empty;

    public ObservableCollection<DupGroup> Groups { get; } = new();

    public event EventHandler? Closed;

    public DuplicateFinderViewModel(IDuplicateFinderService service)
    {
        _service = service;
        _ = ScanAsync();
    }

    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    /// <summary>"1 file" / "12 files".</summary>
    private static string Files(int n) => n == 1 ? L("DuplicateFinder.FilesOne") : L("DuplicateFinder.FilesMany", n);

    [RelayCommand]
    private Task Rescan() => ScanAsync();

    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = ListMessage = L("DuplicateFinder.Scanning");
        Groups.Clear();
        HasGroups = false;

        var found = await _service.FindAsync();
        foreach (var g in found)
            Groups.Add(new DupGroup(g, RecomputeSelection));

        RecomputeSelection();
        HasGroups = Groups.Count > 0;
        ListMessage = L("DuplicateFinder.NoneFound");
        StatusMessage = Groups.Count == 0
            ? ListMessage
            : Groups.Count == 1 ? L("DuplicateFinder.GroupsOne") : L("DuplicateFinder.GroupsMany", Groups.Count);
        IsBusy = false;
    }

    private void RecomputeSelection()
    {
        SelectedCount = Groups.Sum(g => g.Rows.Count(r => r.Delete));
        HasSelection = SelectedCount > 0;
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        if (IsBusy || SelectedCount == 0) return;

        var ids = Groups.SelectMany(g => g.Rows).Where(r => r.Delete).Select(r => r.TrackId).ToList();
        if (ids.Count == 0) return;

        // Confirm before trashing. The rows arrive pre-ticked (every non-keep copy in
        // every group), so the Delete button was the only gate between opening the dialog
        // and deleting files across the whole library in one click.
        var question = OperatingSystem.IsWindows()
            ? L("DuplicateFinder.ConfirmRecycleBin", Files(ids.Count))
            : L("DuplicateFinder.ConfirmTrash", Files(ids.Count));
        var confirmed = await Views.ConfirmationDialog.ShowAsync(
            question + "\n\n" + L("DuplicateFinder.ConfirmKeepNote"));
        if (!confirmed) return;

        IsBusy = true;
        StatusMessage = L("DuplicateFinder.Deleting", Files(ids.Count));
        var n = await _service.DeleteAsync(ids);
        IsBusy = false;

        await ScanAsync();
        StatusMessage = L("DuplicateFinder.Deleted", Files(n));
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    public sealed class DupGroup
    {
        public DupGroup(DuplicateGroup model, Action onChanged)
        {
            var first = model.Tracks[0];
            Title = first.Title;
            Artist = first.PrimaryArtist;
            CopiesText = L("DuplicateFinder.Copies", model.Tracks.Count);
            // Sample rate / bit depth only when they tell the copies apart; otherwise they
            // are the same number on every row. A lossy copy has no bit depth (0), which
            // alone is no difference worth a column.
            var showFormat = model.Tracks.Select(t => t.SampleRate).Where(r => r > 0).Distinct().Count() > 1
                          || model.Tracks.Select(t => t.BitsPerSample).Where(b => b > 0).Distinct().Count() > 1;
            foreach (var t in model.Tracks)
                Rows.Add(new DupRow(t, t.Id == model.SuggestedKeepId, showFormat) { Changed = onChanged });
        }

        public string Title { get; }
        public string Artist { get; }
        /// <summary>"2 copies".</summary>
        public string CopiesText { get; }
        public ObservableCollection<DupRow> Rows { get; } = new();
    }

    public partial class DupRow : ObservableObject
    {
        public DupRow(Track t, bool suggestedKeep, bool showFormat = false)
        {
            TrackId = t.Id;
            FileName = Path.GetFileName(t.FilePath);
            FilePathFull = t.FilePath;
            FolderPathFull = Path.GetDirectoryName(t.FilePath) ?? string.Empty;
            FolderPath = DisplayPath.MiddleEllipsis(FolderPathFull);
            Location = DisplayPath.MiddleEllipsis(t.FilePath, 80);
            Quality = BuildQuality(t, showFormat);
            IsSuggestedKeep = suggestedKeep;
            _delete = !suggestedKeep; // default: keep the best copy, delete the rest
        }

        public Guid TrackId { get; }
        public string FileName { get; }
        public string FolderPath { get; }
        public string FolderPathFull { get; }
        /// <summary>The whole file path, middle-ellipsized (start and file name stay readable).</summary>
        public string Location { get; }
        public string FilePathFull { get; }
        public string Quality { get; }
        /// <summary>The copy the matcher rates best (lossless, then depth/rate/bitrate/size).</summary>
        public bool IsSuggestedKeep { get; }

        [ObservableProperty] private bool _delete;

        /// <summary>The inverse of <see cref="Delete"/>: what the copy card toggles (checked = kept).</summary>
        public bool Keep
        {
            get => !Delete;
            set => Delete = !value;
        }

        public Action? Changed { get; set; }

        partial void OnDeleteChanged(bool value)
        {
            OnPropertyChanged(nameof(Keep));
            Changed?.Invoke();
        }

        private static string BuildQuality(Track t, bool showFormat)
        {
            var parts = new List<string>();
            var codec = string.IsNullOrEmpty(t.CodecShortName)
                ? Path.GetExtension(t.FilePath).TrimStart('.').ToUpperInvariant()
                : t.CodecShortName;
            if (!string.IsNullOrEmpty(codec)) parts.Add(codec);
            if (t.Bitrate > 0) parts.Add($"{t.Bitrate} kbps");
            if (showFormat && t.SampleRate > 0)
                parts.Add(t.BitsPerSample > 0
                    ? $"{t.BitsPerSample}-bit {t.SampleRate / 1000.0:0.#} kHz"
                    : $"{t.SampleRate / 1000.0:0.#} kHz");
            parts.Add(FormatSize(t.FileSize));
            return string.Join(" · ", parts);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}

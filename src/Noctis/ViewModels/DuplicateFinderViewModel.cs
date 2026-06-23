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

    public ObservableCollection<DupGroup> Groups { get; } = new();

    public event EventHandler? Closed;

    public DuplicateFinderViewModel(IDuplicateFinderService service)
    {
        _service = service;
        _ = ScanAsync();
    }

    [RelayCommand]
    private Task Rescan() => ScanAsync();

    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Scanning for duplicates…";
        Groups.Clear();

        var found = await _service.FindAsync();
        foreach (var g in found)
            Groups.Add(new DupGroup(g, RecomputeSelection));

        RecomputeSelection();
        StatusMessage = Groups.Count == 0
            ? "No duplicates found."
            : $"{Groups.Count} duplicate group{(Groups.Count == 1 ? string.Empty : "s")} found";
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

        IsBusy = true;
        StatusMessage = $"Deleting {ids.Count} file{(ids.Count == 1 ? string.Empty : "s")}…";
        var n = await _service.DeleteAsync(ids);
        IsBusy = false;

        await ScanAsync();
        StatusMessage = $"Deleted {n} file{(n == 1 ? string.Empty : "s")}";
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    public sealed class DupGroup
    {
        public DupGroup(DuplicateGroup model, Action onChanged)
        {
            var first = model.Tracks[0];
            Header = $"{first.PrimaryArtist} — {first.Title}";
            Subheader = $"{model.Tracks.Count} copies · {first.DurationFormatted}";
            foreach (var t in model.Tracks)
                Rows.Add(new DupRow(t, t.Id == model.SuggestedKeepId) { Changed = onChanged });
        }

        public string Header { get; }
        public string Subheader { get; }
        public ObservableCollection<DupRow> Rows { get; } = new();
    }

    public partial class DupRow : ObservableObject
    {
        public DupRow(Track t, bool suggestedKeep)
        {
            TrackId = t.Id;
            FileName = Path.GetFileName(t.FilePath);
            FolderPathFull = Path.GetDirectoryName(t.FilePath) ?? string.Empty;
            FolderPath = DisplayPath.MiddleEllipsis(FolderPathFull);
            Quality = BuildQuality(t);
            IsSuggestedKeep = suggestedKeep;
            _delete = !suggestedKeep; // default: keep the best copy, delete the rest
        }

        public Guid TrackId { get; }
        public string FileName { get; }
        public string FolderPath { get; }
        public string FolderPathFull { get; }
        public string Quality { get; }
        public bool IsSuggestedKeep { get; }

        [ObservableProperty] private bool _delete;

        public Action? Changed { get; set; }

        partial void OnDeleteChanged(bool value) => Changed?.Invoke();

        private static string BuildQuality(Track t)
        {
            var parts = new List<string>();
            var codec = string.IsNullOrEmpty(t.CodecShortName)
                ? Path.GetExtension(t.FilePath).TrimStart('.').ToUpperInvariant()
                : t.CodecShortName;
            if (!string.IsNullOrEmpty(codec)) parts.Add(codec);
            if (t.IsLossless) parts.Add("Lossless");
            if (t.Bitrate > 0) parts.Add($"{t.Bitrate} kbps");
            if (t.SampleRate > 0) parts.Add($"{t.SampleRate / 1000.0:0.#} kHz");
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

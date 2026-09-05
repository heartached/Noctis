using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// "Send to Folder" (MusicBee's Send To → Folder (Copy)): copy a selection to a drive or
/// folder, flat or organised with the user's file pattern, identical files skipped.
/// </summary>
public partial class SendToFolderViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Track> _tracks;
    private readonly ISendToFolderService _service;
    private readonly string _organizePattern;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<SendToFolderItem> _plan = Array.Empty<SendToFolderItem>();

    public string TitleText { get; }
    public string SubtitleText { get; }

    [ObservableProperty] private string _destination = string.Empty;
    [ObservableProperty] private bool _organizeIntoFolders;
    [ObservableProperty] private bool _includeLyrics = true;

    public string OrganizePatternText => _organizePattern;

    public ObservableCollection<PlanRow> Rows { get; } = new();
    [ObservableProperty] private string _planSummary = string.Empty;
    [ObservableProperty] private bool _hasPlan;

    [ObservableProperty] private bool _isCopying;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public bool CanStart => HasPlan && !IsCopying && !IsDone;

    public event EventHandler? Closed;

    public SendToFolderViewModel(IReadOnlyList<Track> tracks, ISendToFolderService service, string organizePattern, string? initialDestination = null)
    {
        _tracks = tracks;
        _service = service;
        _organizePattern = string.IsNullOrWhiteSpace(organizePattern) ? FileOrganizePlanner.DefaultPattern : organizePattern;
        TitleText = tracks.Count == 1 ? "Send 1 song to a folder" : $"Send {tracks.Count} songs to a folder";
        SubtitleText = "Copies the files (and their lyrics) to a USB stick, phone or any folder. Nothing in your library moves.";
        if (!string.IsNullOrWhiteSpace(initialDestination)) Destination = initialDestination;
    }

    partial void OnDestinationChanged(string value) => RebuildPlan();
    partial void OnOrganizeIntoFoldersChanged(bool value) => RebuildPlan();
    partial void OnIncludeLyricsChanged(bool value) => RebuildPlan();
    partial void OnHasPlanChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsCopyingChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    private void RebuildPlan()
    {
        if (IsCopying) return;
        IsDone = false;
        Rows.Clear();
        var root = Destination?.Trim() ?? string.Empty;
        if (root.Length == 0 || !Directory.Exists(root))
        {
            _plan = Array.Empty<SendToFolderItem>();
            HasPlan = false;
            PlanSummary = root.Length == 0 ? "Pick a destination folder." : "That folder doesn't exist.";
            return;
        }
        _plan = _service.Plan(_tracks, root, OrganizeIntoFolders ? _organizePattern : null, IncludeLyrics);
        foreach (var item in _plan)
            Rows.Add(new PlanRow(item, root));
        var copy = _plan.Count(p => p.Action != SendToFolderAction.SkipIdentical);
        var skip = _plan.Count - copy;
        var renamed = _plan.Count(p => p.Action == SendToFolderAction.Renamed);
        var lyrics = _plan.Count(p => p.SidecarSource is not null);
        var parts = new List<string> { $"{copy} to copy" };
        if (skip > 0) parts.Add($"{skip} already there");
        if (renamed > 0) parts.Add($"{renamed} renamed to avoid clashes");
        if (lyrics > 0) parts.Add($"{lyrics} lyrics files");
        PlanSummary = string.Join(" · ", parts);
        HasPlan = copy > 0;
        if (!HasPlan && skip > 0) PlanSummary = "Everything is already in that folder.";
    }

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart) return;
        IsCopying = true;
        Progress = 0;
        StatusMessage = "Copying…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<SendToFolderProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Total == 0 ? 1 : p.Done / (double)p.Total;
            StatusMessage = p.CurrentFile.Length == 0 ? "Finishing…" : $"Copying {p.CurrentFile}";
            for (var i = 0; i < Rows.Count && i < p.Done; i++) Rows[i].MarkDone();
        }));
        try
        {
            var result = await _service.CopyAsync(_plan, progress, _cts.Token);
            foreach (var row in Rows) row.MarkDone();
            StatusMessage = result.Cancelled
                ? $"Stopped · {result.Copied} copied"
                : $"Done · {result.Copied} copied" + (result.Skipped > 0 ? $" · {result.Skipped} skipped" : "") + (result.Failed > 0 ? $" · {result.Failed} failed" : "");
            if (result.Errors.Count > 0)
                StatusMessage += " — " + result.Errors[0];
            IsDone = !result.Cancelled;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed — {ex.Message}";
        }
        finally
        {
            IsCopying = false;
            Progress = 1;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsCopying) { _cts?.Cancel(); return; }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public sealed partial class PlanRow : ObservableObject
    {
        public PlanRow(SendToFolderItem item, string root)
        {
            Title = item.Track.Title;
            Subtitle = item.Track.ArtistDisplay;
            var rel = Path.GetRelativePath(root, item.TargetPath);
            Target = rel.StartsWith("..", StringComparison.Ordinal) ? item.TargetPath : rel;
            Action = item.Action switch
            {
                SendToFolderAction.SkipIdentical => "Already there",
                SendToFolderAction.Renamed => "Renamed",
                _ => "Copy",
            };
            IsSkip = item.Action == SendToFolderAction.SkipIdentical;
            HasLyrics = item.SidecarSource is not null;
        }

        public string Title { get; }
        public string Subtitle { get; }
        public string Target { get; }
        public string Action { get; }
        public bool IsSkip { get; }
        public bool HasLyrics { get; }
        [ObservableProperty] private bool _done;
        public void MarkDone() => Done = true;
    }
}

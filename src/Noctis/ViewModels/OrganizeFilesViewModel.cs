using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the auto-organize dialog: preview every planned move, apply off the UI thread,
/// and undo the most recent batch. Nothing touches disk until <see cref="ApplyCommand"/>.
/// </summary>
public partial class OrganizeFilesViewModel : ViewModelBase
{
    private readonly IFileOrganizerService _service;
    private readonly SettingsViewModel _settingsVm;
    private readonly IReadOnlyList<Track> _tracks;
    private IReadOnlyList<OrganizeMove> _plan = Array.Empty<OrganizeMove>();

    [ObservableProperty] private string _pattern;
    [ObservableProperty] private string _targetRoot;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _hasApplicableMoves;

    public ObservableCollection<OrganizeRow> Rows { get; } = new();

    public event EventHandler? Closed;

    public OrganizeFilesViewModel(IReadOnlyList<Track> tracks, IFileOrganizerService service, SettingsViewModel settingsVm)
    {
        _tracks = tracks;
        _service = service;
        _settingsVm = settingsVm;
        _pattern = string.IsNullOrWhiteSpace(settingsVm.OrganizePattern)
            ? FileOrganizePlanner.DefaultPattern
            : settingsVm.OrganizePattern;
        _targetRoot = settingsVm.OrganizeTargetRoot;
        CanUndo = _service.CanUndo;
        _ = PreviewAsync();
    }

    private string EffectiveTargetRoot()
        => string.IsNullOrWhiteSpace(TargetRoot)
            ? _settingsVm.MusicFolders.FirstOrDefault() ?? string.Empty
            : TargetRoot;

    [RelayCommand]
    private Task Preview() => PreviewAsync();

    private async Task PreviewAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        var root = EffectiveTargetRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            Rows.Clear();
            HasApplicableMoves = false;
            StatusMessage = "Set a destination folder (or add a media folder first).";
            IsBusy = false;
            return;
        }

        StatusMessage = "Building preview…";
        var pattern = Pattern;
        var tracks = _tracks;
        var plan = await Task.Run(() => _service.Plan(tracks, pattern, root));
        _plan = plan;

        Rows.Clear();
        foreach (var m in plan)
            Rows.Add(new OrganizeRow(m, root));

        var moveCount = plan.Count(m => m.Action != OrganizeAction.Skip);
        HasApplicableMoves = moveCount > 0;
        StatusMessage = $"{moveCount} to move · {plan.Count - moveCount} already organized";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (IsBusy || !HasApplicableMoves) return;
        IsBusy = true;
        StatusMessage = "Moving files…";

        // Remember the chosen pattern/destination for next time.
        _settingsVm.OrganizePattern = Pattern;
        _settingsVm.OrganizeTargetRoot = TargetRoot;
        await _settingsVm.SaveAsync();

        var result = await _service.ApplyAsync(_plan);
        CanUndo = _service.CanUndo;
        IsBusy = false;

        // Refresh — moved rows now read back as "already organized".
        await PreviewAsync();
        StatusMessage = result.Failed > 0
            ? $"Moved {result.Moved} · {result.Failed} failed"
            : $"Moved {result.Moved} file{(result.Moved == 1 ? string.Empty : "s")}";
    }

    [RelayCommand]
    private async Task UndoLast()
    {
        if (IsBusy || !CanUndo) return;
        IsBusy = true;
        StatusMessage = "Undoing…";
        var result = await _service.UndoLastAsync();
        CanUndo = _service.CanUndo;
        IsBusy = false;
        await PreviewAsync();
        StatusMessage = $"Restored {result.Moved} file{(result.Moved == 1 ? string.Empty : "s")}";
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    public sealed class OrganizeRow
    {
        public OrganizeRow(OrganizeMove move, string targetRoot)
        {
            SourceName = Path.GetFileName(move.SourcePath);
            var rel = move.TargetPath;
            if (!string.IsNullOrEmpty(targetRoot) &&
                move.TargetPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                rel = move.TargetPath.Substring(targetRoot.Length).TrimStart('\\', '/');
            TargetRelativeFull = rel;
            TargetRelative = DisplayPath.MiddleEllipsis(rel);
            ActionText = move.Action switch
            {
                OrganizeAction.Skip => "Already organized",
                OrganizeAction.Conflict => "Move (renamed)",
                _ => "Move"
            };
        }

        public string SourceName { get; }
        public string TargetRelative { get; }
        public string TargetRelativeFull { get; }
        public string ActionText { get; }
    }
}

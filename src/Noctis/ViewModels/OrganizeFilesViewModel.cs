using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>Where the pattern puts one library track, relative to the destination
    /// (e.g. "Artist/Album/01 Title.flac"): a live example under the pattern field.</summary>
    [ObservableProperty] private string _sampleTarget = string.Empty;

    public BulkObservableCollection<OrganizeRow> Rows { get; } = new();

    /// <summary>The planner's tokens, as chips under the pattern field (the view inserts
    /// <see cref="PatternToken.Token"/> at the caret).</summary>
    public IReadOnlyList<PatternToken> Tokens { get; } = new PatternToken[]
    {
        new("AlbumArtist"), new("Artist"), new("Album"), new("TrackNo"),
        new("DiscNo"), new("Title"), new("Year"), new("Genre"),
    };

    public sealed record PatternToken(string Label)
    {
        public string Token => "{" + Label + "}";
    }

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
        UpdateSample();
        _ = PreviewAsync();
    }

    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    partial void OnPatternChanged(string value) => UpdateSample();
    partial void OnTargetRootChanged(string value) => UpdateSample();

    /// <summary>Plans the first library track alone (display only; the full plan is
    /// still built by Preview), so the example follows every keystroke.</summary>
    private void UpdateSample()
    {
        var track = _tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.FilePath));
        var root = EffectiveTargetRoot();
        if (track is null || string.IsNullOrWhiteSpace(root)) { SampleTarget = string.Empty; return; }
        try
        {
            var move = _service.Plan(new[] { track }, Pattern, root).FirstOrDefault();
            SampleTarget = move is null ? string.Empty : new OrganizeRow(move, root).TargetRelativeFull;
        }
        catch (Exception)
        {
            // A half-typed pattern can make an invalid path; the example just goes blank.
            SampleTarget = string.Empty;
        }
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
            StatusMessage = L("OrganizeFiles.NoDestination");
            IsBusy = false;
            return;
        }

        StatusMessage = L("OrganizeFiles.Building");
        var pattern = Pattern;
        var tracks = _tracks;
        var plan = await Task.Run(() => _service.Plan(tracks, pattern, root));
        _plan = plan;

        // One Reset for the whole plan: it holds a row per library track.
        Rows.ReplaceAll(plan.Select(m => new OrganizeRow(m, root)));

        var moveCount = plan.Count(m => m.Action != OrganizeAction.Skip);
        HasApplicableMoves = moveCount > 0;
        StatusMessage = L("OrganizeFiles.Summary", moveCount, plan.Count - moveCount);
        IsBusy = false;
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (IsBusy || !HasApplicableMoves) return;
        IsBusy = true;
        StatusMessage = L("OrganizeFiles.Moving");

        // Remember the chosen pattern/destination for next time.
        _settingsVm.OrganizePattern = Pattern;
        _settingsVm.OrganizeTargetRoot = TargetRoot;
        await _settingsVm.SaveAsync();

        var result = await _service.ApplyAsync(_plan);
        await RemapPlaylistsAsync(result);
        CanUndo = _service.CanUndo;
        IsBusy = false;

        // Refresh — moved rows now read back as "already organized".
        await PreviewAsync();
        StatusMessage = result.Failed > 0
            ? L("OrganizeFiles.MovedFailed", result.Moved, result.Failed)
            : L("OrganizeFiles.Moved", result.Moved);
    }

    [RelayCommand]
    private async Task UndoLast()
    {
        if (IsBusy || !CanUndo) return;
        IsBusy = true;
        StatusMessage = L("OrganizeFiles.Undoing");
        var result = await _service.UndoLastAsync();
        await RemapPlaylistsAsync(result);
        CanUndo = _service.CanUndo;
        IsBusy = false;
        await PreviewAsync();
        StatusMessage = L("OrganizeFiles.Restored", result.Moved);
    }

    /// <summary>Moved tracks get new ids; point the sidebar's live playlists at them.</summary>
    private static Task RemapPlaylistsAsync(OrganizeResult result)
        => App.Services?.GetService<MainWindowViewModel>()?.Sidebar.ApplyTrackIdRemapAsync(result.TrackIdRemap)
           ?? Task.CompletedTask;

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
            IsInPlace = move.Action == OrganizeAction.Skip;
            ActionText = move.Action switch
            {
                OrganizeAction.Skip => L("OrganizeFiles.ActionInPlace"),
                OrganizeAction.Conflict => L("OrganizeFiles.ActionRenamed"),
                _ => L("OrganizeFiles.ActionMove")
            };
        }

        public string SourceName { get; }
        public string TargetRelative { get; }
        public string TargetRelativeFull { get; }
        public string ActionText { get; }
        /// <summary>Already where the pattern puts it: the row is dimmed.</summary>
        public bool IsInPlace { get; }
    }
}

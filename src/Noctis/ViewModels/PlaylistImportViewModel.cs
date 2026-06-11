using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the playlist-import dialog: read an Exportify CSV / TuneMyMusic JSON export,
/// fuzzy-match its entries against the library, then create a playlist from the matches and
/// show a report of the tracks that couldn't be found.
/// </summary>
public partial class PlaylistImportViewModel : ViewModelBase
{
    private readonly IPlaylistImportService _service;
    private PlaylistImportPreview? _preview;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Choose an Exportify CSV or TuneMyMusic JSON export.";
    [ObservableProperty] private string _playlistName = string.Empty;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private bool _canCreate;
    [ObservableProperty] private int _matchedCount;
    [ObservableProperty] private int _missingCount;
    [ObservableProperty] private bool _hasMissing;

    public ObservableCollection<string> MissingTracks { get; } = new();

    public event EventHandler? Closed;

    public PlaylistImportViewModel(IPlaylistImportService service) => _service = service;

    /// <summary>Called by the dialog after the user picks a file.</summary>
    public async Task LoadFileAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Reading and matching…";
        MissingTracks.Clear();
        HasPreview = false;
        CanCreate = false;

        try
        {
            var preview = await _service.AnalyzeAsync(path);
            _preview = preview;
            PlaylistName = preview.SuggestedName;
            MatchedCount = preview.MatchedTrackIds.Count;
            MissingCount = preview.MissingLabels.Count;
            HasMissing = MissingCount > 0;
            foreach (var m in preview.MissingLabels) MissingTracks.Add(m);
            HasPreview = preview.TotalEntries > 0;
            CanCreate = MatchedCount > 0;
            StatusMessage = HasPreview
                ? $"{MatchedCount} matched · {MissingCount} missing of {preview.TotalEntries}"
                : "No tracks found in that file.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read file: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Create()
    {
        if (IsBusy || _preview is null || _preview.MatchedTrackIds.Count == 0) return;
        IsBusy = true;
        StatusMessage = "Creating playlist…";

        await _service.CreateAsync(PlaylistName, _preview.MatchedTrackIds);

        // Reflect the new playlist in the sidebar immediately.
        var main = App.Services?.GetService<MainWindowViewModel>();
        if (main is not null) await main.Sidebar.LoadPlaylistsAsync();

        CanCreate = false;
        IsBusy = false;
        StatusMessage = $"Created \"{PlaylistName}\" with {_preview.MatchedTrackIds.Count} track"
            + (_preview.MatchedTrackIds.Count == 1 ? "." : "s.");
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);
}

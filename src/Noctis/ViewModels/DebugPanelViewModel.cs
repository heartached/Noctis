using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using static Noctis.Services.DebugLogger;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the debug overlay panel. Displays log entries
/// and key live state from the player/app.
/// </summary>
public partial class DebugPanelViewModel : ViewModelBase, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly MainWindowViewModel _mainVm;

    /// <summary>Log entries displayed in the panel (newest first).</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    // ── Filters ──

    [ObservableProperty] private string _selectedCategoryFilter = "All";
    public string[] CategoryFilters { get; } = new[]
    {
        "All", "UI", "Playback", "Lyrics", "Queue", "Search", "ContextMenu", "State", "Error"
    };

    // ── Live state ──

    [ObservableProperty] private string _activeTab = "";
    [ObservableProperty] private string _selectedTrack = "None";
    [ObservableProperty] private string _playbackStatus = "Stopped";
    [ObservableProperty] private string _shuffleState = "Off";
    [ObservableProperty] private string _repeatState = "Off";
    [ObservableProperty] private int _queueLength;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _lyricsStatus = "";
    [ObservableProperty] private int _entryCount;

    public DebugPanelViewModel(PlayerViewModel player, MainWindowViewModel mainVm)
    {
        _player = player;
        _mainVm = mainVm;

        // Subscribe to new log entries
        DebugLogger.EntryAdded += OnEntryAdded;

        // Subscribe to player state for live display
        _player.PropertyChanged += OnPlayerPropertyChanged;

        // Load existing entries
        RefreshEntries();
        RefreshLiveState();
    }

    private void OnEntryAdded(LogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!PassesFilter(entry)) return;
            Entries.Insert(0, entry);
            while (Entries.Count > 200) Entries.RemoveAt(Entries.Count - 1);
            EntryCount = Entries.Count;
        });
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshLiveState);
    }

    private void RefreshLiveState()
    {
        ActiveTab = GetActiveTab();
        SelectedTrack = _player.CurrentTrack?.Title ?? "None";
        PlaybackStatus = _player.State.ToString();
        ShuffleState = _player.IsShuffleEnabled ? "On" : "Off";
        RepeatState = _player.RepeatMode.ToString();
        QueueLength = _player.UpNext.Count;
    }

    private string GetActiveTab()
    {
        var view = _mainVm.CurrentView;
        return view?.GetType().Name.Replace("ViewModel", "") ?? "Unknown";
    }

    partial void OnSelectedCategoryFilterChanged(string value) => RefreshEntries();

    [RelayCommand]
    private void ClearLog()
    {
        DebugLogger.Clear();
        Entries.Clear();
        EntryCount = 0;
    }

    [RelayCommand]
    private async Task CopyAll()
    {
        var sb = new StringBuilder();
        // Entries are newest-first; reverse for chronological output
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            sb.Append(e.Timestamp.ToString("HH:mm:ss.fff"));
            sb.Append($" [{e.Category}:{e.Level}] {e.Action}");
            if (e.Metadata != null) sb.Append($" | {e.Metadata}");
            sb.AppendLine();
        }
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Clipboard : null;
        if (clipboard != null)
            await clipboard.SetTextAsync(sb.ToString());
    }

    [RelayCommand]
    private void RefreshEntries()
    {
        Entries.Clear();
        var all = DebugLogger.GetEntries();
        for (int i = all.Length - 1; i >= 0; i--)
        {
            if (PassesFilter(all[i]))
                Entries.Add(all[i]);
        }
        EntryCount = Entries.Count;
    }

    private bool PassesFilter(LogEntry entry)
    {
        if (SelectedCategoryFilter == "All") return true;
        return Enum.TryParse<Category>(SelectedCategoryFilter, out var cat) && entry.Category == cat;
    }

    public void Dispose()
    {
        DebugLogger.EntryAdded -= OnEntryAdded;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
    }
}

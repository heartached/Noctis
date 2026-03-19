using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Queue view in the main content area.
/// Exposes the PlayerViewModel's queue data with additional UI commands.
/// This is a thin wrapper — the actual queue state lives in PlayerViewModel.
/// </summary>
public partial class QueueViewModel : ViewModelBase
{
    private readonly PlayerViewModel _player;

    public PlayerViewModel Player => _player;

    /// <summary>Reference to UpNext from the player (same collection, shared binding).</summary>
    public ObservableCollection<Track> UpNext => _player.UpNext;

    /// <summary>Reference to History from the player.</summary>
    public ObservableCollection<Track> History => _player.History;

    public QueueViewModel(PlayerViewModel player)
    {
        _player = player;
    }

    [RelayCommand]
    private void RemoveFromQueue(int index) => _player.RemoveFromQueue(index);

    [RelayCommand]
    private void ClearQueue() => _player.ClearQueue();

    [RelayCommand]
    private void PlayFromHistory(Track track)
    {
        // Remove from history and play immediately
        var idx = History.IndexOf(track);
        if (idx >= 0)
        {
            History.RemoveAt(idx);
            _player.ReplaceQueueAndPlay(new List<Track> { track }, 0);
        }
    }
}

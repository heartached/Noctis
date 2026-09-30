using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>A Last Played row: the song and its 1-based position. A record, so an unchanged row compares equal.</summary>
public sealed record HomeSongRow(Track Track, int Number);

/// <summary>
/// The Home tab: the desktop's recent rows (Last Played, Recently Played albums) from the
/// persisted play log, plus Recently Added. Rows are replaced only when their content
/// changed, so revisiting Home does not rebuild every tile.
/// </summary>
public sealed partial class HomePageViewModel : ObservableObject
{
    internal const int LastPlayedMax = 8;
    internal const int AlbumRailMax = 12;

    public HomePageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        shell.Library.Refreshed += (_, _) => Refresh();
    }

    public ShellViewModel Shell { get; }

    public BulkObservableCollection<HomeSongRow> LastPlayed { get; } = new();
    public BulkObservableCollection<Album> RecentlyPlayedAlbums { get; } = new();
    public BulkObservableCollection<Album> RecentlyAddedAlbums { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLastPlayed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasRecentlyPlayed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasRecentlyAdded;

    public bool IsEmpty => !HasLastPlayed && !HasRecentlyPlayed && !HasRecentlyAdded;

    public string Greeting => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    public void Refresh()
    {
        var library = Shell.Library.Service;
        var events = Shell.Library.History?.Events ?? Array.Empty<PlayHistoryEvent>();
        var recent = HomeRowsBuilder.BuildRecentFromLog(events, library.GetTrackById, MobileLibrary.RecentLogScan);

        var lastPlayed = HomeRowsBuilder.BuildLastPlayed(recent, LastPlayedMax)
            .Select((t, i) => new HomeSongRow(t, i + 1)).ToList();
        MobileLibrary.ReplaceIfChanged(LastPlayed, lastPlayed);
        HasLastPlayed = LastPlayed.Count > 0;

        MobileLibrary.ReplaceIfChanged(RecentlyPlayedAlbums, MobileLibrary.RecentAlbums(library, recent, AlbumRailMax));
        HasRecentlyPlayed = RecentlyPlayedAlbums.Count > 0;

        MobileLibrary.ReplaceIfChanged(RecentlyAddedAlbums, MobileLibrary.RecentlyAddedAlbums(library, AlbumRailMax));
        HasRecentlyAdded = RecentlyAddedAlbums.Count > 0;
        OnPropertyChanged(nameof(Greeting));
    }

    /// <summary>A Last Played row plays the Last Played list from it.</summary>
    [RelayCommand]
    private void PlayLastPlayed(HomeSongRow? row)
    {
        if (row == null) return;
        var list = LastPlayed.Select(r => r.Track).ToList();
        var index = list.IndexOf(row.Track);
        if (index >= 0) Shell.Player.PlayTracks(list, index);
    }
}

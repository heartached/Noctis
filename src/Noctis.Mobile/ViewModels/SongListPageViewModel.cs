using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// A flat list of songs: all songs, favourites, recently added, a playlist (and, until the
/// album and artist pages land, an album or an artist). The rows come from a source function
/// re-read on every library refresh, so a rescan or a heart toggle shows in an open list.
/// </summary>
public sealed partial class SongListPageViewModel : MobilePage
{
    private readonly string _title;
    private readonly Func<IEnumerable<Track>> _source;

    public SongListPageViewModel(ShellViewModel shell, string title, Func<IEnumerable<Track>> source)
    {
        Shell = shell;
        _title = title;
        _source = source;
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    public ShellViewModel Shell { get; }

    public override string Title => _title;

    public BulkObservableCollection<Track> Songs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSongs), nameof(CountText))]
    private int _songCount;

    public bool HasSongs => SongCount > 0;

    public string CountText => SongCount == 1 ? "1 song" : $"{SongCount} songs";

    public void Refresh()
    {
        Songs.ReplaceAll(_source());
        SongCount = Songs.Count;
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    /// <summary>A row tap: play this list from that row.</summary>
    [RelayCommand]
    private void Play(Track? track)
    {
        if (track == null) return;
        var list = Songs.ToList();
        var index = list.IndexOf(track);
        if (index >= 0) Shell.Player.PlayTracks(list, index, Title);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Songs.Count > 0) Shell.Player.PlayTracks(Songs.ToList(), 0, Title);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        if (Songs.Count > 0) Shell.Player.PlayShuffled(Songs.ToList(), source: Title);
    }

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}

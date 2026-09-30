using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>The saved playlists (playlists.json), pinned first.</summary>
public sealed partial class PlaylistListPageViewModel : MobilePage
{
    public PlaylistListPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    public ShellViewModel Shell { get; }

    public override string Title => Loc.T("Nav.Playlists");

    public BulkObservableCollection<Playlist> Playlists { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaylists))]
    private int _playlistCount;

    public bool HasPlaylists => PlaylistCount > 0;

    public void Refresh()
    {
        Playlists.ReplaceAll(Shell.Library.Playlists
            .OrderByDescending(p => p.IsPinned)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
        PlaylistCount = Playlists.Count;
    }

    [RelayCommand]
    private void Open(Playlist? playlist) => Shell.OpenPlaylistCommand.Execute(playlist);

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}

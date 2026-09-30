using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>One artist row: the Core artist and the cover the phone shows for it.</summary>
public sealed record ArtistListItem(Artist Artist, string? ArtworkPath)
{
    public string Name => Artist.Name;

    public string Subtitle =>
        $"{Artist.AlbumCount} album{(Artist.AlbumCount == 1 ? "" : "s")} · {Artist.TrackCount} song{(Artist.TrackCount == 1 ? "" : "s")}";
}

/// <summary>All artists, alphabetical.</summary>
public sealed partial class ArtistListPageViewModel : MobilePage
{
    public ArtistListPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    public ShellViewModel Shell { get; }

    public override string Title => Loc.T("Nav.Artists");

    public BulkObservableCollection<ArtistListItem> Artists { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtists))]
    private int _artistCount;

    public bool HasArtists => ArtistCount > 0;

    public void Refresh()
    {
        var library = Shell.Library.Service;
        var artwork = MobileLibrary.ArtistArtwork(library);
        Artists.ReplaceAll(library.Artists
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new ArtistListItem(a, artwork.GetValueOrDefault(a.Name))));
        ArtistCount = Artists.Count;
    }

    [RelayCommand]
    private void Open(ArtistListItem? item)
    {
        if (item != null) Shell.OpenArtistCommand.Execute(item.Name);
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}

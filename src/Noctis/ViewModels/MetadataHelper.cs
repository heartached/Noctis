using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Noctis.Models;
using Noctis.Services;
using Noctis.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Noctis.ViewModels;

/// <summary>
/// Shared helper to open the Metadata window from any ViewModel.
/// </summary>
public static class MetadataHelper
{
    public static async Task OpenMetadataWindow(Track track, bool albumScoped = false)
    {
        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var persistence = App.Services!.GetRequiredService<IPersistenceService>();

        List<Track>? albumTracks = null;
        if (albumScoped)
        {
            albumTracks = library.Tracks
                .Where(t => t.AlbumId == track.AlbumId)
                .ToList();
        }

        var vm = new MetadataViewModel(track, metadata, library, persistence, albumScoped, albumTracks);
        var window = new MetadataWindow(vm);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            await window.ShowDialog(desktop.MainWindow);
        }
        else
        {
            window.Show();
        }
    }
}

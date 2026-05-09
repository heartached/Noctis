using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Noctis.Models;
using Noctis.Services;
using Noctis.Views;
using Noctis.Helpers;
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

        var animatedCovers = new AnimatedCoverService(persistence);
        var vm = new MetadataViewModel(track, metadata, library, persistence, animatedCovers, albumScoped, albumTracks);

        // Live-apply volume adjust and EQ preset when the edited track is currently playing.
        vm.ChangesSaved += (_, _) =>
        {
            var main = App.Services!.GetService<MainWindowViewModel>();
            if (main == null || main.Player.CurrentTrack != track) return;
            var audio = App.Services!.GetRequiredService<IAudioPlayer>();
            audio.VolumeAdjust = track.VolumeAdjust;
            main.Settings.ApplyEqPresetByName(
                string.IsNullOrEmpty(track.EqPreset) ? null : track.EqPreset);
        };

        var window = new MetadataWindow(vm);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            DialogHelper.SizeToOwner(window, desktop.MainWindow);
            await window.ShowDialog(desktop.MainWindow);
        }
        else
        {
            window.Show();
        }
    }
}

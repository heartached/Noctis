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
    /// <summary>
    /// Shows a dialog as a modal owned by the main window (sized to the owner), or as a
    /// standalone window when no desktop lifetime is available.
    /// </summary>
    private static async Task ShowDialogOwned(Window window)
    {
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

    public static async Task OpenReplayGainScannerDialog(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        var service = App.Services!.GetRequiredService<IReplayGainScannerService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var vm = new ReplayGainScannerViewModel(tracks, service, library);
        var window = new ReplayGainScannerDialog(vm);
        await ShowDialogOwned(window);
    }

    /// <summary>Opens the auto-organize tool over all local library tracks.</summary>
    public static async Task OpenOrganizeFilesDialog(SettingsViewModel settings)
    {
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var service = App.Services!.GetRequiredService<IFileOrganizerService>();
        var tracks = library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
        var vm = new OrganizeFilesViewModel(tracks, service, settings);
        var window = new OrganizeFilesDialog(vm);
        await ShowDialogOwned(window);
    }

    /// <summary>Opens the duplicate-finder tool over the local library.</summary>
    public static async Task OpenDuplicateFinderDialog()
    {
        var service = App.Services!.GetRequiredService<IDuplicateFinderService>();
        var vm = new DuplicateFinderViewModel(service);
        var window = new DuplicateFinderDialog(vm);
        await ShowDialogOwned(window);
    }

    /// <summary>Opens the metadata finder over poorly-tagged local tracks.</summary>
    public static async Task OpenMetadataFinderDialog()
    {
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var finder = App.Services!.GetRequiredService<IMetadataFinderService>();
        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var candidates = library.Tracks
            .Where(t => t.SourceType == SourceType.Local && IsPoorlyTagged(t))
            .ToList();
        var vm = new MetadataFinderViewModel(candidates, finder, metadata, library);
        var window = new MetadataFinderDialog(vm);
        await ShowDialogOwned(window);
    }

    private static bool IsPoorlyTagged(Track t) =>
        string.IsNullOrWhiteSpace(t.Title) ||
        string.IsNullOrWhiteSpace(t.Artist) || t.Artist == "Unknown Artist" ||
        string.IsNullOrWhiteSpace(t.Album) || t.Album == "Unknown Album";

    public static async Task OpenAudioConverterDialog(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        var service = App.Services!.GetRequiredService<IAudioConverterService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var vm = new AudioConverterViewModel(tracks, service, library);
        var window = new AudioConverterDialog(vm);
        await ShowDialogOwned(window);
    }

    public static async Task OpenBatchMetadataWindow(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        if (tracks.Count == 1) { await OpenMetadataWindow(tracks[0]); return; }
        await OpenMultiTrackMetadataWindow(tracks);
    }

    /// <summary>
    /// Opens the tabbed metadata editor in multi-select mode for an arbitrary set of
    /// tracks: blank artwork, "N artists / M songs selected" header, Mixed fields, and
    /// edits that fan out to every selected track.
    /// </summary>
    public static async Task OpenMultiTrackMetadataWindow(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        if (tracks.Count == 1) { await OpenMetadataWindow(tracks[0]); return; }

        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var persistence = App.Services!.GetRequiredService<IPersistenceService>();
        var animatedCovers = new AnimatedCoverService(persistence);
        var itunes = App.Services!.GetService<ITunesArtworkService>();
        var lrcLib = App.Services!.GetService<ILrcLibService>();

        var vm = new MetadataViewModel(tracks[0], metadata, library, persistence, animatedCovers,
            albumScoped: true, albumTracks: tracks.ToList(), itunes: itunes, lrcLib: lrcLib, multiSelect: true);

        var window = new MetadataWindow(vm);
        await ShowDialogOwned(window);
    }

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
        var itunes = App.Services!.GetService<ITunesArtworkService>();
        var lrcLib = App.Services!.GetService<ILrcLibService>();
        var vm = new MetadataViewModel(track, metadata, library, persistence, animatedCovers, albumScoped, albumTracks, itunes, lrcLib);

        vm.ChangesSaved += (_, _) =>
        {
            var main = App.Services!.GetService<MainWindowViewModel>();
            if (main == null) return;

            // An animated cover may have been added/removed for this album — re-resolve so
            // surfaces bound to the player (album detail header, now playing, mini-art) update.
            if (main.Player.CurrentTrack?.AlbumId == track.AlbumId)
                main.Player.RefreshAnimatedCover();

            // Live-apply volume adjust and EQ preset when the edited track is currently playing.
            if (main.Player.CurrentTrack != track) return;
            var audio = App.Services!.GetRequiredService<IAudioPlayer>();
            audio.VolumeAdjust = track.VolumeAdjust;
            main.Settings.ApplyEqPresetByName(
                string.IsNullOrEmpty(track.EqPreset) ? null : track.EqPreset);
        };

        var window = new MetadataWindow(vm);
        await ShowDialogOwned(window);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>A one-line confirmation for the pill above the island (<see cref="QueueToastViewModel"/>).</summary>
public sealed record PlayerToast(string Headline, string Detail);

/// <summary>
/// GitHub #186: two optional buttons in the island's right cluster, both off by default
/// (Settings → Player → Player island buttons). Move to Trash moves the playing song into the
/// Trash folder picked in Settings (the Recycle Bin / Trash when none is set or it's gone) and
/// drops it from the library, in one click; Quick Add to Playlist adds it to one of up to three
/// playlists picked in Settings. Each confirms with the pill above the island.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>The Move to Trash button in the right cluster (Settings → Player).</summary>
    [ObservableProperty] private bool _islandShowTrash;

    /// <summary>Where Move to Trash puts the song; empty = the Recycle Bin / Trash.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrashButtonTip))]
    private string _islandTrashFolder = string.Empty;

    /// <summary>The Quick Add to Playlist button in the right cluster (Settings → Player).</summary>
    [ObservableProperty] private bool _islandShowQuickPlaylist;

    /// <summary>The quick playlists' ids, one per slot (<see cref="Guid.Empty"/> = empty).</summary>
    [ObservableProperty] private IReadOnlyList<Guid> _islandQuickPlaylistIds = Array.Empty<Guid>();

    /// <summary>Raised for the island buttons' confirmations (moved, added, already there).</summary>
    public event EventHandler<PlayerToast>? ToastRequested;

    /// <summary>Raised by the quick playlist pop-up's "Choose Quick Playlists" row when none
    /// are set; the shell opens Settings on the Player tab.</summary>
    public event EventHandler? QuickPlaylistSettingsRequested;

    /// <summary>The quick playlists as they are now: current names, deleted ones gone.</summary>
    public IReadOnlyList<Playlist> ResolvedQuickPlaylists => QuickPlaylists.Resolve(IslandQuickPlaylistIds, Playlists);

    /// <summary>Tooltip and accessible name of the Move to Trash button: where a click sends the song.</summary>
    public string TrashButtonTip => Loc.T("PlaybackBar.MoveToTip", TrashDestinationName(IslandTrashFolder));

    /// <summary>"Recycle Bin" (Windows) / "Trash", or the Trash folder's own name.</summary>
    internal static string TrashDestinationName(string? folder)
    {
        if (TrashFolderMover.IsUsable(folder))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(folder!.Trim());
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
        return Loc.T(OperatingSystem.IsWindows() ? "RemoveFromLibrary.RecycleBin" : "RemoveFromLibrary.Trash");
    }

    /// <summary>Only a song that is a file on this computer can be moved; streams, CD tracks
    /// and the phone's desktop songs keep the button disabled.</summary>
    internal static bool CanTrash(Track? track) =>
        track is { SourceType: SourceType.Local, HasFilesystemPath: true }
        && !string.IsNullOrWhiteSpace(track.FilePath);

    private bool CanTrashCurrentTrack() => CanTrash(CurrentTrack);

    private void OnCurrentTrackChangedForBarActions()
    {
        TrashCurrentTrackCommand.NotifyCanExecuteChanged();
        AddCurrentTrackToQuickPlaylistCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Moves a file into the Trash folder; a seam for tests.</summary>
    internal Func<string, string, Task<TrashFolderMover.Result>> MoveToTrashFolder { get; set; } = TrashFolderMover.MoveAsync;

    /// <summary>Sends a song's file to the Recycle Bin / Trash (the Remove from Library path,
    /// sidecars and emptied folders included) and says whether it's gone; a seam for tests.</summary>
    internal Func<Track, Task<bool>> RecycleTrackFile { get; set; } = RecycleAsync;

    private static async Task<bool> RecycleAsync(Track track)
    {
        await LibraryRemovalHelper.TrashLocalFilesAsync(new[] { track });
        return !File.Exists(track.FilePath);
    }

    /// <summary>
    /// The island's Move to Trash button. No confirmation (the point is one click); the pill
    /// names the song and where it went. The next song starts first so the engine lets go of
    /// the file, as Remove from Library does. The song leaves the library only once its file
    /// has moved: a file that stays (held open by another program, the folder unplugged
    /// mid-way) keeps its library entry, and the pill says it couldn't be moved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTrashCurrentTrack))]
    private async Task TrashCurrentTrack()
    {
        var track = CurrentTrack;
        if (track == null || !CanTrash(track)) return;

        var folder = IslandTrashFolder;
        var toFolder = TrashFolderMover.IsUsable(folder);
        var destinationName = TrashDestinationName(toFolder ? folder : null);
        Services.DebugLog.Write("Library", $"island trash: {(toFolder ? "folder" : "bin")}");

        ReleaseCurrentTrackForRemoval();

        bool moved;
        try
        {
            moved = toFolder
                ? (await MoveToTrashFolder(track.FilePath, folder)).Moved
                : await RecycleTrackFile(track);
        }
        catch (Exception ex)
        {
            Services.DebugLog.Write("Library", $"island trash failed: {ex.Message}");
            moved = false;
        }

        if (moved)
            await _library.RemoveTrackAsync(track.Id);

        ToastRequested?.Invoke(this, new PlayerToast(
            Loc.T(moved ? "PlaybackBar.MovedTo" : "PlaybackBar.CouldNotMove", destinationName),
            SongName(track)));
    }

    private bool CanAddCurrentTrackToQuickPlaylist() => CurrentTrack != null;

    /// <summary>
    /// A quick playlist picked in the island's pop-up: adds the playing song through the same
    /// <see cref="SidebarViewModel.AddTracksToPlaylist"/> as Add to Playlist, which never adds
    /// a song twice; the pill says "Added to …" or "Already in …".
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddCurrentTrackToQuickPlaylist))]
    private async Task AddCurrentTrackToQuickPlaylist(Playlist? playlist)
    {
        var track = CurrentTrack;
        if (track == null || playlist == null || _sidebar == null) return;
        // A slot can outlive its playlist between the pop-up opening and the click.
        var live = _sidebar.Playlists.FirstOrDefault(p => p.Id == playlist.Id);
        if (live == null || live.IsSmartPlaylist) return;

        var already = live.TrackIds.Contains(track.Id);
        if (!already)
            await _sidebar.AddTracksToPlaylist(live.Id, new[] { track });

        ToastRequested?.Invoke(this, new PlayerToast(
            Loc.T(already ? "PlaybackBar.AlreadyInPlaylist" : "PlaybackBar.AddedToPlaylist", live.Name),
            SongName(track)));
    }

    /// <summary>The pop-up with no quick playlists set: "Choose Quick Playlists" opens
    /// Settings on the Player tab, where the pickers are.</summary>
    [RelayCommand]
    private void ChooseQuickPlaylists() => QuickPlaylistSettingsRequested?.Invoke(this, EventArgs.Empty);

    private static string SongName(Track track) =>
        string.IsNullOrWhiteSpace(track.Title) ? Loc.T("QueueToast.OneSong") : track.Title;
}

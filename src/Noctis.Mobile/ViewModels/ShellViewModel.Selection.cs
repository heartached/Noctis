using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

public sealed partial class ShellViewModel
{
    // ── Select mode (TrackSelection): one song list at a time ──

    /// <summary>The song list in Select mode, whose action bar stands above the tabs in the mini
    /// player's place; null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecting), nameof(IsMiniBarVisible), nameof(IsMiniBarExpandedVisible), nameof(IsMiniBarInlineVisible))]
    private TrackSelection? _activeSelection;

    public bool IsSelecting => ActiveSelection != null;

    internal void BeginSelection(TrackSelection selection)
    {
        if (ActiveSelection is { } other && !ReferenceEquals(other, selection)) other.End();
        CloseSheet();
        ActiveSelection = selection;
        // The action bar needs the room of the unfolded bar (ReportContentScroll keeps it so).
        UnfoldTabBar();
    }

    internal void EndSelection(TrackSelection selection)
    {
        if (ReferenceEquals(ActiveSelection, selection)) ActiveSelection = null;
    }

    /// <summary>Another page, a tab switch or the player: Select mode does not follow.</summary>
    private void EndActiveSelection() => ActiveSelection?.End();

    /// <summary>The Add to Playlist list on the bottom sheet, straight away, for
    /// <paramref name="tracks"/> (Select mode's action bar).</summary>
    public void ShowPlaylistPicker(IReadOnlyList<Track> tracks)
    {
        var sheet = ContextSheetViewModel.ForTracks(this, tracks);
        sheet.IsPickingPlaylist = true;
        Sheet = sheet;
    }

    // ── Playlists ⋯ › Import Playlist (.m3u / .m3u8) ──

    /// <summary>The system document picker for playlist files (Android); null in tests and on
    /// hosts without one, where the menu leaves Import Playlist out.</summary>
    public IPlaylistFilePicker? PlaylistFiles { get; init; }

    public bool CanImportPlaylists => PlaylistFiles != null;

    /// <summary>The Playlists page's ⋯: Import Playlist.</summary>
    [RelayCommand]
    private void ShowPlaylistsMenu()
    {
        var items = new List<OptionsMenuItem>();
        if (CanImportPlaylists)
            items.Add(new OptionsMenuItem(this, Loc.T("Mobile.Import.Playlist"), false, () => ImportPlaylistsCommand.Execute(null)));
        if (items.Count > 0) ShowOptions(OptionsMenuViewModel.ForItems(items));
    }

    [RelayCommand]
    private async Task ImportPlaylistsAsync()
    {
        if (PlaylistFiles is not { } picker) return;
        IReadOnlyList<PickedPlaylistFile> files;
        try
        {
            files = await picker.PickPlaylistFilesAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Import", $"Picking playlist files failed: {ex.Message}");
            return;
        }
        if (files.Count > 0) await ImportPlaylistFilesAsync(files);
    }

    /// <summary>
    /// Each .m3u/.m3u8 becomes a playlist named after its file (made unique), holding the
    /// phone's songs it matched in its order; a file that matched nothing makes no playlist.
    /// Then the glass prompt says how it went and lists the lines that matched nothing.
    /// </summary>
    internal async Task<IReadOnlyList<PlaylistImportOutcome>> ImportPlaylistFilesAsync(IReadOnlyList<PickedPlaylistFile> files)
    {
        // A snapshot on this thread: a scan may change the library while the match runs.
        var library = Library.Service.Tracks.ToList();
        var outcomes = new List<PlaylistImportOutcome>();
        var notes = new List<string>();
        var created = new List<Playlist>();
        foreach (var file in files)
        {
            if (!PhonePlaylistImport.IsPlaylistFile(file.Name))
            {
                notes.Add(Loc.T("Mobile.Import.NotPlaylist", file.Name));
                continue;
            }
            PlaylistImportOutcome outcome;
            try
            {
                outcome = await Task.Run(() => PhonePlaylistImport.Analyze(file.Name, file.Content, library));
            }
            catch (Exception ex)
            {
                DebugLog.Write("Import", $"Reading a playlist file failed: {ex.Message}");
                notes.Add(Loc.T("Mobile.Import.Unreadable", file.Name));
                continue;
            }
            outcomes.Add(outcome);
            if (outcome.Matched.Count == 0) continue;
            var playlist = await Library.CreatePlaylistAsync(PhonePlaylistImport.UniqueName(outcome.Name, Library.Playlists.Select(p => p.Name)));
            await Library.AddToPlaylistAsync(playlist, outcome.Matched);
            created.Add(playlist);
        }
        ShowImportResult(outcomes, created, notes);
        return outcomes;
    }

    private void ShowImportResult(IReadOnlyList<PlaylistImportOutcome> outcomes, IReadOnlyList<Playlist> created, List<string> notes)
    {
        var found = outcomes.Sum(o => o.Matched.Count);
        var total = outcomes.Sum(o => o.Total);
        string title;
        if (outcomes.Count == 1)
            title = found == 0
                ? Loc.T("Mobile.Import.NoneFound", outcomes[0].Name)
                : Loc.T("Mobile.Import.Result", found, total);
        else
            title = created.Count > 0
                ? Loc.Plural("Mobile.Import.ResultMany", created.Count)
                : Loc.T("Mobile.Import.NothingImported");

        var details = new List<string>(notes);
        foreach (var o in outcomes)
            foreach (var line in o.Unmatched)
                details.Add(outcomes.Count > 1 ? Loc.T("Mobile.Import.UnmatchedIn", o.Name, line) : line);

        var messages = new List<string>();
        if (outcomes.Count > 1 && total > 0) messages.Add(Loc.T("Mobile.Import.SongsFound", found, total));
        if (outcomes.Any(o => o.Unmatched.Count > 0)) messages.Add(Loc.T("Mobile.Import.NotFoundBelow"));

        var actions = created.Count == 1
            ? new[] { new PromptAction(Loc.T("Mobile.Import.OpenPlaylist"), () => OpenPlaylistCommand.Execute(CurrentPlaylist(created[0]))) }
            : Array.Empty<PromptAction>();
        ShowPrompt(new PromptViewModel(this, title, string.Join(" ", messages), actions)
        {
            Details = details,
            CancelLabel = Loc.T(created.Count == 1 ? "Mobile.Select.Done" : "Mobile.OK"),
        });
    }
}

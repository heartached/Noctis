using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class InboxView : UserControl
{
    private TrackContextMenuBuilder? _menuBuilder;
    /// <summary>Culture the menu's strings were read in (they are read once, at Build).</summary>
    private string? _menuCulture;
    private MenuItem? _addToLibraryItem;
    private ListBoxItem? _menuOwnerItem;

    public InboxView()
    {
        InitializeComponent();

        // Double-click a row to play the Inbox from it.
        TrackList.DoubleTapped += OnTrackDoubleTapped;
        TrackList.SelectionChanged += (_, _) => UpdateSelectionButtons();

        // Right-click anywhere on a row (same wiring as the Folders page).
        TrackList.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ListBoxItem item) return;
            // A row parked by RetainingVirtualizingStackPanel is prepared again without being cleared.
            item.ContextRequested -= OnTrackItemContextRequested;
            item.ContextRequested += OnTrackItemContextRequested;
        };
        TrackList.ContainerClearing += (_, e) =>
        {
            if (e.Container is ListBoxItem item)
            {
                item.ContextRequested -= OnTrackItemContextRequested;
                item.ContextMenu = null;
            }
        };

        DataContextChanged += (_, _) =>
        {
            _menuBuilder?.Reset();
            _menuBuilder = null;
        };
    }

    private InboxViewModel? Vm => DataContext as InboxViewModel;

    private List<Track> SelectedTracks() => TrackList.SelectedItems?.OfType<Track>().ToList() ?? new List<Track>();

    private void UpdateSelectionButtons()
    {
        var any = TrackList.SelectedItems?.Count > 0;
        AddSelectedButton.IsEnabled = any;
        DeleteSelectedButton.IsEnabled = any;
    }

    private async void OnAddSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.AcceptAsync(SelectedTracks());
    }

    private async void OnDeleteSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.DeleteAsync(SelectedTracks());
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        // A double-click on a row's own buttons (Spectrogram, Add) is not a play.
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null)
            return;
        if (Vm is { } vm && TrackList.SelectedItem is Track track)
            vm.PlayTrackCommand.Execute(track);
    }

    private ContextMenu GetOrCreateContextMenu()
    {
        var culture = Loc.Instance.Culture.Name;
        if (_menuBuilder != null && _menuCulture == culture) return _menuBuilder.Menu;
        if (_menuBuilder != null)
        {
            DetachMenuFromOwner();
            _menuBuilder = null;
        }

        if (DataContext is not InboxViewModel) return new ContextMenu();

        _menuBuilder = new TrackContextMenuBuilder();
        _menuCulture = culture;
        var menu = _menuBuilder.Build(Loc.T("Inbox.Delete"), null, this, v2: true, removeIsDanger: true);
        // "Add to Library" first among the rows, under the Play / Queue tiles.
        _addToLibraryItem = MenuV2.Row(this, Loc.T("Inbox.AddSelected"), "MenuLineCheck");
        var at = menu.Items.IndexOf(_menuBuilder.AddToPlaylist);
        menu.Items.Insert(at < 0 ? 0 : at, _addToLibraryItem);
        return menu;
    }

    private void BindContextMenuToTrack(Track track)
    {
        GetOrCreateContextMenu();
        if (Vm is not { } vm || _menuBuilder == null) return;

        _menuBuilder.Bind(
            track,
            playCommand: vm.PlayTrackCommand,
            shuffleCommand: vm.ShuffleAllCommand,
            playNextCommand: vm.PlayNextCommand,
            addToQueueCommand: vm.AddToQueueCommand,
            addToPlaylistCommand: vm.AddToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleFavoriteCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsCommand,
            showInExplorerCommand: vm.ShowInExplorerCommand,
            removeCommand: vm.DeleteTrackCommand,
            convertCommand: vm.ConvertTracksCommand);
        if (_addToLibraryItem != null)
        {
            _addToLibraryItem.Command = vm.AcceptTrackCommand;
            _addToLibraryItem.CommandParameter = track;
        }
    }

    private void DetachMenuFromOwner()
    {
        if (_menuOwnerItem != null)
        {
            _menuOwnerItem.ContextMenu = null;
            _menuOwnerItem = null;
        }
        if (_menuBuilder?.Menu?.Parent is Control parent)
            parent.ContextMenu = null;
    }

    private void OnTrackItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not Track track) return;

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();
        MenuOpenAnimation.CloseNow(menu);

        DetachMenuFromOwner();
        _menuOwnerItem = item;
        item.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(item);
        e.Handled = true;
    }
}

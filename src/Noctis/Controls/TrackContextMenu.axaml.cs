using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Controls;

public partial class TrackContextMenu : ContextMenu
{
    private PlaylistMenuPopulator? _populator;
    public static readonly StyledProperty<Track?> TrackProperty =
        AvaloniaProperty.Register<TrackContextMenu, Track?>(nameof(Track));

    public static readonly StyledProperty<ICommand?> PlayCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(PlayCommand));

    public static readonly StyledProperty<ICommand?> ShuffleCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(ShuffleCommand));

    public static readonly StyledProperty<ICommand?> PlayNextCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(PlayNextCommand));

    public static readonly StyledProperty<ICommand?> AddToQueueCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(AddToQueueCommand));

    public static readonly StyledProperty<ICommand?> AddToNewPlaylistCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(AddToNewPlaylistCommand));

    public static readonly StyledProperty<ICommand?> AddToExistingPlaylistCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(AddToExistingPlaylistCommand));

    public static readonly StyledProperty<IEnumerable<Playlist>?> PlaylistsProperty =
        AvaloniaProperty.Register<TrackContextMenu, IEnumerable<Playlist>?>(nameof(Playlists));

    public static readonly StyledProperty<bool> UsePlaylistOnlyParameterForExistingPlaylistProperty =
        AvaloniaProperty.Register<TrackContextMenu, bool>(nameof(UsePlaylistOnlyParameterForExistingPlaylist));

    public static readonly StyledProperty<ICommand?> ToggleFavoriteCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(ToggleFavoriteCommand));

    public static readonly StyledProperty<ICommand?> MetadataCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(MetadataCommand));

    public static readonly StyledProperty<ICommand?> SearchLyricsCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(SearchLyricsCommand));

    public static readonly StyledProperty<bool> ShowSearchLyricsProperty =
        AvaloniaProperty.Register<TrackContextMenu, bool>(nameof(ShowSearchLyrics), true);

    public static readonly StyledProperty<ICommand?> ViewAlbumCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(ViewAlbumCommand));

    public static readonly StyledProperty<ICommand?> ViewArtistCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(ViewArtistCommand));

    public static readonly StyledProperty<ICommand?> ShowInFolderCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(ShowInFolderCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, ICommand?>(nameof(RemoveCommand));

    public static readonly StyledProperty<bool> ShowRemoveCommandProperty =
        AvaloniaProperty.Register<TrackContextMenu, bool>(nameof(ShowRemoveCommand), true);

    public static readonly StyledProperty<string> RemoveLabelProperty =
        AvaloniaProperty.Register<TrackContextMenu, string>(nameof(RemoveLabel), "Remove from Library");

    public Track? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public ICommand? PlayCommand
    {
        get => GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }

    public ICommand? ShuffleCommand
    {
        get => GetValue(ShuffleCommandProperty);
        set => SetValue(ShuffleCommandProperty, value);
    }

    public ICommand? PlayNextCommand
    {
        get => GetValue(PlayNextCommandProperty);
        set => SetValue(PlayNextCommandProperty, value);
    }

    public ICommand? AddToQueueCommand
    {
        get => GetValue(AddToQueueCommandProperty);
        set => SetValue(AddToQueueCommandProperty, value);
    }

    public ICommand? AddToNewPlaylistCommand
    {
        get => GetValue(AddToNewPlaylistCommandProperty);
        set => SetValue(AddToNewPlaylistCommandProperty, value);
    }

    public ICommand? AddToExistingPlaylistCommand
    {
        get => GetValue(AddToExistingPlaylistCommandProperty);
        set => SetValue(AddToExistingPlaylistCommandProperty, value);
    }

    public IEnumerable<Playlist>? Playlists
    {
        get => GetValue(PlaylistsProperty);
        set => SetValue(PlaylistsProperty, value);
    }

    public bool UsePlaylistOnlyParameterForExistingPlaylist
    {
        get => GetValue(UsePlaylistOnlyParameterForExistingPlaylistProperty);
        set => SetValue(UsePlaylistOnlyParameterForExistingPlaylistProperty, value);
    }

    public ICommand? ToggleFavoriteCommand
    {
        get => GetValue(ToggleFavoriteCommandProperty);
        set => SetValue(ToggleFavoriteCommandProperty, value);
    }

    public ICommand? MetadataCommand
    {
        get => GetValue(MetadataCommandProperty);
        set => SetValue(MetadataCommandProperty, value);
    }

    public ICommand? SearchLyricsCommand
    {
        get => GetValue(SearchLyricsCommandProperty);
        set => SetValue(SearchLyricsCommandProperty, value);
    }

    public bool ShowSearchLyrics
    {
        get => GetValue(ShowSearchLyricsProperty);
        set => SetValue(ShowSearchLyricsProperty, value);
    }

    public ICommand? ViewAlbumCommand
    {
        get => GetValue(ViewAlbumCommandProperty);
        set => SetValue(ViewAlbumCommandProperty, value);
    }

    public ICommand? ViewArtistCommand
    {
        get => GetValue(ViewArtistCommandProperty);
        set => SetValue(ViewArtistCommandProperty, value);
    }

    public ICommand? ShowInFolderCommand
    {
        get => GetValue(ShowInFolderCommandProperty);
        set => SetValue(ShowInFolderCommandProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public bool ShowRemoveCommand
    {
        get => GetValue(ShowRemoveCommandProperty);
        set => SetValue(ShowRemoveCommandProperty, value);
    }

    public string RemoveLabel
    {
        get => GetValue(RemoveLabelProperty);
        set => SetValue(RemoveLabelProperty, value);
    }

    public TrackContextMenu()
    {
        InitializeComponent();
        // Items bind to this instance's StyledProperties, so DataContext must be Self.
        DataContext = this;
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, RoutedEventArgs e)
    {
        if (_populator == null)
        {
            MenuItem? addToPlaylist = null;
            Separator? separator = null;
            foreach (var item in Items)
            {
                if (item is MenuItem mi && mi.Header is string h && h == "Add to Playlist")
                {
                    addToPlaylist = mi;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep) { separator = sep; break; }
                    }
                    break;
                }
            }
            if (addToPlaylist == null || separator == null) return;
            _populator = new PlaylistMenuPopulator(addToPlaylist, separator);
        }

        var playlists = Playlists as ObservableCollection<Playlist>
                        ?? (Playlists != null ? new ObservableCollection<Playlist>(Playlists) : null);

        if (UsePlaylistOnlyParameterForExistingPlaylist)
        {
            _populator.Populate(playlists, AddToExistingPlaylistCommand);
        }
        else
        {
            var track = Track;
            _populator.Populate(playlists, AddToExistingPlaylistCommand,
                playlist => new object[] { track!, playlist });
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

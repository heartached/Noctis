using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Models;

/// <summary>
/// Extended nav item for sidebar playlist entries with artwork and track count display.
/// </summary>
public partial class PlaylistNavItem : NavItem
{
    [ObservableProperty] private int _trackCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomArt))]
    [NotifyPropertyChangedFor(nameof(HasCollageArt))]
    [NotifyPropertyChangedFor(nameof(HasSingleArt))]
    [NotifyPropertyChangedFor(nameof(ShowFallbackIcon))]
    private string? _coverArtPath;

    [ObservableProperty] private string _color = "#808080";

    /// <summary>Up to 4 unique album art paths for collage thumbnail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollageArt))]
    [NotifyPropertyChangedFor(nameof(HasSingleArt))]
    [NotifyPropertyChangedFor(nameof(ShowFallbackIcon))]
    private string? _art1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollageArt))]
    [NotifyPropertyChangedFor(nameof(HasSingleArt))]
    private string? _art2;

    [ObservableProperty] private string? _art3;
    [ObservableProperty] private string? _art4;

    public bool HasCustomArt => !string.IsNullOrEmpty(CoverArtPath);
    public bool HasCollageArt => !HasCustomArt && Art1 != null && Art2 != null;
    public bool HasSingleArt => !HasCustomArt && Art1 != null && Art2 == null;
    public bool ShowFallbackIcon => !HasCustomArt && Art1 == null;

    // ── Sidebar folders & pinning ──

    /// <summary>Pinned playlists sort to the top of the sidebar with a pin glyph.</summary>
    [ObservableProperty] private bool _isPinned;

    /// <summary>Folder this playlist belongs to (empty = root).</summary>
    [ObservableProperty] private string _folder = string.Empty;

    /// <summary>True when this row represents a collapsible folder header, not a playlist.</summary>
    public bool IsFolder { get; init; }

    /// <summary>Folder rows: whether the folder's playlists are currently shown.</summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Playlist rows inside an expanded folder render indented.</summary>
    [ObservableProperty] private bool _isInFolder;
}

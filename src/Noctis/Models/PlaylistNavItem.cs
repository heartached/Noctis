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
}

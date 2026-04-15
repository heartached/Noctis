using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Models;

/// <summary>
/// Represents a navigation entry in the sidebar.
/// </summary>
public partial class NavItem : ObservableObject
{
    /// <summary>Unique key used for routing ("songs", "albums", "artists", "queue", "settings").</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display label shown in the sidebar.</summary>
    [ObservableProperty] private string _label = string.Empty;

    /// <summary>Icon glyph character or path identifier.</summary>
    public string IconGlyph { get; set; } = string.Empty;

    /// <summary>If this is a playlist nav item, stores the playlist ID.</summary>
    public Guid? PlaylistId { get; set; }
}

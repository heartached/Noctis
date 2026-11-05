using Avalonia.Media.Imaging;

namespace Noctis.ViewModels;

/// <summary>
/// One album tile of the Edit Info artwork chooser (Controls/ArtworkChooserCard). Owner 11-04:
/// the Artwork and Animated Artwork search pop-ups are one design, so both result types fill
/// the same tile: the cover, title, artist, an optional detail line and a pill per quality.
/// </summary>
public interface IArtworkChooserItem
{
    Bitmap? Poster { get; }
    string CollectionName { get; }
    string ArtistName { get; }
    /// <summary>"2024 · Clean" — what tells two same-titled tiles apart; empty when unknown.</summary>
    string Detail { get; }
    bool HasDetail { get; }

    /// <summary>The poster plays a preview when clicked (animated artwork only).</summary>
    bool CanPreview { get; }
    /// <summary>The poster's tooltip; null when it has no action.</summary>
    string? PosterTip { get; }
    string? PreviewPath { get; }
    bool IsPreviewing { get; }
    bool IsPreviewLoading { get; }

    /// <summary>One pill per quality, lowest first; the primary one is the accent pill.</summary>
    IReadOnlyList<IArtworkChooserChoice> Choices { get; }
}

/// <summary>A quality pill on a chooser tile ("Standard" / "Max", "1080p" / "2160p").</summary>
public interface IArtworkChooserChoice
{
    string Label { get; }
    bool IsPrimary { get; }
    bool IsSecondary { get; }
}

/// <summary>Standard or Max on a still-artwork tile.</summary>
public sealed record ArtworkSearchChoice(ArtworkSearchResult Owner, bool Highest, string Label) : IArtworkChooserChoice
{
    public bool IsPrimary => Highest;
    public bool IsSecondary => !Highest;
}

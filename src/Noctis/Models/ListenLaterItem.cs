namespace Noctis.Models;

public enum ListenLaterKind
{
    Track,
    Album,
    Artist,
}

/// <summary>
/// A "check this out later" bookmark for a track, album, or artist.
/// Display fields are denormalized so entries survive library rescans
/// and removals; IDs are re-resolved against the library when possible.
/// </summary>
public class ListenLaterItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ListenLaterKind Kind { get; set; }

    /// <summary>Track or album ID, depending on Kind. Unused for artists.</summary>
    public Guid TargetId { get; set; }

    /// <summary>Track title / album name / artist name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Artist credit for tracks and albums; empty for artist entries.</summary>
    public string Subtitle { get; set; } = string.Empty;

    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

using System;

namespace Noctis.Models;

/// <summary>
/// How the large now-playing cover is dressed: the plain square, or the same art printed
/// on a compact disc, a vinyl sleeve with the record peeking out, or a cassette label.
/// </summary>
public enum ArtworkMedium
{
    Cover,
    CompactDisc,
    Vinyl,
    Cassette,
}

public static class ArtworkMediums
{
    /// <summary>Setting value for the plain cover — what every existing install shows.</summary>
    public const string DefaultSetting = nameof(ArtworkMedium.Cover);

    /// <summary>Parses the persisted setting string; anything unknown (hand-edited file,
    /// a value from a newer version) falls back to the plain cover instead of throwing.</summary>
    public static ArtworkMedium Parse(string? setting)
    {
        // By NAME only: Enum.TryParse also accepts "2", which would silently pick a costume.
        var name = setting?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return ArtworkMedium.Cover;
        return Enum.TryParse<ArtworkMedium>(name, ignoreCase: true, out var medium) && Enum.IsDefined(medium)
            ? medium
            : ArtworkMedium.Cover;
    }
}

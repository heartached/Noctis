namespace Noctis.Models;

/// <summary>A piece of track data a Queue panel row can show on one of its lines (GitHub #147).</summary>
public enum QueueRowField
{
    /// <summary>Nothing: an empty extra slot, or no second line at all.</summary>
    None,
    Title,
    Artist,
    Album,
    AlbumArtist,
    Composer,
    Genre,
    Year,
    Duration,
    /// <summary>Short codec name ("FLAC", "MP3").</summary>
    Format,
}

/// <summary>What sits at the right end of a Queue panel row (GitHub #147).</summary>
public enum QueueRowSide
{
    /// <summary>The row's queue position (with the played check / playing speaker): the look before #147.</summary>
    Position,
    Duration,
    None,
}

/// <summary>
/// What each Queue panel row shows (GitHub #147): a first and a second line, each one field
/// plus an optional extra field joined with " · ", and the right-hand slot. The default
/// (Title / Artist / Position) is the queue as it looked before the setting existed.
/// Immutable: the player swaps in a new instance when Settings change, so the rows' bindings
/// re-read it.
/// </summary>
public sealed record QueueRowLayout(
    QueueRowField Line1,
    QueueRowField Line1Extra,
    QueueRowField Line2,
    QueueRowField Line2Extra,
    QueueRowSide Right)
{
    public const string Separator = " · ";

    public static QueueRowLayout Default { get; } =
        new(QueueRowField.Title, QueueRowField.None, QueueRowField.Artist, QueueRowField.None, QueueRowSide.Position);

    /// <summary>Fields the first line can lead with (it always shows something, so no None).</summary>
    public static IReadOnlyList<QueueRowField> Line1Choices { get; } =
        Enum.GetValues<QueueRowField>().Where(f => f != QueueRowField.None).ToArray();

    /// <summary>Fields for the second line and both extra slots (None first).</summary>
    public static IReadOnlyList<QueueRowField> OptionalChoices { get; } = Enum.GetValues<QueueRowField>();

    /// <summary>The second line is the artist credit, so it stays a link to the artist page.</summary>
    public bool Line2IsArtistLink => Line2 == QueueRowField.Artist;

    /// <summary>A second line is drawn at all.</summary>
    public bool HasLine2 => Line2 != QueueRowField.None || Line2Extra != QueueRowField.None;

    /// <summary>A second line that is not the artist link: drawn as plain text.</summary>
    public bool Line2IsPlain => HasLine2 && !Line2IsArtistLink;

    public bool ShowsPosition => Right == QueueRowSide.Position;
    public bool ShowsDuration => Right == QueueRowSide.Duration;
    public bool ShowsRight => Right != QueueRowSide.None;

    /// <summary>Text of line 1 or 2 for <paramref name="track"/>; the empty parts drop out.</summary>
    public string LineText(Track track, int line) => line == 1
        ? Join(Format(track, Line1), Format(track, Line1Extra))
        : Join(Format(track, Line2), Format(track, Line2Extra));

    /// <summary>The value one field shows for <paramref name="track"/>; empty when the tag is blank.</summary>
    public static string Format(Track track, QueueRowField field) => field switch
    {
        QueueRowField.Title => track.TitleDisplay,
        QueueRowField.Artist => track.ArtistDisplay,
        QueueRowField.Album => track.Album,
        QueueRowField.AlbumArtist => ArtistCredit.Display(track.AlbumArtist),
        QueueRowField.Composer => track.Composer,
        QueueRowField.Genre => track.GenreDisplay,
        QueueRowField.Year => track.DisplayYear > 0 ? track.DisplayYear.ToString() : string.Empty,
        QueueRowField.Duration => track.DurationFormatted,
        QueueRowField.Format => track.CodecShortName,
        _ => string.Empty,
    };

    private static string Join(string first, string extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return first ?? string.Empty;
        if (string.IsNullOrWhiteSpace(first)) return extra;
        return first + Separator + extra;
    }

    /// <summary>The stored field; anything unknown is <paramref name="fallback"/>.</summary>
    public static QueueRowField ParseField(string? value, QueueRowField fallback) =>
        Enum.TryParse<QueueRowField>(value, ignoreCase: true, out var field) && Enum.IsDefined(field)
            ? field : fallback;

    /// <summary>The stored right slot; anything unknown is <see cref="QueueRowSide.Position"/>.</summary>
    public static QueueRowSide ParseSide(string? value) =>
        Enum.TryParse<QueueRowSide>(value, ignoreCase: true, out var side) && Enum.IsDefined(side)
            ? side : QueueRowSide.Position;

    /// <summary>Reads the layout from the stored strings. Line 1 never ends up empty: a stored
    /// "None" (or garbage) there reads as Title.</summary>
    public static QueueRowLayout From(string? line1, string? line1Extra, string? line2, string? line2Extra, string? right)
    {
        var first = ParseField(line1, QueueRowField.Title);
        if (first == QueueRowField.None) first = QueueRowField.Title;
        return new QueueRowLayout(
            first,
            ParseField(line1Extra, QueueRowField.None),
            ParseField(line2, QueueRowField.Artist),
            ParseField(line2Extra, QueueRowField.None),
            ParseSide(right));
    }

    public static QueueRowLayout FromSettings(AppSettings s) =>
        From(s.QueueRowLine1, s.QueueRowLine1Extra, s.QueueRowLine2, s.QueueRowLine2Extra, s.QueueRowRight);
}

/// <summary>Width of the Queue panel (GitHub #147): drag its left edge, or Settings ▸ Player ▸ Queue.</summary>
public static class QueuePanelMetrics
{
    /// <summary>The fixed width before #147.</summary>
    public const double Default = 330;

    /// <summary>Narrowest: the header's title and four icon chips still fit beside each other.</summary>
    public const double Min = 300;

    public const double Max = 720;

    /// <summary>A stored or dragged width, in range; NaN / infinity read as the default.</summary>
    public static double Clamp(double width) =>
        double.IsFinite(width) ? Math.Clamp(Math.Round(width), Min, Max) : Default;

    /// <summary>The row title's MaxWidth at <paramref name="width"/>: the 165 the 330 panel
    /// always used, plus whatever the panel grew (or minus what it shrank).</summary>
    public static double TitleMaxWidth(double width) => 165 + (Clamp(width) - Default);
}

using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Text of a queue row's first or second line (GitHub #147). A MultiBinding feeds it the row's
/// <see cref="Track"/> and the player's <see cref="QueueRowLayout"/>, so a Settings change
/// re-renders the realized rows without rebuilding the list. Before the layout binding
/// resolves (a row not yet in the window) the default layout applies: title / artist.
/// A third binding to the track's ArtistDisplay is not read; it only re-renders the row when
/// "Show artists as written in the tags" flips (GitHub #187, Track.NotifyArtistDisplayChanged).
/// GitHub #247: the rows draw a line as its lead (trimmed) and its tail (" · 4:12", never
/// trimmed), so a long title no longer hides the extra field.
/// </summary>
public static class QueueRowConverters
{
    public static readonly IMultiValueConverter Line1 = new LineConverter(1, Part.Whole);
    public static readonly IMultiValueConverter Line2 = new LineConverter(2, Part.Whole);
    public static readonly IMultiValueConverter Line1Lead = new LineConverter(1, Part.Lead);
    public static readonly IMultiValueConverter Line1Tail = new LineConverter(1, Part.Tail);
    public static readonly IMultiValueConverter Line2Lead = new LineConverter(2, Part.Lead);
    public static readonly IMultiValueConverter Line2Tail = new LineConverter(2, Part.Tail);

    private enum Part { Whole, Lead, Tail }

    private sealed class LineConverter(int line, Part part) : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count == 0 || values[0] is not Track track) return string.Empty;
            var layout = values.Count > 1 && values[1] is QueueRowLayout l ? l : QueueRowLayout.Default;
            if (part == Part.Whole) return layout.LineText(track, line);
            var (lead, tail) = layout.LineParts(track, line);
            return part == Part.Lead ? lead : tail;
        }
    }
}

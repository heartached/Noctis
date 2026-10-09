using Avalonia.Data.Converters;
using Noctis.Localization;

namespace Noctis.Helpers;

/// <summary>Album page facts that need a singular/plural form.</summary>
public static class AlbumFacts
{
    /// <summary>
    /// Album page footer: "12 songs, 45 min", or "1 song, 3 min" for a one-song album
    /// (the footer's StringFormat read "1 songs"). Values: track count, formatted length.
    /// </summary>
    public static readonly IMultiValueConverter SongsAndLength = new FuncMultiValueConverter<object?, string>(values =>
    {
        var list = values.ToList();
        var count = list.Count > 0 && list[0] is int n ? n : 0;
        var length = list.Count > 1 ? list[1]?.ToString() ?? string.Empty : string.Empty;
        return count == 1
            ? Loc.T("AlbumDetail.FooterOneSong", length)
            : Loc.T("AlbumDetail.FooterSongs", count, length);
    });
}

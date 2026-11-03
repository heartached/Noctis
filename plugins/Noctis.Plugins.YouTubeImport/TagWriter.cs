namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// Writes the tags a download gets, with TagLib# (the copy Noctis ships: the plugin package
/// carries none). The same fields the app's import wrote: title, artist, album artist, album,
/// year, the video link as the comment, no track/disc number, and the thumbnail as front cover.
/// </summary>
internal static class TagWriter
{
    public static void WriteTags(string path, string title, string artist, string album, int year, string comment)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = title;
        tag.Performers = string.IsNullOrWhiteSpace(artist) ? Array.Empty<string>() : new[] { artist };
        tag.AlbumArtists = tag.Performers;
        tag.Album = album;
        tag.Year = year > 0 ? (uint)year : 0;
        tag.Comment = comment;
        tag.Track = 0;
        tag.Disc = 0;
        file.Save();
    }

    /// <summary>Replaces the front cover (and the ambiguous "Other" picture taggers use for it); other pictures stay.</summary>
    public static void WriteCover(string path, byte[] image)
    {
        using var file = TagLib.File.Create(path);
        var kept = file.Tag.Pictures.Where(p => p.Type is not (TagLib.PictureType.FrontCover or TagLib.PictureType.Other));
        var cover = new TagLib.Picture(new TagLib.ByteVector(image))
        {
            Type = TagLib.PictureType.FrontCover,
            MimeType = HttpBounded.MimeType(image),
        };
        file.Tag.Pictures = kept.Prepend(cover).ToArray();
        file.Save();
    }
}

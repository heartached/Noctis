using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Parsed library search (GitHub #107). Every word must match somewhere, but the words may
/// land in different fields, so "madonna like a prayer" or "rock 1994" find tracks whose
/// artist/title or genre/year together cover the query. Field tags narrow a word to one
/// field and combine with free text:
/// <c>artist:madonna remix</c>, <c>album:"ok computer"</c>, <c>genre:jazz year:1990-1999</c>.
/// <para>
/// Fields: title (song, track), artist (also matches the album artist), albumartist,
/// album, genre, composer, year. Year takes 1994, 1990-1999, &gt;2000, &gt;=2000, &lt;1980, &lt;=1980.
/// An unknown prefix ("Re:Zero") stays plain text, a tag with nothing after it yet
/// ("artist:") is ignored while typing, and a lone "+" or "&amp;" joins words rather than
/// requiring one.
/// </para>
/// </summary>
public sealed class SearchQuery
{
    public enum Field { Any, Title, Artist, AlbumArtist, Album, Genre, Composer, Year }

    public sealed class Term
    {
        public Field Field { get; init; }
        public string Text { get; init; } = string.Empty;
        /// <summary><see cref="SearchText.Normalize"/> of <see cref="Text"/>.</summary>
        public string Key { get; init; } = string.Empty;
        /// <summary>Inclusive year bounds; only set for <see cref="Field.Year"/> terms.</summary>
        public int YearMin { get; init; }
        public int YearMax { get; init; }
    }

    public static readonly SearchQuery Empty = new(string.Empty, new List<Term>());

    private readonly List<Term> _terms;

    /// <summary>The trimmed query as typed.</summary>
    public string Raw { get; }

    /// <summary><see cref="SearchText.Normalize"/> of <see cref="Raw"/>.</summary>
    public string RawKey { get; }

    public IReadOnlyList<Term> Terms => _terms;

    public bool IsEmpty => Raw.Length == 0;

    /// <summary>True when any term is tied to a field (a tag like <c>artist:</c>).</summary>
    public bool HasFieldTerms { get; }

    private SearchQuery(string raw, List<Term> terms)
    {
        Raw = raw;
        RawKey = SearchText.Normalize(raw);
        _terms = terms;
        foreach (var t in terms)
            if (t.Field != Field.Any) { HasFieldTerms = true; break; }
    }

    public static SearchQuery Parse(string? text)
    {
        var raw = text?.Trim() ?? string.Empty;
        if (raw.Length == 0) return Empty;

        var terms = new List<Term>();
        foreach (var (prefix, value, quoted) in Tokenize(raw))
        {
            if (prefix != null && TryParseField(prefix, out var field))
            {
                if (value.Length == 0) continue; // "artist:" mid-typing: no constraint yet
                if (field == Field.Year)
                {
                    if (TryParseYearRange(value, out var min, out var max))
                        terms.Add(new Term { Field = Field.Year, Text = value, YearMin = min, YearMax = max });
                    continue; // "year:19" mid-typing: no constraint yet
                }
                terms.Add(new Term { Field = field, Text = value, Key = SearchText.Normalize(value) });
                continue;
            }

            var word = prefix != null ? prefix + ":" + value : value;
            var key = SearchText.Normalize(word);
            // "+", "&", "-" between words are connectors, not something to find. A quoted
            // phrase is kept even so: the user asked for it literally.
            if (key.Length == 0 && !quoted) continue;
            terms.Add(new Term { Field = Field.Any, Text = word, Key = key });
        }

        return new SearchQuery(raw, terms);
    }

    /// <summary>
    /// True when the track satisfies the query. Without field tags the whole query also
    /// matches as one phrase against title, artist or album (the pre-#107 behaviour, which
    /// catches spacing variants like "taylorswift"); otherwise every term must match.
    /// </summary>
    public bool Matches(Track track)
    {
        if (IsEmpty) return true;

        if (!HasFieldTerms &&
            (SearchText.Matches(track.Title, track.SearchTitleKey, Raw, RawKey) ||
             SearchText.Matches(track.Artist, track.SearchArtistKey, Raw, RawKey) ||
             SearchText.Matches(track.Album, track.SearchAlbumKey, Raw, RawKey)))
            return true;

        if (_terms.Count == 0) return false;

        foreach (var term in _terms)
            if (!MatchesTerm(track, term))
                return false;
        return true;
    }

    /// <summary>
    /// Name-only surfaces (the Artists grid): the whole query, or every plain word plus every
    /// artist/albumartist tag, must match <paramref name="name"/>. Tags for fields a name
    /// doesn't carry (genre, year, …) don't narrow the list.
    /// </summary>
    public bool MatchesName(string? name, string nameKey)
    {
        if (IsEmpty) return true;
        if (!HasFieldTerms && SearchText.Matches(name, nameKey, Raw, RawKey)) return true;

        var any = false;
        foreach (var term in _terms)
        {
            if (term.Field is not (Field.Any or Field.Artist or Field.AlbumArtist)) continue;
            any = true;
            if (!SearchText.Matches(name, nameKey, term.Text, term.Key)) return false;
        }
        return any || HasFieldTerms;
    }

    /// <summary>
    /// Album rows that stand for the release itself (picker dialogs): the whole query against
    /// name or album artist, or every term against the album's own fields — name, album
    /// artist, genre, year. Track-only fields (title, composer) never match here.
    /// </summary>
    public bool MatchesAlbum(Album album)
    {
        if (IsEmpty) return true;
        if (!HasFieldTerms &&
            (SearchText.Matches(album.Name, album.SearchNameKey, Raw, RawKey) ||
             SearchText.Matches(album.Artist, album.SearchArtistKey, Raw, RawKey)))
            return true;

        if (_terms.Count == 0) return false;

        foreach (var term in _terms)
        {
            var ok = term.Field switch
            {
                Field.Album => SearchText.Matches(album.Name, album.SearchNameKey, term.Text, term.Key),
                Field.Artist or Field.AlbumArtist => SearchText.Matches(album.Artist, album.SearchArtistKey, term.Text, term.Key),
                Field.Genre => SearchText.Matches(album.Genre, term.Text),
                Field.Year => album.Year >= term.YearMin && album.Year <= term.YearMax,
                Field.Any => SearchText.Matches(album.Name, album.SearchNameKey, term.Text, term.Key)
                             || SearchText.Matches(album.Artist, album.SearchArtistKey, term.Text, term.Key)
                             || SearchText.Matches(album.Genre, term.Text)
                             || IsYear(album.Year, term.Text),
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>The words to highlight in results: term values without their tag prefix.</summary>
    public IReadOnlyList<string> HighlightTerms
    {
        get
        {
            var list = new List<string>(_terms.Count);
            foreach (var t in _terms)
                if (t.Field != Field.Year) list.Add(t.Text);
            return list;
        }
    }

    private static bool MatchesTerm(Track t, Term term) => term.Field switch
    {
        Field.Title => SearchText.Matches(t.Title, t.SearchTitleKey, term.Text, term.Key),
        Field.Artist => SearchText.Matches(t.Artist, t.SearchArtistKey, term.Text, term.Key)
                        || SearchText.Matches(t.AlbumArtist, t.SearchAlbumArtistKey, term.Text, term.Key),
        Field.AlbumArtist => SearchText.Matches(t.AlbumArtist, t.SearchAlbumArtistKey, term.Text, term.Key),
        Field.Album => SearchText.Matches(t.Album, t.SearchAlbumKey, term.Text, term.Key),
        Field.Genre => SearchText.Matches(t.Genre, t.SearchGenreKey, term.Text, term.Key),
        Field.Composer => SearchText.Matches(t.Composer, term.Text),
        Field.Year => t.Year >= term.YearMin && t.Year <= term.YearMax,
        _ => SearchText.Matches(t.Title, t.SearchTitleKey, term.Text, term.Key)
             || SearchText.Matches(t.Artist, t.SearchArtistKey, term.Text, term.Key)
             || SearchText.Matches(t.Album, t.SearchAlbumKey, term.Text, term.Key)
             || SearchText.Matches(t.AlbumArtist, t.SearchAlbumArtistKey, term.Text, term.Key)
             || SearchText.Matches(t.Genre, t.SearchGenreKey, term.Text, term.Key)
             || IsYear(t.Year, term.Text),
    };

    // A bare four-digit word matches the release year exactly; "99" shouldn't pull in 1999.
    private static bool IsYear(int year, string text) =>
        year > 0 && text.Length == 4 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y == year;

    private static bool TryParseField(string prefix, out Field field)
    {
        field = prefix.ToLowerInvariant() switch
        {
            "title" or "song" or "track" => Field.Title,
            "artist" => Field.Artist,
            "albumartist" => Field.AlbumArtist,
            "album" => Field.Album,
            "genre" => Field.Genre,
            "composer" => Field.Composer,
            "year" => Field.Year,
            _ => Field.Any,
        };
        return field != Field.Any;
    }

    private static bool TryParseYearRange(string value, out int min, out int max)
    {
        min = 0;
        max = 0;
        var v = value.Trim();

        if (v.StartsWith(">=", StringComparison.Ordinal) && TryYear(v[2..], out var y)) { min = y; max = int.MaxValue; return true; }
        if (v.StartsWith("<=", StringComparison.Ordinal) && TryYear(v[2..], out y)) { min = 1; max = y; return true; }
        if (v.StartsWith('>') && TryYear(v[1..], out y)) { min = y + 1; max = int.MaxValue; return true; }
        if (v.StartsWith('<') && TryYear(v[1..], out y)) { min = 1; max = y - 1; return true; }

        var dash = v.IndexOf('-', 1);
        if (dash > 0 && TryYear(v[..dash], out var a) && TryYear(v[(dash + 1)..], out var b))
        {
            (min, max) = a <= b ? (a, b) : (b, a);
            return true;
        }

        if (TryYear(v, out y)) { min = y; max = y; return true; }
        return false;
    }

    private static bool TryYear(string s, out int year) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out year) && year is >= 1000 and <= 9999;

    /// <summary>
    /// Splits on whitespace, keeping "quoted phrases" whole. Yields (prefix, value, quoted):
    /// prefix is the text before the first ':' of a word (null when there is none), so
    /// <c>artist:"pink floyd"</c> yields ("artist", "pink floyd", true).
    /// </summary>
    private static IEnumerable<(string? Prefix, string Value, bool Quoted)> Tokenize(string raw)
    {
        var i = 0;
        var sb = new StringBuilder();
        while (i < raw.Length)
        {
            while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
            if (i >= raw.Length) yield break;

            string? prefix = null;
            if (raw[i] != '"')
            {
                // A tag prefix is letters only, directly followed by ':'.
                var j = i;
                while (j < raw.Length && char.IsLetter(raw[j])) j++;
                if (j > i && j < raw.Length && raw[j] == ':')
                {
                    prefix = raw[i..j];
                    i = j + 1;
                }
            }

            sb.Clear();
            var quoted = false;
            if (i < raw.Length && raw[i] == '"')
            {
                quoted = true;
                i++;
                while (i < raw.Length && raw[i] != '"') sb.Append(raw[i++]);
                if (i < raw.Length) i++; // closing quote (an unclosed one runs to the end)
            }
            else
            {
                while (i < raw.Length && !char.IsWhiteSpace(raw[i])) sb.Append(raw[i++]);
            }

            yield return (prefix, sb.ToString().Trim(), quoted);
        }
    }
}

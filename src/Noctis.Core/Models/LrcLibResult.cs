using System.Text.Json.Serialization;

namespace Noctis.Models;

/// <summary>
/// Represents a lyrics result from the LRCLIB API.
/// </summary>
public class LrcLibResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; set; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("instrumental")]
    public bool Instrumental { get; set; }

    [JsonPropertyName("plainLyrics")]
    public string? PlainLyrics { get; set; }

    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; set; }

    /// <summary>Word-level YAML Lyricsfile (LRCGET v2.0+). Null/empty when not published.</summary>
    [JsonPropertyName("lyricsfile")]
    public string? Lyricsfile { get; set; }

    /// <summary>LRCLIB's own flag: false when its Lyricsfile only times whole lines.</summary>
    [JsonPropertyName("hasWordSync")]
    public bool? HasWordSync { get; set; }

    public bool HasSyncedLyrics => !string.IsNullOrWhiteSpace(SyncedLyrics);
    public bool HasLyricsfile => !string.IsNullOrWhiteSpace(Lyricsfile);

    /// <summary>
    /// A Lyricsfile that actually times words. LRCLIB attaches a Lyricsfile to line-synced
    /// results too (hasWordSync false, no "words:" in it), and counting any Lyricsfile as
    /// word-synced labelled those "Word-synced" in Search Lyrics and let them beat a source
    /// with real word timings for "best match".
    /// </summary>
    public bool HasWordSyncedLyricsfile =>
        HasLyricsfile && HasWordSync != false && Lyricsfile!.Contains("words:", StringComparison.Ordinal);
    public bool HasLyrics => !string.IsNullOrWhiteSpace(PlainLyrics) || HasSyncedLyrics || HasLyricsfile;
}

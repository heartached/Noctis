using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord, Cosmo 11-08: search songs by their lyrics ("include lyrics" / "lyrics only"). The
/// Songs search reads each song's lyrics once into <see cref="LyricsSearchIndex"/> (store-backed
/// lyrics and the online cache's .lrc), case/accent/punctuation-insensitive, LRC tags stripped.
/// </summary>
public class LyricsSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private string CacheDir => Path.Combine(_root, "lyrics_cache");
    private string StoreDir => Path.Combine(_root, "lyrics_store");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Track T(string title, string? plain = null, string? synced = null, string artist = "Artist")
    {
        var t = new Track { Id = Guid.NewGuid(), Title = title, Artist = artist, Album = "Album" };
        if (plain != null) t.Lyrics = plain;
        if (synced != null) t.SyncedLyrics = synced;
        return t;
    }

    private List<Track> Search(List<Track> tracks, string query, LyricsSearchMode mode,
        LyricsSearchIndex index, Dictionary<Track, string>? snippets = null)
    {
        index.Refresh(tracks);
        return LibrarySongsViewModel.BuildFilteredAndSortedTracks(tracks, query, "Title", true, false, "All",
            mode, index, snippets);
    }

    [Fact]
    public void SplitLines_StripsTimestampsWordTagsVoiceMarkersAndMetadata()
    {
        var lines = LyricsSearchIndex.SplitLines(
            "[ar:Someone]\n[ti:Song]\n[00:01.00]v1: <00:01.00>Hello <00:01.50>darkness\n[00:05.00]my old friend\n\n[00:09.00]Hello darkness");
        Assert.Equal(new[] { "Hello darkness", "my old friend" }, lines);
    }

    [Fact]
    public void SplitLines_ReadsAPastedTtmlDocument()
    {
        var lines = LyricsSearchIndex.SplitLines(
            "<?xml version=\"1.0\"?><tt xmlns=\"http://www.w3.org/ns/ttml\"><head><metadata>Agent</metadata></head><body><div>" +
            "<p begin=\"1s\"><span>Hel</span><span>lo</span> world</p><p begin=\"2s\">Rock &amp; roll</p></div></body></tt>");
        Assert.Equal(new[] { "Hello world", "Rock & roll" }, lines);
    }

    [Fact]
    public void IncludeLyrics_FindsTagAndLyricMatches_WithASnippetForLyricOnlyRows()
    {
        var index = new LyricsSearchIndex(null);
        var byTitle = T("Darkness Falls");
        var byLyrics = T("The Sound of Silence", synced: "[00:01.00]Hello darkness, my old friend\n[00:04.00]I've come to talk");
        var neither = T("Other", plain: "Nothing here");
        var snippets = new Dictionary<Track, string>();

        var result = Search(new List<Track> { byTitle, byLyrics, neither }, "darkness", LyricsSearchMode.Include, index, snippets);

        Assert.Equal(new[] { byTitle, byLyrics }, result); // tag matches rank first
        Assert.Equal("Hello darkness, my old friend", snippets[byLyrics]);
        Assert.False(snippets.ContainsKey(byTitle));
    }

    [Fact]
    public void LyricsOnly_IgnoresTagMatches()
    {
        var index = new LyricsSearchIndex(null);
        var byTitle = T("Darkness Falls");
        var byLyrics = T("Silence", plain: "Hello darkness my old friend");

        var result = Search(new List<Track> { byTitle, byLyrics }, "darkness", LyricsSearchMode.Only, index);

        Assert.Equal(new[] { byLyrics }, result);
    }

    [Fact]
    public void Off_DoesNotSearchLyrics()
    {
        var index = new LyricsSearchIndex(null);
        var byLyrics = T("Silence", plain: "Hello darkness my old friend");
        var result = Search(new List<Track> { byLyrics }, "darkness", LyricsSearchMode.Off, index);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("DARKNESS")]          // case
    [InlineData("hello darkness")]    // spaces fold away
    [InlineData("helló dárkness")]    // accents in the query
    [InlineData("dont")]              // apostrophe in the lyrics
    [InlineData("cafe")]              // accents in the lyrics
    public void Matching_IsCaseAccentAndPunctuationInsensitive(string query)
    {
        var index = new LyricsSearchIndex(null);
        var song = T("Song", synced: "[00:01.00]Hello darkness\n[00:02.00]Don't stop at the café");
        Assert.Single(Search(new List<Track> { song }, query, LyricsSearchMode.Only, index));
    }

    [Fact]
    public void TimestampsAreNotSearchable_AndWordsDoNotMatchAcrossLines()
    {
        var index = new LyricsSearchIndex(null);
        var song = T("Song", synced: "[00:01.00]first line ends\n[00:02.00]second line");
        Assert.Empty(Search(new List<Track> { song }, "0001", LyricsSearchMode.Only, index));
        Assert.Empty(Search(new List<Track> { song }, "ends second", LyricsSearchMode.Only, index));
    }

    [Fact]
    public void ShortQueries_DoNotSearchLyrics()
    {
        var index = new LyricsSearchIndex(null);
        var song = T("Song", plain: "oh oh oh");
        Assert.Empty(Search(new List<Track> { song }, "oh", LyricsSearchMode.Only, index));
        Assert.Empty(Search(new List<Track> { song }, "oh", LyricsSearchMode.Include, index));
    }

    [Fact]
    public void EditedLyrics_AreReindexed()
    {
        var index = new LyricsSearchIndex(null);
        var song = T("Song", plain: "old words here");
        var tracks = new List<Track> { song };
        Assert.Single(Search(tracks, "old words", LyricsSearchMode.Only, index));

        song.Lyrics = "brand new text";
        Assert.Empty(Search(tracks, "old words", LyricsSearchMode.Only, index));
        Assert.Single(Search(tracks, "brand new", LyricsSearchMode.Only, index));
    }

    [Fact]
    public void StoreBackedLyrics_AreSearched()
    {
        var store = new LyricsStore(StoreDir);
        var song = T("Song", plain: "words from the store");
        song.CommitLyricsToStore(store); // now read from lyrics_store\{id}.json
        var index = new LyricsSearchIndex(null);

        Assert.Single(Search(new List<Track> { song }, "from the store", LyricsSearchMode.Only, index));
    }

    [Fact]
    public void Peek_DoesNotEnterTheStoreLru()
    {
        var id = Guid.NewGuid();
        new LyricsStore(StoreDir).Write(id, "plain", "");
        var store = new LyricsStore(StoreDir);

        Assert.Equal("plain", store.Peek(id).Plain);
        File.Delete(store.GetPath(id));

        Assert.True(store.Read(id).IsEmpty); // a Read after the Peek went to disk again
    }

    [Fact]
    public void OnlineCacheLrc_IsSearched_ForSongsWithoutTheirOwnLyrics()
    {
        Directory.CreateDirectory(CacheDir);
        var cached = T("Cached");
        var own = T("Own", plain: "my own words");
        File.WriteAllText(Path.Combine(CacheDir, $"{cached.Id}.lrc"), "[00:01.00]words from the internet");
        File.WriteAllText(Path.Combine(CacheDir, $"{own.Id}.lrc"), "[00:01.00]words from the internet");
        var index = new LyricsSearchIndex(CacheDir);

        var result = Search(new List<Track> { cached, own }, "internet", LyricsSearchMode.Only, index);

        Assert.Equal(new[] { cached }, result); // the song's own lyrics win over the cache
    }

    [Fact]
    public void StoredIds_ListsTheStoreFolder()
    {
        var store = new LyricsStore(StoreDir);
        Assert.Empty(store.StoredIds()); // no folder yet
        var id = Guid.NewGuid();
        store.Write(id, "plain", "");
        Assert.Equal(new[] { id }, store.StoredIds());
        Assert.Equal("plain", store.Peek(id).Plain);
    }
}

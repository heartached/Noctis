using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class PlaylistSearchTests
{
    private static Track Make(string title, string artist, string album) =>
        new() { Title = title, Artist = artist, Album = album };

    [Fact]
    public void MatchesSearch_BlankQuery_MatchesEverything()
    {
        Assert.True(PlaylistViewModel.MatchesSearch(Make("X", "Y", "Z"), ""));
        Assert.True(PlaylistViewModel.MatchesSearch(Make("X", "Y", "Z"), "   "));
    }

    [Fact]
    public void MatchesSearch_TitleSubstring_Matches()
    {
        Assert.True(PlaylistViewModel.MatchesSearch(Make("Cruel Summer", "Taylor Swift", "Lover"), "summer"));
    }

    [Fact]
    public void MatchesSearch_ArtistSubstring_Matches()
    {
        Assert.True(PlaylistViewModel.MatchesSearch(Make("Cruel Summer", "Taylor Swift", "Lover"), "taylor"));
    }

    [Fact]
    public void MatchesSearch_AlbumSubstring_Matches()
    {
        // Album is a visible column, so searching by album name should find the track.
        Assert.True(PlaylistViewModel.MatchesSearch(Make("Cruel Summer", "Taylor Swift", "Lover"), "lover"));
    }

    [Fact]
    public void MatchesSearch_NoMatch_ReturnsFalse()
    {
        Assert.False(PlaylistViewModel.MatchesSearch(Make("Cruel Summer", "Taylor Swift", "Lover"), "reputation"));
    }

    [Fact]
    public void MatchesSearch_ApostropheAndAccentInsensitive_Matches()
    {
        var track = Make("Don’t You (Taylor’s Version)", "Beyoncé", "1989");
        Assert.True(PlaylistViewModel.MatchesSearch(track, "dont you"));
        Assert.True(PlaylistViewModel.MatchesSearch(track, "Dont You"));
        Assert.True(PlaylistViewModel.MatchesSearch(track, "beyonce"));
    }
}

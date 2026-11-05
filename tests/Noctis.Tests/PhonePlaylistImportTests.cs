using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Beta tester 11-04: "no M3U playlist import — some people will use Android only". Playlists ⋯
/// › Import Playlist reads .m3u/.m3u8 files with the desktop's parser (now in Noctis.Core) and
/// matches each line to the phone's songs: by file name and the folders above it first (a
/// playlist written on a PC names folders the phone does not have), then by title and artist,
/// the #EXTINF length breaking ties. The result says how many matched and lists the rest.
/// </summary>
public class PhonePlaylistImportTests
{
    // A phone's song: a SAF document URI, as the Android scan stores them.
    private static Track Phone(string title, string artist, string path, int seconds = 200) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = "Album",
        Duration = TimeSpan.FromSeconds(seconds),
        FilePath = "content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3A"
                   + Uri.EscapeDataString(path),
    };

    // ── Parsing ──

    [Fact]
    public void Parse_Extinf_GivesArtistTitleAndLength_AndAttributesDoNotBreakIt()
    {
        var result = PlaylistImportParser.ParseM3u(
            "#EXTM3U\n#EXTINF:215,Adele - Hello\nhello.mp3\n#EXTINF:-1 tvg-id=\"x\",Just A Title\nb.mp3\n#EXTINF:183.5,Coldplay - Yellow\nc.flac\n",
            "List", string.Empty);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(("Adele", "Hello"), (result.Entries[0].Artist, result.Entries[0].Title));
        Assert.Equal(TimeSpan.FromSeconds(215), result.Entries[0].Duration);
        Assert.Equal("Adele - Hello", result.Entries[0].DisplayText);
        Assert.Null(result.Entries[1].Duration);   // -1 = unknown
        Assert.Equal("Just A Title", result.Entries[1].Title);
        Assert.Equal(TimeSpan.FromSeconds(183.5), result.Entries[2].Duration);
    }

    [Fact]
    public void Parse_RelativePaths_StayRelativeWithoutAFolder_AndDotsAreIgnoredByTheMatcher()
    {
        var result = PlaylistImportParser.ParseM3u("../Music/Adele/25/01 Hello.mp3\r\nsub\\02 - Skyfall.flac\r\n", "List", string.Empty);
        Assert.Equal("../Music/Adele/25/01 Hello.mp3", result.Entries[0].FilePath);
        Assert.Equal("Hello", result.Entries[0].Title);   // track number gone
        Assert.Equal("sub/02 - Skyfall.flac", result.Entries[1].FilePath);
        Assert.Equal("Skyfall", result.Entries[1].Title);
        Assert.Equal(new[] { "Music", "Adele", "25", "01 Hello.mp3" }, FuzzyTrackMatcher.PathTail(result.Entries[0].FilePath));
    }

    [Fact]
    public void Parse_WindowsPaths_AreNeverResolvedUnderTheBaseFolder()
    {
        var result = PlaylistImportParser.ParseM3u("C:\\Users\\Me\\Music\\Queen\\Bohemian Rhapsody.mp3\n", "List", "/storage/emulated/0");
        Assert.Equal("C:/Users/Me/Music/Queen/Bohemian Rhapsody.mp3", result.Entries[0].FilePath);
        Assert.Equal("Bohemian Rhapsody", result.Entries[0].Title);
    }

    [Fact]
    public void Parse_UrlEncodedAndFileUris_AreDecoded()
    {
        var result = PlaylistImportParser.ParseM3u(
            "file:///C:/Music/AC%2FDC/Back%20in%20Black.mp3\nMusic/Sigur%20R%C3%B3s/Hopp%C3%ADpolla.flac\n", "List", string.Empty);
        Assert.EndsWith("Back in Black.mp3", result.Entries[0].FilePath);
        Assert.StartsWith("C:/", result.Entries[0].FilePath);
        Assert.Equal("Music/Sigur Rós/Hoppípolla.flac", result.Entries[1].FilePath);
        Assert.Equal("Hoppípolla", result.Entries[1].Title);
    }

    [Fact]
    public void DecodeText_Utf8WithAndWithoutBom_AndLatin1Fallback()
    {
        const string text = "#EXTINF:100,Beyoncé - Déjà Vu\nDéjà Vu.mp3\n";
        var utf8 = Encoding.UTF8.GetBytes(text);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray();
        var latin1 = Encoding.Latin1.GetBytes(text);

        Assert.Equal(text, PlaylistImportParser.DecodeText(utf8));
        Assert.Equal(text, PlaylistImportParser.DecodeText(bom));
        Assert.Equal(text, PlaylistImportParser.DecodeText(latin1));   // invalid UTF-8 → Latin-1
        var parsed = PlaylistImportParser.ParseM3u(PlaylistImportParser.DecodeText(latin1), "x", string.Empty);
        Assert.Equal("Déjà Vu", parsed.Entries[0].Title);
    }

    [Fact]
    public void Parse_ABomLeftInTheText_DoesNotHideTheFirstLine()
    {
        var parsed = PlaylistImportParser.ParseM3u("\uFEFF#EXTM3U\n#EXTINF:10,A - B\nb.mp3\n", "x", string.Empty);
        Assert.Equal("B", Assert.Single(parsed.Entries).Title);
    }

    // ── Matching ──

    [Fact]
    public void Match_ByPathTail_WhenThePcAndPhoneFoldersDiffer()
    {
        var hello = Phone("Hello", "Adele", "Music/Adele/25/01 Hello.mp3");
        var other = Phone("Something Else", "Other", "Music/Other/01 Hello.mp3");   // same file name, other folder
        var lib = new[] { hello, other };
        var outcome = PhonePlaylistImport.Analyze("Mine.m3u8",
            Encoding.UTF8.GetBytes("#EXTM3U\nD:\\Lib\\Adele\\25\\01 Hello.mp3\n"), lib);

        Assert.Same(hello, Assert.Single(outcome.Matched));   // Adele/25 agree, not just the name
        Assert.Empty(outcome.Unmatched);
        Assert.Equal("Mine", outcome.Name);
    }

    [Fact]
    public void Match_ByUniqueFileName_EvenWithNoFoldersInCommon()
    {
        var song = Phone("Skyfall", "Adele", "Download/skyfall-final.flac");
        var outcome = PhonePlaylistImport.Analyze("x.m3u", Encoding.UTF8.GetBytes("/home/me/stuff/skyfall-final.flac\n"), new[] { song });
        Assert.Same(song, Assert.Single(outcome.Matched));
    }

    [Fact]
    public void Match_ByTitleAndArtist_WhenNoFileMatches_TheLengthPicksBetweenTwins()
    {
        var studio = Phone("Hotel California", "Eagles", "Music/a.mp3", seconds: 391);
        var remaster = Phone("Hotel California", "Eagles", "Music/b.mp3", seconds: 430);
        var outcome = PhonePlaylistImport.Analyze("x.m3u",
            Encoding.UTF8.GetBytes("#EXTINF:431,Eagles - Hotel California\nC:/Elsewhere/hc.mp3\n"), new[] { studio, remaster });
        Assert.Same(remaster, Assert.Single(outcome.Matched));
    }

    [Fact]
    public void Match_WithoutExtinf_UsesTheFileNamesTitle()
    {
        var song = Phone("Yellow", "Coldplay", "Music/Coldplay/Parachutes/yellow.m4a");
        var outcome = PhonePlaylistImport.Analyze("x.m3u", Encoding.UTF8.GetBytes("C:\\Rips\\05 - Coldplay - Yellow.mp3\n"), new[] { song });
        Assert.Same(song, Assert.Single(outcome.Matched));
    }

    [Fact]
    public void Unmatched_AreReported_AsExtinfTextOrFileName()
    {
        var hello = Phone("Hello", "Adele", "Music/hello.mp3");
        var outcome = PhonePlaylistImport.Analyze("Road Trip.m3u", Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXTINF:200,Adele - Hello\nhello.mp3\n#EXTINF:300,Led Zeppelin - Kashmir\nC:/x/kashmir.mp3\nC:/x/Unknown Track.ogg\n"),
            new[] { hello });

        Assert.Equal(3, outcome.Total);
        Assert.Single(outcome.Matched);
        Assert.Equal(new[] { "Led Zeppelin - Kashmir", "Unknown Track.ogg" }, outcome.Unmatched);
    }

    [Fact]
    public void UniqueName_AddsANumberWhenTheNameIsTaken()
    {
        Assert.Equal("Mix", PhonePlaylistImport.UniqueName("Mix", new[] { "Other" }));
        Assert.Equal("Mix 3", PhonePlaylistImport.UniqueName("Mix", new[] { "mix", "Mix 2" }));
    }

    [Fact]
    public void OnlyM3uFiles_AreImported()
    {
        Assert.True(PhonePlaylistImport.IsPlaylistFile("a.M3U"));
        Assert.True(PhonePlaylistImport.IsPlaylistFile("a.m3u8"));
        Assert.False(PhonePlaylistImport.IsPlaylistFile("a.pls"));
    }

    // ── The shell: playlists made, the result prompt ──

    private sealed class FakePicker : IPlaylistFilePicker
    {
        public List<PickedPlaylistFile> Files { get; } = new();
        public Task<IReadOnlyList<PickedPlaylistFile>> PickPlaylistFilesAsync() => Task.FromResult<IReadOnlyList<PickedPlaylistFile>>(Files);
    }

    private static ShellViewModel WithPicker(MobileFixtures.Rig rig, FakePicker picker) =>
        new(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
            PlaylistFiles = picker,
        };

    [Fact]
    public async Task Import_MakesThePlaylistNamedAfterTheFile_AndSaysHowManyMatched()
    {
        var a = MobileFixtures.Song("Hello", "Adele");
        var b = MobileFixtures.Song("Yellow", "Coldplay");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var picker = new FakePicker();
        picker.Files.Add(new PickedPlaylistFile("Road Trip.m3u8", Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXTINF:90,Coldplay - Yellow\nC:/m/yellow.mp3\n#EXTINF:90,Adele - Hello\nC:/m/hello.mp3\n#EXTINF:90,Nobody - Nothing\nC:/m/x.mp3\n")));
        var shell = WithPicker(rig, picker);

        // The Playlists page's ⋯ offers Import Playlist only where the phone can pick files.
        Assert.False(rig.Shell.CanImportPlaylists);
        Assert.True(shell.CanImportPlaylists);
        shell.ShowPlaylistsMenuCommand.Execute(null);
        Assert.Equal("Import Playlist", Assert.Single(shell.OptionsMenu!.Items).Label);
        shell.CloseSheet();

        await shell.ImportPlaylistsCommand.ExecuteAsync(null);

        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal("Road Trip", saved.Name);
        Assert.Equal(new[] { b.Id, a.Id }, saved.TrackIds);   // the file's order
        var prompt = shell.Prompt!;
        Assert.Equal("Imported 2 of 3 songs", prompt.Title);
        Assert.Equal(new[] { "Nobody - Nothing" }, prompt.Details);
        Assert.Equal("Open Playlist", Assert.Single(prompt.Actions).Label);
        Assert.Equal("Done", prompt.CancelLabel);

        await prompt.Actions[0].RunCommand.ExecuteAsync(null);
        Assert.Equal("Road Trip", Assert.IsType<SongListPageViewModel>(shell.CurrentPage).Title);
    }

    [Fact]
    public async Task Import_AFileThatMatchesNothing_MakesNoPlaylist_AndSaysSo()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Hello", "Adele") });
        var picker = new FakePicker();
        picker.Files.Add(new PickedPlaylistFile("Empty.m3u", Encoding.UTF8.GetBytes("#EXTINF:1,X - Y\nC:/y.mp3\n")));
        var shell = WithPicker(rig, picker);

        await shell.ImportPlaylistsCommand.ExecuteAsync(null);
        Assert.Empty(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal("None of the songs in “Empty” are on this phone", shell.Prompt!.Title);
        Assert.Empty(shell.Prompt.Actions);
        Assert.Equal("OK", shell.Prompt.CancelLabel);
    }

    [Fact]
    public async Task Import_SeveralFiles_SumsUp_NamesStayUnique_AndOtherFilesAreNoted()
    {
        var a = MobileFixtures.Song("Hello", "Adele");
        var mix = new Playlist { Name = "Mix" };
        using var rig = MobileFixtures.MakeRig(new[] { a }, seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix }));
        var picker = new FakePicker();
        picker.Files.Add(new PickedPlaylistFile("Mix.m3u", Encoding.UTF8.GetBytes("#EXTINF:1,Adele - Hello\nhello.mp3\n")));
        picker.Files.Add(new PickedPlaylistFile("Other.m3u8", Encoding.UTF8.GetBytes("#EXTINF:1,Adele - Hello\nhello.mp3\n#EXTINF:1,X - Gone\ngone.mp3\n")));
        picker.Files.Add(new PickedPlaylistFile("cover.jpg", new byte[] { 1, 2, 3 }));
        var shell = WithPicker(rig, picker);

        await shell.ImportPlaylistsCommand.ExecuteAsync(null);

        var names = (await rig.Persistence.LoadPlaylistsAsync()).Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Mix", "Mix 2", "Other" }, names);
        var prompt = shell.Prompt!;
        Assert.Equal("Imported 2 playlists", prompt.Title);
        Assert.Contains("2 of 3 songs found.", prompt.Message);
        Assert.Contains("“cover.jpg” isn’t an M3U playlist", prompt.Details);
        Assert.Contains("Other: X - Gone", prompt.Details);
    }
}

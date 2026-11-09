using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.MetadataSearch;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord, Cosmo 11-08: "switch a song like instrumental or like remasters". Song menu ▸ Other
/// Versions lists the library's other recordings of a song (same primary artist, same title once
/// version suffixes are stripped) and switches the playing song to the one picked.
/// </summary>
public class SongVersionsTests
{
    private static Track T(string title, string artist = "Oasis", string album = "Album", int year = 0,
        double minutes = 4) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Year = year,
        FilePath = $"C:/m/{Guid.NewGuid():N}.flac",
        Duration = TimeSpan.FromMinutes(minutes),
    };

    // ── Matching ──

    [Theory]
    [InlineData("Wonderwall", "Wonderwall (Remastered)")]
    [InlineData("Wonderwall", "Wonderwall - Remastered 2014")]
    [InlineData("Wonderwall", "Wonderwall [2014 Remaster]")]
    [InlineData("Wonderwall", "Wonderwall (Instrumental)")]
    [InlineData("Wonderwall", "Wonderwall (Live)")]
    [InlineData("Wonderwall", "Wonderwall - Live at Knebworth")]
    [InlineData("Wonderwall", "Wonderwall (Acoustic)")]
    [InlineData("Wonderwall", "Wonderwall (Radio Edit)")]
    [InlineData("Wonderwall", "Wonderwall (Explicit)")]
    [InlineData("Wonderwall", "Wonderwall (Clean)")]
    [InlineData("Wonderwall", "Wonderwall (Deluxe)")]
    [InlineData("Wonderwall", "Wonderwall (Demo)")]
    [InlineData("Wonderwall", "Wonderwall (Mono)")]
    [InlineData("Wonderwall", "Wonderwall (Stereo Mix)")]
    [InlineData("Wonderwall", "Wonderwall (Live) [2014 Remaster]")]
    [InlineData("Wonderwall", "Wonderwall (feat. Someone)")]
    [InlineData("Wonderwall (Instrumental)", "Wonderwall (Live)")]
    [InlineData("Wonderwall", "wonderwall (remastered)")]
    [InlineData("Don't Look Back in Anger", "Dont Look Back In Anger (Remastered)")]
    [InlineData("All Too Well", "All Too Well (Taylor's Version)")]
    public void SameSong_IsAVersion(string a, string b)
    {
        var x = T(a);
        var y = T(b);
        Assert.True(SongVersions.AreVersions(x, y));
        Assert.Contains(y, SongVersions.FindOtherVersions(x, new[] { x, y }));
        Assert.Contains(x, SongVersions.FindOtherVersions(y, new[] { x, y }));
    }

    [Theory]
    [InlineData("Live Forever", "Live")]            // "Live" is the song, not a version marker
    [InlineData("Live Forever", "Forever")]
    [InlineData("Live Forever", "Live Forever Young")]
    [InlineData("Wonderwall", "Wonderwall Part 2")]
    [InlineData("Song (Part 1)", "Song (Part 2)")]
    [InlineData("Instrumental", "Wonderwall")]     // a song called Instrumental
    [InlineData("Acoustic", "Acoustic Song")]
    [InlineData("(Don't Fear) The Reaper", "The Reaper")]
    [InlineData("Intro", "Intro")]                 // every album's Intro is not a version
    [InlineData("Interlude", "Interlude (Live)")]
    [InlineData("Track 3", "Track 3")]
    public void DifferentSong_IsNotAVersion(string a, string b)
    {
        var x = T(a);
        var y = T(b);
        Assert.False(SongVersions.AreVersions(x, y));
        Assert.Empty(SongVersions.FindOtherVersions(x, new[] { x, y }));
    }

    [Fact]
    public void DifferentArtist_IsNotAVersion_ButFeaturedCreditsAre()
    {
        var oasis = T("Wonderwall");
        var cover = T("Wonderwall (Live)", artist: "Ryan Adams");
        var feat = T("Wonderwall (Live)", artist: "Oasis feat. Someone");
        var accent = T("Wonderwall (Acoustic)", artist: "OASIS");

        var found = SongVersions.FindOtherVersions(oasis, new[] { oasis, cover, feat, accent });

        Assert.DoesNotContain(cover, found);
        Assert.Contains(feat, found);
        Assert.Contains(accent, found);
    }

    [Fact]
    public void Diacritics_InArtistAndTitle_AreFolded()
    {
        var a = T("Déjà Vu", artist: "Beyoncé");
        var b = T("Deja Vu (Live)", artist: "Beyonce");
        Assert.True(SongVersions.AreVersions(a, b));
    }

    [Fact]
    public void AmpersandTitles_AreFound()
    {
        var a = T("Rock & Roll");
        var b = T("Rock & Roll (Remastered)");
        Assert.Contains(b, SongVersions.FindOtherVersions(a, new[] { a, b }));
    }

    [Fact]
    public void Itself_SameFile_AndUnknownArtist_AreLeftOut()
    {
        var a = T("Wonderwall");
        var sameFile = T("Wonderwall (Remastered)");
        sameFile.FilePath = a.FilePath;
        var unknownA = T("Song", artist: "Unknown Artist");
        var unknownB = T("Song (Live)", artist: "Unknown Artist");

        Assert.Empty(SongVersions.FindOtherVersions(a, new[] { a, sameFile }));
        Assert.Empty(SongVersions.FindOtherVersions(unknownA, new[] { unknownA, unknownB }));
    }

    [Fact]
    public void DeluxeDuplicate_SameTitleOnAnotherAlbum_IsAVersion()
    {
        var a = T("Wonderwall", album: "(What's the Story) Morning Glory?");
        var b = T("Wonderwall", album: "(What's the Story) Morning Glory? (Deluxe)");
        Assert.Contains(b, SongVersions.FindOtherVersions(a, new[] { a, b }));
    }

    // ── Labels ──

    [Theory]
    [InlineData("Wonderwall", "")]
    [InlineData("Wonderwall (Live)", "Live")]
    [InlineData("Wonderwall - Remastered 2014", "Remastered 2014")]
    [InlineData("Wonderwall (Live) [2014 Remaster]", "Live · 2014 Remaster")]
    [InlineData("Wonderwall (Live) - 2014 Remaster", "Live · 2014 Remaster")]
    [InlineData("Wonderwall (feat. X) (Acoustic)", "Acoustic")]
    public void VersionLabel_IsTheStrippedSuffix(string title, string expected)
        => Assert.Equal(expected, SongVersions.VersionLabel(title));

    [Fact]
    public void AnalyzeTitle_IsUnchanged_ByTheSegmentCapture()
    {
        var (b, markers) = MatchText.AnalyzeTitle("One More Time (Short Radio Edit)");
        Assert.Equal("onemoretime", b);
        Assert.Contains("radio", markers);
        Assert.Contains("edit", markers);
    }

    // ── Switch position rule ──

    [Theory]
    [InlineData(90, 240, 241, 90)]    // remaster: about as long → same spot
    [InlineData(90, 240, 249, 90)]    // within 10 s
    [InlineData(90, 240, 260, 0)]     // 20 s longer → from the top
    [InlineData(90, 600, 625, 90)]    // within 5 % of a long song
    [InlineData(90, 240, 330, 0)]     // live take
    [InlineData(2, 240, 240, 0)]      // barely started
    [InlineData(239, 240, 240, 0)]    // past the new one's end-2 s
    [InlineData(90, 0, 240, 0)]       // unknown length
    [InlineData(90, 240, 0, 0)]
    public void SwitchStartPosition_KeepsThePlaceOnlyForMatchingLengths(double pos, double from, double to, double expected)
        => Assert.Equal(TimeSpan.FromSeconds(expected),
            SongVersions.SwitchStartPosition(TimeSpan.FromSeconds(pos), TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to)));

    // ── Player ──

    private static (PlayerViewModel Vm, FakeAudioPlayer Player) CreatePlayer()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player);
    }

    [AvaloniaFact]
    public void SwitchingThePlayingSong_ReplacesItInPlace_AtTheSamePosition()
    {
        var (vm, player) = CreatePlayer();
        var a = T("A");
        var b = T("Wonderwall", minutes: 4);
        var c = T("C");
        var remaster = T("Wonderwall (Remastered)", minutes: 4.05);
        vm.ReplaceQueueAndPlay(new List<Track> { a, b, c }, 1);
        Dispatcher.UIThread.RunJobs();
        var historyBefore = vm.History.ToList();
        vm.Position = TimeSpan.FromSeconds(95);

        vm.SwitchToVersion(b, remaster);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(remaster, vm.CurrentTrack);
        Assert.Equal(new[] { c }, vm.UpNext.ToArray());
        Assert.Equal(historyBefore, vm.History.ToList()); // the old version is not "played"
        Assert.Equal(95_000, player.PendingSeekMs);
        Assert.Equal(remaster.FilePath, player.PlayedPaths.Last());
    }

    [AvaloniaFact]
    public void SwitchingToAMuchLongerVersion_StartsFromTheTop()
    {
        var (vm, player) = CreatePlayer();
        var b = T("Wonderwall", minutes: 4);
        var live = T("Wonderwall (Live)", minutes: 6);
        vm.ReplaceQueueAndPlay(new List<Track> { b }, 0);
        Dispatcher.UIThread.RunJobs();
        vm.Position = TimeSpan.FromSeconds(95);

        vm.SwitchToVersion(b, live);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(live, vm.CurrentTrack);
        Assert.True(player.PendingSeekMs <= 0);
    }

    [AvaloniaFact]
    public void PickingAVersionFromAnotherRow_PlaysItNow_KeepingTheQueue()
    {
        var (vm, _) = CreatePlayer();
        var a = T("A");
        var c = T("C");
        var other = T("Wonderwall");
        var live = T("Wonderwall (Live)");
        vm.ReplaceQueueAndPlay(new List<Track> { a, c }, 0);
        Dispatcher.UIThread.RunJobs();

        vm.SwitchToVersion(other, live);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(live, vm.CurrentTrack);
        Assert.Contains(c, vm.UpNext);
        Assert.Same(a, vm.History.First());
    }

    [AvaloniaFact]
    public void SwitchingToAMissingFile_KeepsThePlayingSong()
    {
        var (vm, player) = CreatePlayer();
        var b = T("Wonderwall");
        var gone = T("Wonderwall (Live)");
        player.MissingPaths.Add(gone.FilePath);
        vm.ReplaceQueueAndPlay(new List<Track> { b }, 0);
        Dispatcher.UIThread.RunJobs();

        vm.SwitchToVersion(b, gone);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(b, vm.CurrentTrack);
    }

    // ── Menu ──

    [AvaloniaFact]
    public void Menu_ShowsOtherVersions_OnlyWhenThereAreSome()
    {
        var a = T("Wonderwall", album: "Morning Glory", year: 1995);
        var live = T("Wonderwall (Live)", album: "Familiar to Millions", year: 2000);
        var lonely = T("Some Might Say");
        var library = new List<Track> { a, live, lonely };
        var oldSource = TrackContextMenuBuilder.OtherVersionsSource;
        var oldCommand = TrackContextMenuBuilder.SwitchVersionCommand;
        VersionSwitchRequest? picked = null;
        try
        {
            TrackContextMenuBuilder.OtherVersionsSource = t => SongVersions.FindOtherVersions(t, library);
            TrackContextMenuBuilder.SwitchVersionCommand = new RelayCommand<VersionSwitchRequest>(r => picked = r);

            var item = new MenuItem();
            TrackContextMenuBuilder.BindOtherVersions(item, a);
            Assert.True(item.IsVisible);
            var row = Assert.IsType<MenuItem>(Assert.Single(item.Items));
            // Short row; the album is in the tooltip (owner 11-08).
            Assert.Equal("Live · 2000", row.Header);
            Assert.Equal("Live · Familiar to Millions · 2000", ToolTip.GetTip(row));
            // Each row shows its version's album cover (owner 11-08).
            Assert.Contains("mv2-cover", Assert.IsType<Border>(row.Icon).Classes);
            row.Command!.Execute(row.CommandParameter);
            Assert.Same(a, picked!.From);
            Assert.Same(live, picked.To);

            TrackContextMenuBuilder.BindOtherVersions(item, live);
            Assert.Equal("Original · 1995", (string)((MenuItem)item.Items[0]!).Header!);

            // Two rows that would read the same get their album back.
            var live2 = T("Wonderwall (Live)", album: "Knebworth 1996", year: 2000);
            library.Add(live2);
            TrackContextMenuBuilder.BindOtherVersions(item, a);
            var headers = item.Items.Cast<MenuItem>().Select(m => (string)m.Header!).ToList();
            Assert.Contains("Live · Familiar to Millions · 2000", headers);
            Assert.Contains("Live · Knebworth 1996 · 2000", headers);
            library.Remove(live2);

            TrackContextMenuBuilder.BindOtherVersions(item, lonely);
            Assert.False(item.IsVisible);
            Assert.Empty(item.Items);
        }
        finally
        {
            TrackContextMenuBuilder.OtherVersionsSource = oldSource;
            TrackContextMenuBuilder.SwitchVersionCommand = oldCommand;
        }
    }
}

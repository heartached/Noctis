using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioCd;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audio CD support without a drive: path conventions, disc → Track mapping, the
/// service's insert/eject state machine through a fake probe + reader, the player's
/// path classification, the sidebar entry, and the section view-model.
/// The libvlc cdda read itself needs real hardware and is not covered here.
/// </summary>
public class AudioCdTests
{
    // ── Fakes ──

    private sealed class FakeProbe : IAudioCdDriveProbe
    {
        public List<string> Roots { get; set; } = new();
        public bool Ready { get; set; }
        public bool SupportsReadyProbe { get; set; } = true;
        public IReadOnlyList<string> GetOpticalDriveRoots() => Roots.ToList();
        public bool IsDiscReady(string driveRoot) => Ready;
    }

    private sealed class FakeReader : IAudioCdReader
    {
        public AudioCdDisc? Disc { get; set; }
        public int Reads { get; private set; }
        public List<string> Mrls { get; } = new();
        public Task<AudioCdDisc?> ReadAsync(string driveRoot, string mrl, CancellationToken ct = default)
        {
            Reads++;
            Mrls.Add(mrl);
            return Task.FromResult(Disc == null ? null : Disc with { DriveRoot = driveRoot, Mrl = mrl });
        }
    }

    private static AudioCdDisc Disc(int tracks, string? title = null, string? artist = null, bool cdText = false)
    {
        var list = new List<AudioCdTrackInfo>();
        for (var i = 1; i <= tracks; i++)
            list.Add(new AudioCdTrackInfo(i,
                cdText ? $"Song {i}" : null,
                cdText ? "The Band" : null,
                null,
                TimeSpan.FromSeconds(180 + i * 7),
                $"cdda:///D:/#{i}"));
        return new AudioCdDisc(@"D:\", "cdda:///D:/", list, title, artist);
    }

    // ── Paths ──

    [Fact]
    public void DiscMrl_PerPlatform()
    {
        Assert.Equal("cdda:///D:/", AudioCdPaths.BuildDiscMrl(@"D:\", isWindows: true));
        Assert.Equal("cdda:///E:/", AudioCdPaths.BuildDiscMrl("E", isWindows: true));
        Assert.Equal("cdda:///dev/sr0", AudioCdPaths.BuildDiscMrl("/dev/sr0", isWindows: false));
    }

    [Fact]
    public void TrackPath_RoundTrips_AndRejectsGarbage()
    {
        var path = AudioCdPaths.BuildTrackPath("cdda:///D:/", 7);
        Assert.Equal("cdda:///D:/#7", path);
        Assert.True(AudioCdPaths.IsAudioCdPath(path));
        Assert.True(AudioCdPaths.TryParseTrackPath(path, out var mrl, out var n));
        Assert.Equal("cdda:///D:/", mrl);
        Assert.Equal(7, n);

        Assert.False(AudioCdPaths.TryParseTrackPath("cdda:///D:/", out _, out _));      // no track
        Assert.False(AudioCdPaths.TryParseTrackPath("cdda:///D:/#0", out _, out _));    // 1-based
        Assert.False(AudioCdPaths.TryParseTrackPath("cdda:///D:/#x", out _, out _));
        Assert.False(AudioCdPaths.TryParseTrackPath(@"C:\music\a.flac#3", out _, out _));
        Assert.False(AudioCdPaths.IsAudioCdPath("https://music.example.com/rest/stream.view"));
        Assert.False(AudioCdPaths.IsAudioCdPath(null));
    }

    [Fact]
    public void DiscId_DependsOnTrackLengths_NotDriveLetter()
    {
        var a = Disc(5) with { DriveRoot = @"D:\", Mrl = "cdda:///D:/" };
        var b = Disc(5) with { DriveRoot = @"E:\", Mrl = "cdda:///E:/" };
        Assert.Equal(a.DiscId, b.DiscId);
        Assert.NotEqual(Disc(5).DiscId, Disc(6).DiscId);
    }

    // ── Mapping ──

    [Fact]
    public void MapTracks_FallsBackToTrackNumbers_WithoutCdText()
    {
        var tracks = AudioCdService.MapTracks(Disc(3));

        Assert.Equal(3, tracks.Count);
        Assert.Equal("Track 1", tracks[0].Title);
        Assert.Equal("Unknown Artist", tracks[0].Artist);
        Assert.Equal("Audio CD", tracks[0].Album);
        Assert.Equal("cdda:///D:/#2", tracks[1].FilePath);
        Assert.Equal(2, tracks[1].TrackNumber);
        Assert.Equal("2", tracks[1].SourceTrackId);
        Assert.Equal(SourceType.AudioCd, tracks[1].SourceType);
        Assert.True(tracks[1].IsRemoteStream);           // "not a local file" — tag writes and deletes must no-op
        Assert.Equal(TimeSpan.FromSeconds(194), tracks[1].Duration);
        Assert.Equal("CDDA", tracks[1].Codec);
        Assert.Equal(44100, tracks[1].SampleRate);
        Assert.Equal(16, tracks[1].BitsPerSample);
    }

    [Fact]
    public void MapTracks_UsesCdText_WhenPresent()
    {
        var tracks = AudioCdService.MapTracks(Disc(2, title: "Greatest Hits", artist: "The Band", cdText: true));
        Assert.Equal("Song 1", tracks[0].Title);
        Assert.Equal("The Band", tracks[0].Artist);
        Assert.Equal("Greatest Hits", tracks[0].Album);
        Assert.Equal("The Band", tracks[0].AlbumArtist);
        Assert.Equal(tracks[0].AlbumId, tracks[1].AlbumId);
    }

    [Fact]
    public void MapTracks_IdsAreStableAcrossReads_AndDistinctPerTrack()
    {
        var first = AudioCdService.MapTracks(Disc(3));
        var again = AudioCdService.MapTracks(Disc(3));
        Assert.Equal(first.Select(t => t.Id), again.Select(t => t.Id));
        Assert.Equal(3, first.Select(t => t.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("Audio CD - Track 03", null)]
    [InlineData("Track 03", null)]
    [InlineData("Track 3", null)]
    [InlineData("  Come Together ", "Come Together")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Reader_Clean_DropsLibvlcPlaceholderTitles(string? meta, string? expected)
        => Assert.Equal(expected, LibVlcAudioCdReader.Clean(meta));

    // ── Service state machine ──

    [Fact]
    public async Task NoDrive_MeansNoSection_NoReads()
    {
        var probe = new FakeProbe();
        var reader = new FakeReader { Disc = Disc(3) };
        using var svc = new AudioCdService(probe, reader, isWindows: true, isSupported: true);

        Assert.False(svc.HasDrive);
        await svc.RefreshAsync();
        Assert.Null(svc.CurrentDisc);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Unsupported_NeverProbes()
    {
        var probe = new FakeProbe { Roots = { @"D:\" }, Ready = true };
        var reader = new FakeReader { Disc = Disc(3) };
        using var svc = new AudioCdService(probe, reader, isWindows: true, isSupported: false);

        Assert.False(svc.IsSupported);
        Assert.False(svc.HasDrive);
        svc.StartWatching();
        await svc.RefreshAsync();
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Refresh_ReadsTheDisc_UsingThePlatformMrl()
    {
        var probe = new FakeProbe { Roots = { @"D:\" }, Ready = true };
        var reader = new FakeReader { Disc = Disc(4) };
        using var svc = new AudioCdService(probe, reader, isWindows: true, isSupported: true);
        var changed = 0;
        svc.DiscChanged += (_, _) => changed++;

        await svc.RefreshAsync();

        Assert.NotNull(svc.CurrentDisc);
        Assert.Equal(4, svc.CurrentTracks.Count);
        Assert.Equal("cdda:///D:/", reader.Mrls.Single());
        Assert.False(svc.IsReading);
        Assert.True(changed >= 2);   // reading on, disc published (+ reading off)
    }

    [Fact]
    public async Task Poll_InsertThenEject_PublishesAndClears()
    {
        var probe = new FakeProbe { Roots = { @"D:\" }, Ready = false };
        var reader = new FakeReader { Disc = Disc(2) };
        using var svc = new AudioCdService(probe, reader, isWindows: true, isSupported: true);

        svc.Poll();                                   // empty tray: nothing
        Assert.Null(svc.CurrentDisc);
        Assert.Equal(0, reader.Reads);

        probe.Ready = true;
        svc.Poll();                                   // insert → one read
        await WaitUntil(() => svc.CurrentDisc != null);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(2, svc.CurrentTracks.Count);

        svc.Poll();                                   // steady state: no re-read
        Assert.Equal(1, reader.Reads);

        probe.Ready = false;
        svc.Poll();                                   // eject → cleared without a read
        Assert.Null(svc.CurrentDisc);
        Assert.Empty(svc.CurrentTracks);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task Poll_DriveRemoved_ClearsDiscAndRaisesDriveState()
    {
        var probe = new FakeProbe { Roots = { @"D:\" }, Ready = true };
        var reader = new FakeReader { Disc = Disc(2) };
        using var svc = new AudioCdService(probe, reader, isWindows: true, isSupported: true);
        await svc.RefreshAsync();
        Assert.NotNull(svc.CurrentDisc);
        var driveEvents = 0;
        svc.DriveStateChanged += (_, _) => driveEvents++;

        probe.Roots.Clear();
        svc.Poll();

        Assert.False(svc.HasDrive);
        Assert.Null(svc.CurrentDisc);
        Assert.Equal(1, driveEvents);
    }

    [Fact]
    public async Task Linux_NoReadyProbe_ReadsOnlyOnDemand()
    {
        var probe = new FakeProbe { Roots = { "/dev/sr0" }, SupportsReadyProbe = false };
        var reader = new FakeReader { Disc = Disc(2) };
        using var svc = new AudioCdService(probe, reader, isWindows: false, isSupported: true);

        svc.Poll();
        svc.Poll();
        Assert.Equal(0, reader.Reads);                // polling never spins the drive

        await svc.RefreshAsync();
        Assert.Equal("cdda:///dev/sr0", reader.Mrls.Single());
        Assert.Equal(2, svc.CurrentTracks.Count);
    }

    [Fact]
    public async Task DataDisc_ReadsAsNoAudioCd()
    {
        var probe = new FakeProbe { Roots = { @"D:\" }, Ready = true };
        var reader = new FakeReader { Disc = null };
        using var svc = new AudioCdService(probe, reader, isWindows: true, isSupported: true);

        await svc.RefreshAsync();

        Assert.Null(svc.CurrentDisc);
        Assert.Empty(svc.CurrentTracks);
        Assert.Equal(1, reader.Reads);
    }

    // ── Player path classification ──

    [Fact]
    public void Player_ClassifiesCdPaths_AsPathlessMedia()
    {
        Assert.True(VlcAudioPlayer.IsAudioCdPath("cdda:///D:/#1"));
        Assert.True(VlcAudioPlayer.IsPathlessMedia("cdda:///D:/#1"));
        Assert.True(VlcAudioPlayer.IsPathlessMedia("https://x/stream"));
        Assert.False(VlcAudioPlayer.IsRemoteStreamPath("cdda:///D:/#1"));
        Assert.False(VlcAudioPlayer.IsPathlessMedia(@"C:\music\a.flac"));
    }

    // ── Sidebar ──

    [AvaloniaFact]
    public void Sidebar_AudioCdEntry_SitsAfterServer_OrAboveSettings_AndRemovesCleanly()
    {
        var sidebar = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService());

        sidebar.SetAudioCdSectionVisible(true);
        var keys = sidebar.NavItems.Select(i => i.Key).ToList();
        Assert.Equal("cd", keys[keys.IndexOf("settings") - 1]);

        sidebar.SetServerSectionVisible(true);
        keys = sidebar.NavItems.Select(i => i.Key).ToList();
        Assert.Equal(keys.IndexOf("server") + 1, keys.IndexOf("cd"));

        sidebar.SetAudioCdSectionVisible(true);       // idempotent
        Assert.Single(sidebar.NavItems, i => i.Key == "cd");

        sidebar.SetAudioCdSectionVisible(false);
        Assert.DoesNotContain(sidebar.NavItems, i => i.Key == "cd");
        Assert.Contains(sidebar.NavItems, i => i.Key == "server");
    }

    // ── View-model ──

    [AvaloniaFact]
    public async Task ViewModel_TracksStates_AndPlaysThroughTheQueue()
    {
        var probe = new FakeProbe { Roots = { @"D:\" }, Ready = false };
        var reader = new FakeReader { Disc = Disc(3, cdText: true, title: "Live") };
        using var svc = new AudioCdService(probe, reader, isWindows: true, isSupported: true);
        var lib = new FakeLibraryService();
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new AudioCdViewModel(svc, player);

        vm.OnNavigatedTo();
        Assert.True(vm.HasDrive);
        Assert.False(vm.HasDisc);
        Assert.True(vm.ShowEmptyState);
        Assert.Equal("No audio CD in the drive.", vm.StatusText);

        probe.Ready = true;
        await svc.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        vm.Sync();

        Assert.True(vm.ShowTracks);
        Assert.Equal(3, vm.Tracks.Count);
        Assert.Equal("Live", vm.DiscTitle);
        Assert.Contains("3 tracks", vm.DiscMeta);
        Assert.Equal("", vm.StatusText);

        vm.PlayTrackCommand.Execute(vm.Tracks[1]);
        Assert.Equal("cdda:///D:/#2", audio.PlayedPaths.Last());
        Assert.Equal("cdda:///D:/#2", player.CurrentTrack?.FilePath);

        probe.Ready = false;
        svc.Poll();
        Dispatcher.UIThread.RunJobs();
        vm.Sync();
        Assert.False(vm.HasDisc);
        Assert.Empty(vm.Tracks);
    }

    private static async Task WaitUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "condition not met in time");
    }
}

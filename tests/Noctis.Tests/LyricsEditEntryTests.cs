using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #212: add lyrics to a song that has none, or fix wrong ones, from the lyrics page.
/// The page's Add Lyrics / Edit Lyrics opens Edit Info on the song's lyrics: plain text or
/// LRC typed or pasted, timed lyrics fixed in their raw text, saved where Settings ▸ Lyrics ▸
/// Save lyrics to says, and shown on the page straight after the save.
/// </summary>
public class LyricsEditEntryTests : IDisposable
{
    private const string Lrc = "[00:01.00]first line\n[00:02.00]second line";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyricsedit212-" + Guid.NewGuid().ToString("N"));

    public LyricsEditEntryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Lf(string s) => s.Replace("\r\n", "\n").TrimEnd();

    private static async Task<(MetadataViewModel Vm, Track Track)> OpenOnLyrics(string path, LyricsSaveTarget target)
    {
        var track = new MetadataService().ReadTrackMetadata(path)!;
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            TrashFile = p => { File.Delete(p); return true; },
            LyricsSaveTarget = () => target,
        };
        vm.OpenOnLyrics(); // as MetadataHelper.OpenLyricsEditor does, before the load
        await vm.InitializeAsync();
        return (vm, track);
    }

    // ── Edit Info opened from Add Lyrics / Edit Lyrics ──

    [Fact]
    public async Task AddLyrics_OpensOnThePlainLyricsBox_SwitchedOn()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "mp3");
        var (vm, _) = await OpenOnLyrics(path, LyricsSaveTarget.Sidecar);

        Assert.Equal(MetadataViewModel.PlainLyricsTabIndex, vm.SelectedTabIndex);
        Assert.True(vm.HasCustomLyrics); // the box is not greyed out
        Assert.False(vm.PlainLyricsChanged); // opening is no edit
    }

    [Fact]
    public async Task EditLyrics_OnTimedLyrics_OpensOnTheirRawText()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), Lrc);
        var (vm, _) = await OpenOnLyrics(path, LyricsSaveTarget.Sidecar);

        Assert.Equal(MetadataViewModel.SyncedLyricsTabIndex, vm.SelectedTabIndex);
        Assert.True(vm.ShowRawSyncedLyrics);
    }

    [Theory]
    [InlineData(LyricsSaveTarget.Sidecar)]
    [InlineData(LyricsSaveTarget.Embedded)]
    public async Task PastedLrc_InThePlainBox_BecomesTheSyncedLyrics_AndSavesWhereTheSettingSays(LyricsSaveTarget target)
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "mp3");
        var (vm, track) = await OpenOnLyrics(path, target);

        vm.Lyrics = Lrc.Replace("\n", "\r\n"); // one paste

        Assert.Equal(Lrc, vm.SyncedLyrics);
        Assert.True(vm.HasCustomSyncedLyrics);
        Assert.Equal("first line\nsecond line", vm.Lyrics);
        Assert.Equal(2, vm.SyncedLyricLines.Count);
        Assert.Equal(MetadataViewModel.SyncedLyricsTabIndex, vm.SelectedTabIndex);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        Assert.Equal(Lrc, Lf(track.SyncedLyrics));
        var lrcPath = Path.ChangeExtension(path, ".lrc");
        if (target == LyricsSaveTarget.Sidecar)
        {
            Assert.Equal(Lrc, Lf(File.ReadAllText(lrcPath)));
            Assert.True(string.IsNullOrEmpty(EmbeddedLyrics.ReadFile(path).TimedText));
        }
        else
        {
            Assert.False(File.Exists(lrcPath));
            Assert.Equal(Lrc, Lf(EmbeddedLyrics.ReadFile(path).TimedText));
        }
    }

    [Fact]
    public void TypingATimestamp_IsNotAPaste()
    {
        Assert.False(MetadataViewModel.LooksLikePastedLrc("", "[00:01.00]one"));
        Assert.False(MetadataViewModel.LooksLikePastedLrc("[00:01.00]one\n", "[00:01.00]one\n[00:02.00]t"));
        Assert.False(MetadataViewModel.LooksLikePastedLrc("a\nb\nc\nd\ne", "a\nb\nc\nd\ne\n[00:01.00]x\n[00:02.00]y"));
        Assert.True(MetadataViewModel.LooksLikePastedLrc("", "[00:01.00]one\n[00:02.00]two"));
        Assert.True(MetadataViewModel.LooksLikePastedLrc("old words", "[ti:Song]\n[00:01.00]one\n[00:02.00]two\n[00:03.00]three"));
    }

    [Fact]
    public async Task RawSyncedText_IsEditable_AndTheFixSaves()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), Lrc);
        var (vm, track) = await OpenOnLyrics(path, LyricsSaveTarget.Sidecar);

        // A wrong word fixed in the raw text: the Timestamp Lyrics rows follow it.
        var fixedLrc = Lrc.Replace("second line", "second LINE");
        vm.SyncedLyrics = fixedLrc;
        Assert.Equal("second LINE", vm.SyncedLyricLines[1].DisplayText);

        // A row's timing edited afterwards still writes back into the text (no loop).
        vm.SyncedLyricLines[0].TimestampText = "0:01.50";
        Assert.StartsWith("[00:01.50]first line", vm.SyncedLyrics);
        Assert.Equal(2, vm.SyncedLyricLines.Count);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        Assert.Equal("[00:01.50]first line\n[00:02.00]second LINE", Lf(File.ReadAllText(Path.ChangeExtension(path, ".lrc"))));
        Assert.Contains("second LINE", track.SyncedLyrics);
    }

    [AvaloniaFact]
    public void MetadataWindow_TabIndexes_AreTheLyricsTabs()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "T", Artist = "A", Album = "X", FilePath = Path.Combine(_dir, "t.mp3") };
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var window = new Noctis.Views.MetadataWindow(vm);
        var tabs = window.FindControl<TabControl>("MetaTabControl")!;
        var items = tabs.Items.OfType<TabItem>().ToList();
        Assert.Equal(Localization.Loc.T("Metadata.PlainLyrics"), items[MetadataViewModel.PlainLyricsTabIndex].Header);
        Assert.Equal(Localization.Loc.T("Metadata.SyncedLyrics"), items[MetadataViewModel.SyncedLyricsTabIndex].Header);
    }

    // ── The lyrics page's entry points ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private (LyricsViewModel Vm, PlayerViewModel Player, Track Track) MakePage()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new MetadataService(),
            new TestPersistenceService(), new FakeLibraryService())
        {
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = "No Words Yet",
            Artist = "Artist",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(_dir, "song.mp3"),
            SourceType = SourceType.Local,
        };
        player.CurrentTrack = track;
        return (vm, player, track);
    }

    private static async Task PumpUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task SettleAsync(LyricsViewModel vm)
    {
        for (var i = 0; i < 60 && !vm.ShowSearchButton && vm.LyricLines.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Page_AddLyrics_OpensTheEditor_AndShowsTheSavedLyricsAtOnce()
    {
        var (vm, _, track) = MakePage();
        Track? opened = null;
        vm.OpenLyricsEditor = t =>
        {
            opened = t;
            // What Edit Info's Save leaves on the track (and the .lrc beside it).
            t.SyncedLyrics = Lrc;
            t.Lyrics = "first line\nsecond line";
            vm.OnLyricsEdited(t);
            return Task.CompletedTask;
        };
        vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(vm);
        Assert.True(vm.ShowSearchButton);
        Assert.True(vm.CanEditLyrics);
        Assert.Equal("Add Lyrics", vm.EditLyricsLabel);

        await vm.EditLyricsCommand.ExecuteAsync(null);
        await PumpUntilAsync(() => vm.LyricLines.Count > 0);

        Assert.Same(track, opened);
        Assert.False(vm.ShowSearchButton);
        Assert.Equal(new[] { "first line", "second line" }, vm.LyricLines.Where(l => l.Timestamp.HasValue).Select(l => l.Text).ToArray());
        Assert.Equal("Edit Lyrics", vm.EditLyricsLabel);
    }

    [AvaloniaFact]
    public async Task Page_LyricsAddedToASongMarkedAsHavingNone_TakeTheMarkOff()
    {
        var (vm, _, track) = MakePage();
        track.NoLyrics = true;
        vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(vm);

        track.SyncedLyrics = Lrc;
        vm.OnLyricsEdited(track);
        await PumpUntilAsync(() => vm.LyricLines.Count > 0);

        Assert.False(track.NoLyrics);
        Assert.False(vm.IsMarkedNoLyrics);
        Assert.NotEmpty(vm.LyricLines);
    }

    [AvaloniaFact]
    public void Page_NoEditor_ForAStream()
    {
        var (vm, player, _) = MakePage();
        player.CurrentTrack = new Track { Title = "Stream", FilePath = "https://example.invalid/stream", SourceType = SourceType.Jellyfin };
        Assert.False(vm.CanEditLyrics);
    }

    [AvaloniaFact]
    public void SearchDialog_AddLyrics_ClosesTheSearch_ThenOpensTheEditor()
    {
        var (vm, _, track) = MakePage();
        var opened = 0;
        vm.OpenLyricsEditor = _ => { opened++; return Task.CompletedTask; };
        var search = vm.CreateLyricsSearchViewModel(track);
        var closed = false;
        search.Closed += (_, _) => closed = true;

        Assert.True(search.CanAddOwnLyrics);
        search.AddLyricsCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(1, opened);
    }

    [AvaloniaFact]
    public void IslandMenu_EditLyrics_OnTheLyricsPageOnly_LabelFollowsThePage()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = new Track { Title = "T", FilePath = Path.Combine(_dir, "t.mp3"), SourceType = SourceType.Local };
        var ran = 0;
        player.SetEditLyricsAction(() => ran++);

        Assert.False(player.CanEditLyrics);
        player.EditCurrentTrackLyricsCommand.Execute(null);
        Assert.Equal(0, ran);

        player.SetLyricsPageActions(() => { }, () => { }, () => { }, () => { }, () => { },
            isSyncedActive: false, isPlainActive: false, isSyncedAvailable: false, canShare: false);
        Assert.True(player.CanEditLyrics);
        Assert.Equal("Add Lyrics", player.EditLyricsHeader);
        player.UpdateLyricsPageState(isSyncedActive: true, isPlainActive: false, isSyncedAvailable: true, canShare: true);
        Assert.Equal("Edit Lyrics", player.EditLyricsHeader);

        player.EditCurrentTrackLyricsCommand.Execute(null);
        Assert.Equal(1, ran);
    }
}

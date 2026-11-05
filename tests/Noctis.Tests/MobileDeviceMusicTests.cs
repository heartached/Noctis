using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Beta request: "there's no option to scan every folder automatically". Find All Music
/// Automatically takes every song in Android's media index (MediaStore.Audio): which rows are
/// songs, their content URIs, leaving a picked folder's songs to that folder, and the
/// permission flow (asked when turned on, refused, taken away later, allowed in the settings).
/// </summary>
public class MobileDeviceMusicTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string Tree = "content://com.android.externalstorage.documents/tree/";

    internal sealed class FakeDeviceMusic : IDeviceMusic
    {
        public bool Granted;
        public bool GrantOnRequest;
        public int Requests;
        public int SettingsOpened;
        public bool HasPermission => Granted;
        public Task<bool> RequestPermissionAsync()
        {
            Requests++;
            Granted |= GrantOnRequest;
            return Task.FromResult(Granted);
        }
        public void OpenAppSettings() => SettingsOpened++;
        public IReadOnlyList<string> Folders { get; set; } = Array.Empty<string>();
    }

    private static DeviceMusicRow Row(long id, string name = "song.mp3", long durationMs = 200_000, bool music = true,
        bool ringtone = false, bool notification = false, bool alarm = false, bool podcast = false, bool recording = false,
        string? volume = "external_primary", string? relative = "Music/Album/", string? data = null, long size = 1234, long modified = 1_700_000_000) =>
        new(id, name, size, modified, durationMs, music, ringtone, notification, alarm, podcast, recording, volume, relative, data);

    // ── The query's rows → library entries ──

    [Fact]
    public void Select_MapsASongRow_ToItsContentUri_NameSizeAndModified()
    {
        var item = Assert.Single(DeviceMusicScan.Select(new[] { Row(42, "Tone.flac", size: 9876, modified: 1_700_000_000) }, Array.Empty<string>()));
        Assert.Equal("content://media/external/audio/media/42", item.Path);
        Assert.Equal("Tone.flac", item.Name);
        Assert.Equal(9876, item.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime, item.ModifiedUtc);
        Assert.True(DeviceMusicScan.IsDeviceMusicPath(item.Path));
        Assert.True(LibraryService.PathIsUnder(item.Path, DeviceMusicScan.Root));   // removal/unavailable rules see it under the root
    }

    [Fact]
    public void Select_SkipsRingtonesNotificationsAlarmsPodcastsRecordingsAndNonMusic()
    {
        var rows = new[]
        {
            Row(1, "keep.mp3"),
            Row(2, "ring.mp3", ringtone: true),
            Row(3, "ping.ogg", notification: true),
            Row(4, "wake.mp3", alarm: true),
            Row(5, "show.mp3", podcast: true),
            Row(6, "memo.m4a", recording: true),
            Row(7, "clip.mp3", music: false),
            Row(8, "call.m4a", relative: "Recordings/Call/"),        // Android before 12: no IS_RECORDING
            Row(9, "voice.m4a", relative: "Recordings/"),
            Row(10, "doc.pdf"),                                         // not a format Noctis reads
            Row(11, ""),                                                // no name
        };
        Assert.Equal(new[] { "keep.mp3" }, DeviceMusicScan.Select(rows, Array.Empty<string>()).Select(i => i.Name));
    }

    [Theory]
    [InlineData(5_000, false)]
    [InlineData(14_999, false)]
    [InlineData(15_000, true)]
    [InlineData(0, true)]   // the index doesn't know the length: kept, the scan reads it
    public void Select_DropsFilesShorterThanTheMinimum(long durationMs, bool kept) =>
        Assert.Equal(kept, DeviceMusicScan.Select(new[] { Row(1, durationMs: durationMs) }, Array.Empty<string>()).Any());

    [Theory]
    [InlineData("external_primary", "Music/Album/", null, "primary:Music/Album")]
    [InlineData("1a2b-3c4d", "Music/", null, "1a2b-3c4d:Music")]
    [InlineData(null, null, "/storage/emulated/0/Music/Album/x.mp3", "primary:Music/Album")]   // Android 8-9: _data only
    [InlineData(null, null, "/storage/1A2B-3C4D/Music/x.mp3", "1A2B-3C4D:Music")]
    [InlineData(null, null, "/storage/emulated/0/x.mp3", "primary:")]
    [InlineData(null, null, null, null)]
    public void Location_NamesTheFolder_TheWaySafDoes(string? volume, string? relative, string? data, string? expected) =>
        Assert.Equal(expected, DeviceMusicScan.Location(Row(1, volume: volume, relative: relative, data: data)));

    // ── No duplicates with the folders ──

    [Fact]
    public void Select_LeavesSongsInsideAPickedFolder_ToThatFolder()
    {
        var rows = new[]
        {
            Row(1, "a.mp3", relative: "Music/Album/"),
            Row(2, "b.mp3", relative: "Music/"),
            Row(3, "c.mp3", relative: "MusicVideos/"),                       // only starts the same
            Row(4, "d.mp3", relative: "Download/"),
            Row(5, "e.mp3", volume: "1a2b-3c4d", relative: "Songs/"),       // a card folder, picked in upper case
            Row(6, "f.mp3", volume: "9999-0000", relative: "Music/"),       // another card
        };
        var folders = new[] { Tree + "primary%3AMusic", Tree + "1A2B-3C4D%3ASongs" };
        Assert.Equal(new[] { "c.mp3", "d.mp3", "f.mp3" }, DeviceMusicScan.Select(rows, folders).Select(i => i.Name));
    }

    [Fact]
    public void Select_AWholeCardPicked_CoversEverySongOnIt()
    {
        var rows = new[] { Row(1, "a.mp3", volume: "1a2b-3c4d", relative: "Any/Where/"), Row(2, "b.mp3") };
        Assert.Equal(new[] { "b.mp3" }, DeviceMusicScan.Select(rows, new[] { Tree + "1A2B-3C4D%3A" }).Select(i => i.Name));
    }

    [Theory]
    [InlineData(Tree + "primary%3AMusic%2FTones", "primary:Music/Tones")]
    [InlineData(Tree + "1A2B-3C4D%3A", "1A2B-3C4D:")]
    [InlineData("content://com.android.providers.downloads.documents/tree/downloads", null)]   // can't be matched
    [InlineData("C:/Music", null)]
    public void FolderLocation_ReadsTheExternalStorageTree(string folder, string? expected) =>
        Assert.Equal(expected, DeviceMusicScan.FolderLocation(folder));

    // ── The switch, the permission and the scan ──

    private async Task<(LibraryViewModel Vm, FakeLibraryService Library, PersistenceService Persistence)> Make(
        FakeDeviceMusic? device, bool allMusic = false, params string[] folders)
    {
        var persistence = new PersistenceService(_root);
        var settings = await persistence.LoadSettingsAsync();
        settings.MusicFolders.AddRange(folders);
        settings.PhoneAllMusic = allMusic;
        await persistence.SaveSettingsAsync(settings);
        var library = new FakeLibraryService();
        var vm = new LibraryViewModel(library, persistence, new MobileFixtures.NoPicker(), marshal: a => a()) { DeviceMusic = device };
        await vm.InitializeAsync();
        return (vm, library, persistence);
    }

    [Fact]
    public async Task TurningOn_AsksForThePermission_SavesAndScansTheIndexWithTheFolders()
    {
        var device = new FakeDeviceMusic { GrantOnRequest = true };
        var (vm, library, persistence) = await Make(device, folders: Tree + "primary%3AMusic");

        await vm.SetAllMusicAsync(true);

        Assert.Equal(1, device.Requests);
        Assert.True((await persistence.LoadSettingsAsync()).PhoneAllMusic);
        Assert.Equal(new[] { Tree + "primary%3AMusic", DeviceMusicScan.Root }, library.ScanPasses.Last());
        Assert.Equal(new[] { Tree + "primary%3AMusic" }, device.Folders);   // what the index leaves out
        Assert.False(vm.AllMusicDenied);
    }

    [Fact]
    public async Task Refused_StaysOff_SaysWhy_AndOffersTheAppSettings()
    {
        var device = new FakeDeviceMusic { GrantOnRequest = false };
        var (vm, library, persistence) = await Make(device);

        vm.AllMusic = true;                                    // the switch
        for (var i = 0; i < 100 && !vm.AllMusicDenied; i++) await Task.Delay(10);

        Assert.False(vm.AllMusic);
        Assert.True(vm.AllMusicDenied);
        Assert.True(vm.ShowAllMusicHelp);
        Assert.Equal(Noctis.Localization.Loc.T("Mobile.Folders.AllMusicDenied"), vm.AllMusicHelpText);
        Assert.False((await persistence.LoadSettingsAsync()).PhoneAllMusic);
        Assert.Empty(library.ScanPasses);
        Assert.True(vm.ShowConnectCard);                       // still nothing to show: the first-run card stays

        vm.OpenAppSettingsCommand.Execute(null);
        Assert.Equal(1, device.SettingsOpened);

        // Allowed in the system settings: coming back turns it on without another tap.
        device.Granted = true;
        vm.RecheckDeviceMusic();
        for (var i = 0; i < 100 && library.ScanPasses.Count == 0; i++) await Task.Delay(10);
        Assert.True(vm.AllMusic);
        Assert.False(vm.AllMusicDenied);
        Assert.True((await persistence.LoadSettingsAsync()).PhoneAllMusic);
        Assert.Contains(DeviceMusicScan.Root, library.ScanPasses.Last());
    }

    [Fact]
    public async Task PermissionTakenAway_KeepsWalkingTheRoot_SoItsSongsAreKept_AndShowsTheCard()
    {
        var device = new FakeDeviceMusic { Granted = false };
        var (vm, library, _) = await Make(device, allMusic: true);
        library.AbortScanWithRoots = new[] { DeviceMusicScan.Root };   // the real service: root out of reach

        await vm.RescanCommand.ExecuteAsync(null);

        Assert.Contains(DeviceMusicScan.Root, library.ScanPasses.Last());
        Assert.True(vm.AllMusic);
        Assert.True(vm.AllMusicLost);
        Assert.True(vm.ShowAllMusicLost);
        Assert.True(vm.HasNotice);
        Assert.False(vm.NeedsReconnect);                       // not a folder: no Reconnect Folder card
        Assert.Empty(vm.UnavailableFolders);

        device.Granted = true;
        library.AbortScanWithRoots = null;
        var passes = library.ScanPasses.Count;
        vm.RecheckDeviceMusic();
        for (var i = 0; i < 100 && library.ScanPasses.Count == passes; i++) await Task.Delay(10);
        Assert.False(vm.AllMusicLost);
        Assert.True(library.ScanPasses.Count > passes);
    }

    [Fact]
    public async Task TurningOff_SavesAndRescans_EvenWithNoFolderLeft()
    {
        var device = new FakeDeviceMusic { Granted = true };
        var (vm, library, persistence) = await Make(device, allMusic: true);
        Assert.True(vm.AllMusic);
        Assert.True(vm.HasMusic);
        Assert.False(vm.ShowConnectCard);

        await vm.SetAllMusicAsync(false);

        Assert.False((await persistence.LoadSettingsAsync()).PhoneAllMusic);
        Assert.Empty(library.ScanPasses.Last());               // the pass that takes the songs out
    }

    [Fact]
    public async Task WithoutTheHost_TheOptionIsHidden_AndASavedOnIsIgnored()
    {
        var (vm, library, _) = await Make(device: null, allMusic: true);
        Assert.False(vm.CanFindAllMusic);
        Assert.False(vm.AllMusic);
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Empty(library.ScanPasses);                      // no folders, no index: nothing to scan
        Assert.Equal(Noctis.Localization.Loc.T("Mobile.Notice.AddMusicText"), vm.ConnectCardText);
    }

    [Fact]
    public async Task ScanAtStartup_RunsWithOnlyTheIndex()
    {
        var device = new FakeDeviceMusic { Granted = true };
        var (vm, library, _) = await Make(device, allMusic: true);
        await vm.ScanAtStartupAsync();
        Assert.Equal(new[] { DeviceMusicScan.Root }, library.ScanPasses.Single());
    }

    // ── Settings › Music Folders ──

    [AvaloniaFact]
    public async Task MusicFolders_ShowsTheSwitchAboveTheFolders_AndItsRowValue()
    {
        var device = new FakeDeviceMusic { Granted = true };
        using var rig = MobileFixtures.MakeRig(deviceMusic: device);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            vm.OpenPaneCommand.Execute(SettingsPane.Folders);
            MobileFixtures.Land(window);

            var page = MobileFixtures.Find<SettingsPage>(view);
            var toggle = MobileFixtures.Named<ToggleButton>(page, "AllMusicSwitch");
            Assert.True(toggle.IsEffectivelyVisible);
            var cards = MobileFixtures.Named<StackPanel>(page, "FoldersPane");
            Assert.True(cards.Children.IndexOf(MobileFixtures.Named<StackPanel>(page, "AllMusicGroup"))
                        < cards.Children.IndexOf(cards.Children.OfType<StackPanel>().First(p => p.GetVisualDescendants().OfType<ItemsControl>().Any(i => i.Name == "FolderList"))));

            Assert.Equal(Noctis.Localization.Loc.T("Mobile.Folders.NoneValue"), vm.FoldersValue);
            await rig.Shell.Library.SetAllMusicAsync(true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Noctis.Localization.Loc.T("Mobile.Folders.AllMusicValue"), vm.FoldersValue);
            Assert.Equal(Noctis.Localization.Loc.T("Mobile.Folders.WithAllMusic"), vm.FoldersFootnote);
            Assert.True(vm.CanRescan);
            Assert.False(MobileFixtures.Named<StackPanel>(page, "AllMusicHelpGroup").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FirstRun_OffersFindAllMusic_BeforeChoosingAFolder()
    {
        using var rig = MobileFixtures.MakeRig(deviceMusic: new FakeDeviceMusic());
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            Assert.True(MobileFixtures.Named<Button>(view, "FindAllMusicButton").IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<Button>(view, "ChooseFolderButton").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Eyeball probe: the Music Folders page with the switch off, on, and refused, to
    /// NOCTIS_SCAN_SHOTS (NOCTIS_TEST_SKIA=1).</summary>
    [AvaloniaFact]
    public async Task Shots_MusicFoldersSwitch()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_SCAN_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var app = Application.Current!;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        try { FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection()); }
        catch (ArgumentException) { }
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        var theme = new MobileTheme();
        try
        {
            foreach (var name in new[] { "Dark", "Light" })
            {
                theme.Apply(app, name, MobileTheme.DefaultAccent);
                var device = new FakeDeviceMusic();
                using var rig = MobileFixtures.MakeRig(deviceMusic: device, seed: async p =>
                {
                    var s = await p.LoadSettingsAsync();
                    s.MusicFolders.Add(Tree + "1A2B-3C4D%3AMusic");
                    await p.SaveSettingsAsync(s);
                });
                var window = MobileFixtures.Mount(rig.Shell, out _, 412, 1100);
                window.Background = Brushes.Transparent;
                try
                {
                    rig.Shell.OpenSettingsCommand.Execute(null);
                    var settings = (SettingsPageViewModel)rig.Shell.CurrentPage!;
                    await settings.Loaded;
                    settings.Pane = SettingsPane.Folders;
                    Pump(600);
                    Save(window, Path.Combine(dir, $"scan-off-{name}.png"));

                    await rig.Shell.Library.SetAllMusicAsync(true);    // refused (GrantOnRequest false)
                    Pump(300);
                    Save(window, Path.Combine(dir, $"scan-denied-{name}.png"));

                    device.GrantOnRequest = true;
                    await rig.Shell.Library.SetAllMusicAsync(true);
                    Pump(300);
                    Save(window, Path.Combine(dir, $"scan-on-{name}.png"));
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            theme.Remove(app);
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Motion.FinishAll();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string path) => window.CaptureRenderedFrame()!.Save(path);
}

using System.ComponentModel;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin host 1.1: restricted mode, first-enable approval, settings migrations, clean
/// unload, callback containment, and every new hook with its permission gate.
/// </summary>
public class PluginHostApiTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Id = "dev.test.plugin";

    private static string M(params string[] permissions) => PluginSandbox.Manifest(id: Id, permissions: permissions);

    private static (PlayerViewModel Player, FakeAudioPlayer Audio, FakeLibraryService Library) NewPlayer()
    {
        var audio = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        return (player, audio, library);
    }

    private static Track T(string title, string artist = "Artist", string album = "Album") => new()
    {
        Title = title, Artist = artist, Album = album, AlbumArtist = artist,
        Duration = TimeSpan.FromSeconds(200), FilePath = Path.Combine(Path.GetTempPath(), title + ".mp3"),
    };

    // ── Restricted mode and its default ──

    [Fact]
    public void FreshInstall_WithoutPlugins_StartsRestricted()
    {
        var host = _box.NewHost();
        host.LoadAll();
        Assert.False(_box.Settings.CommunityPluginsEnabled);
        Assert.False(host.CommunityPluginsEnabled);
    }

    [AvaloniaFact]
    public void ExistingPlugins_KeepRunning_WhenTheSwitchIsFirstDecided()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Noctis.SamplePlugin.dll"),
            Path.Combine(_box.WriteFolder("PulseRingPlugin", null), "Noctis.SamplePlugin.dll"));
        var host = _box.NewHost();
        host.LoadAll();

        Assert.True(_box.Settings.CommunityPluginsEnabled);
        Assert.Equal(PluginStatus.Running, host.Plugins.Single().Status);
        Assert.True(_box.Settings.PluginPermissionGrants.ContainsKey("PulseRingPlugin"));
        host.UnloadAll();
    }

    [AvaloniaFact]
    public async Task UnrecoverableSettings_LeaveInstalledPluginsRestricted_AndUnapproved()
    {
        // settings.json lost (or reset) while a plugin the user never approved stays on disk.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Noctis.SamplePlugin.dll"),
            Path.Combine(_box.WriteFolder("PulseRingPlugin", null), "Noctis.SamplePlugin.dll"));
        await File.WriteAllTextAsync(Path.Combine(_box.Root, "settings.json"), "<<<not json>>>");
        var settings = await new PersistenceService(_box.Root).LoadSettingsAsync();
        var host = new PluginHost(null, _box.Root, () => settings, () => { }, "1.5.3");
        host.LoadAll();

        Assert.False(settings.CommunityPluginsEnabled);
        Assert.Equal(PluginStatus.Restricted, host.Plugins.Single().Status);
        Assert.False(host.Plugins.Single().IsRunning);
        Assert.Empty(settings.PluginPermissionGrants);
        host.UnloadAll();
    }

    [AvaloniaFact]
    public void RestrictedMode_ListsPlugins_ButRunsNoCode()
    {
        _box.Settings.CommunityPluginsEnabled = false;
        _box.Settings.PluginPermissionGrants[Id] = new List<string>();
        var host = _box.NewHost();
        var created = 0;
        var manifest = PluginManifest.Parse(M());
        var dir = Path.Combine(_box.PluginsDir, Id);
        Directory.CreateDirectory(dir);

        var plugin = host.AddInProcess(dir, manifest, () => { created++; return new ScriptedPlugin(); });

        Assert.Equal(0, created);
        Assert.Equal(PluginStatus.Restricted, plugin.Status);
        Assert.False(plugin.IsRunning);
        Assert.False(plugin.CanToggle);
        Assert.Single(host.Plugins);

        // A real DLL is not even read while restricted.
        _box.WriteFolder("dev.test.real", PluginSandbox.Manifest(id: "dev.test.real", entry: "Real.dll"),
            ("Real.dll", new byte[] { 1, 2, 3 })); // not an assembly: loading it would fail loudly
        host.LoadAll();
        var real = host.Plugins.Single();
        Assert.Equal(PluginStatus.Restricted, real.Status);
        Assert.False(real.HasError);
    }

    [AvaloniaFact]
    public void TurningCommunityPluginsOn_StartsApprovedPlugins_AndOffStopsThem()
    {
        _box.Settings.CommunityPluginsEnabled = false;
        _box.Settings.PluginPermissionGrants["dev.noctis.samples.tracktools"] = new List<string> { "menu.commands", "library.read", "lyrics.provider", "notifications" };
        InstallTrackTools();
        var host = _box.NewHost();
        host.LoadAll();
        Assert.Equal(PluginStatus.Restricted, host.Plugins.Single().Status);
        var changed = 0;
        host.CommunityPluginsChanged += (_, _) => changed++;

        host.SetCommunityPluginsEnabled(true);
        Assert.Equal(PluginStatus.Running, host.Plugins.Single().Status);
        Assert.True(host.Plugins.Single().CanToggle);

        host.SetCommunityPluginsEnabled(false);
        Assert.Equal(PluginStatus.Restricted, host.Plugins.Single().Status);
        Assert.Empty(host.TrackCommands);
        Assert.Equal(2, changed);
    }

    // ── First-enable approval ──

    [AvaloniaFact]
    public void FirstEnable_AsksForApproval_DecliningKeepsItOff_ApprovingStartsIt()
    {
        _box.Settings.CommunityPluginsEnabled = true;
        var host = _box.NewHost();
        var asked = new List<LoadedPlugin>();
        var answer = false;
        host.ConfirmEnable = p => { asked.Add(p); return Task.FromResult(answer); };
        var script = new ScriptedPlugin();
        var plugin = _box.AddInProcess(host, script, M(PluginPermissions.Notifications));

        Assert.Equal(PluginStatus.Disabled, plugin.Status); // never approved: not started
        Assert.Equal(0, script.Initialized);

        plugin.IsEnabled = true; // the user flips the switch
        Assert.Single(asked);
        Assert.False(plugin.IsEnabled);
        Assert.False(plugin.IsRunning);
        Assert.False(_box.Settings.PluginPermissionGrants.ContainsKey(Id));

        answer = true;
        plugin.IsEnabled = true;
        Assert.Equal(2, asked.Count);
        Assert.True(plugin.IsRunning);
        Assert.Equal(new[] { "notifications" }, _box.Settings.PluginPermissionGrants[Id]);

        // Approved once: off and on again does not ask.
        plugin.IsEnabled = false;
        plugin.IsEnabled = true;
        Assert.Equal(2, asked.Count);
        Assert.True(plugin.IsRunning);
    }

    [AvaloniaFact]
    public void UpdateAskingForMorePermissions_WaitsForApprovalAgain()
    {
        _box.Approve((Id, new[] { "notifications" }));
        var host = _box.NewHost();
        var plugin = _box.AddInProcess(host, new ScriptedPlugin(), M(PluginPermissions.Notifications, PluginPermissions.LibraryRead));
        Assert.Equal(PluginStatus.NeedsApproval, plugin.Status);
        Assert.False(plugin.IsRunning);
    }

    // ── Gating by manifest ──

    [AvaloniaFact]
    public void IncompatiblePlugins_AreRefused_WithTheReason()
    {
        _box.Settings.CommunityPluginsEnabled = true;
        _box.WriteFolder("a", PluginSandbox.Manifest(id: "dev.test.api2", apiVersion: "2.0"), ("Test.Plugin.dll", new byte[1]));
        _box.WriteFolder("b", PluginSandbox.Manifest(id: "dev.test.future", minAppVersion: "9.0.0"), ("Test.Plugin.dll", new byte[1]));
        _box.WriteFolder("c", "{ \"id\": 5 }", ("Test.Plugin.dll", new byte[1]));
        _box.WriteFolder("d", PluginSandbox.Manifest(id: "dev.test.nodll"));
        var host = _box.NewHost(appVersion: "1.5.2");
        host.LoadAll();

        var api2 = host.Plugins.Single(p => p.FolderName == "a");
        Assert.Equal(PluginStatus.Incompatible, api2.Status);
        Assert.Contains("plugin API 2.0", api2.Error);
        Assert.False(api2.CanToggle);

        var future = host.Plugins.Single(p => p.FolderName == "b");
        Assert.Equal(PluginStatus.Incompatible, future.Status);
        Assert.Contains("Needs Noctis 9.0.0", future.Error);

        var broken = host.Plugins.Single(p => p.FolderName == "c");
        Assert.Equal(PluginStatus.Invalid, broken.Status);
        Assert.Contains("\"id\" must be a string", broken.Error);

        var noDll = host.Plugins.Single(p => p.FolderName == "d");
        Assert.Equal(PluginStatus.Invalid, noDll.Status);
        Assert.Contains("Test.Plugin.dll", noDll.Error);
    }

    [AvaloniaFact]
    public void DuplicateIds_OnlyTheFirstFolderCounts()
    {
        _box.Settings.CommunityPluginsEnabled = true;
        _box.WriteFolder("one", M(), ("Test.Plugin.dll", new byte[1]));
        _box.WriteFolder("two", M(), ("Test.Plugin.dll", new byte[1]));
        var host = _box.NewHost();
        host.LoadAll();
        var two = host.Plugins.Single(p => p.FolderName == "two");
        Assert.Equal(PluginStatus.Invalid, two.Status);
        Assert.Contains("already uses the id", two.Error);
    }

    // ── Migrations ──

    [AvaloniaFact]
    public void DisabledList_MovesFromFolderNamesToIds()
    {
        _box.Settings.CommunityPluginsEnabled = true;
        _box.Settings.DisabledPlugins.Add("OldFolderName");
        _box.WriteFolder("OldFolderName", M(), ("Test.Plugin.dll", new byte[1]));
        var host = _box.NewHost();
        host.LoadAll();

        Assert.Equal(new[] { Id }, _box.Settings.DisabledPlugins);
        Assert.Equal(PluginStatus.Disabled, host.Plugins.Single().Status);
        Assert.True(_box.Saves > 0);
    }

    [AvaloniaFact]
    public void LegacyDataFolder_MovesOutOfThePluginFolder()
    {
        var dir = _box.WriteFolder("PulseRingPlugin", null, ("data/settings.json", PluginSandbox.Utf8("{\"x\":1}")));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Noctis.SamplePlugin.dll"), Path.Combine(dir, "Noctis.SamplePlugin.dll"));
        var host = _box.NewHost();
        host.LoadAll();

        var plugin = host.Plugins.Single();
        var moved = Path.Combine(_box.DataRoot, "PulseRingPlugin", "settings.json");
        Assert.Equal(Path.Combine(_box.DataRoot, "PulseRingPlugin"), plugin.DataDirectory);
        Assert.True(File.Exists(moved));
        Assert.Equal("{\"x\":1}", File.ReadAllText(moved));
        Assert.False(Directory.Exists(Path.Combine(dir, "data")));
        host.UnloadAll();
    }

    [Fact]
    public void DataMigration_NeverMergesIntoExistingData()
    {
        var old = Path.Combine(_box.Root, "old");
        var fresh = Path.Combine(_box.Root, "new");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(old, "a.txt"), "old");
        File.WriteAllText(Path.Combine(fresh, "a.txt"), "new");

        PluginHost.MigrateDataFolder(old, fresh);

        Assert.Equal("new", File.ReadAllText(Path.Combine(fresh, "a.txt")));
        Assert.True(File.Exists(Path.Combine(old, "a.txt")));
    }

    [AvaloniaFact]
    public void DataFolder_Of11Plugins_ShippedInThePackage_IsLeftAlone()
    {
        _box.Approve((Id, Array.Empty<string>()));
        var dir = _box.WriteFolder(Id, M(), ("Test.Plugin.dll", new byte[1]), ("data/asset.txt", new byte[] { 1 }));
        var host = _box.NewHost();
        host.LoadAll(); // fails to load the fake DLL, which is fine here
        Assert.True(File.Exists(Path.Combine(dir, "data", "asset.txt")));
    }

    // ── Clean unload ──

    [AvaloniaFact]
    public void Disable_DropsEverySubscriptionAndHook_EvenWhenThePluginForgets()
    {
        var (player, audio, _) = NewPlayer();
        _box.Approve((Id, new[] { "playback.control", "menu.commands", "lyrics.provider" }));
        var host = _box.NewHost(player);
        var changes = 0;
        var script = new ScriptedPlugin
        {
            OnInit = h =>
            {
                h.NowPlaying.TrackChanged += (_, _) => changes++; // never unsubscribed
                h.RegisterTrackCommand("Cmd", null, _ => { });
                h.RegisterLyricsProvider(new FixedLyrics("P", null));
                h.OnTrackScrobbled((_, _) => { });
            },
        };
        var subscribersBefore = PropertyChangedSubscribers(player);
        var plugin = _box.AddInProcess(host, script, M("playback.control", "menu.commands", "lyrics.provider"));
        Assert.Equal(subscribersBefore + 1, PropertyChangedSubscribers(player));
        Assert.Single(host.TrackCommands);
        Assert.Single(host.LyricsProviders);
        Assert.True(host.HasScrobbleListeners);

        player.CurrentTrack = T("one");
        Assert.Equal(1, changes);

        var adapter = plugin.Adapter!;
        host.SetEnabled(plugin, false);

        Assert.Equal(1, script.ShutDown);
        Assert.True(adapter.IsDisposed);
        Assert.False(adapter.IsSubscribedToPlayer);
        Assert.Equal(subscribersBefore, PropertyChangedSubscribers(player));
        Assert.Empty(host.TrackCommands);
        Assert.Empty(host.LyricsProviders);
        Assert.False(host.HasScrobbleListeners);

        player.CurrentTrack = T("two");
        Assert.Equal(1, changes);

        // A timer the plugin left behind still holds the host: every call is inert now.
        script.Host!.Playback.PlayPause();
        Assert.Empty(audio.PlayedPaths);
        using var late = script.Host.RegisterTrackCommand("Late", null, _ => { });
        Assert.Empty(host.TrackCommands);
        Assert.Null(script.Host.NowPlaying.Track);
    }

    [AvaloniaFact]
    public void UnloadAll_ReleasesThePlayerSubscriptionOfEveryPlugin()
    {
        var (player, _, _) = NewPlayer();
        _box.Approve((Id, Array.Empty<string>()), ("dev.test.second", Array.Empty<string>()));
        var host = _box.NewHost(player);
        var before = PropertyChangedSubscribers(player);
        _box.AddInProcess(host, new ScriptedPlugin(), M());
        _box.AddInProcess(host, new ScriptedPlugin(), PluginSandbox.Manifest(id: "dev.test.second"));
        Assert.Equal(before + 2, PropertyChangedSubscribers(player));

        host.UnloadAll();
        Assert.Equal(before, PropertyChangedSubscribers(player));
    }

    private static int PropertyChangedSubscribers(ObservableObject o)
    {
        var field = typeof(ObservableObject).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((PropertyChangedEventHandler?)field.GetValue(o))?.GetInvocationList().Length ?? 0;
    }

    // ── Containment ──

    [AvaloniaFact]
    public void ThrowingCallback_MarksThePluginFailed_AndStopsIt()
    {
        var (player, _, _) = NewPlayer();
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(player);
        var calls = 0;
        var script = new ScriptedPlugin { OnInit = h => h.NowPlaying.TrackChanged += (_, _) => { calls++; throw new InvalidOperationException("kaboom"); } };
        var plugin = _box.AddInProcess(host, script, M());

        player.CurrentTrack = T("one"); // must not throw into the player
        Assert.Equal(PluginStatus.Failed, plugin.Status);
        Assert.Contains("kaboom", plugin.Error);
        Assert.False(plugin.IsRunning);
        Assert.Equal(1, script.ShutDown);

        player.CurrentTrack = T("two");
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public void CallbackThatBlocksTheUi_PastTheBudget_StopsThePlugin()
    {
        var (player, _, _) = NewPlayer();
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(player);
        host.CallbackBudget = TimeSpan.FromMilliseconds(40);
        var plugin = _box.AddInProcess(host, new ScriptedPlugin { OnInit = h => h.NowPlaying.TrackChanged += (_, _) => Thread.Sleep(150) }, M());

        player.CurrentTrack = T("one");
        Assert.Equal(PluginStatus.Failed, plugin.Status);
        Assert.Contains("blocked Noctis", plugin.Error);
    }

    [AvaloniaFact]
    public void ThrowingInitialize_IsContained()
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost();
        var plugin = _box.AddInProcess(host, new ScriptedPlugin { OnInit = _ => throw new FormatException("bad init") }, M());
        Assert.Equal(PluginStatus.Failed, plugin.Status);
        Assert.Contains("bad init", plugin.Error);
        Assert.Null(plugin.Adapter);
    }

    // ── Permission gating ──

    [AvaloniaFact]
    public void EveryGatedHook_ThrowsWithoutItsPermission()
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost();
        var script = new ScriptedPlugin();
        _box.AddInProcess(host, script, M()); // declares nothing
        var h = script.Host!;

        Assert.Equal("playback.control", Assert.Throws<PluginPermissionException>(() => h.Playback).Permission);
        Assert.Equal("library.read", Assert.Throws<PluginPermissionException>(() => h.Library).Permission);
        Assert.Equal("notifications", Assert.Throws<PluginPermissionException>(() => h.Notify("x")).Permission);
        Assert.Equal("lyrics.provider", Assert.Throws<PluginPermissionException>(() => h.RegisterLyricsProvider(new FixedLyrics("x", null))).Permission);
        Assert.Equal("menu.commands", Assert.Throws<PluginPermissionException>(() => h.RegisterTrackCommand("x", null, _ => { })).Permission);
        Assert.Equal("menu.commands", Assert.Throws<PluginPermissionException>(() => h.RegisterTrackCommand("x", null, _ => Task.CompletedTask)).Permission);

        // Ungated: now playing, settings, scrobble hook, visual layers, log.
        _ = h.NowPlaying.Track;
        _ = h.Settings.GetString("nope");
        h.OnTrackScrobbled((_, _) => { }).Dispose();
        h.Log("ok");
    }

    [AvaloniaFact]
    public void UsingAnUndeclaredHookInInitialize_FailsThePluginWithTheReason()
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost();
        var plugin = _box.AddInProcess(host, new ScriptedPlugin { OnInit = h => h.Notify("hi") }, M());
        Assert.Equal(PluginStatus.Failed, plugin.Status);
        Assert.Contains("\"notifications\"", plugin.Error);
    }

    // ── Hooks ──

    [AvaloniaFact]
    public void PlaybackControl_DrivesThePlayer()
    {
        var (player, audio, _) = NewPlayer();
        _box.Approve((Id, new[] { "playback.control" }));
        var host = _box.NewHost(player);
        var script = new ScriptedPlugin();
        _box.AddInProcess(host, script, M("playback.control"));
        var track = T("song");
        player.CurrentTrack = track;

        script.Host!.Playback.Play();
        Assert.Equal(new[] { track.FilePath }, audio.PlayedPaths);
        Assert.True(player.IsPlaying);

        script.Host.Playback.Pause();
        Assert.Equal(PlaybackState.Paused, audio.State);
        script.Host.Playback.PlayPause();
        Assert.Equal(PlaybackState.Playing, audio.State);
        script.Host.Playback.Next();      // empty queue: a no-op, not a crash
        script.Host.Playback.Previous();
        script.Host.Playback.Seek(TimeSpan.FromSeconds(30));
    }

    [AvaloniaFact]
    public void LibrarySearch_ReturnsPlainCopies()
    {
        var library = new FakeLibraryService();
        var abbey = T("Come Together", "The Beatles", "Abbey Road");
        abbey.PlayCount = 7;
        abbey.IsFavorite = true;
        library.TrackList.Add(abbey);
        library.TrackList.Add(T("Help!", "The Beatles", "Help!"));
        library.TrackList.Add(T("Café del Mar", "Energy 52", "Café"));
        _box.Approve((Id, new[] { "library.read" }));
        var host = _box.NewHost(library: library);
        var script = new ScriptedPlugin();
        _box.AddInProcess(host, script, M("library.read"));
        var lib = script.Host!.Library;

        Assert.Equal(3, lib.TrackCount);
        var hit = Assert.Single(lib.Search("beatles abbey"));
        Assert.IsType<TrackInfo>(hit);
        Assert.Equal(("Come Together", "The Beatles", "Abbey Road", 7, true), (hit.Title, hit.Artist, hit.Album, hit.PlayCount, hit.IsFavorite));
        Assert.Equal(abbey.Id.ToString("D"), hit.Id);
        Assert.Single(lib.Search("cafe")); // accent-insensitive
        Assert.Equal(2, lib.Search("", limit: 2).Count);
        Assert.Empty(lib.Search("beatles", limit: 0));
        Assert.Equal(2, lib.Search("the beatles", limit: 100_000).Count);

        // Copies: nothing a plugin does to a result reaches the library.
        var copy = hit with { Title = "changed" };
        Assert.Equal("Come Together", abbey.Title);
        Assert.NotEqual(copy, hit);
    }

    [AvaloniaFact]
    public async Task LyricsProvider_AnswersAreMapped_AndFailuresContained()
    {
        _box.Approve((Id, new[] { "lyrics.provider" }));
        var host = _box.NewHost();
        host.LyricsTimeout = TimeSpan.FromMilliseconds(300);
        var provider = new FixedLyrics("My Lyrics", new PluginLyrics("[00:01.00]hello", null));
        var script = new ScriptedPlugin { OnInit = h => h.RegisterLyricsProvider(provider) };
        var plugin = _box.AddInProcess(host, script, M("lyrics.provider"));

        var source = Assert.Single(host.LyricsProviders);
        Assert.Equal("My Lyrics", source.Name);
        Assert.Contains("Lyrics: My Lyrics", plugin.Extensions);
        var result = await source.FindAsync("Artist", "Title", "Album", TimeSpan.FromSeconds(200));
        Assert.Equal("[00:01.00]hello", result!.SyncedLyrics);
        Assert.Equal(("Artist", "Title", "Album"), (provider.Last!.Artist, provider.Last.Title, provider.Last.Album));

        provider.Throw = true;
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => source.FindAsync("a", "t", "", TimeSpan.Zero));
        Assert.Contains("provider failed", ex.Message);
        Assert.True(plugin.IsRunning); // transport-style failures do not fail the plugin

        provider.Throw = false;
        provider.Hang = true; // ignores cancellation, never answers
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timeout = await Assert.ThrowsAsync<LyricsProviderException>(() => source.FindAsync("a", "t", "", TimeSpan.Zero));
        Assert.IsType<TimeoutException>(timeout.InnerException);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));

        provider.Hang = false;
        provider.BlockSync = true; // blocks before its first await: must not block the caller
        sw.Restart();
        var pending = source.FindAsync("a", "t", "", TimeSpan.Zero);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"caller blocked {sw.ElapsedMilliseconds} ms");
        await Assert.ThrowsAsync<LyricsProviderException>(() => pending);
    }

    [AvaloniaFact]
    public void TrackCommand_GetsACopyOfTheTrack_AndAThrowFailsThePlugin()
    {
        _box.Approve((Id, new[] { "menu.commands" }));
        var host = _box.NewHost();
        TrackInfo? got = null;
        var boom = false;
        var script = new ScriptedPlugin
        {
            OnInit = h => h.RegisterTrackCommand("Inspect", "M0 0L10 10", t => { if (boom) throw new IOException("disk"); got = t; }),
        };
        var plugin = _box.AddInProcess(host, script, M("menu.commands"));
        var command = Assert.Single(host.TrackCommands);
        Assert.Equal(("Inspect", "M0 0L10 10", "Test Plugin"), (command.Label, command.Icon, command.PluginName));

        var track = T("Song", "Band", "Record");
        command.Execute(track);
        Assert.Equal(("Song", "Band", "Record", track.FilePath), (got!.Title, got.Artist, got.Album, got.FilePath));

        boom = true;
        command.Execute(track);
        Assert.Equal(PluginStatus.Failed, plugin.Status);
        Assert.Contains("disk", plugin.Error);
        Assert.Empty(host.TrackCommands);
    }

    [AvaloniaFact]
    public async Task AsyncTrackCommand_FaultAfterAwait_FailsThePlugin()
    {
        _box.Approve((Id, new[] { "menu.commands" }));
        var host = _box.NewHost();
        var script = new ScriptedPlugin
        {
            OnInit = h => h.RegisterTrackCommand("Later", null, async _ => { await Task.Delay(10); throw new InvalidOperationException("late failure"); }),
        };
        var plugin = _box.AddInProcess(host, script, M("menu.commands"));
        host.TrackCommands.Single().Execute(T("x"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (plugin.Status != PluginStatus.Failed && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.Equal(PluginStatus.Failed, plugin.Status);
        Assert.Contains("late failure", plugin.Error);
    }

    [AvaloniaFact]
    public void TrackMenu_ShowsPluginCommands_OnEveryBind()
    {
        _box.Approve((Id, new[] { "menu.commands" }));
        var host = _box.NewHost();
        var ran = 0;
        _box.AddInProcess(host, new ScriptedPlugin { OnInit = h => h.RegisterTrackCommand("Plugin action", null, _ => ran++) }, M("menu.commands"));
        var previous = Noctis.Helpers.TrackContextMenuBuilder.PluginCommandSource;
        Noctis.Helpers.TrackContextMenuBuilder.PluginCommandSource = () => host.TrackCommands;
        try
        {
            var builder = new Noctis.Helpers.TrackContextMenuBuilder();
            var resources = new Avalonia.Controls.Border();
            foreach (var key in new[] { "HeartFillIcon", "StarIcon", "TrashIcon" })
                resources.Resources[key] = Avalonia.Media.Geometry.Parse("M0 0L1 1");
            builder.Build("Remove from Library", null, resources);
            var none = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
            var track = T("x");
            void Bind() => builder.Bind(track, none, none, none, none, none, none, none, none, none, none);

            Bind();
            Bind(); // rebinding must not duplicate
            var items = builder.Menu.Items.OfType<Avalonia.Controls.MenuItem>().Where(i => (i.Header as string) == "Plugin action").ToList();
            Assert.Single(items);
            items[0].Command!.Execute(null);
            Assert.Equal(1, ran);

            host.UnloadAll();
            Bind();
            Assert.DoesNotContain(builder.Menu.Items.OfType<Avalonia.Controls.MenuItem>(), i => (i.Header as string) == "Plugin action");
        }
        finally { Noctis.Helpers.TrackContextMenuBuilder.PluginCommandSource = previous; }
    }

    [AvaloniaFact]
    public void ScrobbleHook_ReceivesTheTrackAndStartTime()
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost();
        var got = new List<(TrackInfo Track, DateTimeOffset At)>();
        var script = new ScriptedPlugin { OnInit = h => h.OnTrackScrobbled((t, at) => got.Add((t, at))) };
        var plugin = _box.AddInProcess(host, script, M());
        Assert.True(host.HasScrobbleListeners);

        var started = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        host.RaiseTrackScrobbled(T("Listened"), started);
        var (track, at) = Assert.Single(got);
        Assert.Equal("Listened", track.Title);
        Assert.Equal(new DateTimeOffset(started), at);

        host.SetEnabled(plugin, false);
        host.RaiseTrackScrobbled(T("Again"), started);
        Assert.Single(got);
    }

    [AvaloniaFact]
    public void DeclaredSettings_HaveDefaults_PersistChanges_AndNotifyThePlugin()
    {
        _box.Approve((Id, Array.Empty<string>()));
        const string settings = """
            [
              { "key": "greeting", "label": "Greeting", "type": "string", "default": "hello" },
              { "key": "loud", "label": "Loud", "type": "bool", "default": false },
              { "key": "count", "label": "Count", "type": "number", "default": 3, "min": 1, "max": 5 },
              { "key": "mode", "label": "Mode", "type": "choice", "choices": ["soft", "hard"], "default": "soft" }
            ]
            """;
        var manifest = PluginSandbox.Manifest(id: Id, settingsJson: settings);
        var host = _box.NewHost();
        var changed = new List<string>();
        var script = new ScriptedPlugin { OnInit = h => h.Settings.Changed += (_, key) => changed.Add(key) };
        var plugin = _box.AddInProcess(host, script, manifest);
        var s = script.Host!.Settings;

        Assert.Equal("hello", s.GetString("greeting"));
        Assert.False(s.GetBool("loud"));
        Assert.Equal(3, s.GetNumber("count"));
        Assert.Equal("soft", s.GetString("mode"));
        Assert.True(plugin.HasSettings);
        Assert.Equal(4, plugin.SettingItems.Count);

        var saves = _box.Saves;
        plugin.SettingItems.Single(i => i.Key == "loud").BoolValue = true;
        plugin.SettingItems.Single(i => i.Key == "count").NumberValue = 99; // clamped to max
        plugin.SettingItems.Single(i => i.Key == "mode").SelectedChoice = "hard";
        plugin.SettingItems.Single(i => i.Key == "mode").SelectedChoice = "not-a-choice"; // ignored

        Assert.True(s.GetBool("loud"));
        Assert.Equal(5, s.GetNumber("count"));
        Assert.Equal("hard", s.GetString("mode"));
        Assert.Equal(new[] { "loud", "count", "mode" }, changed);
        Assert.Equal(saves + 3, _box.Saves);
        Assert.Equal("true", _box.Settings.PluginSettingValues[Id]["loud"]);

        // A new host (next launch) reads the stored values.
        host.UnloadAll();
        var again = _box.NewHost();
        var script2 = new ScriptedPlugin();
        _box.AddInProcess(again, script2, manifest);
        Assert.True(script2.Host!.Settings.GetBool("loud"));
        Assert.Equal("hard", script2.Host.Settings.GetString("mode"));
    }

    [AvaloniaFact]
    public void Notify_RaisesANotice_ThrottledPerPlugin()
    {
        _box.Approve((Id, new[] { "notifications" }));
        var host = _box.NewHost();
        var notices = new List<PluginNotice>();
        host.NotificationRequested += (_, n) => notices.Add(n);
        var script = new ScriptedPlugin();
        _box.AddInProcess(host, script, M("notifications"));

        script.Host!.Notify("first");
        script.Host.Notify("second"); // within a second: dropped
        script.Host.Notify("   ");
        var n = Assert.Single(notices);
        Assert.Equal(("Test Plugin", "first"), (n.PluginName, n.Message));
    }

    [AvaloniaFact]
    public void DataDirectory_IsOutsideThePluginFolder_AndCreated()
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost();
        var script = new ScriptedPlugin();
        var plugin = _box.AddInProcess(host, script, M());
        Assert.Equal(Path.Combine(_box.DataRoot, Id), script.Host!.DataDirectory);
        Assert.True(Directory.Exists(script.Host.DataDirectory));
        Assert.Equal(plugin.Directory, script.Host.PluginDirectory);
        Assert.Equal("1.5.3", script.Host.AppVersion);
    }

    // ── The real sample through the isolated loader ──

    [AvaloniaFact]
    public async Task TrackToolsSample_LoadsFromItsFolder_AndRegistersItsHooks()
    {
        InstallTrackTools();
        _box.Approve(("dev.noctis.samples.tracktools", new[] { "menu.commands", "library.read", "lyrics.provider", "notifications" }));
        var library = new FakeLibraryService();
        library.TrackList.Add(T("A", "Band", "One"));
        library.TrackList.Add(T("B", "Band", "Two"));
        var host = _box.NewHost(library: library);
        var notices = new List<PluginNotice>();
        host.NotificationRequested += (_, n) => notices.Add(n);
        host.LoadAll();

        var plugin = host.Plugins.Single();
        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal("Track Tools", plugin.Name);
        Assert.Equal(3, plugin.SettingItems.Count);
        Assert.Equal("More by this artist", host.TrackCommands.Single().Label);
        var lyrics = host.LyricsProviders.Single();
        Assert.Null(await lyrics.FindAsync("Band", "A", "One", TimeSpan.Zero)); // stub off by default

        host.TrackCommands.Single().Execute(library.TrackList[0]);
        Assert.Contains("1 more by Band", Assert.Single(notices).Message);

        plugin.SettingItems.Single(i => i.Key == "stubLyrics").BoolValue = true;
        var answer = await lyrics.FindAsync("Band", "A", "One", TimeSpan.Zero);
        Assert.Contains("Placeholder lyrics for \"A\"", answer!.PlainLyrics);

        // Loaded from memory: the folder is not locked and can be removed right away.
        Assert.True(host.Remove(plugin, deleteData: true));
        Assert.False(Directory.Exists(plugin.Directory));
    }

    private void InstallTrackTools()
    {
        var dir = _box.WriteFolder("dev.noctis.samples.tracktools",
            File.ReadAllText(PluginSandbox.RepoFile("samples/Noctis.SamplePlugin.TrackTools/plugin.json")));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Noctis.SamplePlugin.TrackTools.dll"),
            Path.Combine(dir, "Noctis.SamplePlugin.TrackTools.dll"));
    }

    /// <summary>A scriptable lyrics provider.</summary>
    internal sealed class FixedLyrics : ILyricsProvider
    {
        private readonly PluginLyrics? _answer;
        public FixedLyrics(string name, PluginLyrics? answer) { Name = name; _answer = answer; }
        public string Name { get; }
        public LyricsQuery? Last;
        public bool Throw, Hang, BlockSync;

        public async Task<PluginLyrics?> FindAsync(LyricsQuery query, CancellationToken ct)
        {
            Last = query;
            if (BlockSync) Thread.Sleep(1000);
            if (Throw) throw new HttpRequestException("provider failed");
            if (Hang) await Task.Delay(Timeout.Infinite, CancellationToken.None);
            await Task.Yield();
            return _answer;
        }
    }
}

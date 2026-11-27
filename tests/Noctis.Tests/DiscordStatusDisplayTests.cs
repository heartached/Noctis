using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Headless.XUnit;
using DiscordRPC;
using DiscordRPC.IO;
using DiscordRPC.Logging;
using Newtonsoft.Json.Linq;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord ask (2026-10-14): "switch the Discord presence from showing the artist to the
/// title". Settings ▸ Integrations ▸ Status on Discord picks what the one-line status reads
/// after "Listening to": the artist (default, as before), the song title or the app name.
/// </summary>
public class DiscordStatusDisplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "discord-status-" + Guid.NewGuid().ToString("N"));

    public DiscordStatusDisplayTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Connected presence service that records what it was asked to publish.</summary>
    private sealed class RecordingDiscord : IDiscordPresenceService
    {
        public List<DiscordPresenceTrack> Updates { get; } = new();
        public bool IsConnected => true;
        public Func<bool>? IsEnabled { get; set; }
        public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task UpdateAsync(DiscordPresenceTrack track, TimeSpan position, TimeSpan? duration, bool isPlaying)
        {
            Updates.Add(track);
            return Task.CompletedTask;
        }
        public Task ClearAsync() => Task.CompletedTask;
        public void InvalidateArtworkCache() { }
        public void Dispose() { }
    }

    private SettingsViewModel NewVm() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    [Theory]
    [InlineData(DiscordStatusDisplay.Artist, StatusDisplayType.State)]
    [InlineData(DiscordStatusDisplay.Title, StatusDisplayType.Details)]
    [InlineData(DiscordStatusDisplay.AppName, StatusDisplayType.Name)]
    public void EachChoice_MapsToTheFieldThatCarriesIt(DiscordStatusDisplay choice, StatusDisplayType expected)
    {
        // Details carries the title and State the artist (DiscordPresenceService.UpdateAsync).
        Assert.Equal(expected, DiscordPresenceService.MapStatusDisplay(choice));
    }

    [Fact]
    public void Default_IsTheArtist_LikeBeforeTheSettingExisted()
    {
        Assert.Equal(DiscordStatusDisplay.Artist, new DiscordPresenceTrack("Song", "Artist", null).StatusDisplay);
        Assert.Equal(DiscordStatusDisplay.Artist, DiscordPresenceOptions.ParseStatusDisplay(new AppSettings().DiscordStatusDisplay));
        Assert.Equal(DiscordStatusDisplay.Artist, DiscordPresenceOptions.ParseStatusDisplay(null));
        Assert.Equal(DiscordStatusDisplay.Artist, DiscordPresenceOptions.ParseStatusDisplay("Karaoke"));
        Assert.Equal(DiscordStatusDisplay.Artist, DiscordPresenceOptions.ParseStatusDisplay("7"));
        Assert.Equal(DiscordStatusDisplay.Title, DiscordPresenceOptions.ParseStatusDisplay("title"));
    }

    /// <summary>
    /// DiscordRpcClient.SetPresence skips a presence that Matches the last one sent
    /// (SkipIdenticalPresence), and Matches never reads StatusDisplay. A paused song has no
    /// timestamps to differ, so changing only the status line was dropped until the next
    /// song. The service turns the skip off for exactly that case.
    /// </summary>
    [Fact]
    public void LibrarySkip_IgnoresTheStatusLine_SoTheServiceForcesThatSend()
    {
        var sent = new RichPresence { Type = ActivityType.Listening, Details = "Song", State = "Artist", StatusDisplay = StatusDisplayType.State };
        var next = new RichPresence { Type = ActivityType.Listening, Details = "Song", State = "Artist", StatusDisplay = StatusDisplayType.Details };

        var matches = typeof(RichPresence).GetMethod("Matches", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(RichPresence) }, null);
        Assert.NotNull(matches);
        Assert.True((bool)matches!.Invoke(sent, new object[] { next })!,
            "DiscordRPC now compares StatusDisplay; the forced send in DiscordPresenceService.UpdateAsync can go");

        Assert.True(DiscordPresenceService.StatusDisplayChanged(sent, next));
        Assert.False(DiscordPresenceService.StatusDisplayChanged(sent, sent));
        Assert.False(DiscordPresenceService.StatusDisplayChanged(null, next));
    }

    [AvaloniaFact]
    public async Task Choice_SurvivesSaveAndReload_AndResetPutsTheArtistBack()
    {
        var vm = NewVm();
        await vm.LoadAsync();

        Assert.Equal("Artist", vm.SelectedDiscordStatusDisplayOption?.Value);
        Assert.Equal(new[] { "Artist", "Title", "AppName" }, vm.DiscordStatusDisplayOptions.Select(o => o.Value));
        Assert.Equal(new[] { Loc.T("Settings.DiscordStatusDisplay.Artist"), Loc.T("Settings.DiscordStatusDisplay.Title"), Loc.T("Settings.DiscordStatusDisplay.AppName") },
            vm.DiscordStatusDisplayOptions.Select(o => o.Label));

        vm.SelectedDiscordStatusDisplayOption = vm.DiscordStatusDisplayOptions.Single(o => o.Value == "Title");
        await vm.SaveAsync();

        var reloaded = NewVm();
        await reloaded.LoadAsync();
        Assert.Equal("Title", reloaded.SelectedDiscordStatusDisplayOption?.Value);
        Assert.Equal("Title", reloaded.GetSettings().DiscordStatusDisplay);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.Equal("Artist", reloaded.SelectedDiscordStatusDisplayOption?.Value);
    }

    [AvaloniaFact]
    public async Task HandEditedUnknownValue_ReadsAsTheArtist()
    {
        await new PersistenceService(_root).SaveSettingsAsync(new AppSettings { DiscordStatusDisplay = "Album" });

        var vm = NewVm();
        await vm.LoadAsync();

        Assert.Equal("Artist", vm.SelectedDiscordStatusDisplayOption?.Value);
    }

    [AvaloniaFact]
    public async Task ChangingTheChoice_RepublishesTheCurrentSongAtOnce()
    {
        var vm = NewVm();
        await vm.LoadAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SetPlayer(player);
        var discord = new RecordingDiscord();
        vm.SetDiscordPresence(discord);

        var song = new Track { Title = "Never Gonna Give You Up", Artist = "Rick Astley", FilePath = "a.flac" };
        player.ReplaceQueueAndPlay(new List<Track> { song }, 0);
        Assert.Equal(PlaybackState.Playing, player.State);

        vm.SelectedDiscordStatusDisplayOption = vm.DiscordStatusDisplayOptions.Single(o => o.Value == "Title");
        var update = Assert.Single(discord.Updates);
        Assert.Equal(DiscordStatusDisplay.Title, update.StatusDisplay);
        Assert.Equal("Never Gonna Give You Up", update.Title);

        vm.SelectedDiscordStatusDisplayOption = vm.DiscordStatusDisplayOptions.Single(o => o.Value == "AppName");
        Assert.Equal(DiscordStatusDisplay.AppName, discord.Updates[^1].StatusDisplay);

        // The album toggle's re-publish carries the status choice along instead of resetting it.
        vm.DiscordShowAlbum = false;
        Assert.Equal(DiscordStatusDisplay.AppName, discord.Updates[^1].StatusDisplay);
        Assert.False(discord.Updates[^1].ShowAlbum);
    }
    /// <summary>
    /// Stands in for Discord's IPC pipe: answers the handshake with READY and every
    /// SET_ACTIVITY with the activity echoed back, as the real client does, and records the
    /// activities the library actually wrote.
    /// </summary>
    private sealed class FakeDiscordPipe : INamedPipeClient
    {
        private readonly ConcurrentQueue<PipeFrame> _inbox = new();
        public ConcurrentQueue<JObject?> Activities { get; } = new();
        public ILogger Logger { get; set; } = new NullLogger();
        public bool IsConnected { get; private set; }
        [Obsolete] public int ConnectedPipe => 0;
        public bool Connect(int pipe) => IsConnected = true;
        public bool ReadFrame(out PipeFrame frame) => _inbox.TryDequeue(out frame);

        public bool WriteFrame(PipeFrame frame)
        {
            var body = JObject.Parse(frame.Message);
            if (frame.Opcode == Opcode.Handshake)
            {
                _inbox.Enqueue(new PipeFrame(Opcode.Frame, new JObject
                {
                    ["cmd"] = "DISPATCH", ["evt"] = "READY", ["nonce"] = null,
                    ["data"] = new JObject
                    {
                        ["v"] = 1,
                        ["config"] = new JObject { ["cdn_host"] = "cdn.discordapp.com", ["api_endpoint"] = "//discord.com/api", ["environment"] = "production" },
                        ["user"] = new JObject { ["id"] = "1", ["username"] = "test", ["discriminator"] = "0" },
                    },
                }));
            }
            else if (frame.Opcode == Opcode.Frame && (string?)body["cmd"] == "SET_ACTIVITY")
            {
                var activity = body["args"]?["activity"] as JObject;
                Activities.Enqueue(activity);
                _inbox.Enqueue(new PipeFrame(Opcode.Frame, new JObject
                {
                    ["cmd"] = "SET_ACTIVITY", ["evt"] = null, ["nonce"] = body["nonce"],
                    ["data"] = activity == null ? null : (JToken)activity.DeepClone(),
                }));
            }
            return true;
        }

        public void Close() => IsConnected = false;
        public void Dispose() => IsConnected = false;
    }

    private static async Task<JObject> NextActivityAsync(FakeDiscordPipe pipe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (pipe.Activities.TryDequeue(out var a) && a != null) return a;
            await Task.Delay(10);
        }
        throw new TimeoutException("No SET_ACTIVITY reached the pipe");
    }

    private static async Task<DiscordPresenceService> ConnectedServiceAsync(FakeDiscordPipe pipe)
    {
        var service = new DiscordPresenceService(() => new DiscordRpcClient("1470224696976085096", client: pipe));
        Assert.True(await service.ConnectAsync());
        return service;
    }

    /// <summary>
    /// Owner (2026-10-18): changing Status on Discord didn't seem to change Discord. The
    /// frame DiscordRPC writes must carry status_display_type (Discord: 0 Name, 1 State,
    /// 2 Details) and a change must go out at once — here while paused, where nothing else
    /// in the activity differs and the library's identical-presence skip would eat it.
    /// </summary>
    [Fact]
    public async Task ChangingTheChoiceWhilePaused_WritesTheNewStatusDisplayTypeToThePipe()
    {
        var pipe = new FakeDiscordPipe();
        using var service = await ConnectedServiceAsync(pipe);

        var song = new DiscordPresenceTrack("Please Please Please", "Sabrina Carpenter", "Short n' Sweet");
        await service.UpdateAsync(song, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), isPlaying: false);
        var first = await NextActivityAsync(pipe);
        Assert.Equal(2, (int)first["type"]!);                 // Listening
        Assert.Equal(1, (int)first["status_display_type"]!); // State = artist (default)
        Assert.Equal("Please Please Please", (string?)first["details"]);
        Assert.Equal("Sabrina Carpenter", (string?)first["state"]);

        foreach (var (choice, wire) in new[] { (DiscordStatusDisplay.Title, 2), (DiscordStatusDisplay.AppName, 0), (DiscordStatusDisplay.Artist, 1) })
        {
            await service.UpdateAsync(song with { StatusDisplay = choice }, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), isPlaying: false);
            var sent = await NextActivityAsync(pipe);
            Assert.Equal(wire, (int)sent["status_display_type"]!);
            Assert.Equal("Please Please Please", (string?)sent["details"]);
        }
    }

    [Fact]
    public async Task ChangingTheChoiceWhilePlaying_WritesTheNewStatusDisplayType()
    {
        var pipe = new FakeDiscordPipe();
        using var service = await ConnectedServiceAsync(pipe);

        var song = new DiscordPresenceTrack("Espresso", "Sabrina Carpenter", null);
        await service.UpdateAsync(song, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3), isPlaying: true);
        Assert.Equal(1, (int)(await NextActivityAsync(pipe))["status_display_type"]!);

        await service.UpdateAsync(song with { StatusDisplay = DiscordStatusDisplay.Title }, TimeSpan.FromSeconds(11), TimeSpan.FromMinutes(3), isPlaying: true);
        var sent = await NextActivityAsync(pipe);
        Assert.Equal(2, (int)sent["status_display_type"]!);
        Assert.NotNull(sent["timestamps"]);
    }

    /// <summary>
    /// Owner 11-26: "make sure the Status on Discord option is actually working and is it
    /// supposed to update in real time". The whole path, Settings to the pipe: a song playing,
    /// its presence on Discord, then each option changed in Settings. Each change writes
    /// exactly one new activity with the new status_display_type (or album line) and the
    /// song's timestamps as they were.
    /// </summary>
    [AvaloniaFact]
    public async Task ChangingTheOptionsWhilePlaying_SendsOneActivityEach_WithTheTimestampsKept()
    {
        var (vm, player, pipe, service) = await PlayingThroughThePipeAsync();
        using var _ = service;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = await NextActivityAsync(pipe);
        Assert.Equal(1, (int)first["status_display_type"]!);
        Assert.Equal("Short n' Sweet", (string?)first["assets"]?["large_text"]);

        foreach (var (choice, wire) in new[] { ("Title", 2), ("AppName", 0), ("Artist", 1) })
        {
            player.Position = clock.Elapsed; // the song plays on meanwhile
            vm.SelectedDiscordStatusDisplayOption = vm.DiscordStatusDisplayOptions.Single(o => o.Value == choice);
            var sent = await NextActivityAsync(pipe);
            Assert.Equal(wire, (int)sent["status_display_type"]!);
            Assert.Equal(2, (int)sent["type"]!);
            Assert.Equal("Espresso", (string?)sent["details"]);
            Assert.Equal("Sabrina Carpenter", (string?)sent["state"]);
            AssertSameTimestamps(first, sent);
            await AssertNothingMoreAsync(pipe);
        }

        player.Position = clock.Elapsed;
        vm.DiscordShowAlbum = false;
        var noAlbum = await NextActivityAsync(pipe);
        Assert.Null((string?)noAlbum["assets"]?["large_text"]);
        Assert.Equal(1, (int)noAlbum["status_display_type"]!);
        AssertSameTimestamps(first, noAlbum);
        await AssertNothingMoreAsync(pipe);

        player.Position = clock.Elapsed;
        vm.DiscordShowAlbum = true;
        Assert.Equal("Short n' Sweet", (string?)(await NextActivityAsync(pipe))["assets"]?["large_text"]);
        await AssertNothingMoreAsync(pipe);
        GC.KeepAlive(player);
    }

    /// <summary>
    /// A pause clears the presence (MainWindowViewModel: Paused/Stopped → ClearAsync). Changing
    /// an option then must not bring the card back: it used to send a paused activity, which
    /// showed the song on Discord while nothing played, until the next play/pause.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Stopped)]
    public async Task ChangingTheOptions_WhileNothingPlays_SendsNothing(PlaybackState state)
    {
        var (vm, player, pipe, service) = await PlayingThroughThePipeAsync();
        using var _ = service;
        await NextActivityAsync(pipe);
        if (state == PlaybackState.Paused) player.PlayPauseCommand.Execute(null);
        else player.StopPlaybackCommand.Execute(null);
        Assert.Equal(state, player.State);
        await service.ClearAsync(); // what the window does on pause / stop
        for (var waited = 0; !pipe.Activities.Contains(null) && waited < 10_000; waited += 20) await Task.Delay(20);
        Assert.Contains(null, pipe.Activities);
        pipe.Activities.Clear();

        vm.SelectedDiscordStatusDisplayOption = vm.DiscordStatusDisplayOptions.Single(o => o.Value == "Title");
        vm.DiscordShowAlbum = false;
        await AssertNothingMoreAsync(pipe, includeClears: true);
    }

    private async Task<(SettingsViewModel Vm, PlayerViewModel Player, FakeDiscordPipe Pipe, DiscordPresenceService Service)> PlayingThroughThePipeAsync()
    {
        var vm = NewVm();
        await vm.LoadAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SetPlayer(player);
        var pipe = new FakeDiscordPipe();
        var service = await ConnectedServiceAsync(pipe);
        vm.SetDiscordPresence(service);

        var song = new Track { Title = "Espresso", Artist = "Sabrina Carpenter", Album = "Short n' Sweet", FilePath = "a.flac", Duration = TimeSpan.FromMinutes(3) };
        player.ReplaceQueueAndPlay(new List<Track> { song }, 0);
        Assert.Equal(PlaybackState.Playing, player.State);
        // The window's publish for the song (MainWindowViewModel.UpdateDiscordPresenceAsync),
        // with a cover so the album line shows.
        await service.UpdateAsync(new DiscordPresenceTrack(song.Title, song.Artist, song.Album, "https://loon.example/a.jpg"),
            player.Position, song.Duration, isPlaying: true);
        return (vm, player, pipe, service);
    }

    private static void AssertSameTimestamps(JObject first, JObject sent)
    {
        // Rebuilt from the player's position, so equal to within the time between the two sends.
        Assert.InRange(Math.Abs((long)sent["timestamps"]!["start"]! - (long)first["timestamps"]!["start"]!), 0, 1000);
        Assert.InRange(Math.Abs((long)sent["timestamps"]!["end"]! - (long)first["timestamps"]!["end"]!), 0, 1000);
    }

    private static async Task AssertNothingMoreAsync(FakeDiscordPipe pipe, bool includeClears = false)
    {
        // DiscordRPC writes from its own worker loop, a few hundred ms after the call.
        await Task.Delay(1500);
        var left = pipe.Activities.ToArray();
        Assert.Empty(includeClears ? left : left.Where(a => a != null));
    }

    /// <summary>
    /// Discord answers a 1-character details/state with error 4000 and drops the whole
    /// activity (checked against the desktop client 2026-10-18). DiscordRPC trims fields, so
    /// a tag with a stray space ("A ") must still reach the pipe as 2+ characters.
    /// </summary>
    [Fact]
    public async Task OneLetterTagWithAStraySpace_StillGoesOutAsTwoCharacters()
    {
        var pipe = new FakeDiscordPipe();
        using var service = await ConnectedServiceAsync(pipe);

        await service.UpdateAsync(new DiscordPresenceTrack("A ", " B", null), TimeSpan.Zero, TimeSpan.FromMinutes(3), isPlaying: false);
        var sent = await NextActivityAsync(pipe);
        Assert.True(((string?)sent["details"])!.Length >= 2, (string?)sent["details"]);
        Assert.True(((string?)sent["state"])!.Length >= 2, (string?)sent["state"]);
        Assert.StartsWith("A", (string?)sent["details"]);
        Assert.StartsWith("B", (string?)sent["state"]);
    }
}

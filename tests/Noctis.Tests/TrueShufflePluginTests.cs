using System.Diagnostics;
using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Plugins.TrueShuffle;
using Noctis.Plugins.TrueShuffle.Shuffle;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// True Shuffle (by Alfamin, plugin API 1.5): the modes' rules on the engine, then the plugin through
/// the real plugin host and player (user-queued songs first, the rolling window, the session ending
/// when the queue is replaced), a 50,000-song library planned off the UI thread, the cycle file past
/// the original 50,000-song cap, and the Release zip.
/// </summary>
public class TrueShufflePluginTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private static Song S(int i, int plays = 0, bool skip = false, bool disliked = false)
        => new(Id(i), plays, skip, disliked, null);

    private static string Id(int i) => new Guid(i, 0, 0, new byte[8]).ToString("D");

    private static Func<int, int> Seeded(int seed)
    {
        var r = new Random(seed);
        return n => r.Next(n);
    }

    // ── The modes ──

    [Fact]
    public void EqualChance_EveryOrderIsEquallyLikely()
    {
        var songs = Enumerable.Range(0, 4).Select(i => S(i)).ToList();
        var draw = Seeded(7);
        var counts = new Dictionary<string, int>();
        const int runs = 24_000;
        for (var k = 0; k < runs; k++)
        {
            var plan = ShuffleEngine.Build(songs, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: draw);
            var key = string.Join(",", plan.Songs.Select(s => s.Id[..8]));
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        Assert.Equal(24, counts.Count); // all 4! orders occur
        // 1000 expected per order; 5 standard deviations either side.
        Assert.All(counts.Values, c => Assert.InRange(c, 845, 1155));
    }

    [Fact]
    public void EqualChance_EverySongIsEquallyLikelyInEveryPlace_AndHasNoRecencyRule()
    {
        const int n = 10, runs = 20_000;
        var songs = Enumerable.Range(0, n).Select(i => S(i, plays: i * 3)).ToList();
        var draw = Seeded(3);
        var at = new int[n, n];
        for (var k = 0; k < runs; k++)
        {
            // Remembered songs and the last song do not matter to Equal chance.
            var plan = ShuffleEngine.Build(songs, ShuffleMode.EqualChance, new[] { Id(0), Id(1) }, null, Id(2), draw: draw);
            Assert.Equal(n, plan.Songs.Count);
            for (var p = 0; p < n; p++) at[int.Parse(plan.Songs[p].Id[..8], System.Globalization.NumberStyles.HexNumber), p]++;
        }
        for (var s = 0; s < n; s++)
        for (var p = 0; p < n; p++)
            Assert.InRange(at[s, p], 1800, 2200); // 2000 expected, about 4.7 sd
    }

    [Fact]
    public void NoRepeats_NoSongComesTwiceUntilEverySongHasPlayed_ThenANewCycle()
    {
        var songs = Enumerable.Range(0, 25).Select(i => S(i)).ToList();
        var seen = new HashSet<string>();
        var heard = new List<string>();
        var draw = Seeded(11);
        string? last = null;
        // Re-plan before every song (the harshest case: every plan starts from what was heard).
        for (var k = 0; k < 25; k++)
        {
            var plan = ShuffleEngine.Build(songs, ShuffleMode.NoRepeats, seen, null, last, draw: draw);
            Assert.False(plan.NewCycle);
            var next = plan.Songs[0].Id;
            Assert.DoesNotContain(next, heard);
            heard.Add(next);
            seen.Add(next);
            last = next;
        }
        Assert.Equal(25, heard.Distinct().Count());

        var fresh = ShuffleEngine.Build(songs, ShuffleMode.NoRepeats, seen, null, last, draw: draw);
        Assert.True(fresh.NewCycle);
        Assert.Equal(25, fresh.Songs.Count);
        Assert.NotEqual(last, fresh.Songs[0].Id); // the new cycle does not start with the song just heard
        Assert.DoesNotContain(fresh.Seen, k => songs.Any(s => s.Id == k)); // the source's songs are forgotten
    }

    [Fact]
    public void NoRepeats_KeepsWhatWasHeardOutsideTheSource_WhenTheSourceStartsOver()
    {
        var source = Enumerable.Range(0, 3).Select(i => S(i)).ToList();
        var outside = Id(99);
        var plan = ShuffleEngine.Build(source, ShuffleMode.NoRepeats, source.Select(s => s.Id).Append(outside), null, null, draw: Seeded(1));
        Assert.True(plan.NewCycle);
        Assert.Contains(outside, plan.Seen);
    }

    [Fact]
    public void NeverPlayed_PicksOnlySongsNeverPlayed_AndNeverFallsBackToFamiliarOnes()
    {
        var songs = Enumerable.Range(0, 20).Select(i => S(i, plays: i % 4 == 0 ? 0 : i)).ToList();
        var unplayed = songs.Where(s => s.PlayCount == 0).Select(s => s.Id).ToHashSet();
        var plan = ShuffleEngine.Build(songs, ShuffleMode.NeverPlayed, Array.Empty<string>(), null, null, draw: Seeded(2));
        Assert.Equal(unplayed.OrderBy(x => x), plan.Songs.Select(s => s.Id).OrderBy(x => x));

        // Started in a session counts as played, even before Noctis's count catches up.
        var started = unplayed.Take(2).ToArray();
        plan = ShuffleEngine.Build(songs, ShuffleMode.NeverPlayed, started, null, null, draw: Seeded(2));
        Assert.Equal(unplayed.Count - 2, plan.Songs.Count);
        Assert.DoesNotContain(plan.Songs, s => started.Contains(s.Id));

        // None left: an empty plan, no new cycle of familiar songs.
        plan = ShuffleEngine.Build(songs, ShuffleMode.NeverPlayed, unplayed, null, null, draw: Seeded(2));
        Assert.Empty(plan.Songs);
        Assert.False(plan.NewCycle);
    }

    [Fact]
    public void Discovery_PlaysTheLeastPlayedFirst_RandomWithinAPlayCount()
    {
        var songs = Enumerable.Range(0, 30).Select(i => S(i, plays: i % 5)).ToList();
        var plan = ShuffleEngine.Build(songs, ShuffleMode.Discovery, Array.Empty<string>(), null, null, draw: Seeded(5));
        var counts = plan.Songs.Select(s => s.PlayCount).ToList();
        Assert.Equal(counts.OrderBy(c => c), counts);
        var first = ShuffleEngine.Build(songs, ShuffleMode.Discovery, Array.Empty<string>(), null, null, draw: Seeded(6));
        Assert.NotEqual(plan.Songs.Take(6).Select(s => s.Id), first.Songs.Take(6).Select(s => s.Id));
    }

    [Fact]
    public void EveryMode_LeavesOutSkipWhenShuffling_Snoozed_AndOnRequestNotLiked()
    {
        var now = DateTimeOffset.UtcNow;
        var songs = new List<Song>
        {
            S(0), S(1, skip: true), S(2, disliked: true), new(Id(3), 0, false, false, now.AddHours(1)), new(Id(4), 0, false, false, now.AddHours(-1)),
        };
        foreach (var mode in new[] { ShuffleMode.EqualChance, ShuffleMode.NoRepeats, ShuffleMode.Discovery, ShuffleMode.NeverPlayed })
        {
            var all = ShuffleEngine.Build(songs, mode, Array.Empty<string>(), null, null, now: now, draw: Seeded(1)).Songs.Select(s => s.Id).ToHashSet();
            Assert.Equal(new[] { Id(0), Id(2), Id(4) }.ToHashSet(), all);
            var liked = ShuffleEngine.Build(songs, mode, Array.Empty<string>(), null, null, skipDisliked: true, now: now, draw: Seeded(1)).Songs.Select(s => s.Id).ToHashSet();
            Assert.Equal(new[] { Id(0), Id(4) }.ToHashSet(), liked);
        }
    }

    [Fact]
    public void ForYou_PicksOnlyFromTheLibrary_FavorsWhatIsLoved_AndDoesNotRepeatAnArtistBackToBack()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var facts = Enumerable.Range(0, 60).Select(i => new TrackFacts(Id(i), "Song " + i, "Artist " + (i % 6), "", new[] { i % 2 == 0 ? "rock" : "jazz" },
            2000 + i % 10, 0, "", i % 3, i < 10 ? 5 : 0, i < 10, false, null)).ToList();
        var plays = new List<PlayRecord>();
        for (var i = 0; i < 10; i++) plays.Add(new PlayRecord(Id(i), now.AddDays(-30), 200_000, 200_000, 1, PlayEnd.Finished));
        for (var i = 50; i < 60; i++) plays.Add(new PlayRecord(Id(i), now.AddDays(-2), 200_000, 20_000, 0.1, PlayEnd.Skipped));
        var model = TasteModel.Build(plays, facts, Array.Empty<IReadOnlyCollection<string>>(), now);
        var ctx = new ForYouContext(Array.Empty<string>(), Array.Empty<string>(), 0.1, 25, 42, now);
        var plan = ForYouPlanner.Plan(facts.Select(f => f.Id).Append(Id(999)).ToList(), model, ctx);

        Assert.Equal(25, plan.Count);
        Assert.Equal(plan.Count, plan.Distinct().Count());
        Assert.DoesNotContain(Id(999), plan); // not in the library: never picked
        var at = plan.Select(id => facts.FindIndex(f => f.Id == id)).ToList();
        Assert.True(at.Take(10).Count(i => i < 10) >= 3, "loved songs lead (1 in 6 of the library): " + string.Join(",", at));
        Assert.True(plan.Count(id => facts.FindIndex(f => f.Id == id) >= 50) <= 3, "songs skipped early stay back");
        for (var k = 1; k < plan.Count; k++)
            Assert.NotEqual(model.Facts(plan[k - 1])!.MainArtist, model.Facts(plan[k])!.MainArtist);
        Assert.Equal(plan, ForYouPlanner.Plan(facts.Select(f => f.Id).ToList(), model, ctx)); // deterministic for a seed
    }

    // ── The remembered cycle ──

    [Fact]
    public async Task CycleStore_KeepsACycleOfMoreThan50000Songs()
    {
        // The original capped it at 50,000 ids (and 4 MB): a whole-library No repeats on a bigger
        // library could not be saved any more past that point, and the persistent modes stopped.
        var store = new CycleStore(Path.Combine(_box.Root, "data"));
        var ids = Enumerable.Range(0, 60_000).Select(Id).ToArray();
        await store.SaveAsync(new CycleData(1, ids, ids[^1]));
        var back = await store.LoadAsync();
        Assert.Equal(60_000, back.Seen.Length);
        Assert.Equal(ids[^1], back.Last);
    }

    [Fact]
    public async Task CycleStore_LeavesAForeignFileUntouched()
    {
        var dir = Path.Combine(_box.Root, "data2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cycle.json"), "{\"Version\":1,\"Seen\":[\"not-a-track-id\"],\"Last\":null}");
        await Assert.ThrowsAsync<IOException>(() => new CycleStore(dir).LoadAsync());
        Assert.Contains("not-a-track-id", File.ReadAllText(Path.Combine(dir, "cycle.json")));
    }

    // ── Through the real host and player ──

    private sealed record Rig(PlayerViewModel Player, FakeLibraryService Library, PluginHost Host, TrueShufflePlugin Plugin, ShuffleController Shuffle, List<Track> Tracks);

    private static Track Tr(int i, int plays = 0) => new()
    {
        Id = new Guid(i, 0, 0, new byte[8]), Title = "Song " + i, Artist = "Artist " + (i % 9), Album = "Album " + (i / 10), AlbumArtist = "Artist " + (i % 9),
        Duration = TimeSpan.FromSeconds(200), FilePath = Path.Combine(Path.GetTempPath(), "ts-" + i + ".mp3"), PlayCount = plays,
    };

    private Rig NewRig(int songs, Func<int, int>? plays = null)
    {
        var audio = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        for (var i = 0; i < songs; i++) library.TrackList.Add(Tr(i, plays?.Invoke(i) ?? 0));
        var player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var manifest = PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle/plugin.json")));
        _box.Approve((manifest.Id, manifest.Permissions.ToArray()));
        var host = _box.NewHost(player, library, "1.6.6");
        // The 1 s budget is wall-clock: a GC pause or a busy machine while the rest of the suite
        // runs in parallel pushed a TrackChanged past it, the host stopped the plugin and the
        // session silently got no more songs. The budget itself is tested in PluginHostApiTests.
        host.CallbackBudget = TimeSpan.FromMinutes(1);
        var plugin = new TrueShufflePlugin();
        var dir = Path.Combine(_box.PluginsDir, manifest.Id);
        Directory.CreateDirectory(dir);
        var loaded = host.AddInProcess(dir, manifest, () => plugin);
        Assert.Equal(PluginStatus.Running, loaded.Status);
        var shuffle = plugin.Controller!;
        shuffle.Draw = Seeded(9);
        return new Rig(player, library, host, plugin, shuffle, library.TrackList);
    }

    private static List<string> UpNext(PlayerViewModel p) => p.UpNext.Select(t => t.Id.ToString("D")).ToList();
    private static string Cur(PlayerViewModel p) => p.CurrentTrack!.Id.ToString("D");

    [AvaloniaFact]
    public async Task SongsYouQueue_PlayFirst_InTheOrderYouAddedThem_AheadOfTheShuffle()
    {
        var rig = NewRig(300);
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[0] }, 0);
        Assert.True(await rig.Shuffle.StartAsync(ShuffleMode.NoRepeats, ShuffleSource.Library, false));
        Assert.Equal(ShuffleController.WindowSize, p.UpNext.Count);
        Assert.False(p.IsShuffleEnabled); // Noctis's own shuffle is off: the order is True Shuffle's

        var a = rig.Tracks[100]; var b = rig.Tracks[101]; var c = rig.Tracks[102]; var d = rig.Tracks[103];
        p.AddToQueue(a);              // Noctis appends at the very end, behind 200 shuffled songs
        p.AddToQueue(b);
        p.AddNext(c);                 // Play Next goes ahead of the user's earlier songs
        p.AddRangeToQueue(new List<Track> { d });
        var up = UpNext(p);
        Assert.Equal(new[] { c, a, b, d }.Select(t => t.Id.ToString("D")), up.Take(4));
        Assert.Equal(up.Count, up.Distinct().Count()); // a song you queue is not also left further down
        Assert.Equal(new[] { c, a, b, d }.Select(t => t.Id.ToString("D")), rig.Shuffle.Mine);

        foreach (var expected in new[] { c, a, b, d })
        {
            p.NextCommand.Execute(null);
            Assert.Equal(expected.Id, p.CurrentTrack!.Id);
        }
        p.NextCommand.Execute(null);
        Assert.Contains(Cur(p), rig.Shuffle.Current!.Placed); // then the shuffle carries on
        Assert.Empty(rig.Shuffle.Mine);
    }

    [AvaloniaFact]
    public async Task NoRepeats_ThroughThePlayer_PlaysEverySongOnceBeforeAnyComesBack()
    {
        var rig = NewRig(40);
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[0] }, 0);
        Assert.True(await rig.Shuffle.StartAsync(ShuffleMode.NoRepeats, ShuffleSource.Library, false));
        var played = new List<string> { Cur(p) };
        for (var k = 0; k < 39; k++)
        {
            p.NextCommand.Execute(null);
            played.Add(Cur(p));
        }
        Assert.Equal(40, played.Distinct().Count());
        Assert.Equal(played.Skip(1).ToHashSet(), rig.Shuffle.Seen.ToHashSet()); // started = heard (the song playing at Apply was not part of the session)
        await rig.Shuffle.Saved;
        Assert.True(File.Exists(Path.Combine(_box.DataRoot, TrueShufflePlugin.Id, "cycle.json")));
    }

    [AvaloniaFact]
    public async Task NeverPlayed_ThroughThePlayer_QueuesOnlyUnplayedSongs()
    {
        var rig = NewRig(50, plays: i => i % 5 == 0 ? 0 : 3);
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[1] }, 0);
        Assert.True(await rig.Shuffle.StartAsync(ShuffleMode.NeverPlayed, ShuffleSource.Library, false));
        Assert.Equal(10, p.UpNext.Count);
        Assert.All(p.UpNext, t => Assert.Equal(0, t.PlayCount));
    }

    [AvaloniaFact]
    public async Task ThePlaylistSource_ShufflesThatPlaylistOnly()
    {
        var rig = NewRig(100);
        var list = new Playlist { Name = "Gym", TrackIds = rig.Tracks.Skip(10).Take(15).Select(t => t.Id).ToList() };
        rig.Host.PlaylistSource = () => new[] { list };
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[0] }, 0);
        Assert.True(await rig.Shuffle.StartAsync(ShuffleMode.EqualChance, new ShuffleSource(SourceKind.Playlist, list.Id.ToString("D"), "Gym"), false));
        Assert.Equal(list.TrackIds.Select(g => g.ToString("D")).OrderBy(x => x), UpNext(p).OrderBy(x => x));
        Assert.Equal(rig.Tracks[0].Id, p.CurrentTrack!.Id); // the playing song plays on
    }

    [AvaloniaFact]
    public async Task PlayingSomethingElse_EndsTheSession_AndLeavesTheQueueAlone()
    {
        var rig = NewRig(80);
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[0] }, 0);
        Assert.True(await rig.Shuffle.StartAsync(ShuffleMode.EqualChance, ShuffleSource.Library, false));
        var album = rig.Tracks.Skip(10).Take(8).ToList();
        p.ReplaceQueueAndPlay(album, 0);
        Assert.Null(rig.Shuffle.Current);
        Assert.Equal(album.Skip(1).Select(t => t.Id), p.UpNext.Select(t => t.Id));
        p.AddToQueue(rig.Tracks[50]); // no session: Noctis's own order
        Assert.Equal(rig.Tracks[50].Id, p.UpNext[^1].Id);
    }

    [AvaloniaFact]
    public async Task NextPreviousAndPickingFromTheQueue_KeepTheSession()
    {
        var rig = NewRig(80);
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[0] }, 0);
        Assert.True(await rig.Shuffle.StartAsync(ShuffleMode.NoRepeats, ShuffleSource.Library, false));
        p.NextCommand.Execute(null);
        p.NextCommand.Execute(null);
        p.PreviousCommand.Execute(null);
        Assert.NotNull(rig.Shuffle.Current);
        p.PreviousCommand.Execute(null); // back to the song that played when the shuffle began
        Assert.Equal(rig.Tracks[0].Id, p.CurrentTrack!.Id);
        Assert.NotNull(rig.Shuffle.Current);
        p.NextCommand.Execute(null);
        p.RemoveFromQueue(3);
        p.NextCommand.Execute(null);
        Assert.NotNull(rig.Shuffle.Current);
    }

    [AvaloniaFact]
    public async Task TheLatestChoiceWins_WhenModesArePickedQuickly()
    {
        var rig = NewRig(200, plays: i => i % 2);
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[1] }, 0);
        var first = rig.Shuffle.StartAsync(ShuffleMode.EqualChance, ShuffleSource.Library, false);
        var second = rig.Shuffle.StartAsync(ShuffleMode.NeverPlayed, ShuffleSource.Library, false);
        Assert.False(await first);
        Assert.True(await second);
        Assert.Equal(ShuffleMode.NeverPlayed, rig.Shuffle.Current!.Mode);
        Assert.All(p.UpNext, t => Assert.Equal(0, t.PlayCount));
    }

    [AvaloniaFact]
    public async Task TheWindow_IsRefilledBehind_WhenItRunsLow()
    {
        var rig = NewRig(600);
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[0] }, 0);
        Assert.True(await rig.Shuffle.StartAsync(ShuffleMode.NoRepeats, ShuffleSource.Library, false));
        var heard = new HashSet<string> { Cur(p) };
        for (var k = 0; k < ShuffleController.WindowSize - ShuffleController.RefillAt; k++)
        {
            p.NextCommand.Execute(null);
            Assert.True(heard.Add(Cur(p)));
        }
        Assert.True(p.UpNext.Count > ShuffleController.RefillAt + 100, $"refilled: {p.UpNext.Count} up next");
        Assert.DoesNotContain(UpNext(p), heard.Contains);
    }

    /// <summary>
    /// A 50,000-song library: the library copy and the ordering run on a worker thread. The UI thread
    /// only starts it and puts 200 songs up next; a refill takes from the plan without copying the
    /// library again (the original copied the whole library on the UI thread at every refill).
    /// </summary>
    [AvaloniaFact]
    public async Task FiftyThousandSongs_ArePlannedOffTheUiThread_WithoutStallingIt()
    {
        var rig = NewRig(50_000);
        var p = rig.Player;
        p.ReplaceQueueAndPlay(new List<Track> { rig.Tracks[0] }, 0);

        var sw = Stopwatch.StartNew();
        var start = rig.Shuffle.StartAsync(ShuffleMode.NoRepeats, ShuffleSource.Library, false);
        var syncPart = sw.Elapsed;
        Assert.False(start.IsCompleted); // the plan is made on a worker
        Assert.True(syncPart < TimeSpan.FromMilliseconds(150), $"UI thread held {syncPart.TotalMilliseconds:0} ms before the worker took over");

        Assert.True(await start);
        Assert.Equal(ShuffleController.WindowSize, p.UpNext.Count);

        // Taking the next part of the plan is O(window), not O(library).
        var session = rig.Shuffle.Current!;
        var queue = new PlaybackQueue(Cur(p), UpNext(p));
        sw.Restart();
        var batch = session.Take(ShuffleController.WindowSize, new HashSet<string>(), queue, ShuffleMode.NoRepeats, DateTimeOffset.UtcNow);
        Assert.Equal(ShuffleController.WindowSize, batch.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(20), $"a refill took {sw.Elapsed.TotalMilliseconds:0.0} ms");
        Assert.Equal(50_000 - 1, session.Plan.Count);
    }

    // ── Package ──

    [AvaloniaFact]
    public void ReleaseZip_InstallsFromFile_AndRuns()
    {
        var zip = PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle/bin/Release/Noctis.Plugins.TrueShuffle-1.0.0.zip");
        if (!File.Exists(zip)) Assert.Skip("build it first: dotnet build plugins/Noctis.Plugins.TrueShuffle -c Release");
        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "LICENSE", "Noctis.Plugins.TrueShuffle.deps.json", "Noctis.Plugins.TrueShuffle.dll", "icon.png", "plugin.json" }, names);
            using var license = new StreamReader(archive.GetEntry("LICENSE")!.Open());
            var text = license.ReadToEnd();
            Assert.Contains("MIT License", text);
            Assert.Contains("Copyright (c) 2026 Alfamin", text);
        }

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        host.LoadAll();
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var plugin = result.Plugin!;
        Assert.Equal(("True Shuffle", "1.0.0", "Alfamin"), (plugin.Name, plugin.Version, plugin.Manifest?.Author));
        Assert.Equal(TrueShufflePlugin.About, plugin.Manifest!.Description); // no "By Alfamin." (the card says by X)
        Assert.DoesNotContain("Alfamin", plugin.Manifest.Description);
        Assert.Equal(PluginStatus.Disabled, plugin.Status); // waits for the user's approval
        Assert.DoesNotContain("network", plugin.Manifest.Permissions);

        host.SetEnabled(plugin, true);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal("True Shuffle", Assert.Single(plugin.Views).Name);
        Assert.Equal("True Shuffle", Assert.Single(host.TrackCommands).Label);
        Assert.Equal(4, plugin.SettingItems.Count);
        Assert.True(host.Remove(plugin, deleteData: true));
    }

    [Fact]
    public void UserFacingText_HasNoEmDashes()
    {
        foreach (var f in Directory.GetFiles(PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".json")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain("—", File.ReadAllText(f));
    }
}

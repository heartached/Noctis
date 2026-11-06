using System;
using System.Diagnostics;
using System.Linq;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone beta: "the search is a bit laggy". Search re-runs on the UI thread at every keystroke,
/// so what one keystroke costs over a big library is what the user feels while typing. Measured
/// here on a 12,000-song library (desktop CPU; a mid-range phone core is several times slower).
/// </summary>
public class MobileSearchPerfTests
{
    private readonly ITestOutputHelper _output;
    public MobileSearchPerfTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] Words =
    {
        "love", "night", "heart", "fire", "dream", "rain", "light", "home", "blue", "gold",
        "summer", "shadow", "river", "storm", "angel", "ghost", "city", "wild", "stone", "echo",
    };

    internal static MobileFixtures.Rig BigLibrary(int songs = 12_000, int artists = 900)
    {
        var rng = new Random(7);
        var tracks = new Track[songs];
        var albums = new System.Collections.Generic.List<Album>();
        for (var a = 0; a < songs / 12; a++)
        {
            var artist = $"{Words[rng.Next(Words.Length)]} {Words[rng.Next(Words.Length)]} {a % artists}";
            var albumTracks = new Track[12];
            for (var i = 0; i < 12; i++)
            {
                var t = MobileFixtures.Song($"{Words[rng.Next(Words.Length)]} {Words[rng.Next(Words.Length)]} {a}-{i}", artist,
                    plays: rng.Next(50));
                t.AlbumArtworkPath = $"/covers/{a}.jpg";
                albumTracks[i] = t;
                tracks[a * 12 + i] = t;
            }
            var album = MobileFixtures.MakeAlbum($"{Words[rng.Next(Words.Length)]} {a}", artist, albumTracks);
            album.ArtworkPath = $"/covers/{a}.jpg";
            albums.Add(album);
        }
        var rig = MobileFixtures.MakeRig(tracks, albums.ToArray());
        foreach (var name in albums.Select(a => a.Artist).Distinct())
            rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = name, TrackCount = 12 });
        return rig;
    }

    /// <summary>Median ms of one keystroke of "love" typed out, then deleted, ten times over.</summary>
    internal static double KeystrokeMedianMs(SearchPageViewModel search)
    {
        var keys = new[] { "l", "lo", "lov", "love", "lov", "lo", "l", "" };
        foreach (var k in keys) search.Query = k;   // warm-up: the lazily cached keys
        var times = new System.Collections.Generic.List<double>();
        for (var round = 0; round < 10; round++)
            foreach (var k in keys)
            {
                var start = Stopwatch.GetTimestamp();
                search.Query = k;
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        times.Sort();
        return times[times.Count / 2];
    }

    [Fact]
    public void Keystroke_On_A_Big_Library_Stays_Cheap()
    {
        using var rig = BigLibrary();
        var search = rig.Shell.Search;
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var median = KeystrokeMedianMs(search);
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / (8 * 11);
        _output.WriteLine($"12k songs: median keystroke {median:F2} ms, {allocated / 1024.0:F0} KB allocated per keystroke");
        // Before the artist keys and the artist → cover map were kept: 465 KB (3.5 ms).
        Assert.True(allocated < 350 * 1024, $"{allocated / 1024} KB per keystroke");
        search.Query = "love";
        Assert.True(search.HasSongs || search.HasFeatured);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async System.Threading.Tasks.Task FastTyping_IsOneSearch_WhenTheTypingPauses()
    {
        using var rig = BigLibrary(1_200, 100);
        var search = rig.Shell.Search;
        search.TypingDebounce = TimeSpan.FromMilliseconds(150);
        var runs = search.RunCount;

        search.Query = "l";                       // the first letter answers at once
        Assert.False(search.HasPendingSearch);
        Assert.Equal(runs + 1, search.RunCount);
        search.Query = "lo";
        search.Query = "lov";
        search.Query = "love";
        Assert.True(search.HasPendingSearch);
        Assert.Equal(runs + 1, search.RunCount);

        var deadline = Environment.TickCount64 + 5_000;
        while (search.HasPendingSearch && Environment.TickCount64 < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await System.Threading.Tasks.Task.Delay(10);
        }
        Assert.Equal(runs + 2, search.RunCount);
        Assert.True(search.HasSongs || search.HasFeatured);
        Assert.All(search.Songs, t => Assert.Contains("love", (t.Title + " " + t.Artist + " " + t.Album).ToLowerInvariant()));

        search.Query = "";                        // clearing answers at once
        Assert.False(search.HasPendingSearch);
        Assert.True(search.ShowPrompt);
        Assert.Empty(search.Songs);
    }
}

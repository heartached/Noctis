using System;
using System.Linq;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord #mobile-beta (Mistery): the phone's Shuffle honours Snooze for a Month the way the
/// desktop's does (ShuffleHelper.WeightedShuffle, ToggleShuffle, BuildRepeatAllPass): a snoozed
/// or Skip When Shuffling song is never dealt into the shuffled order, comes back on Shuffle off,
/// and each Repeat All pass is shuffled again.
/// </summary>
public class PhoneSnoozeShuffleTests
{
    private static List<Track> Tracks(int n) =>
        Enumerable.Range(0, n).Select(i => new Track { Title = $"t{i}" }).ToList();

    private static void Snooze(Track t) => t.SnoozedUntil = DateTime.UtcNow.AddDays(30);

    [Fact]
    public void Shuffle_on_holds_out_snoozed_and_skip_tracks_and_off_puts_them_back_in_place()
    {
        var q = new PlaybackQueue(); var t = Tracks(6);
        Snooze(t[2]);
        t[4].SkipWhenShuffling = true;
        q.ReplaceAll(t, 0);                     // upnext t1..t5

        q.SetShuffle(true, new Random(3));

        Assert.Equal(new[] { t[1], t[3], t[5] }.OrderBy(x => x.Title), q.UpNext.OrderBy(x => x.Title));

        q.SetShuffle(false);

        Assert.Equal(t.Skip(1).ToList(), q.UpNext.ToList());
    }

    [Fact]
    public void An_expired_snooze_is_shuffled_like_any_song()
    {
        var q = new PlaybackQueue(); var t = Tracks(4);
        t[2].SnoozedUntil = DateTime.UtcNow.AddMinutes(-1);
        q.ReplaceAll(t, 0);

        q.SetShuffle(true, new Random(3));

        Assert.Contains(t[2], q.UpNext);
    }

    [Fact]
    public void Held_out_tracks_survive_a_restore_and_come_back_on_shuffle_off()
    {
        var q = new PlaybackQueue(); var t = Tracks(5);
        Snooze(t[3]);
        q.ReplaceAll(t, 0);
        q.SetShuffle(true, new Random(3));

        var restored = PlaybackQueue.Restore(q.Snapshot(), id => t.FirstOrDefault(x => x.Id == id));
        Assert.DoesNotContain(t[3], restored.UpNext);
        restored.SetShuffle(false);

        Assert.Equal(t.Skip(1).ToList(), restored.UpNext.ToList());
    }

    [Fact]
    public void RepeatAll_with_shuffle_on_deals_each_pass_again_without_the_held_out_tracks()
    {
        var q = new PlaybackQueue { RepeatMode = RepeatMode.All }; var t = Tracks(8);
        Snooze(t[5]);
        q.ReplaceAll(t, 0);
        q.SetShuffle(true, new Random(11));

        // Play the whole first pass.
        while (q.UpNext.Count > 0) q.Advance(QueueAdvance.Natural);
        var first = q.Advance(QueueAdvance.Natural);   // the wrap

        Assert.True(q.IsShuffleEnabled);
        var pass = new[] { first! }.Concat(q.UpNext).ToList();
        Assert.DoesNotContain(t[5], pass);
        Assert.Equal(7, pass.Count);
        Assert.NotEqual(t.Where(x => x != t[5]).ToList(), pass);   // not the list order (seed 11)

        q.SetShuffle(false);                           // the cycle is the Shuffle-off order
        Assert.Equal(t.Where(x => !ReferenceEquals(x, first)).ToList(), q.UpNext.ToList());
    }

    [Fact]
    public void PlayShuffled_never_starts_on_a_snoozed_song_and_plays_nothing_when_all_are()
    {
        var songs = Enumerable.Range(0, 6).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        foreach (var s in songs.Take(5)) Snooze(s);
        using var rig = MobileFixtures.MakeRig(songs);
        var player = rig.Shell.Player;

        for (var seed = 0; seed < 10; seed++)
        {
            player.PlayShuffled(songs, new Random(seed));
            Assert.Same(songs[5], player.CurrentTrack);
            Assert.Empty(player.UpNext);
        }

        Snooze(songs[5]);
        player.PlayTracks(new[] { MobileFixtures.Song("Other") }, 0);
        var before = player.CurrentTrack;
        player.PlayShuffled(songs, new Random(1));
        Assert.Same(before, player.CurrentTrack);
    }
}

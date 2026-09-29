using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Pins the desktop queue semantics the Android player will rely on.</summary>
public class PlaybackQueueTests
{
    private static List<Track> Tracks(int n) =>
        Enumerable.Range(0, n).Select(i => new Track { Title = $"t{i}" }).ToList();

    [Fact]
    public void ReplaceAll_sets_current_and_upnext()
    {
        var q = new PlaybackQueue(); var t = Tracks(4);

        var cur = q.ReplaceAll(t, 1);

        Assert.Same(t[1], cur);
        Assert.Equal(new[] { t[2], t[3] }, q.UpNext);
        Assert.Empty(q.History);
    }

    [Fact]
    public void ReplaceAll_moves_the_old_current_to_history()
    {
        var q = new PlaybackQueue(); var a = Tracks(2); var b = Tracks(2);
        q.ReplaceAll(a, 0);

        q.ReplaceAll(b, 0);

        Assert.Equal(new[] { a[0] }, q.History);
    }

    [Fact]
    public void Advance_natural_plays_the_head_of_upnext_and_records_history()
    {
        var q = new PlaybackQueue(); var t = Tracks(3);
        q.ReplaceAll(t, 0);

        var cur = q.Advance(QueueAdvance.Natural);

        Assert.Same(t[1], cur);
        Assert.Equal(new[] { t[2] }, q.UpNext);
        Assert.Equal(new[] { t[0] }, q.History);
    }

    [Fact]
    public void Advance_on_empty_queue_stops()
    {
        var q = new PlaybackQueue(); var t = Tracks(1);
        q.ReplaceAll(t, 0);

        Assert.Null(q.Advance(QueueAdvance.Natural));
        Assert.Null(q.Current);
        Assert.Equal(new[] { t[0] }, q.History);
    }

    [Fact]
    public void RepeatOne_replays_current_only_on_natural_end()
    {
        var q = new PlaybackQueue { RepeatMode = RepeatMode.One }; var t = Tracks(2);
        q.ReplaceAll(t, 0);

        Assert.Same(t[0], q.Advance(QueueAdvance.Natural));
        Assert.Same(t[1], q.Advance(QueueAdvance.UserSkip));
    }

    [Fact]
    public void RepeatAll_restarts_the_cycle_from_where_the_user_started()
    {
        var q = new PlaybackQueue { RepeatMode = RepeatMode.All }; var t = Tracks(3);
        q.ReplaceAll(t, 1);                     // current t1, upnext [t2]; cycle t1 t2 t0 (t0 lives only in the cycle)
        q.Advance(QueueAdvance.Natural);        // t2, queue now empty

        var cur = q.Advance(QueueAdvance.Natural);

        Assert.Same(t[1], cur);                 // wrap restarts at the track the user started on
        Assert.Equal(new[] { t[2], t[0] }, q.UpNext);
        Assert.Empty(q.History);
        Assert.Same(t[2], q.Advance(QueueAdvance.Natural));
        Assert.Same(t[0], q.Advance(QueueAdvance.Natural));
    }

    [Fact]
    public void RepeatAll_user_skip_on_last_track_also_wraps()
    {
        var q = new PlaybackQueue { RepeatMode = RepeatMode.All }; var t = Tracks(2);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.UserSkip);

        Assert.Same(t[0], q.Advance(QueueAdvance.UserSkip));
    }

    [Fact]
    public void Back_pops_history_and_pushes_current_to_the_front()
    {
        var q = new PlaybackQueue(); var t = Tracks(3);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.Natural);        // current t1, history [t0]

        var cur = q.Back();

        Assert.Same(t[0], cur);
        Assert.Equal(new[] { t[1], t[2] }, q.UpNext);
        Assert.Empty(q.History);
    }

    [Fact]
    public void Back_with_empty_history_keeps_current()
    {
        var q = new PlaybackQueue(); var t = Tracks(1);
        q.ReplaceAll(t, 0);

        Assert.Same(t[0], q.Back());
    }

    [Fact]
    public void AddNext_and_Add_place_tracks_at_front_and_back()
    {
        var q = new PlaybackQueue(); var t = Tracks(4);
        q.ReplaceAll(t.Take(2).ToList(), 0);   // upnext [t1]

        q.AddNext(t[2]);
        q.Add(t[3]);

        Assert.Equal(new[] { t[2], t[1], t[3] }, q.UpNext);
    }

    [Fact]
    public void Move_and_RemoveAt_edit_upnext_in_place()
    {
        var q = new PlaybackQueue(); var t = Tracks(4);
        q.ReplaceAll(t, 0);                     // upnext t1 t2 t3

        q.Move(2, 0);
        Assert.Equal(new[] { t[3], t[1], t[2] }, q.UpNext);
        q.RemoveAt(1);
        Assert.Equal(new[] { t[3], t[2] }, q.UpNext);
        q.RemoveAt(9);                          // out of range: no-op
        Assert.Equal(2, q.UpNext.Count);
    }

    [Fact]
    public void Shuffle_on_permutes_upnext_and_off_restores_the_unplayed_order()
    {
        var q = new PlaybackQueue(); var t = Tracks(6);
        q.ReplaceAll(t, 0);                     // upnext t1..t5

        q.SetShuffle(true, new Random(7));

        Assert.True(q.IsShuffleEnabled);
        Assert.Equal(5, q.UpNext.Count);
        Assert.Equal(t.Skip(1).OrderBy(x => x.Title), q.UpNext.OrderBy(x => x.Title)); // same set
        Assert.NotEqual(t.Skip(1).ToList(), q.UpNext.ToList());                       // permuted (seed 7)

        var played = q.Advance(QueueAdvance.Natural);
        q.SetShuffle(false);

        Assert.False(q.IsShuffleEnabled);
        Assert.Equal(t.Skip(1).Where(x => !ReferenceEquals(x, played)).ToList(), q.UpNext.ToList());
    }

    [Fact]
    public void History_is_capped()
    {
        var q = new PlaybackQueue(historyCap: 2); var t = Tracks(5);
        q.ReplaceAll(t, 0);
        for (int i = 0; i < 4; i++) q.Advance(QueueAdvance.Natural);

        Assert.Equal(new[] { t[3], t[2] }, q.History);
    }

    [Fact]
    public void Snapshot_and_Restore_round_trip_including_the_repeat_cycle()
    {
        var q = new PlaybackQueue { RepeatMode = RepeatMode.All }; var t = Tracks(4);
        q.ReplaceAll(t, 1);                     // cycle t1 t2 t3 t0; current t1
        q.Advance(QueueAdvance.Natural);        // current t2, upnext [t3], history [t1]
        var byId = t.ToDictionary(x => x.Id);

        var r = PlaybackQueue.Restore(q.Snapshot(), id => byId.GetValueOrDefault(id));

        Assert.Same(q.Current, r.Current);
        Assert.Equal(q.UpNext, r.UpNext);
        Assert.Equal(q.History, r.History);
        Assert.Equal(RepeatMode.All, r.RepeatMode);
        Assert.Same(t[3], r.Advance(QueueAdvance.Natural));
        Assert.Same(t[1], r.Advance(QueueAdvance.Natural)); // wrap: the cycle survived the round trip
        Assert.Same(t[2], r.Advance(QueueAdvance.Natural));
    }

    [Fact]
    public void Restore_drops_ids_that_no_longer_resolve()
    {
        var t = Tracks(3);
        var state = new PlaybackQueueState(t[0].Id, new[] { t[1].Id, Guid.NewGuid(), t[2].Id },
            Array.Empty<Guid>(), Array.Empty<Guid>(), RepeatMode.Off, false, Array.Empty<Guid>());
        var byId = t.ToDictionary(x => x.Id);

        var r = PlaybackQueue.Restore(state, id => byId.GetValueOrDefault(id));

        Assert.Same(t[0], r.Current);
        Assert.Equal(new[] { t[1], t[2] }, r.UpNext);
    }

    [Fact]
    public void RemoveWhere_drops_matches_everywhere_and_reports_a_matching_current()
    {
        var q = new PlaybackQueue(); var t = Tracks(5);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.Natural);            // t0 -> history, t1 current
        q.SetShuffle(true, new Random(1));          // remembers t2..t4 as the original order
        var t0 = t[0]; var t1 = t[1]; var t3 = t[3];
        bool Remote(Track x) => x == t0 || x == t1 || x == t3;

        var currentGone = q.RemoveWhere(Remote);

        Assert.True(currentGone);
        Assert.Null(q.Current);
        Assert.DoesNotContain(t3, q.UpNext);
        Assert.DoesNotContain(t0, q.History);
        q.SetShuffle(false);                        // restoring the order must not bring t3 back
        Assert.Equal(new[] { t[2], t[4] }, q.UpNext);
        Assert.DoesNotContain(q.Snapshot().RepeatCycleIds, id => id == t0.Id || id == t1.Id || id == t3.Id);
    }

    [Fact]
    public void RemoveWhere_keeps_a_current_that_does_not_match()
    {
        var q = new PlaybackQueue(); var t = Tracks(3);
        q.ReplaceAll(t, 0);

        Assert.False(q.RemoveWhere(x => x == t[2]));
        Assert.Same(t[0], q.Current);
        Assert.Equal(new[] { t[1] }, q.UpNext);
    }
}

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
    public void RepeatAll_started_mid_list_wraps_to_the_first_track()
    {
        var q = new PlaybackQueue { RepeatMode = RepeatMode.All }; var t = Tracks(3);
        q.ReplaceAll(t, 1);                     // current t1, upnext [t2]; cycle t0 t1 t2 (t0 lives only in the cycle)
        q.Advance(QueueAdvance.Natural);        // t2, queue now empty

        var cur = q.Advance(QueueAdvance.Natural);

        // The list's first track, as on a playlist: wrapping to the picked track replayed
        // only the tail on the first pass (Discord 10-25).
        Assert.Same(t[0], cur);
        Assert.Equal(new[] { t[1], t[2] }, q.UpNext);
        Assert.Equal(new[] { t[2], t[1] }, q.History); // GitHub #124: kept for Back
        Assert.Same(t[1], q.Advance(QueueAdvance.Natural));
        Assert.Same(t[2], q.Advance(QueueAdvance.Natural));
        Assert.Same(t[0], q.Advance(QueueAdvance.Natural)); // and every pass after
    }

    [Fact]
    public void RepeatAll_Back_on_a_new_pass_returns_to_the_last_pass()
    {
        var q = new PlaybackQueue { RepeatMode = RepeatMode.All }; var t = Tracks(3);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.Natural);
        q.Advance(QueueAdvance.Natural);
        Assert.Same(t[0], q.Advance(QueueAdvance.Natural)); // wrapped

        Assert.Same(t[2], q.Back());
        Assert.Same(t[1], q.Back());
    }

    [Fact]
    public void RepeatAll_without_a_cycle_does_not_double_the_next_pass()
    {
        var t = Tracks(2);
        var state = new PlaybackQueueState(t[1].Id, Array.Empty<Guid>(), new[] { t[0].Id },
            Array.Empty<Guid>(), RepeatMode.All, false, Array.Empty<Guid>());
        var byId = t.ToDictionary(x => x.Id);
        var q = PlaybackQueue.Restore(state, id => byId.GetValueOrDefault(id));

        Assert.Same(t[0], q.Advance(QueueAdvance.Natural)); // fallback wrap from History
        Assert.Equal(new[] { t[1] }, q.UpNext);
        q.Advance(QueueAdvance.Natural);
        Assert.Same(t[0], q.Advance(QueueAdvance.Natural)); // second wrap: same cycle
        Assert.Equal(new[] { t[1] }, q.UpNext);
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
    public void Songs_queued_while_shuffled_survive_shuffle_off()
    {
        var q = new PlaybackQueue(); var t = Tracks(4); var next = new Track { Title = "next" }; var last = new Track { Title = "last" };
        q.ReplaceAll(t, 0);                     // upnext t1..t3
        q.SetShuffle(true, new Random(7));

        q.AddNext(next);                        // Play Next while shuffled
        q.Add(last);                            // Add to Queue while shuffled
        q.SetShuffle(false);

        Assert.Equal(new[] { next, t[1], t[2], t[3], last }, q.UpNext);
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
        q.ReplaceAll(t, 1);                     // cycle t0 t1 t2 t3; current t1
        q.Advance(QueueAdvance.Natural);        // current t2, upnext [t3], history [t1]
        var byId = t.ToDictionary(x => x.Id);

        var r = PlaybackQueue.Restore(q.Snapshot(), id => byId.GetValueOrDefault(id));

        Assert.Same(q.Current, r.Current);
        Assert.Equal(q.UpNext, r.UpNext);
        Assert.Equal(q.History, r.History);
        Assert.Equal(RepeatMode.All, r.RepeatMode);
        Assert.Same(t[3], r.Advance(QueueAdvance.Natural));
        Assert.Same(t[0], r.Advance(QueueAdvance.Natural)); // wrap: the cycle survived the round trip
        Assert.Same(t[1], r.Advance(QueueAdvance.Natural));
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
    [Fact]
    public void PlayedInQueue_counts_only_this_queue_and_is_not_capped_like_history()
    {
        var q = new PlaybackQueue(historyCap: 3); var a = Tracks(2); var b = Tracks(6);
        q.ReplaceAll(a, 0);
        q.Advance(QueueAdvance.Natural);
        Assert.Equal(1, q.PlayedInQueue);

        q.ReplaceAll(b, 0);                         // a0 and a1 are in History, not in this queue
        Assert.Equal(0, q.PlayedInQueue);
        Assert.Equal(2, q.History.Count);

        for (var i = 0; i < 5; i++) q.Advance(QueueAdvance.UserSkip);
        Assert.Same(b[5], q.Current);
        Assert.Equal(5, q.PlayedInQueue);           // History stopped at its cap of 3
        Assert.Equal(3, q.History.Count);
    }

    [Fact]
    public void PlayedInQueue_steps_back_with_Back_and_is_unknown_once_Back_leaves_the_queue()
    {
        var q = new PlaybackQueue(); var a = Tracks(2); var b = Tracks(3);
        q.ReplaceAll(a, 0);
        q.ReplaceAll(b, 0);
        q.Advance(QueueAdvance.Natural);
        Assert.Equal(1, q.PlayedInQueue);

        q.Back();
        Assert.Same(b[0], q.Current);
        Assert.Equal(0, q.PlayedInQueue);

        q.Back();                                   // into the previous queue's history
        Assert.Same(a[0], q.Current);
        Assert.Null(q.PlayedInQueue);

        q.ReplaceAll(b, 1);
        Assert.Equal(0, q.PlayedInQueue);
    }

    [Fact]
    public void PlayedInQueue_restarts_on_a_RepeatAll_wrap_and_holds_on_a_RepeatOne_replay()
    {
        var q = new PlaybackQueue(); var t = Tracks(2);
        q.ReplaceAll(t, 0);
        q.RepeatMode = RepeatMode.One;
        q.Advance(QueueAdvance.Natural);
        Assert.Equal(0, q.PlayedInQueue);

        q.RepeatMode = RepeatMode.All;
        q.Advance(QueueAdvance.Natural);
        Assert.Equal(1, q.PlayedInQueue);
        q.Advance(QueueAdvance.Natural);            // wraps to t0
        Assert.Same(t[0], q.Current);
        Assert.Equal(0, q.PlayedInQueue);
    }

    [Fact]
    public void PlayedInQueue_drops_the_played_tracks_RemoveWhere_takes_out_of_history()
    {
        var q = new PlaybackQueue(); var a = Tracks(1); var t = Tracks(4);
        q.ReplaceAll(a, 0);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.Natural);
        q.Advance(QueueAdvance.Natural);            // t0, t1 played; t2 current; a0 from the queue before
        var a0 = a[0]; var t0 = t[0];

        q.RemoveWhere(x => x == t0 || x == a0);

        Assert.Equal(1, q.PlayedInQueue);
        Assert.Same(t[1], q.Back());
        Assert.Equal(0, q.PlayedInQueue);
    }

    [Fact]
    public void PlayedInQueue_round_trips_and_is_unknown_in_a_snapshot_without_it()
    {
        var q = new PlaybackQueue(); var t = Tracks(4);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.Natural);
        q.Advance(QueueAdvance.Natural);
        var byId = t.ToDictionary(x => x.Id);

        var snapshot = q.Snapshot();
        Assert.Equal(2, snapshot.PlayedInQueue);
        Assert.Equal(2, PlaybackQueue.Restore(snapshot, id => byId.GetValueOrDefault(id)).PlayedInQueue);

        var legacy = new PlaybackQueueState(t[2].Id, new[] { t[3].Id }, new[] { t[1].Id, t[0].Id },
            Array.Empty<Guid>(), RepeatMode.Off, false, Array.Empty<Guid>());
        Assert.Null(PlaybackQueue.Restore(legacy, id => byId.GetValueOrDefault(id)).PlayedInQueue);
    }

    // ── Add Before Current Song / History jump (Discord 11-07) ──

    [Fact]
    public void AddBeforeCurrent_puts_the_tracks_just_before_current_in_order_and_previous_plays_the_last()
    {
        var q = new PlaybackQueue(); var t = Tracks(3); var add = Tracks(2);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.UserSkip);                       // t1 plays, t0 played

        Assert.True(q.AddBeforeCurrent(add));

        Assert.Same(t[1], q.Current);                           // keeps playing
        Assert.Equal(new[] { add[1], add[0], t[0] }, q.History);
        Assert.Equal(new[] { t[2] }, q.UpNext);
        Assert.Equal(3, q.PlayedInQueue);                       // Track 4 of 5
        Assert.Same(add[1], q.Back());
        Assert.Same(add[0], q.Back());
        Assert.Same(t[0], q.Back());
        Assert.Equal(new[] { add[0], add[1], t[1], t[2] }, q.UpNext);
    }

    [Fact]
    public void AddBeforeCurrent_does_nothing_without_a_current_track()
    {
        var q = new PlaybackQueue(); var add = Tracks(1);

        Assert.False(q.AddBeforeCurrent(add));
        Assert.Empty(q.History);
        Assert.Null(q.Current);
    }

    [Fact]
    public void AddBeforeCurrent_keeps_the_history_cap()
    {
        var q = new PlaybackQueue(historyCap: 3); var t = Tracks(2); var add = Tracks(5);
        q.ReplaceAll(t, 0);

        q.AddBeforeCurrent(add);

        Assert.Equal(new[] { add[4], add[3], add[2] }, q.History);
    }

    [Fact]
    public void JumpBack_makes_the_played_track_current_and_returns_the_later_ones_to_upnext_without_copies()
    {
        var q = new PlaybackQueue(); var t = Tracks(5);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.Natural);
        q.Advance(QueueAdvance.Natural);
        q.Advance(QueueAdvance.Natural);                        // t3 plays; history t2 t1 t0

        Assert.Same(t[1], q.JumpBack(1));

        Assert.Equal(new[] { t[0] }, q.History);
        Assert.Equal(new[] { t[2], t[3], t[4] }, q.UpNext);
        Assert.Equal(1, q.PlayedInQueue);
        Assert.Equal(5, q.History.Count + 1 + q.UpNext.Count); // nothing duplicated or lost
    }

    [Fact]
    public void JumpBack_out_of_range_changes_nothing()
    {
        var q = new PlaybackQueue(); var t = Tracks(3);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.Natural);

        Assert.Null(q.JumpBack(1));
        Assert.Null(q.JumpBack(-1));
        Assert.Same(t[1], q.Current);
        Assert.Equal(new[] { t[0] }, q.History);
    }

    [Fact]
    public void JumpBack_after_the_queue_ran_out_plays_the_track_then_the_rest_again()
    {
        var q = new PlaybackQueue(); var t = Tracks(3);
        q.ReplaceAll(t, 0);
        q.Advance(QueueAdvance.Natural);
        q.Advance(QueueAdvance.Natural);
        q.Advance(QueueAdvance.Natural);                        // ran out: history t2 t1 t0, no current
        Assert.Null(q.Current);

        Assert.Same(t[1], q.JumpBack(1));

        Assert.Equal(new[] { t[2] }, q.UpNext);
        Assert.Equal(new[] { t[0] }, q.History);
    }

    // ── One-list moves across Current (Queue page with History on) ──

    private static PlaybackQueue Playing(List<Track> t, int played)
    {
        var q = new PlaybackQueue();
        q.ReplaceAll(t, 0);
        for (int i = 0; i < played; i++) q.Advance(QueueAdvance.Natural);
        return q;
    }

    [Fact]
    public void MoveInTimeline_upnext_song_above_current_goes_just_before_it()
    {
        var t = Tracks(5); var q = Playing(t, 1);              // [t0] t1 [t2 t3 t4]

        Assert.True(q.MoveInTimeline(3, 1));                  // t3 to where t1 is

        Assert.Same(t[1], q.Current);
        Assert.Equal(new[] { t[3], t[0] }, q.History);         // newest first
        Assert.Equal(new[] { t[2], t[4] }, q.UpNext);
        Assert.Equal(2, q.PlayedInQueue);
        Assert.Same(t[3], q.Back());
    }

    [Fact]
    public void MoveInTimeline_upnext_song_deep_into_history()
    {
        var t = Tracks(5); var q = Playing(t, 2);              // [t0 t1] t2 [t3 t4]

        q.MoveInTimeline(4, 0);                                // t4 before t0

        Assert.Equal(new[] { t[1], t[0], t[4] }, q.History);
        Assert.Equal(new[] { t[3] }, q.UpNext);
        Assert.Equal(3, q.PlayedInQueue);
    }

    [Fact]
    public void MoveInTimeline_played_song_below_current_plays_again_after_it()
    {
        var t = Tracks(5); var q = Playing(t, 2);              // [t0 t1] t2 [t3 t4]

        Assert.True(q.MoveInTimeline(0, 2));                   // t0 just after t2

        Assert.Same(t[2], q.Current);
        Assert.Equal(new[] { t[1] }, q.History);
        Assert.Equal(new[] { t[0], t[3], t[4] }, q.UpNext);
        Assert.Equal(1, q.PlayedInQueue);
        Assert.Same(t[0], q.Advance(QueueAdvance.UserSkip));
    }

    [Fact]
    public void MoveInTimeline_within_history_and_within_upnext()
    {
        var t = Tracks(6); var q = Playing(t, 2);              // [t0 t1] t2 [t3 t4 t5]

        q.MoveInTimeline(0, 1);                                // [t1 t0]
        Assert.Equal(new[] { t[0], t[1] }, q.History);
        q.MoveInTimeline(3, 5);                                // [t4 t5 t3]
        Assert.Equal(new[] { t[4], t[5], t[3] }, q.UpNext);
        Assert.Same(t[2], q.Current);
        Assert.Equal(2, q.PlayedInQueue);
    }

    [Fact]
    public void MoveInTimeline_refuses_current_itself_out_of_range_and_a_stopped_queue()
    {
        var t = Tracks(3); var q = Playing(t, 1);              // [t0] t1 [t2]

        Assert.False(q.MoveInTimeline(1, 0));                  // Current does not move itself
        Assert.False(q.MoveInTimeline(0, 3));
        Assert.False(q.MoveInTimeline(-1, 0));
        Assert.Equal(new[] { t[0] }, q.History);
        Assert.Equal(new[] { t[2] }, q.UpNext);

        q.Advance(QueueAdvance.Natural); q.Advance(QueueAdvance.Natural);   // ran out
        Assert.False(q.MoveInTimeline(0, 1));
    }

    [Fact]
    public void MoveInTimeline_keeps_a_duplicate_of_current_where_it_was()
    {
        var a = new Track { Title = "a" }; var b = new Track { Title = "b" };
        var q = new PlaybackQueue();
        q.ReplaceAll(new[] { a, b, a }, 0);
        q.Advance(QueueAdvance.Natural);
        q.Advance(QueueAdvance.Natural);                       // [a b] a []  (the same a played and current)

        q.MoveInTimeline(1, 0);                                // b before the played a

        Assert.Same(a, q.Current);
        Assert.Equal(new[] { a, b }, q.History);
        Assert.Empty(q.UpNext);
    }

    [Fact]
    public void MoveInTimeline_played_song_back_in_upnext_survives_shuffle_off()
    {
        var t = Tracks(4); var q = Playing(t, 1);              // [t0] t1 [t2 t3]
        q.SetShuffle(true, new Random(1));

        q.MoveInTimeline(0, 1);                                // t0 after t1
        q.SetShuffle(false);

        Assert.Contains(t[0], q.UpNext);
        Assert.Equal(3, q.UpNext.Count);
    }
}

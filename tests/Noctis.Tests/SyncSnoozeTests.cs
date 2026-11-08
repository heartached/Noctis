using System;
using Noctis.Models;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Snooze for a Month travels in TrackSyncState as an optional "snoozedUntil": absent means the
/// sender says nothing (older apps, never-snoozed songs keep their old payload byte for byte), a
/// past date means not snoozed (how an Unsnooze travels).
/// </summary>
public class SyncSnoozeTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnOldPayload_WithoutTheField_ReadsAsSayingNothing()
    {
        const string old = "{\"favorite\":true,\"rating\":4,\"disliked\":false,\"playCount\":12,\"lastPlayed\":\"2026-11-01T10:00:00Z\"}";
        var state = SyncJson.Deserialize<TrackSyncState>(old)!;
        Assert.True(state.Favorite);
        Assert.Equal(4, state.Rating);
        Assert.Null(state.SnoozedUntil);
    }

    [Fact]
    public void ANeverSnoozedSong_KeepsTheOldPayload_ASnoozedOneAddsTheField()
    {
        var track = new Track { Rating = 2, PlayCount = 7 };
        var json = SyncJson.Serialize(TrackSyncState.From(track));
        Assert.DoesNotContain("snoozedUntil", json);
        Assert.Equal("{\"favorite\":false,\"rating\":2,\"disliked\":false,\"playCount\":7}", json);

        track.SnoozedUntil = Now.AddDays(30);
        var snoozed = SyncJson.Serialize(TrackSyncState.From(track));
        Assert.Contains("\"snoozedUntil\":\"2026-12-07T12:00:00Z\"", snoozed);
        Assert.Equal(Now.AddDays(30), SyncJson.Deserialize<TrackSyncState>(snoozed)!.SnoozedUntil);
    }

    [Fact]
    public void AnUnsnooze_SaysNotSnoozed_OnlyWhenTheSongWentOutSnoozed()
    {
        var track = new Track();
        var sentSnoozed = new TrackSyncState(false, 0, false, 0, null, null, Now.AddDays(3));
        var sentPlain = new TrackSyncState(false, 0, false, 0, null, null);

        Assert.Equal(TrackSyncState.NotSnoozed, TrackSyncState.From(track, sentSnoozed).SnoozedUntil);
        Assert.Null(TrackSyncState.From(track, sentPlain).SnoozedUntil);
        Assert.Null(TrackSyncState.From(track, null).SnoozedUntil);
        // Said once, it stays said: the next record of the same song is the same payload.
        var unsnoozed = TrackSyncState.From(track, sentSnoozed);
        Assert.Equal(unsnoozed, TrackSyncState.From(track, unsnoozed));
    }

    [Fact]
    public void APushedSnooze_IsClamped_PastIsNotSnoozed_AndAbsentStaysAbsent()
    {
        Assert.Null(LibrarySyncService.ClampSnooze(null, Now));
        Assert.Equal(TrackSyncState.NotSnoozed, LibrarySyncService.ClampSnooze(Now.AddDays(-2), Now));
        Assert.Equal(TrackSyncState.NotSnoozed, LibrarySyncService.ClampSnooze(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), Now));
        Assert.Equal(Now.AddDays(30), LibrarySyncService.ClampSnooze(Now.AddDays(30), Now));
        Assert.Equal(Now + LibrarySyncService.MaxSnooze, LibrarySyncService.ClampSnooze(Now.AddYears(50), Now));
    }

    [Fact]
    public void ThePhonesParser_ReadsTheField_AndAnOldPayloadWithoutIt()
    {
        using var withField = System.Text.Json.JsonDocument.Parse("{\"favorite\":false,\"rating\":0,\"disliked\":false,\"playCount\":0,\"snoozedUntil\":\"2026-12-07T12:00:00Z\"}");
        using var without = System.Text.Json.JsonDocument.Parse("{\"favorite\":true,\"rating\":3,\"disliked\":false,\"playCount\":0}");

        Assert.Equal(Now.AddDays(30), Noctis.Mobile.Services.Account.NoctisAccountService.ParseTrackState(withField.RootElement, Now).SnoozedUntil);
        var old = Noctis.Mobile.Services.Account.NoctisAccountService.ParseTrackState(without.RootElement, Now);
        Assert.Null(old.SnoozedUntil);
        Assert.Equal(3, old.Rating);
    }
}

using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class AutoMixTransitionPlannerTests
{
    [Fact]
    public void CreateTransitionPlan_SkipsRepeatOne()
    {
        var plan = AutoMixTransitionPlanner.CreateTransitionPlan(
            Track("One", 1, bpm: 120, key: "8A"),
            Track("Two", 2, bpm: 122, key: "9A"),
            Options(repeatMode: RepeatMode.One));

        Assert.Equal(AutoMixTransitionType.None, plan.TransitionType);
        Assert.Contains("repeat-one", plan.Reason);
    }

    [Fact]
    public void CreateTransitionPlan_SkipsSequentialAlbumByDefault()
    {
        var albumId = Guid.NewGuid();
        var plan = AutoMixTransitionPlanner.CreateTransitionPlan(
            Track("One", 1, albumId, bpm: 120, key: "8A"),
            Track("Two", 2, albumId, bpm: 122, key: "9A"),
            Options());

        Assert.False(plan.IsEnabled);
        Assert.Contains("same album sequential", plan.Reason);
    }

    [Fact]
    public void CreateTransitionPlan_UsesBeatMatchedCrossfadeForCompatibleMetadata()
    {
        var plan = AutoMixTransitionPlanner.CreateTransitionPlan(
            Track("One", 1, bpm: 120, key: "8A"),
            Track("Two", 2, bpm: 124, key: "9A"),
            Options(avoidAlbums: false));

        Assert.Equal(AutoMixTransitionType.BeatMatchedCrossfade, plan.TransitionType);
        Assert.True(plan.UsedBpmData);
        Assert.True(plan.UsedKeyData);
        Assert.Equal(AutoMixFadeCurve.EqualPower, plan.FadeCurve);
    }

    [Fact]
    public void CreateTransitionPlan_FallsBackWhenBpmIsMissing()
    {
        var plan = AutoMixTransitionPlanner.CreateTransitionPlan(
            Track("One", 1, bpm: 0, key: ""),
            Track("Two", 2, bpm: 124, key: "9A"),
            Options(avoidAlbums: false));

        Assert.Equal(AutoMixTransitionType.SimpleCrossfade, plan.TransitionType);
        Assert.True(plan.MissingBpmData);
        Assert.True(plan.MissingKeyData);
    }

    [Fact]
    public void IsContentTypeExcluded_CatchesSpokenAndClassical()
    {
        Assert.True(AutoMixTransitionPlanner.IsContentTypeExcluded(
            Track("Podcast", 1, genre: "Podcast")));
        Assert.True(AutoMixTransitionPlanner.IsContentTypeExcluded(
            Track("Movement", 1, genre: "Classical")));
    }

    [Fact]
    public void ClampTransitionDuration_RespectsStrengthAndTrackLength()
    {
        var duration = AutoMixTransitionPlanner.ClampTransitionDuration(
            TimeSpan.FromSeconds(30),
            Track("One", 1, duration: TimeSpan.FromSeconds(60)),
            Track("Two", 2, duration: TimeSpan.FromSeconds(70)),
            AutoMixStrength.Extended);

        Assert.Equal(TimeSpan.FromSeconds(12), duration);
    }

    [Fact]
    public void FadeMath_EqualPower_ReachesExpectedEndpoints()
    {
        var start = AutoMixFadeMath.GetFadeFactors(0, AutoMixFadeCurve.EqualPower);
        var middle = AutoMixFadeMath.GetFadeFactors(0.5, AutoMixFadeCurve.EqualPower);
        var end = AutoMixFadeMath.GetFadeFactors(1, AutoMixFadeCurve.EqualPower);

        Assert.Equal(1, start.Out, precision: 6);
        Assert.Equal(0, start.In, precision: 6);
        Assert.True(middle.Out > 0.70 && middle.In > 0.70);
        Assert.Equal(0, end.Out, precision: 6);
        Assert.Equal(1, end.In, precision: 6);
    }

    [Fact]
    public void PreparedValidation_RejectsTrackMismatch()
    {
        var prepared = Prepared(Track("One", 1));
        var validation = AutoMixPreparedTransitionValidator.Validate(
            prepared,
            Track("Two", 2),
            10,
            false,
            RepeatMode.Off,
            AutoMixTransitionMode.AutoMix,
            42);

        Assert.False(validation.IsValid);
        Assert.Contains("id mismatch", validation.Reason);
    }

    [Fact]
    public void PreparedValidation_RejectsQueueChangesAfterPreload()
    {
        var next = Track("Two", 2);
        var validation = AutoMixPreparedTransitionValidator.Validate(
            Prepared(next),
            next,
            11,
            false,
            RepeatMode.Off,
            AutoMixTransitionMode.AutoMix,
            42);

        Assert.False(validation.IsValid);
        Assert.Contains("queue changed", validation.Reason);
    }

    [Fact]
    public void PreparedValidation_RejectsRepeatOne()
    {
        var next = Track("Two", 2);
        var validation = AutoMixPreparedTransitionValidator.Validate(
            Prepared(next),
            next,
            10,
            false,
            RepeatMode.One,
            AutoMixTransitionMode.AutoMix,
            42);

        Assert.False(validation.IsValid);
        Assert.Contains("repeat-one", validation.Reason);
    }

    [Fact]
    public void PreparedValidation_RejectsManualSkipSessionChange()
    {
        var next = Track("Two", 2);
        var validation = AutoMixPreparedTransitionValidator.Validate(
            Prepared(next),
            next,
            10,
            false,
            RepeatMode.Off,
            AutoMixTransitionMode.AutoMix,
            43);

        Assert.False(validation.IsValid);
        Assert.Contains("session changed", validation.Reason);
    }

    [Fact]
    public void PreparedValidation_AcceptsMatchingSnapshot()
    {
        var next = Track("Two", 2);
        var validation = AutoMixPreparedTransitionValidator.Validate(
            Prepared(next),
            next,
            10,
            false,
            RepeatMode.Off,
            AutoMixTransitionMode.AutoMix,
            42);

        Assert.True(validation.IsValid);
    }

    private static AutoMixPlannerOptions Options(
        RepeatMode repeatMode = RepeatMode.Off,
        bool shuffle = false,
        bool avoidAlbums = true) =>
        new(
            AutoMixTransitionMode.AutoMix,
            AutoMixStrength.Balanced,
            true,
            avoidAlbums,
            true,
            repeatMode,
            shuffle,
            false);

    private static AutoMixPreparedTransitionSnapshot Prepared(Track track) =>
        new(
            track.Id,
            track.FilePath,
            10,
            false,
            RepeatMode.Off,
            AutoMixTransitionMode.AutoMix,
            42);

    private static Track Track(
        string title,
        int trackNumber,
        Guid? albumId = null,
        int bpm = 120,
        string key = "8A",
        string genre = "Pop",
        TimeSpan? duration = null) =>
        new()
        {
            Title = title,
            Artist = "Artist",
            AlbumArtist = "Artist",
            Album = "Album",
            AlbumId = albumId ?? Guid.NewGuid(),
            TrackNumber = trackNumber,
            DiscNumber = 1,
            Bpm = bpm,
            MusicalKey = key,
            Genre = genre,
            MediaKind = "Music",
            Duration = duration ?? TimeSpan.FromMinutes(3),
            FilePath = Path.Combine("C:\\Music", $"{title}.flac")
        };
}

using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for defects found in the 2026-07-24 audit. Each test fails against
/// the pre-fix behaviour, so they document the bug as much as the fix.
/// </summary>
public class AuditRegressionTests
{
    // ── TitleFormatter: filename sanitization ───────────────────────────────

    [Theory]
    [InlineData("../../../../Documents/taxes")]
    [InlineData("..\\..\\evil")]
    [InlineData("sub/dir/name")]
    public void SanitizeForFilename_StripsPathSeparators(string hostile)
    {
        var result = TitleFormatter.SanitizeForFilename(hostile);

        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
    }

    [Fact]
    public void SanitizeForFilename_RejectsRelativeSegments()
    {
        // ".." must not survive as a directory reference in any form.
        Assert.DoesNotContain("..", TitleFormatter.SanitizeForFilename(".."));
        Assert.DoesNotContain("..", TitleFormatter.SanitizeForFilename("../.."));
    }

    [Fact]
    public void SanitizeForFilename_StripsLeadingDash()
    {
        // A leading '-' makes the value look like a CLI option to any tool the path is
        // passed to positionally — ffmpeg reads "-y" as a flag, not an output path.
        Assert.False(TitleFormatter.SanitizeForFilename("-y").StartsWith('-'));
    }

    [Fact]
    public void Expand_DoesNotLetTagValuesEscapeTheDirectory()
    {
        var track = new Track { Artist = "Artist", Title = "../../escape" };

        var expanded = TitleFormatter.Expand("%artist% - %title%", track, sanitizeForFilename: true);

        Assert.DoesNotContain('/', expanded);
        Assert.DoesNotContain('\\', expanded);
    }

    // ── KeyDetector: degenerate input must not report a key ─────────────────

    [Fact]
    public void KeyDetector_ReturnsEmptyForSilence()
    {
        var silence = new float[48000];

        var (key, confidence) = KeyDetector.Detect(silence, 48000, silence.Length);

        Assert.Equal(string.Empty, key);
        Assert.Equal(0, confidence);
    }

    [Fact]
    public void KeyDetector_ReturnsEmptyForNaNSamples()
    {
        // ffmpeg passes NaN straight through for a corrupt pcm_f32le WAV. The old code
        // fell through every comparison and returned PitchNames[0] — "C major" — which
        // was then written to the library and into the user's file tags.
        var buffer = new float[48000];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = float.NaN;

        var (key, _) = KeyDetector.Detect(buffer, 48000, buffer.Length);

        Assert.Equal(string.Empty, key);
    }

    // ── BpmDetector: bounds ─────────────────────────────────────────────────

    [Fact]
    public void BpmDetector_HandlesBufferShorterThanAFrame()
    {
        // 513..1023 samples drove frames=1 and read past `count` into mono[0..1023].
        var buffer = new float[600];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = 0.25f;

        var (bpm, _) = BpmDetector.Detect(buffer, 44100, buffer.Length);

        Assert.Equal(0, bpm);
    }

    // ── AutoMixKeyTempo: octave matching must not saturate ──────────────────

    [Fact]
    public void IsTempoCompatible_RejectsBoundaryFalsePositive()
    {
        // 240 vs 128: the doubled candidate 256 used to clamp back to exactly 240,
        // giving a distance of 0 — so every track at 120+ BPM was "compatible" with any
        // 240 BPM track and the planner picked a beat-matched crossfade regardless.
        Assert.False(AutoMixKeyTempo.IsTempoCompatible(240, 128));
    }

    [Fact]
    public void IsTempoCompatible_StillMatchesGenuineOctaves()
    {
        Assert.True(AutoMixKeyTempo.IsTempoCompatible(140, 70));
        Assert.True(AutoMixKeyTempo.IsTempoCompatible(120, 120));
    }

    // ── AutoMixTransitionPlanner: StopTimeMs must not be subtracted twice ───

    [Fact]
    public void EstimateSilenceProfile_DoesNotCapStartTrim()
    {
        // StartTimeMs is a user-set trim point, not an estimate. Capping it at 5s made
        // AutoMix play 15s of an intro the user had removed.
        var track = new Track { StartTimeMs = 20000, Duration = TimeSpan.FromMinutes(4) };

        var profile = AutoMixTransitionPlanner.EstimateSilenceProfile(track);

        Assert.Equal(TimeSpan.FromMilliseconds(20000), profile.StartSilence);
    }

    // ── DuplicateMatcher: different releases are not duplicates ─────────────

    [Fact]
    public void DuplicateMatcher_DoesNotGroupDifferentAlbums()
    {
        var studio = new Track
        {
            Id = Guid.NewGuid(), Artist = "Artist", Title = "Song",
            Album = "Studio Album", Duration = TimeSpan.FromMinutes(3),
            FilePath = "/music/studio/song.flac"
        };
        var hits = new Track
        {
            Id = Guid.NewGuid(), Artist = "Artist", Title = "Song",
            Album = "Greatest Hits", Duration = TimeSpan.FromMinutes(3),
            FilePath = "/music/hits/song.flac"
        };

        var groups = DuplicateMatcher.FindDuplicates(new[] { studio, hits });

        Assert.Empty(groups);
    }

    [Fact]
    public void DuplicateMatcher_StillGroupsEditionsOfTheSameAlbum()
    {
        var standard = new Track
        {
            Id = Guid.NewGuid(), Artist = "Artist", Title = "Song",
            Album = "The Album", Duration = TimeSpan.FromMinutes(3),
            FilePath = "/music/standard/song.flac"
        };
        var deluxe = new Track
        {
            Id = Guid.NewGuid(), Artist = "Artist", Title = "Song",
            Album = "The Album (Deluxe Edition)", Duration = TimeSpan.FromMinutes(3),
            FilePath = "/music/deluxe/song.flac"
        };

        var groups = DuplicateMatcher.FindDuplicates(new[] { standard, deluxe });

        Assert.Single(groups);
    }

    // ── SmartPlaylistEvaluator: "never played" must be expressible ──────────

    [Fact]
    public void GetOperatorsForField_OffersNullCheckForLastPlayed()
    {
        var operators = SmartPlaylistEvaluator.GetOperatorsForField(RuleField.LastPlayed);

        Assert.Contains(RuleOperator.IsFalse, operators);
    }

    [Fact]
    public void Evaluate_RandomSortIsStableAcrossReloads()
    {
        // Evaluate runs on every search keystroke and sort change; an unseeded shuffle
        // replaced the playlist's contents each time.
        var tracks = Enumerable.Range(0, 50)
            .Select(i => new Track { Id = Guid.NewGuid(), Title = $"T{i}", PlayCount = i })
            .ToList();

        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            IsSmartPlaylist = true,
            MatchAll = true,
            LimitCount = 10,
            SortBy = SmartPlaylistSortBy.Random,
            Rules = { new SmartPlaylistRule { Field = RuleField.PlayCount, Operator = RuleOperator.GreaterThan, Value = "-1" } }
        };

        var first = SmartPlaylistEvaluator.Evaluate(playlist, tracks).Select(t => t.Id).ToList();
        var second = SmartPlaylistEvaluator.Evaluate(playlist, tracks).Select(t => t.Id).ToList();

        Assert.Equal(first, second);
    }
}

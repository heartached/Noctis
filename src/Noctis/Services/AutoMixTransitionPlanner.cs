using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

public static class AutoMixTransitionPlanner
{
    private static readonly string[] ExcludedContentTerms =
    {
        "podcast", "audiobook", "audio book", "spoken", "speech", "interview",
        "classical", "meditation", "mindfulness", "live set", "dj set", "concert"
    };

    public static bool ShouldAutoMix(Track current, Track next, AutoMixPlannerOptions options) =>
        CreateTransitionPlan(current, next, options).IsEnabled;

    public static AutoMixTransitionPlan CreateTransitionPlan(
        Track current,
        Track next,
        AutoMixPlannerOptions options)
    {
        var currentSilence = EstimateSilenceProfile(current);
        var nextSilence = EstimateSilenceProfile(next);

        if (options.Mode == AutoMixTransitionMode.Off)
            return AutoMixTransitionPlan.None("AutoMix skipped: transition mode is off", currentSilence, nextSilence);

        if (options.ManualTransition)
            return AutoMixTransitionPlan.None("AutoMix skipped: manual skip/previous", currentSilence, nextSilence);

        if (options.RepeatMode == RepeatMode.One)
            return AutoMixTransitionPlan.None("AutoMix skipped: repeat-one enabled", currentSilence, nextSilence);

        if (IsContentTypeExcluded(current) || IsContentTypeExcluded(next))
            return AutoMixTransitionPlan.None("AutoMix skipped: content type or genre excluded", currentSilence, nextSilence);

        if (options.AvoidAutoMixForAlbums && !options.ShuffleEnabled && IsSequentialAlbumTrack(current, next))
            return AutoMixTransitionPlan.None("AutoMix skipped: same album sequential track", currentSilence, nextSilence);

        var currentSeconds = GetEffectiveDurationSeconds(current);
        var nextSeconds = GetEffectiveDurationSeconds(next);
        if (currentSeconds < 45 || nextSeconds < 45)
            return AutoMixTransitionPlan.None("AutoMix skipped: track too short", currentSilence, nextSilence);

        if (options.Mode == AutoMixTransitionMode.Crossfade)
            return CreateCrossfadePlan(
                current,
                next,
                options,
                currentSilence,
                nextSilence,
                AutoMixTransitionType.SimpleCrossfade,
                TimeSpan.FromSeconds(6),
                "AutoMix planned: SimpleCrossfade 6s");

        var missingBpm = current.Bpm <= 0 || next.Bpm <= 0;
        var missingKey = NormalizeCamelotKey(current.MusicalKey) == null ||
                         NormalizeCamelotKey(next.MusicalKey) == null;
        var silenceTrim = options.RemoveSilenceBetweenSongs &&
                          (currentSilence.EndSilence >= TimeSpan.FromSeconds(1.5) ||
                           nextSilence.StartSilence >= TimeSpan.FromSeconds(1.5));

        if (silenceTrim && missingBpm)
        {
            return CreateCrossfadePlan(
                current,
                next,
                options,
                currentSilence,
                nextSilence,
                AutoMixTransitionType.SilenceTrim,
                TimeSpan.FromSeconds(0),
                "AutoMix planned: SilenceTrim; missing BPM/key fallback",
                missingBpm,
                missingKey);
        }

        if (missingBpm)
        {
            return CreateCrossfadePlan(
                current,
                next,
                options,
                currentSilence,
                nextSilence,
                AutoMixTransitionType.SimpleCrossfade,
                TimeSpan.FromSeconds(StrengthBaseSeconds(options.Strength) - 2),
                "AutoMix fallback: missing BPM/key",
                missingBpm,
                missingKey);
        }

        var tempoCompatible = IsTempoCompatible(current.Bpm, next.Bpm);
        var keyCompatible = !options.BeatMatchWhenMetadataAvailable ||
                            missingKey ||
                            AreKeysCompatible(current.MusicalKey, next.MusicalKey);

        if (options.BeatMatchWhenMetadataAvailable && tempoCompatible && keyCompatible)
        {
            var duration = TimeSpan.FromSeconds(StrengthBaseSeconds(options.Strength) + (missingKey ? 0 : 2));
            return CreateCrossfadePlan(
                current,
                next,
                options,
                currentSilence,
                nextSilence,
                AutoMixTransitionType.BeatMatchedCrossfade,
                duration,
                $"AutoMix planned: BeatMatchedCrossfade {duration.TotalSeconds:0}s",
                false,
                missingKey,
                usedBpm: true,
                usedKey: !missingKey);
        }

        if (tempoCompatible || keyCompatible)
        {
            var duration = TimeSpan.FromSeconds(StrengthBaseSeconds(options.Strength) - 1);
            return CreateCrossfadePlan(
                current,
                next,
                options,
                currentSilence,
                nextSilence,
                AutoMixTransitionType.SimpleCrossfade,
                duration,
                $"AutoMix planned: SimpleCrossfade {duration.TotalSeconds:0}s",
                false,
                missingKey,
                usedBpm: true,
                usedKey: !missingKey);
        }

        if (silenceTrim)
        {
            return CreateCrossfadePlan(
                current,
                next,
                options,
                currentSilence,
                nextSilence,
                AutoMixTransitionType.SilenceTrim,
                TimeSpan.Zero,
                "AutoMix planned: SilenceTrim; tempo/key mismatch",
                false,
                missingKey,
                usedBpm: true,
                usedKey: !missingKey);
        }

        return CreateCrossfadePlan(
            current,
            next,
            options,
            currentSilence,
            nextSilence,
            AutoMixTransitionType.SafeFade,
            TimeSpan.FromSeconds(2),
            "AutoMix planned: SafeFade 2s",
            false,
            missingKey,
            usedBpm: true,
            usedKey: !missingKey);
    }

    public static bool IsSequentialAlbumTrack(Track current, Track next) =>
        current.AlbumId != Guid.Empty &&
        current.AlbumId == next.AlbumId &&
        current.DiscNumber == next.DiscNumber &&
        current.TrackNumber > 0 &&
        next.TrackNumber == current.TrackNumber + 1;

    public static bool IsContentTypeExcluded(Track track)
    {
        var values = new[]
        {
            track.MediaKind,
            track.Genre,
            track.Grouping,
            track.WorkName,
            track.Comment
        };

        return values.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            ExcludedContentTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsTempoCompatible(int currentBpm, int nextBpm) =>
        currentBpm > 0 &&
        nextBpm > 0 &&
        GetNormalizedBpmDifference(currentBpm, nextBpm) <= 10;

    public static TimeSpan ClampTransitionDuration(TimeSpan requested, Track current, Track next, AutoMixStrength strength)
    {
        var maxByStrength = strength switch
        {
            AutoMixStrength.Subtle => 6,
            AutoMixStrength.Extended => 12,
            _ => 9
        };

        var currentLimit = Math.Max(1, GetEffectiveDurationSeconds(current) * 0.20);
        var nextLimit = Math.Max(1, GetEffectiveDurationSeconds(next) * 0.20);
        var safeMax = Math.Min(maxByStrength, Math.Min(currentLimit, nextLimit));
        return TimeSpan.FromSeconds(Math.Clamp(requested.TotalSeconds, 1, safeMax));
    }

    public static AudioSilenceProfile EstimateSilenceProfile(Track track)
    {
        var start = track.StartTimeMs > 0
            ? TimeSpan.FromMilliseconds(Math.Min(track.StartTimeMs, 5000))
            : TimeSpan.Zero;

        var end = TimeSpan.Zero;
        if (track.StopTimeMs > 0 && track.Duration > TimeSpan.Zero)
        {
            var trailingMs = track.Duration.TotalMilliseconds - track.StopTimeMs;
            if (trailingMs > 0)
                end = TimeSpan.FromMilliseconds(Math.Min(trailingMs, 5000));
        }

        // TODO: Replace this metadata-only estimate with waveform/PCM analysis when
        // Noctis has a background audio analysis cache. The planner treats this as
        // advisory so playback remains safe without exact silence data.
        return new AudioSilenceProfile(start, end, true);
    }

    private static AutoMixTransitionPlan CreateCrossfadePlan(
        Track current,
        Track next,
        AutoMixPlannerOptions options,
        AudioSilenceProfile currentSilence,
        AudioSilenceProfile nextSilence,
        AutoMixTransitionType type,
        TimeSpan requestedDuration,
        string reason,
        bool missingBpm = false,
        bool missingKey = false,
        bool usedBpm = false,
        bool usedKey = false)
    {
        var useSilenceTrim = options.RemoveSilenceBetweenSongs &&
                             (currentSilence.EndSilence > TimeSpan.Zero ||
                              nextSilence.StartSilence > TimeSpan.Zero);
        var duration = type == AutoMixTransitionType.SilenceTrim
            ? TimeSpan.Zero
            : ClampTransitionDuration(requestedDuration, current, next, options.Strength);
        var transitionEnd = GetTransitionEnd(current) - currentSilence.EndSilence;
        if (transitionEnd < TimeSpan.Zero)
            transitionEnd = TimeSpan.Zero;

        var fadeStart = transitionEnd - duration;
        if (fadeStart < TimeSpan.Zero)
            fadeStart = TimeSpan.Zero;

        return new AutoMixTransitionPlan(
            type,
            duration,
            fadeStart,
            nextSilence.StartSilence,
            type == AutoMixTransitionType.BeatMatchedCrossfade
                ? AutoMixFadeCurve.EqualPower
                : AutoMixFadeCurve.SmoothEase,
            reason,
            useSilenceTrim,
            usedBpm,
            usedKey,
            missingBpm,
            missingKey,
            currentSilence,
            nextSilence);
    }

    private static TimeSpan GetTransitionEnd(Track current)
    {
        if (current.StopTimeMs > 0)
            return TimeSpan.FromMilliseconds(current.StopTimeMs);
        return current.Duration;
    }

    private static int StrengthBaseSeconds(AutoMixStrength strength) => strength switch
    {
        AutoMixStrength.Subtle => 5,
        AutoMixStrength.Extended => 10,
        _ => 7
    };

    private static double GetEffectiveDurationSeconds(Track track)
    {
        var durationMs = track.Duration.TotalMilliseconds;
        if (track.StopTimeMs > 0)
            durationMs = Math.Min(durationMs, track.StopTimeMs);
        if (track.StartTimeMs > 0 && track.StartTimeMs < durationMs)
            durationMs -= track.StartTimeMs;
        return Math.Max(0, durationMs / 1000d);
    }

    private static double GetNormalizedBpmDifference(int currentBpm, int nextBpm)
    {
        var current = Math.Clamp(currentBpm, 40, 240);
        var candidates = new[]
        {
            Math.Clamp(nextBpm, 40, 240),
            Math.Clamp(nextBpm * 2, 40, 240),
            Math.Clamp(nextBpm / 2.0, 40, 240)
        };

        return candidates.Min(candidate => Math.Abs(current - candidate));
    }

    private static bool AreKeysCompatible(string? currentKey, string? nextKey)
    {
        var current = NormalizeCamelotKey(currentKey);
        var next = NormalizeCamelotKey(nextKey);
        if (current == null || next == null)
            return true;

        if (current.Value.Number == next.Value.Number)
            return true;

        return current.Value.Mode == next.Value.Mode &&
               Math.Abs(current.Value.Number - next.Value.Number) is 1 or 11;
    }

    private static (int Number, char Mode)? NormalizeCamelotKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var normalized = key.Trim().ToUpperInvariant().Replace(" ", "");
        var match = Regex.Match(normalized, @"^(\d{1,2})([AB])$");
        if (!match.Success)
        {
            normalized = normalized
                .Replace("MINOR", "M")
                .Replace("MIN", "M")
                .Replace("MAJOR", "")
                .Replace("MAJ", "")
                .Replace("-", "")
                .Replace("_", "");

            var isMinor = normalized.EndsWith("M", StringComparison.Ordinal);
            var root = isMinor ? normalized[..^1] : normalized;
            return (root, isMinor) switch
            {
                ("G#" or "AB", true) => (1, 'A'),
                ("D#" or "EB", true) => (2, 'A'),
                ("A#" or "BB", true) => (3, 'A'),
                ("F", true) => (4, 'A'),
                ("C", true) => (5, 'A'),
                ("G", true) => (6, 'A'),
                ("D", true) => (7, 'A'),
                ("A", true) => (8, 'A'),
                ("E", true) => (9, 'A'),
                ("B", true) => (10, 'A'),
                ("F#" or "GB", true) => (11, 'A'),
                ("C#" or "DB", true) => (12, 'A'),
                ("B" or "CB", false) => (1, 'B'),
                ("F#" or "GB", false) => (2, 'B'),
                ("C#" or "DB", false) => (3, 'B'),
                ("G#" or "AB", false) => (4, 'B'),
                ("D#" or "EB", false) => (5, 'B'),
                ("A#" or "BB", false) => (6, 'B'),
                ("F", false) => (7, 'B'),
                ("C", false) => (8, 'B'),
                ("G", false) => (9, 'B'),
                ("D", false) => (10, 'B'),
                ("A", false) => (11, 'B'),
                ("E", false) => (12, 'B'),
                _ => null
            };
        }

        var number = int.Parse(match.Groups[1].Value);
        if (number < 1 || number > 12)
            return null;

        return (number, match.Groups[2].Value[0]);
    }
}

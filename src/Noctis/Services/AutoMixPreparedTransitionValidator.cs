using Noctis.Models;

namespace Noctis.Services;

public sealed record AutoMixPreparedTransitionSnapshot(
    Guid TrackId,
    string FilePath,
    long QueueVersion,
    bool ShuffleEnabled,
    RepeatMode RepeatMode,
    AutoMixTransitionMode TransitionMode,
    long PlaybackSessionId);

public sealed record AutoMixPreparedTransitionValidation(bool IsValid, string Reason)
{
    public static AutoMixPreparedTransitionValidation Valid { get; } = new(true, "valid");
    public static AutoMixPreparedTransitionValidation Invalid(string reason) => new(false, reason);
}

public static class AutoMixPreparedTransitionValidator
{
    public static AutoMixPreparedTransitionValidation Validate(
        AutoMixPreparedTransitionSnapshot? prepared,
        Track? nextTrack,
        long queueVersion,
        bool shuffleEnabled,
        RepeatMode repeatMode,
        AutoMixTransitionMode transitionMode,
        long playbackSessionId)
    {
        if (prepared == null)
            return AutoMixPreparedTransitionValidation.Invalid("no prepared track");

        if (transitionMode == AutoMixTransitionMode.Off)
            return AutoMixPreparedTransitionValidation.Invalid("AutoMix disabled");

        if (repeatMode == RepeatMode.One)
            return AutoMixPreparedTransitionValidation.Invalid("repeat-one enabled");

        if (nextTrack == null)
            return AutoMixPreparedTransitionValidation.Invalid("next track missing");

        if (string.IsNullOrWhiteSpace(nextTrack.FilePath))
            return AutoMixPreparedTransitionValidation.Invalid("next track path missing");

        if (prepared.TrackId != nextTrack.Id)
            return AutoMixPreparedTransitionValidation.Invalid("prepared track id mismatch");

        if (!TryGetFullPath(prepared.FilePath, out var preparedPath) ||
            !TryGetFullPath(nextTrack.FilePath, out var nextPath) ||
            !string.Equals(preparedPath, nextPath, StringComparison.OrdinalIgnoreCase))
            return AutoMixPreparedTransitionValidation.Invalid("prepared track path mismatch");

        if (prepared.QueueVersion != queueVersion)
            return AutoMixPreparedTransitionValidation.Invalid("queue changed after preload");

        if (prepared.ShuffleEnabled != shuffleEnabled)
            return AutoMixPreparedTransitionValidation.Invalid("shuffle changed after preload");

        if (prepared.RepeatMode != repeatMode)
            return AutoMixPreparedTransitionValidation.Invalid("repeat changed after preload");

        if (prepared.TransitionMode != transitionMode)
            return AutoMixPreparedTransitionValidation.Invalid("transition mode changed after preload");

        if (prepared.PlaybackSessionId != playbackSessionId)
            return AutoMixPreparedTransitionValidation.Invalid("playback session changed after preload");

        return AutoMixPreparedTransitionValidation.Valid;
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

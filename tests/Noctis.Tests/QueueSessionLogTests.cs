using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Andre, 10-03): "on Shuffle, Next sometimes does nothing". The Copy Logs export
/// showed 33 × "Next | queueLen=0" but nothing of how the queue got empty: only Playback
/// entries reached the session log, and every queue action (song click, Shuffle, Add to
/// Queue) is a Queue entry.
/// </summary>
public class QueueSessionLogTests
{
    [Fact]
    public void QueueEntries_ReachTheSessionLog_UnderTheirOwnTag()
    {
        var loggerOn = DebugLogger.IsEnabled;
        var mirror = DebugLogger.MirrorPlaybackToSessionLog;
        try
        {
            DebugLogger.IsEnabled = true;
            DebugLogger.MirrorPlaybackToSessionLog = true;
            var tag = Guid.NewGuid().ToString("N");

            DebugLogger.Info(DebugLogger.Category.Queue, "ToggleShuffle", $"enabled=True, {tag}");
            DebugLogger.Info(DebugLogger.Category.UI, "UiChatter", tag);

            var log = DebugLog.Snapshot();
            Assert.Contains($"[Queue] ToggleShuffle | enabled=True, {tag}", log);
            Assert.DoesNotContain($"UiChatter | {tag}", log);
        }
        finally
        {
            DebugLogger.IsEnabled = loggerOn;
            DebugLogger.MirrorPlaybackToSessionLog = mirror;
        }
    }
}

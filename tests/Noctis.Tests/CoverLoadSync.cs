using Avalonia.Threading;
using Noctis.Controls;

namespace Noctis.Tests;

/// <summary>
/// Waits for CachedImage cover loads in headless tests by what happened, not by the clock. A
/// decode runs on a below-normal pool thread: with every core busy (a CI box, a build next to
/// the tests) it can wait seconds, so a fixed 80-120 ms settle failed with nothing decoded yet,
/// and a load still pending when the test ended landed in the next test's decoder.
/// </summary>
internal static class CoverLoadSync
{
    /// <summary>
    /// Pumps the UI thread and the headless render timer for at least <paramref name="minMs"/>
    /// (layout, the arrange's Loaded post and timers get their turn), then until no cover load
    /// is pending for three passes in a row. Not for a step where a decode is held on purpose:
    /// wait for that one's own condition with <see cref="Until"/>.
    /// </summary>
    public static async Task Settle(int minMs = 120, int budgetMs = 60000)
    {
        var start = Environment.TickCount64;
        var quiet = 0;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            quiet = CachedImage.PendingLoads == 0 ? quiet + 1 : 0;
            var elapsed = Environment.TickCount64 - start;
            if ((elapsed >= minMs && quiet >= 3) || elapsed > budgetMs) break;
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps until <paramref name="condition"/> holds (or the budget runs out).</summary>
    public static async Task Until(Func<bool> condition, int budgetMs = 60000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }
}

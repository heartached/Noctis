using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The pure decision core of the crash-log preservation feature: how a previous
/// session's on-disk journal is classified (clean exit / managed crash / killed
/// process), which preserved files retention deletes, and what the surfaced
/// block looks like. No IO — the file plumbing in CrashJournal stays thin.
/// </summary>
public class CrashJournalTests
{
    private const string Clean = "=== clean shutdown ===";

    // ── Classify ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n")]
    public void Classify_NothingWritten_IsClean(string? content)
        => Assert.Equal(CrashJournal.SessionEnd.Clean, CrashJournal.Classify(content));

    [Fact]
    public void Classify_CleanMarkerAtTail_IsClean()
    {
        var content = "Noctis 1.3.8\n[21:06:41] [Startup] done\n" + Clean + "\n";
        Assert.Equal(CrashJournal.SessionEnd.Clean, CrashJournal.Classify(content));
    }

    [Fact]
    public void Classify_CleanMarkerWithTrailingBlankLines_IsClean()
    {
        var content = "line\n" + Clean + "\n\n   \n";
        // The writer appends a newline after the marker; blank tails must not
        // turn a clean exit into a scary banner.
        Assert.Equal(CrashJournal.SessionEnd.Clean, CrashJournal.Classify(content));
    }

    [Fact]
    public void Classify_LinesAfterCleanMarker_IsNotClean()
    {
        // A marker mid-file means the run CONTINUED after a Clear/reset raced —
        // only a marker as the last line proves the shutdown path ran last.
        var content = Clean + "\n[21:07:00] [Library] still running\n";
        Assert.Equal(CrashJournal.SessionEnd.Killed, CrashJournal.Classify(content));
    }

    [Fact]
    public void Classify_FatalMarker_IsCrashed()
    {
        var content =
            "Noctis 1.3.8\n" +
            "=== FATAL: AppDomain.UnhandledException ===\n" +
            "[21:06:41] [AppDomain.UnhandledException] System.AccessViolationException: boom\n";
        Assert.Equal(CrashJournal.SessionEnd.Crashed, CrashJournal.Classify(content));
    }

    [Fact]
    public void Classify_NoMarkerAtAll_IsKilled()
    {
        var content = "Noctis 1.3.8\n[21:06:41] [Startup] startup timings...\n";
        Assert.Equal(CrashJournal.SessionEnd.Killed, CrashJournal.Classify(content));
    }

    [Fact]
    public void Classify_FatalWinsOverMissingCleanTail()
    {
        // Crash after hours of logging: fatal marker sits mid-file, tail is the
        // exception text. Must read as Crashed, not Killed.
        var content = "a\nb\n=== FATAL: Program.Main ===\nSystem.Exception: x\n   at Noctis.Program.Main()\n";
        Assert.Equal(CrashJournal.SessionEnd.Crashed, CrashJournal.Classify(content));
    }

    // ── retention pruning ────────────────────────────────────────────

    [Fact]
    public void Prune_KeepsNewestByStampAndDeletesTheRest()
    {
        var files = new[]
        {
            "crashlog-20260801-120000.log",
            "crashlog-20260803-210641.log",
            "crashlog-20260802-090000.log",
            "crashlog-20260730-000001.log",
        };

        var victims = CrashJournal.SelectPruneVictims(files, keep: 2);

        Assert.Equal(new[]
        {
            "crashlog-20260801-120000.log",
            "crashlog-20260730-000001.log",
        }, victims);
    }

    [Fact]
    public void Prune_UnderTheCap_DeletesNothing()
        => Assert.Empty(CrashJournal.SelectPruneVictims(
            new[] { "crashlog-20260803-210641.log" }, keep: 5));

    [Fact]
    public void Prune_IgnoresNullNames()
        => Assert.Empty(CrashJournal.SelectPruneVictims(new string?[] { null }, keep: 1));

    // ── stamp parsing ────────────────────────────────────────────────

    [Fact]
    public void TryParseStamp_RoundTrips()
    {
        Assert.True(CrashJournal.TryParseStamp("crashlog-20260803-210641.log", out var stamp));
        Assert.Equal(new DateTime(2026, 8, 3, 21, 6, 41), stamp);
    }

    [Theory]
    [InlineData("crash.log")]
    [InlineData("crashlog-garbage.log")]
    [InlineData("session.log")]
    public void TryParseStamp_RejectsForeignNames(string name)
        => Assert.False(CrashJournal.TryParseStamp(name, out _));

    // ── surfaced block ───────────────────────────────────────────────

    [Fact]
    public void Block_ForCrashedSession_SaysCrashedAndKeepsContent()
    {
        var content = "=== FATAL: Program.Main ===\nSystem.Exception: boom\n";
        var block = CrashJournal.BuildPreservedBlock("crashlog-20260803-210641.log", content);

        Assert.Contains("CRASHED", block);
        Assert.Contains("2026-08-03 21:06:41", block);
        Assert.Contains("System.Exception: boom", block);
        Assert.Contains("current session below", block);
    }

    [Fact]
    public void Block_ForKilledSession_UsesNeutralWording()
    {
        var block = CrashJournal.BuildPreservedBlock(
            "crashlog-20260803-210641.log", "[21:06:41] [Startup] fine\n");

        // A task-manager kill is indistinguishable from a native crash, so the
        // banner must not claim a crash it cannot prove.
        Assert.DoesNotContain("CRASHED", block);
        Assert.Contains("did not shut down cleanly", block);
    }

    [Fact]
    public void Block_LongLogs_AreBoundedToTheTail()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => $"line {i}");
        var block = CrashJournal.BuildPreservedBlock(
            "crashlog-20260803-210641.log", string.Join("\n", lines));

        Assert.DoesNotContain("line 1\n", block);
        Assert.Contains("line 1000", block);
        Assert.Contains("full file is in the data folder", block);
    }
}

using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// How many threads the speech model computes with (l2-speed 10-20): logical CPUs − 1 as before,
/// capped at the full-speed core count (at most 16) instead of a flat 8. On a 12-core Ryzen,
/// 12 threads took 0.82-0.84× the time of 8 with identical words; fewer threads than the old rule
/// on a 4-core/8-thread emulation was slower, so small CPUs keep the old count.
/// </summary>
public class LyricsStudioThreadsTests
{
    [Theory]
    [InlineData(24, 12, 12)] // Ryzen 9 5900X: 12 cores / 24 threads (was 8)
    [InlineData(32, 16, 16)] // 16 cores / 32 threads (was 8)
    [InlineData(64, 32, 16)] // capped at 16
    [InlineData(8, 4, 7)]    // 4-core laptop with hyper-threading: unchanged
    [InlineData(12, 6, 8)]   // 6-core laptop with hyper-threading: unchanged
    [InlineData(16, 8, 8)]   // 8 cores / 16 threads: unchanged
    [InlineData(8, 8, 7)]    // 8 cores without hyper-threading: one stays free for the app
    [InlineData(20, 6, 8)]   // Intel 6P + 8E: unchanged
    [InlineData(2, 1, 1)]
    [InlineData(1, 1, 1)]
    public void Threads_UseTheCoresOfBigCpus_AndKeepTheOldCountOnSmallOnes(int logical, int performanceCores, int expected)
    {
        Assert.Equal(expected, WhisperTranscriber.ThreadsFor(logical, performanceCores));
    }

    [Fact]
    public void Threads_UnknownTopology_KeepTheOldRule()
    {
        Assert.Equal(8, WhisperTranscriber.ThreadsFor(24, null));
        Assert.Equal(3, WhisperTranscriber.ThreadsFor(4, null));
    }

    [Fact]
    public void Threads_NeverMoreThanTheCpusTheProcessMayUse()
    {
        // A process limited to 4 logical CPUs on a 12-core machine: one of them stays free for the app.
        Assert.Equal(3, WhisperTranscriber.ThreadsFor(4, 12));
    }

    [Fact]
    public void HybridCpu_CountsOnlyTheFastestClass()
    {
        Assert.Equal(6, CpuTopology.PerformanceCount(new byte[] { 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.Equal(12, CpuTopology.PerformanceCount(Enumerable.Repeat((byte)0, 12).ToArray()));
        Assert.Null(CpuTopology.PerformanceCount(Array.Empty<byte>()));
    }

    [Fact]
    public void LinuxCpuList_Parses()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 8, 10, 11 }, CpuTopology.ParseCpuList("0-3,8,10-11\n").OrderBy(x => x));
    }

    [Fact]
    public void ThisMachine_ReportsSomething()
    {
        var cores = CpuTopology.PerformanceCores;
        Assert.True(cores is null || cores >= 1 && cores <= Environment.ProcessorCount);
        Assert.InRange(WhisperTranscriber.Threads, 1, 16);
    }
}

using Noctis.Models;
using Noctis.Services.AudioAnalysis;
using Xunit;

namespace Noctis.Tests.AudioAnalysis;

public class AudioAnalysisCoordinatorTests
{
    [Fact]
    public void NeedsAnalysis_SkipsWhenBothFieldsPresentFromTags()
    {
        var t = new Track { Bpm = 120, MusicalKey = "A minor" };
        Assert.False(AudioAnalysisCoordinator.NeedsAnalysis(t));
    }

    [Fact]
    public void NeedsAnalysis_TrueWhenBpmMissing()
    {
        var t = new Track { Bpm = 0, MusicalKey = "A minor" };
        Assert.True(AudioAnalysisCoordinator.NeedsAnalysis(t));
    }

    [Fact]
    public void NeedsAnalysis_TrueWhenKeyMissing()
    {
        var t = new Track { Bpm = 120, MusicalKey = "" };
        Assert.True(AudioAnalysisCoordinator.NeedsAnalysis(t));
    }
}

using Noctis.Services.AudioAnalysis;
using Xunit;

namespace Noctis.Tests.AudioAnalysis;

public class AudioAnalysisStoreTests
{
    [Fact]
    public async Task UpsertThenGetRoundTrips()
    {
        using var persistence = new TestPersistenceService();
        var store = new AudioAnalysisStore(persistence);
        await store.UpsertAsync(new TrackAnalysisRecord("/x.flac", 100, "2026-01-01T00:00:00Z", 128, 0.8, "A minor", 0.7, "2026-06-11T00:00:00Z"), default);

        var got = await store.GetAsync("/x.flac", default);
        Assert.NotNull(got);
        Assert.Equal(128, got!.Bpm);
        Assert.Equal("A minor", got.MusicalKey);
    }

    [Fact]
    public async Task MissingReturnsNull()
    {
        using var persistence = new TestPersistenceService();
        var store = new AudioAnalysisStore(persistence);
        Assert.Null(await store.GetAsync("/nope.flac", default));
    }
}

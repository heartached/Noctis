using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A32: App.axaml.cs abandons MainWindowViewModel.ShutdownAsync after a 4 s deadline,
/// but the settings/volume save, the queue snapshot and the play-history/play-count
/// flushes ran last — behind the server stop (2 s), the scan checkpoint (5 s) and the
/// scrobble flush (3 s). A quit mid-scan or with a slow scrobble skipped them. The
/// method needs the whole app graph to run, so this source-level guard (in the spirit
/// of SettingsStorageRefreshTests) pins the order instead.
/// </summary>
public class ShutdownSaveOrderTests
{
    private static string ShutdownBody()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Noctis", "ViewModels", "MainWindowViewModel.cs"));
        var start = source.IndexOf("public async Task ShutdownAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ShutdownAsync not found — update this test");
        var end = source.IndexOf("/// <summary>", start, StringComparison.Ordinal);
        Assert.True(end > start, "end of ShutdownAsync not found — update this test");
        return source[start..end];
    }

    private static int At(string body, string call)
    {
        var i = body.IndexOf(call, StringComparison.Ordinal);
        Assert.True(i >= 0, $"{call} not found in ShutdownAsync — update this test");
        return i;
    }

    [Fact]
    public void UserStateSaves_RunBeforeTheSlowSteps()
    {
        var body = ShutdownBody();
        var firstSlowStep = new[]
        {
            At(body, "Plugins.UnloadAll()"),
            At(body, "Settings.StopNoctisServerAsync()"),
            At(body, "_library.PauseActiveScanForShutdownAsync("),
            At(body, "FlushPendingScrobblesAsync()"),
            At(body, "tagWriter.FlushAsync()"),
        }.Min();

        Assert.True(At(body, "Settings.SetVolume(Player.Volume)") < firstSlowStep);
        Assert.True(At(body, "Settings.SaveAsync()") < firstSlowStep);
        Assert.True(At(body, "Player.SaveQueueStateAsync()") < firstSlowStep);
        Assert.True(At(body, "_playHistory.FlushAsync()") < firstSlowStep);
        Assert.True(At(body, "Player.FlushPendingPlayStateAsync()") < firstSlowStep);
    }

    [Fact]
    public void FullLibrarySave_WaitsForTheScanCheckpoint()
    {
        // Mid-scan the library holds a partial, in-memory-only track list; writing
        // library.json before the scan is stopped would persist that subset.
        var body = ShutdownBody();
        Assert.True(At(body, "Player.FlushPendingLibrarySaveAsync()")
                    > At(body, "_library.PauseActiveScanForShutdownAsync("));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Noctis.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}

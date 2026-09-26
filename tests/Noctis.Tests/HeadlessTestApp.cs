using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Noctis.Tests.HeadlessTestApp))]
// Test classes run one at a time: plain [Fact] tests that post to Dispatcher.UIThread or
// time a window raced the headless session's thread when classes ran in parallel, so a
// loaded CI runner failed a different probe on every run ("a different thread owns it",
// a streamed slice landing after its clear, a 308px window). Costs ~30 s locally.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Noctis.Tests;

/// <summary>
/// Minimal Avalonia application for headless view tests. Loads only the Fluent
/// theme (control templates); views under test carry their own local styles.
/// </summary>
public class HeadlessTestApp : Application
{
    /// <summary>
    /// NOCTIS_TEST_SKIA=1 swaps the headless drawing stubs for real Skia rendering, so a
    /// probe can save what a control actually draws (Window.CaptureRenderedFrame) and the
    /// PNG can be eyeballed. Off by default: the suite's layout assertions are measured
    /// against the stubs.
    /// </summary>
    public static bool RealRendering =>
        Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") == "1";

    public static AppBuilder BuildAvaloniaApp() => RealRendering
        ? AppBuilder
            .Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        : AppBuilder
            .Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

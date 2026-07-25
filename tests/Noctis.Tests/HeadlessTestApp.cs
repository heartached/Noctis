using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Noctis.Tests.HeadlessTestApp))]

namespace Noctis.Tests;

/// <summary>
/// Minimal Avalonia application for headless view tests. Loads only the Fluent
/// theme (control templates); views under test carry their own local styles.
/// </summary>
public class HeadlessTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<HeadlessTestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

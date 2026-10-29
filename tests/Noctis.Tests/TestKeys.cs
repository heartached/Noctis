using Avalonia.Input;

namespace Noctis.Tests;

/// <summary>
/// Raw input for the app's select-all / add-to-selection modifier (PlatformHelper.PrimaryModifier):
/// ⌘ on macOS, Ctrl elsewhere, so the selection tests exercise the gesture each platform uses.
/// </summary>
internal static class TestKeys
{
    public static RawInputModifiers Primary =>
        OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
}

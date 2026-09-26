using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Noctis.Helpers;

/// <summary>
/// macOS hands "Open With Noctis", a Finder double-click and files dropped on the Dock
/// icon to the app as an open-documents Apple Event, never on the command line — and
/// LaunchServices re-activates the running app instead of starting a second process, so
/// neither Program.Main's argv scan nor the single-instance pipe ever sees those files.
/// Avalonia raises the event as <see cref="IActivatableLifetime.Activated"/> with
/// <see cref="FileActivatedEventArgs"/>; nothing listened, so nothing played.
/// </summary>
public static class FileActivation
{
    /// <summary>
    /// Calls <paramref name="open"/> with the local paths of every file activation the
    /// lifetime raises (other activation kinds are ignored). Returns the action that
    /// detaches it, or null when the platform has no activatable lifetime (Windows and
    /// Linux, whose files arrive through argv and the single-instance pipe).
    /// </summary>
    public static Action? Subscribe(IActivatableLifetime? lifetime, Action<IReadOnlyList<string>> open)
    {
        if (lifetime == null)
            return null;

        EventHandler<ActivatedEventArgs> handler = (_, e) =>
        {
            var paths = LocalPaths(e);
            if (paths.Count > 0)
                open(paths);
        };
        lifetime.Activated += handler;
        return () => lifetime.Activated -= handler;
    }

    /// <summary>Local file paths carried by a file activation; empty for any other kind.</summary>
    public static IReadOnlyList<string> LocalPaths(ActivatedEventArgs e) =>
        e is FileActivatedEventArgs files
            ? files.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray()
            : Array.Empty<string>();
}

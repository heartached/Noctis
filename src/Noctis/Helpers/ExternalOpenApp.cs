using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>
/// The "open the audio file in an external program" track action (right-click → Open in …).
/// Reads the configured executable lazily through the canonical SettingsViewModel (same
/// accessor pattern as AudioConverterService), so changing it in Settings takes effect on
/// the next menu open without a restart.
/// </summary>
public static class ExternalOpenApp
{
    private static string ConfiguredPath =>
        App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings().ExternalOpenAppPath?.Trim()
        ?? string.Empty;

    /// <summary>Whether the menu item should be shown: always on Windows (the native
    /// "Open with" picker is the fallback), elsewhere only once a program is configured.</summary>
    public static bool IsAvailable =>
        PlatformHelper.IsWindows || ConfiguredPath.Length > 0;

    /// <summary>"Open in {app}" when a program is configured, otherwise the picker label.</summary>
    public static string MenuHeader
    {
        get
        {
            var path = ConfiguredPath;
            if (path.Length > 0)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name))
                    return $"Open in {name}";
            }
            return "Open File With";
        }
    }

    public static void Open(Track? track)
    {
        var file = track?.FilePath;
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;

        var app = ConfiguredPath;
        // macOS .app bundles are directories, so check both. A configured-but-missing
        // program falls back to the Windows picker (no-op elsewhere) instead of failing
        // silently with a stale path.
        if (app.Length > 0 && (File.Exists(app) || Directory.Exists(app)))
            PlatformHelper.OpenFileWith(app, file);
        else
            PlatformHelper.ShowOpenWithDialog(file);
    }
}

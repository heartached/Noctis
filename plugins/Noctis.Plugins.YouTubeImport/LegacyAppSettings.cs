using System.Text.Json;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// What the user set in Noctis before Add from YouTube was a plugin (10-29): the app's own
/// settings.json still holds YouTubeDownloadFolder and YtDlpPath (kept as legacy fields). Read,
/// never written, from the app's data folder when the plugin's data folder sits in the usual
/// &lt;data&gt;/plugin-data/&lt;id&gt;/ (see <see cref="AppDataRoot"/>); anywhere else there is nothing.
/// </summary>
internal static class LegacyAppSettings
{
    public const string DownloadFolderKey = "youTubeDownloadFolder";
    public const string YtDlpPathKey = "ytDlpPath";

    /// <summary>The app's data folder (&lt;data&gt;) for a plugin data folder &lt;data&gt;/plugin-data/&lt;id&gt;, else null.</summary>
    internal static string? AppDataRoot(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return null;
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(dataDirectory));
            if (dir.Parent is { } parent && string.Equals(parent.Name, "plugin-data", StringComparison.OrdinalIgnoreCase) && parent.Parent is { } root)
                return root.FullName;
        }
        catch { /* odd path: nothing to read */ }
        return null;
    }

    /// <summary>A string from the app's settings.json (trimmed), or empty when it is missing or unreadable.</summary>
    internal static string Read(string? dataDirectory, string key)
    {
        if (AppDataRoot(dataDirectory) is not { } root) return string.Empty;
        try
        {
            var path = Path.Combine(root, "settings.json");
            if (!File.Exists(path)) return string.Empty;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString()?.Trim() ?? string.Empty;
        }
        catch { /* a broken or locked file: nothing carried over */ }
        return string.Empty;
    }
}

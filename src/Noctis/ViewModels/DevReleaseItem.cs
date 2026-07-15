using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// One row in the Settings → About → Developer Mode version manager:
/// a GitHub release that can be installed in place (or opened on GitHub
/// when this copy can't self-install).
/// </summary>
public sealed class DevReleaseItem
{
    public required string TagName { get; init; }
    public required string VersionDisplay { get; init; }
    public string DateDisplay { get; init; } = "";
    public bool IsPrerelease { get; init; }
    /// <summary>True when this release is the running build.</summary>
    public bool IsCurrent { get; init; }
    /// <summary>True when the in-app installer can apply this release directly.</summary>
    public bool CanInstall { get; init; }
    /// <summary>Warning from the release notes (e.g. a known startup crash), if any.</summary>
    public string? WarningText { get; init; }
    public required UpdateInfo Info { get; init; }

    public bool HasWarning => !string.IsNullOrEmpty(WarningText);

    /// <summary>True when this platform's installer asset exists for an in-app download.</summary>
    public bool HasDownload => Info.InstallerApiUrl is not null;

    public string ActionLabel =>
        CanInstall ? "Install"
        : HasDownload ? "Download"
        : "View on GitHub";
}

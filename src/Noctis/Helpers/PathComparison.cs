namespace Noctis.Helpers;

/// <summary>
/// Comparer/comparison for on-disk path identity. Linux filesystems are
/// case-sensitive — /music/rock and /music/Rock are different directories, so
/// case-insensitive matching there lets a removal or exclusion for one hit
/// tracks in the other. Windows and default macOS volumes are case-insensitive,
/// where OrdinalIgnoreCase is the correct identity. Mirrors the inline pattern
/// already used by the scan cycle guards (LibraryService.EnumerateAudioFiles,
/// SmbMediaSourceConnector, AudioConverterService.IsUnder).
/// </summary>
public static class PathComparison
{
    public static readonly StringComparer Comparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static readonly StringComparison Comparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

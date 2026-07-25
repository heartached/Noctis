using System;
using System.IO;

namespace Noctis.Tests;

/// <summary>
/// Absolute paths for fixtures that exercise real path handling.
///
/// A literal like <c>C:\Music</c> is only absolute on Windows. On macOS/Linux
/// <see cref="Path.GetFullPath(string)"/> resolves it against the working
/// directory and <c>\</c> is an ordinary filename character, so services that
/// split on <see cref="Path.DirectorySeparatorChar"/> see one long segment.
/// Since CI runs the suite on all three platforms, fixtures build their paths
/// from the native volume root and separator instead of hardcoding drives.
/// </summary>
internal static class TestPaths
{
    /// <summary>An absolute path on the primary volume (<c>C:\</c> or <c>/</c>).</summary>
    public static string Primary(params string[] parts)
        => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    /// <summary>
    /// An absolute path on a second, unrelated volume — for the "outside every
    /// library root" cases that must not resolve under <see cref="Primary"/>.
    /// </summary>
    public static string Other(params string[] parts)
        => Path.Combine(OperatingSystem.IsWindows() ? @"D:\" : "/mnt/other", Path.Combine(parts));
}

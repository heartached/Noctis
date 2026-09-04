namespace Noctis.Helpers;

/// <summary>
/// Pure logic behind the Windows "Open with" registration, kept registry-free so it
/// can be unit-tested on every CI leg: reading the executable back out of a recorded
/// <c>shell\open\command</c> value, deciding whether it names this copy, and deciding
/// whether a stale registration should be re-pointed silently.
/// </summary>
public static class FileAssociationCommand
{
    /// <summary>Formats the <c>shell\open\command</c> value for <paramref name="exePath"/>.</summary>
    public static string Format(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>
    /// The executable a recorded command launches: the first quoted token, or the
    /// first whitespace-delimited token when it was written unquoted. Null for blank.
    /// </summary>
    public static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        if (s[0] == '"')
        {
            var close = s.IndexOf('"', 1);
            return close > 1 ? s.Substring(1, close - 1) : null;
        }
        var space = s.IndexOf(' ');
        return space > 0 ? s.Substring(0, space) : s;
    }

    /// <summary>True when the recorded command launches exactly <paramref name="exePath"/>.
    /// Whole-path comparison, not a substring test: "C:\Noctis\Noctis.exe" must not count
    /// as registered because "C:\Noctis\Noctis.exe.bak" happens to contain it.</summary>
    public static bool PointsAt(string? command, string exePath)
    {
        var recorded = ExtractExePath(command);
        return recorded != null
               && string.Equals(NormalizePath(recorded), NormalizePath(exePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a registration this user once made should be moved to the running copy
    /// without asking: only when it exists, names a different executable, and that
    /// executable is gone (the app was moved, renamed or updated in place). A recorded
    /// exe that still exists is left alone — a second copy (a dev build next to the
    /// installed one) must never silently steal the registration.
    /// </summary>
    public static bool ShouldRepoint(string? recordedCommand, string currentExePath, Func<string, bool> fileExists)
    {
        var recorded = ExtractExePath(recordedCommand);
        if (recorded == null) return false;                     // never registered
        if (PointsAt(recordedCommand, currentExePath)) return false; // already us
        return !fileExists(recorded);
    }

    private static string NormalizePath(string path)
        => path.Replace('/', '\\').TrimEnd('\\');
}

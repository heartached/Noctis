namespace Noctis.Helpers;

/// <summary>Path-shortening helpers for dense list rows in dialogs.</summary>
public static class DisplayPath
{
    /// <summary>
    /// Middle-ellipsizes a long path so the start (drive/root) and the end
    /// (the folder that actually identifies the file) both stay readable and
    /// trailing badges in the row never run out of room.
    /// </summary>
    public static string MiddleEllipsis(string path, int max = 72)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max) return path;
        int head = max / 3;
        int tail = max - head - 1;
        return string.Concat(path.AsSpan(0, head), "…", path.AsSpan(path.Length - tail));
    }
}

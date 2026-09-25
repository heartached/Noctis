namespace Noctis.Helpers;

/// <summary>
/// Finds the music video that belongs to an audio file (Discord, aaron 09-15): a clip
/// with the same base name next to the song, or in a <c>videos</c> folder beside it.
/// </summary>
public static class MusicVideoLocator
{
    public static readonly string[] Extensions = { ".mp4", ".m4v", ".mov", ".mkv", ".webm" };
    private static readonly string[] SubFolders = { "videos", "Videos", "video", "Video" };

    public static string? Find(string? audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath)) return null;
        string? folder; string stem;
        try
        {
            folder = Path.GetDirectoryName(audioPath);
            stem = Path.GetFileNameWithoutExtension(audioPath);
        }
        catch { return null; }
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem)) return null;

        var sibling = FindIn(folder, stem);
        if (sibling != null) return sibling;
        foreach (var sub in SubFolders)
        {
            var dir = Path.Combine(folder, sub);
            if (!Directory.Exists(dir)) continue;
            var candidate = FindIn(dir, stem);
            if (candidate != null) return candidate;
        }
        return null;
    }

    // One listing matched case-insensitively, in Extensions order: probing exact names
    // with File.Exists missed song.MP4 / Song.mov on case-sensitive file systems (Linux).
    private static string? FindIn(string dir, string stem)
    {
        string? best = null;
        var bestRank = Extensions.Length;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                var rank = Array.FindIndex(Extensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
                if (rank < 0 || rank >= bestRank) continue;
                if (!Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase)) continue;
                best = file;
                bestRank = rank;
            }
        }
        catch { return null; }
        return best;
    }
}

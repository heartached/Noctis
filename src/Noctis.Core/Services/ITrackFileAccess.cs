namespace Noctis.Services;

/// <summary>A sidecar's bytes and which requested extension matched (lower-case as requested).</summary>
public sealed record SidecarFile(string Extension, byte[] Bytes);

/// <summary>
/// Reads the files that belong to a track without assuming it has a filesystem path. The
/// desktop finds "song.ttml" beside "song.mp3" by path; an Android SAF track is a tree
/// document URI with no sibling path, so the lookup goes through the provider there.
/// Implementations never throw: a missing, unreadable or revoked file is null.
/// </summary>
public interface ITrackFileAccess
{
    /// <summary>The first sibling named "&lt;audio stem&gt;&lt;ext&gt;" for ext in
    /// <paramref name="extensions"/> order (see <see cref="SidecarNames.Match"/>), or null.</summary>
    SidecarFile? ReadSidecar(string trackPath, IReadOnlyList<string> extensions);

    /// <summary>A readable, seekable stream over the audio file, or null. Caller disposes.</summary>
    Stream? OpenAudio(string trackPath);

    /// <summary>The audio's file name as TagLib should see it (its extension picks the reader),
    /// or null for the last segment of <paramref name="trackPath"/>. A source whose paths carry
    /// no file name (a desktop song's noctis-remote id) names the local copy it opens here.</summary>
    string? AudioFileName(string trackPath) => null;
}

/// <summary>The desktop's sidecar naming rule, path-free so every file source shares it.</summary>
public static class SidecarNames
{
    /// <summary>
    /// Same folder, same stem (the audio name minus its last extension, so "01. Intro.v2.flac"
    /// → "01. Intro.v2"), extension compared case-insensitively. The desktop checks three fixed
    /// casings (.ttml/.TTML/.Ttml); a directory listing lets every casing match. Returns the
    /// sibling's real name and the requested extension that matched.
    /// </summary>
    public static (string Name, string Extension)? Match(string audioName, IEnumerable<string> siblingNames, IReadOnlyList<string> extensions)
    {
        var stem = Path.GetFileNameWithoutExtension(audioName);
        if (string.IsNullOrEmpty(stem)) return null;
        var names = siblingNames as IReadOnlyList<string> ?? siblingNames.ToList();
        foreach (var ext in extensions)
        {
            var wanted = stem + ext;
            foreach (var name in names)
                if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    return (name, ext);
        }
        return null;
    }
}

using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>One row of Android's audio index (MediaStore.Audio.Media), as the Android head reads it.
/// <see cref="Volume"/>/<see cref="RelativePath"/> are API 29+ ("external_primary", "Music/Album/");
/// <see cref="Data"/> is the absolute path older Androids give instead. <see cref="IsRecording"/>
/// is API 31+ (false before).</summary>
public sealed record DeviceMusicRow(
    long Id,
    string DisplayName,
    long Size,
    long DateModifiedSeconds,
    long DurationMs,
    bool IsMusic,
    bool IsRingtone,
    bool IsNotification,
    bool IsAlarm,
    bool IsPodcast,
    bool IsRecording,
    string? Volume,
    string? RelativePath,
    string? Data);

/// <summary>A song the index gave that the library takes: <see cref="Path"/> is its content URI
/// (the track's identity and what ExoPlayer plays).</summary>
public sealed record DeviceMusicItem(string Path, string Name, long Size, DateTime ModifiedUtc);

/// <summary>
/// The pure part of "All Music on This Phone" (the Android head only runs the query): which rows
/// are songs, what each becomes, and which are left to a music folder that already holds them.
/// </summary>
public static class DeviceMusicScan
{
    /// <summary>The library root standing for the whole index: every song's URI is this plus
    /// "/{id}" (MediaStore.Audio.Media.EXTERNAL_CONTENT_URI, all external volumes), so the
    /// library's "under this root" rule covers them like a folder's.</summary>
    public const string Root = "content://media/external/audio/media";

    /// <summary>Shorter files are not songs (notification sounds, voice notes, UI clicks the
    /// index did not flag). A duration the index does not know (0) is kept.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(15);

    /// <summary>Folders recorders save into, for Androids before 12 that have no IS_RECORDING.</summary>
    private static readonly string[] RecordingFolders = { "Recordings", "Call", "Voice Recorder", "Sounds/Call" };

    public static bool IsDeviceMusicPath(string? path) =>
        path != null && (path.Equals(Root, StringComparison.Ordinal) || path.StartsWith(Root + "/", StringComparison.Ordinal));

    public static string PathFor(long id) => $"{Root}/{id}";

    /// <summary>The rows the library takes, in order: songs (IS_MUSIC, not a ringtone,
    /// notification, alarm, podcast or recording, at least <see cref="MinimumDuration"/>, a format
    /// Noctis reads) outside every one of <paramref name="folders"/>.</summary>
    public static IEnumerable<DeviceMusicItem> Select(IEnumerable<DeviceMusicRow> rows, IReadOnlyCollection<string> folders)
    {
        var covered = folders.Select(FolderLocation).Where(l => l != null).Select(l => l!).ToList();
        foreach (var row in rows)
        {
            if (!IsSong(row)) continue;
            if (Location(row) is { } where && covered.Any(c => IsCovered(where, c))) continue;
            if (!TryModified(row.DateModifiedSeconds, out var modified)) modified = DateTime.UnixEpoch;
            yield return new DeviceMusicItem(PathFor(row.Id), row.DisplayName, row.Size, modified);
        }
    }

    internal static bool IsSong(DeviceMusicRow row)
    {
        if (row.Id <= 0 || string.IsNullOrWhiteSpace(row.DisplayName)) return false;
        if (!row.IsMusic || row.IsRingtone || row.IsNotification || row.IsAlarm || row.IsPodcast || row.IsRecording) return false;
        if (row.DurationMs > 0 && row.DurationMs < MinimumDuration.TotalMilliseconds) return false;
        if (!MetadataService.SupportedExtensions.Contains(System.IO.Path.GetExtension(row.DisplayName))) return false;
        if (Location(row) is { } where)
        {
            var path = where[(where.IndexOf(':') + 1)..];
            if (RecordingFolders.Any(f => path.Equals(f, StringComparison.OrdinalIgnoreCase)
                                          || path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    /// <summary>The folder a row's file is in, the way a SAF folder on the phone's own storage
    /// names it: "primary:Music/Album", "1A2B-3C4D:Music" on a card. Null when unknown.</summary>
    public static string? Location(DeviceMusicRow row)
    {
        if (!string.IsNullOrEmpty(row.Volume) && row.RelativePath != null)
        {
            var volume = row.Volume is "external_primary" or "external" ? "primary" : row.Volume;
            return $"{volume}:{row.RelativePath.Trim('/')}";
        }
        if (string.IsNullOrEmpty(row.Data)) return null;
        var data = row.Data.Replace('\\', '/');
        var slash = data.LastIndexOf('/');
        if (slash <= 0) return null;
        var dir = data[..slash];
        foreach (var primary in new[] { "/storage/emulated/0", "/sdcard", "/mnt/sdcard" })
        {
            if (dir.Equals(primary, StringComparison.OrdinalIgnoreCase)) return "primary:";
            if (dir.StartsWith(primary + "/", StringComparison.OrdinalIgnoreCase)) return "primary:" + dir[(primary.Length + 1)..];
        }
        const string storage = "/storage/";
        if (!dir.StartsWith(storage, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = dir[storage.Length..];
        var cut = rest.IndexOf('/');
        return cut < 0 ? rest + ":" : $"{rest[..cut]}:{rest[(cut + 1)..]}";
    }

    /// <summary>A SAF folder as a location ("primary:Music"): only the phone's storage provider
    /// names its documents after the volume and path. Other providers (Downloads, a cloud app)
    /// give null, and their songs cannot be matched.</summary>
    public static string? FolderLocation(string folder)
    {
        const string prefix = "content://com.android.externalstorage.documents/tree/";
        if (!folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = Uri.UnescapeDataString(folder[prefix.Length..]);
        var document = id.IndexOf("/document/", StringComparison.Ordinal);
        if (document >= 0) id = id[..document];
        return id.Contains(':') ? id.TrimEnd('/') : null;
    }

    /// <summary>Whether a file in <paramref name="location"/> is inside the folder
    /// <paramref name="folder"/> (both "volume:path"). Case-blind: Android's shared storage is,
    /// and the index spells a card's id in lower case where SAF uses upper.</summary>
    public static bool IsCovered(string location, string folder)
    {
        var colon = folder.IndexOf(':');
        if (colon <= 0) return false;
        if (colon == folder.Length - 1)   // a whole volume ("1A2B-3C4D:")
            return location.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        return location.Equals(folder, StringComparison.OrdinalIgnoreCase)
               || location.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryModified(long seconds, out DateTime utc)
    {
        try
        {
            utc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            utc = default;
            return false;
        }
    }
}

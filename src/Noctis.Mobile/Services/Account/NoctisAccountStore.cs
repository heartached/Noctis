using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Services;
using Noctis.Services.Sync;

namespace Noctis.Mobile.Services.Account;

/// <summary>What the phone last agreed with the desktop about a song: absent = not a favorite, no rating, not disliked,
/// never snoozed. <paramref name="Snoozed"/> is the snooze end last agreed (TrackSyncState.SnoozedUntil; a past
/// date = not snoozed); null in a file from before it, or when no snooze was ever exchanged.</summary>
internal sealed record SyncedTrackState(bool Favorite, int Rating, bool Disliked, DateTime? Snoozed = null);

/// <summary>A phone-side favorite/rating/dislike/snooze change not yet pushed. Play counts never go this way (scrobbles do).
/// <paramref name="SnoozedUntil"/>: the snooze end to send (TrackSyncState.SnoozedUntil), null to say nothing about it
/// (and in a file from before it).</summary>
internal sealed record PendingTrackState(bool Favorite, int Rating, bool Disliked, DateTime? FavoritedAt, DateTime UpdatedUtc,
    DateTime? SnoozedUntil = null);

/// <summary>A play of a desktop song, sent as a scrobble on the next sync. <paramref name="Scrobble"/>:
/// null for a play recorded without a verdict (counted only), else a <see cref="PlayScrobble"/>'s wire
/// word; "pending" waits for the phone to tell whether it passed the scrobble rule
/// (<see cref="PlayScrobbleWire"/>). A file from before the field reads as null.</summary>
internal sealed record PendingPlay(string Id, long Time, string? Scrobble = null);

/// <summary>Sync bookkeeping for one server + user: the ledger checkpoint and the last agreed state.</summary>
internal sealed class NoctisSyncState
{
    public string ServerUrl { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    /// <summary>Ledger sequence applied up to (the next pull asks for changes after it).</summary>
    public long Seq { get; set; }
    /// <summary>Last agreed state per song (sync id → state); only songs with non-default state.
    /// The echo guard: a library save whose state equals this is not a phone edit.</summary>
    public Dictionary<string, SyncedTrackState> Baseline { get; set; } = new();
    /// <summary>Last agreed version of each desktop playlist (Guid "N" → state).</summary>
    public Dictionary<string, PlaylistSyncState> Playlists { get; set; } = new();
    /// <summary>Desktop songs the phone already has as its own files (desktop id "N" → the phone
    /// track's id "N"): one song in the library, the file plays, state syncs under the desktop id.</summary>
    public Dictionary<string, string> Links { get; set; } = new();
    /// <summary>Links that ended (the local file left, or the desktop dropped the song), phone id →
    /// desktop id, until the synced playlists are pointed back at the desktop song.</summary>
    public Dictionary<string, string> Retired { get; set; } = new();
}

/// <summary>Phone changes waiting for the next sync. Written on every change.</summary>
internal sealed class NoctisPendingState
{
    public string ServerUrl { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public Dictionary<string, PendingTrackState> Tracks { get; set; } = new();
    public List<PendingPlay> Plays { get; set; } = new();
    /// <summary>The computer's playlists deleted on the phone (Guid "N" → when), sent as
    /// tombstones on the next sync.</summary>
    public Dictionary<string, DateTime> DeletedPlaylists { get; set; } = new();
    /// <summary>The computer's playlists whose custom cover was removed on the phone (Guid "N" →
    /// when), sent as "no cover" with the next push of the playlist.</summary>
    public Dictionary<string, DateTime> RemovedCovers { get; set; } = new();
}

/// <summary>The phone's own songs already sent to (or found on) the computer of one server + user,
/// so the next "Send phone songs to computer" skips them.</summary>
internal sealed class NoctisUploadState
{
    public string ServerUrl { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    /// <summary>Library track ids (Guid "N") of the songs sent.</summary>
    public HashSet<string> Sent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The account's files in the platform's no-backup directory (Android NoBackupFilesDir; tests a
/// temp folder): <c>device.json</c> (this install's id, kept across sign-outs),
/// <c>account.json</c> (server, user, device key, pinned fingerprint — never the password),
/// <c>sync.json</c> (checkpoint + last agreed state), <c>pending.json</c> (unpushed edits and
/// queued plays) and <c>uploads.json</c> (phone songs already sent). Every write goes to a temp file first and is then moved over the old one, so a
/// crash mid-write leaves the previous version. Unreadable files read as empty.
/// </summary>
public sealed class NoctisAccountStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _writeGate = new();

    public NoctisAccountStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    private string DevicePath => Path.Combine(Directory, "device.json");
    private string AccountPath => Path.Combine(Directory, "account.json");
    private string SyncPath => Path.Combine(Directory, "sync.json");
    private string PendingPath => Path.Combine(Directory, "pending.json");
    private string UploadsPath => Path.Combine(Directory, "uploads.json");

    /// <summary>This install's device id: 16 random bytes as hex, created on first use and kept for good.</summary>
    public string LoadOrCreateDeviceId()
    {
        lock (_writeGate)
        {
            var stored = Read<DeviceFile>(DevicePath)?.DeviceId;
            if (IsValidDeviceId(stored)) return stored!;
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            WriteAtomic(DevicePath, JsonSerializer.Serialize(new DeviceFile { DeviceId = id }, Json));
            return id;
        }
    }

    /// <summary>The server's rule for device ids: [A-Za-z0-9_-]{8,64}.</summary>
    public static bool IsValidDeviceId(string? id) =>
        id is { Length: >= 8 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public NoctisAccount? LoadAccount()
    {
        var account = Read<NoctisAccount>(AccountPath);
        return account is { ServerUrl.Length: > 0, DeviceKey.Length: > 0, DeviceId.Length: > 0 } ? account : null;
    }

    public void SaveAccount(NoctisAccount account)
    {
        lock (_writeGate) WriteAtomic(AccountPath, JsonSerializer.Serialize(account, Json));
    }

    public void DeleteAccount()
    {
        lock (_writeGate) NoctisServerClient.TryDelete(AccountPath);
    }

    internal NoctisSyncState LoadSyncState() => Read<NoctisSyncState>(SyncPath) ?? new NoctisSyncState();

    internal void SaveSyncState(NoctisSyncState state)
    {
        var json = JsonSerializer.Serialize(state, Json);
        lock (_writeGate) WriteAtomic(SyncPath, json);
    }

    internal NoctisPendingState LoadPending() => Read<NoctisPendingState>(PendingPath) ?? new NoctisPendingState();

    internal void SavePending(NoctisPendingState pending)
    {
        var json = JsonSerializer.Serialize(pending, Json);
        lock (_writeGate) WriteAtomic(PendingPath, json);
    }

    internal NoctisUploadState LoadUploads() => Read<NoctisUploadState>(UploadsPath) ?? new NoctisUploadState();

    internal void SaveUploads(NoctisUploadState uploads)
    {
        var json = JsonSerializer.Serialize(uploads, Json);
        lock (_writeGate) WriteAtomic(UploadsPath, json);
    }

    /// <summary>Sign-out: forgets the checkpoint, the agreed state, anything unsent and the record of
    /// songs sent (the computer's own check finds them again). The device id stays.</summary>
    internal void DeleteSyncData()
    {
        lock (_writeGate)
        {
            NoctisServerClient.TryDelete(SyncPath);
            NoctisServerClient.TryDelete(PendingPath);
            NoctisServerClient.TryDelete(UploadsPath);
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            DebugLog.Write("Account", $"{Path.GetFileName(path)} unreadable ({ex.GetType().Name}); starting empty");
            return null;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts && ex is IOException or UnauthorizedAccessException)
            {
                // Windows refuses to replace a file someone has open (a virus scanner, the indexer,
                // anything reading it) for as long as they hold it. Android's rename never does.
                Thread.Sleep(attempt * 25);
            }
        }
    }

    /// <summary>Replace attempts before a write gives up (about 0.7 s of waiting in all).</summary>
    private const int MoveAttempts = 8;

    private sealed class DeviceFile
    {
        public string DeviceId { get; set; } = string.Empty;
    }
}

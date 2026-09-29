using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Services;
using Noctis.Services.Sync;

namespace Noctis.Mobile.Services.Account;

/// <summary>What the phone last agreed with the desktop about a song: absent = not a favorite, no rating, not disliked.</summary>
internal sealed record SyncedTrackState(bool Favorite, int Rating, bool Disliked);

/// <summary>A phone-side favorite/rating/dislike change not yet pushed. Play counts never go this way (scrobbles do).</summary>
internal sealed record PendingTrackState(bool Favorite, int Rating, bool Disliked, DateTime? FavoritedAt, DateTime UpdatedUtc);

/// <summary>A finished play of a desktop song, sent as a scrobble on the next sync.</summary>
internal sealed record PendingPlay(string Id, long Time);

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
}

/// <summary>Phone changes waiting for the next sync. Written on every change.</summary>
internal sealed class NoctisPendingState
{
    public string ServerUrl { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public Dictionary<string, PendingTrackState> Tracks { get; set; } = new();
    public List<PendingPlay> Plays { get; set; } = new();
}

/// <summary>
/// The account's files in the platform's no-backup directory (Android NoBackupFilesDir; tests a
/// temp folder): <c>device.json</c> (this install's id, kept across sign-outs),
/// <c>account.json</c> (server, user, device key, pinned fingerprint — never the password),
/// <c>sync.json</c> (checkpoint + last agreed state) and <c>pending.json</c> (unpushed edits and
/// queued plays). Every write goes to a temp file first and is then moved over the old one, so a
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

    /// <summary>Sign-out: forgets the checkpoint, the agreed state and anything unsent. The device id stays.</summary>
    internal void DeleteSyncData()
    {
        lock (_writeGate)
        {
            NoctisServerClient.TryDelete(SyncPath);
            NoctisServerClient.TryDelete(PendingPath);
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
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class DeviceFile
    {
        public string DeviceId { get; set; } = string.Empty;
    }
}

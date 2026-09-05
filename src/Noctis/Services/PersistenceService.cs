using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// JSON-file-based persistence. All data lives under %APPDATA%\Noctis\.
///
/// File layout:
///   %APPDATA%\Noctis\
///   ├── settings.json
///   ├── library.json
///   ├── playlists.json
///   ├── queue.json
///   ├── indexes.json
///   └── artwork\
///       ├── {albumId}.jpg
///       └── ...
/// </summary>
public class PersistenceService : IPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // library.json is machine-only and by far the largest file we write (it is
    // re-serialized in full on every rating/favorite/play-count change). Indentation
    // roughly doubled it for no benefit, so the library uses a compact writer.
    // Reading is unaffected — the parser ignores whitespace either way.
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; }

    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private string LibraryPath => Path.Combine(DataDirectory, "library.json");
    private string PlaylistsPath => Path.Combine(DataDirectory, "playlists.json");
    private string QueuePath => Path.Combine(DataDirectory, "queue.json");
    private string IndexCachePath => Path.Combine(DataDirectory, "indexes.json");
    private string ArtworkDirectory => Path.Combine(DataDirectory, "artwork");
    private string AnimatedCoverDirectory => Path.Combine(DataDirectory, "animated_covers");

    public PersistenceService()
        // Data root is %APPDATA%\Noctis by default; NOCTIS_DATA_DIR overrides
        // (used by dev builds so they don't clobber a parallel install).
        : this(Helpers.AppPaths.DataRoot)
    {
    }

    // Per-track lyric text lives here, not in library.json — see LyricsStore.
    private readonly LyricsStore _lyricsStore;
    internal LyricsStore LyricsStore => _lyricsStore;

    // Test seam: points the service at an isolated data root so the real
    // save/load/protect logic is exercisable against a temp directory.
    internal PersistenceService(string dataRoot)
    {
        DataDirectory = dataRoot;
        _lyricsStore = new LyricsStore(Path.Combine(DataDirectory, "lyrics_store"));

        // Ensure directories exist
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ArtworkDirectory);
        Directory.CreateDirectory(AnimatedCoverDirectory);

        // On macOS/Linux the credential fields in settings.json are stored in plaintext
        // (DPAPI is Windows-only), and the default umask leaves the data root at 0755 and
        // the files inside at 0644 — readable by every other local account. Tighten the
        // directory to owner-only; individual files are chmod'd after each write.
        TryRestrictToOwner(DataDirectory, isDirectory: true);
    }

    /// <summary>
    /// Best-effort chmod to owner-only (0700 for directories, 0600 for files) on Unix.
    /// No-op on Windows, where ACL inheritance from %APPDATA% already restricts access.
    /// </summary>
    private static void TryRestrictToOwner(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // Filesystem may not support Unix modes (e.g. a mounted exFAT volume).
            // Losing the hardening is not worth failing the save over.
        }
    }

    // ── Settings ──────────────────────────────────────────────

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var (loaded, outcome) = await LoadJsonWithOutcomeAsync<AppSettings>(SettingsPath);

        // A parse failure used to just log to Debug.WriteLine and hand back defaults — and
        // the very next save (any settings change, or the unconditional save on window
        // close) overwrote the damaged file, destroying the only copy of the user's music
        // folders, themes, EQ and both scrobbler credentials. Move it aside first, then try
        // the rolling backup before falling back to defaults.
        if (outcome == LoadOutcome.Corrupt)
        {
            SettingsLoadFailed = true;
            var quarantined = QuarantineCorruptFile(SettingsPath);
            loaded = await TryParseAsync<AppSettings>(SettingsBackupPath);
            DebugLog.Write("Persistence",
                $"settings.json could not be parsed and was moved to " +
                $"'{Path.GetFileName(quarantined) ?? "(rename failed)"}'. " +
                (loaded is null
                    ? "No usable backup — starting from defaults."
                    : "Recovered the previous settings from settings.json.bak."));
            DebugLogger.Error(DebugLogger.Category.Error, "settings.json corrupt", quarantined);
        }

        var settings = loaded ?? new AppSettings();
        settings.ClampToValidRanges();
        // Decrypt at-rest-protected credentials (see SaveSettingsAsync).
        // Legacy plaintext values pass through unchanged.
        settings.LastFmSessionKey = UnprotectSecret(settings.LastFmSessionKey);
        settings.ListenBrainzToken = UnprotectSecret(settings.ListenBrainzToken);
        foreach (var conn in settings.SourceConnections)
            conn.TokenOrPassword = UnprotectSecret(conn.TokenOrPassword);
        return settings;
    }

    /// <inheritdoc />
    public bool SettingsLoadFailed { get; private set; }

    private string SettingsBackupPath => SettingsPath + ".bak";

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        // Roll the last known-good file aside before overwriting it. settings.json is
        // small and saves are debounced, so the copy is cheap next to the write itself —
        // and it is the difference between "the file got truncated" and "the user lost
        // every setting they ever chose".
        try
        {
            if (File.Exists(SettingsPath))
            {
                File.Copy(SettingsPath, SettingsBackupPath, overwrite: true);
                // The backup holds the same credential fields as the original, which are
                // plaintext everywhere DPAPI isn't available.
                TryRestrictToOwner(SettingsBackupPath, isDirectory: false);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Persistence", $"Could not refresh settings.json.bak: {ex.Message}");
        }

        // Protect scrobbler credentials at rest (Windows DPAPI, current user).
        // Applied to the serialized tree, not the live object, so in-memory
        // settings keep the plaintext values the services use.
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions);
        if (node is JsonObject obj)
        {
            ProtectField(obj, "lastFmSessionKey");
            ProtectField(obj, "listenBrainzToken");
            // Remote-source (Navidrome/WebDAV/SMB) passwords get the same
            // at-rest protection as the scrobbler tokens.
            if (obj["sourceConnections"] is System.Text.Json.Nodes.JsonArray connections)
                foreach (var conn in connections)
                    if (conn is JsonObject connObj)
                        ProtectField(connObj, "tokenOrPassword");
        }
        await SaveJsonAsync(SettingsPath, node);
    }

    // ── Library ───────────────────────────────────────────────

    /// <inheritdoc />
    public bool LibraryLoadFailed { get; private set; }

    /// <inheritdoc />
    public string? LastCorruptFilePath { get; private set; }

    public async Task<List<Track>?> LoadLibraryAsync()
    {
        var (tracks, outcome) = await LoadLibraryWithOutcomeAsync();

        // "File absent" (first run, or the user cleared their data) is normal and must
        // stay writable. "File present but unparseable" is not: returning null there used
        // to hand back an empty library, and the very next save — a startup scan, a
        // favorite toggle, the 5s post-play debounce — overwrote the real file with it.
        if (outcome == LoadOutcome.Corrupt)
        {
            LibraryLoadFailed = true;
            LastCorruptFilePath = QuarantineCorruptFile(LibraryPath);
            DebugLog.Write("Persistence",
                $"library.json could not be parsed and was moved to " +
                $"'{Path.GetFileName(LastCorruptFilePath)}'. Library writes are disabled for " +
                "this session so the damaged file can be recovered by hand.");
            DebugLogger.Error(DebugLogger.Category.Error, "library.json corrupt",
                LastCorruptFilePath);
        }

        return tracks;
    }

    /// <summary>
    /// Library-specific load: streams the track array element by element
    /// (instead of materializing the whole list first) so the one-time lyric
    /// migration below never holds more than one track's legacy lyric strings
    /// in RAM — on a lyric-heavy 100k library the old inline fields were
    /// hundreds of MB. Each streamed track immediately moves any inline lyrics
    /// from the old JSON format into the per-track store and drops them.
    /// Mirrors LoadJsonWithOutcomeAsync's temp-promotion and corrupt handling.
    /// </summary>
    private async Task<(List<Track>? Value, LoadOutcome Outcome)> LoadLibraryWithOutcomeAsync()
    {
        var path = LibraryPath;
        var tempPath = path + ".tmp";
        if (!File.Exists(path) && File.Exists(tempPath))
        {
            if (await TryParseAsync<List<Track>>(tempPath) is not null)
            {
                try { File.Move(tempPath, path); }
                catch { /* fall through; we re-check File.Exists below */ }
            }
            else
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        if (!File.Exists(path)) return (null, LoadOutcome.Missing);

        try
        {
            var tracks = new List<Track>();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, useAsync: true);
            await foreach (var track in JsonSerializer.DeserializeAsyncEnumerable<Track>(stream, JsonOptions))
            {
                if (track is null) continue;
                track.MigrateLegacyLyricsToStore(_lyricsStore);
                tracks.Add(track);
            }
            return (tracks, LoadOutcome.Ok);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PersistenceService] Failed to load {Path.GetFileName(path)}: {ex.Message}");
            DebugLog.Write("Persistence", $"Failed to load {Path.GetFileName(path)}: {ex.Message}");
            return (null, LoadOutcome.Corrupt);
        }
    }

    public async Task SaveLibraryAsync(List<Track> tracks)
    {
        if (LibraryLoadFailed)
        {
            // Fail loud rather than silently discarding: the caller is about to persist a
            // library that was never successfully loaded.
            DebugLog.Write("Persistence",
                "Refusing to save library.json — the existing file failed to load this session.");
            return;
        }

        // Flush pending lyric edits to the per-track store BEFORE the lyric-free
        // JSON reaches disk: a crash between the two leaves the lyrics safe in
        // the store, never dropped. Runs on the pool — commits do comparison
        // reads and small writes, and only tracks whose lyric setters ran since
        // the last save do any I/O at all.
        await Task.Run(() =>
        {
            for (var i = 0; i < tracks.Count; i++)
                tracks[i].CommitLyricsToStore(_lyricsStore);
        });

        await SaveJsonAsync(LibraryPath, tracks, CompactJsonOptions);
    }

    // ── Playlists ─────────────────────────────────────────────

    public async Task<List<Playlist>> LoadPlaylistsAsync()
    {
        return await LoadJsonAsync<List<Playlist>>(PlaylistsPath) ?? new List<Playlist>();
    }

    public async Task SavePlaylistsAsync(List<Playlist> playlists)
    {
        await SaveJsonAsync(PlaylistsPath, playlists);
    }

    // ── Queue State ───────────────────────────────────────────

    public async Task<QueueState?> LoadQueueStateAsync()
    {
        return await LoadJsonAsync<QueueState>(QueuePath);
    }

    public async Task SaveQueueStateAsync(QueueState state)
    {
        await SaveJsonAsync(QueuePath, state);
    }

    // ── Index Cache ───────────────────────────────────────────

    public async Task<LibraryIndexCache?> LoadIndexCacheAsync()
    {
        return await LoadJsonAsync<LibraryIndexCache>(IndexCachePath);
    }

    public async Task SaveIndexCacheAsync(LibraryIndexCache cache)
    {
        await SaveJsonAsync(IndexCachePath, cache);
    }

    // ── Artwork ───────────────────────────────────────────────

    public string GetArtworkPath(Guid albumId)
    {
        return Path.Combine(ArtworkDirectory, $"{albumId}.jpg");
    }

    public void SaveArtwork(Guid albumId, byte[] imageData)
    {
        // The "Unknown Artist::Unknown Album" bucket is one AlbumId shared by every
        // track without a real album identity, and art cached under it shows on ALL
        // of them at once — one file's embedded or folder cover would be stamped
        // onto every untagged track in the library (and stick, since writers
        // short-circuit on File.Exists). It is not an album; never cache art for it.
        if (albumId == Track.UnknownAlbumBucketId) return;

        try
        {
            var path = GetArtworkPath(albumId);
            File.WriteAllBytes(path, imageData);
        }
        catch
        {
            // Non-critical: if artwork save fails, we just won't have a cached image
        }
    }

    // ── Animated Cover ────────────────────────────────────────

    public string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? ".mp4" : extension;
        if (!ext.StartsWith('.')) ext = "." + ext;
        var fileName = trackId.HasValue
            ? $"{albumId}__{trackId.Value}{ext}"
            : $"{albumId}{ext}";
        return Path.Combine(AnimatedCoverDirectory, fileName);
    }

    public void EnsureAnimatedCoverDir()
    {
        Directory.CreateDirectory(AnimatedCoverDirectory);
    }

    // ── Secret protection (Windows DPAPI) ─────────────────────
    // Scrobbler credentials are encrypted per-user at rest so settings.json no
    // longer holds them in plaintext. Windows-only: macOS/Linux keep plaintext
    // (Keychain/libsecret integration is out of scope). Fails open on protect
    // (plaintext beats losing the session) and fails closed on unprotect (a blob
    // from another machine/user just forces a re-authentication).

    private const string ProtectedPrefix = "enc:dpapi:";

    private static void ProtectField(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is JsonValue value && value.TryGetValue<string>(out var raw))
        {
            var protectedValue = ProtectSecret(raw);
            if (!ReferenceEquals(protectedValue, raw))
                obj[propertyName] = protectedValue;
        }
    }

    internal static string ProtectSecret(string value)
    {
        if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows() ||
            value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return value;
        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(value), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(bytes);
        }
        catch
        {
            return value;
        }
    }

    internal static string UnprotectSecret(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return value;
        if (!OperatingSystem.IsWindows())
            return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(value[ProtectedPrefix.Length..]);
            var raw = System.Security.Cryptography.ProtectedData.Unprotect(
                bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Helpers ───────────────────────────────────────────────

    private enum LoadOutcome
    {
        /// <summary>File parsed successfully (the value may still be null JSON).</summary>
        Ok,
        /// <summary>No file on disk — first run, or the user cleared their data.</summary>
        Missing,
        /// <summary>File exists but could not be parsed. Callers must not overwrite it.</summary>
        Corrupt
    }

    private static async Task<T?> LoadJsonAsync<T>(string path) where T : class
        => (await LoadJsonWithOutcomeAsync<T>(path)).Value;

    private static async Task<(T? Value, LoadOutcome Outcome)> LoadJsonWithOutcomeAsync<T>(string path)
        where T : class
    {
        // If the main file doesn't exist but the temp does, a previous save crashed
        // mid-write. Only promote the temp if it actually parses — a temp from a crash
        // *during* serialization is by definition incomplete, and blindly renaming it
        // turned "main file missing" into "main file corrupt".
        var tempPath = path + ".tmp";
        if (!File.Exists(path) && File.Exists(tempPath))
        {
            if (await TryParseAsync<T>(tempPath) is not null)
            {
                try { File.Move(tempPath, path); }
                catch { /* fall through; we re-check File.Exists below */ }
            }
            else
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        if (!File.Exists(path)) return (null, LoadOutcome.Missing);

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, useAsync: true);
            return (await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions), LoadOutcome.Ok);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PersistenceService] Failed to load {Path.GetFileName(path)}: {ex.Message}");
            DebugLog.Write("Persistence", $"Failed to load {Path.GetFileName(path)}: {ex.Message}");
            return (null, LoadOutcome.Corrupt);
        }
    }

    /// <summary>Parses a file without side effects. Returns null when it can't be read.</summary>
    private static async Task<T?> TryParseAsync<T>(string path) where T : class
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renames an unparseable file aside so the next save can't destroy it. Returns the new
    /// path, or null when the rename itself failed.
    /// </summary>
    private static string? QuarantineCorruptFile(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = $"{path}.corrupt-{stamp}";
            File.Move(path, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Persistence", $"Could not quarantine {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    // Serializing large payloads (library.json) is CPU-heavy, and awaits inside
    // SaveJsonCoreAsync would otherwise resume on the calling (UI) thread —
    // fire-and-forget saves from the play path measurably stalled rendering.
    // Task.Run keeps the whole save on the thread pool.
    private static Task SaveJsonAsync<T>(string path, T data, JsonSerializerOptions? options = null)
        => Task.Run(() => SaveJsonCoreAsync(path, data, options ?? JsonOptions));

    // One writer per target file: two overlapping saves share the fixed ".tmp"
    // name (opened FileShare.None), so the loser threw a sharing violation —
    // unobserved on fire-and-forget paths — or the temp was renamed mid-write.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _writeGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static async Task SaveJsonCoreAsync<T>(string path, T data, JsonSerializerOptions options)
    {
        var gate = _writeGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await SaveJsonSerializedAsync(path, data, options);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task SaveJsonSerializedAsync<T>(string path, T data, JsonSerializerOptions options)
    {
        // Write to temp file first, then rename — prevents data loss on crash
        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 65536, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, data, options);

                // Force the data pages out before the rename. Without this the rename's
                // metadata can reach disk first, so a power loss leaves the destination
                // present but truncated — which reads back as a corrupt library.
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            // Owner-only before the file is visible at its final name (Unix; no-op on Windows).
            TryRestrictToOwner(tempPath, isDirectory: false);

            // Atomic rename (on NTFS this is atomic for same-volume moves)
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Clean up temp file on failure
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            // Propagate the error so callers know the save failed.
            // Previously this was silently swallowed, causing data loss
            // (library, playlists, settings) without any notification.
            System.Diagnostics.Debug.WriteLine($"[PersistenceService] Failed to save {Path.GetFileName(path)}: {ex.Message}");
            throw;
        }
    }
}

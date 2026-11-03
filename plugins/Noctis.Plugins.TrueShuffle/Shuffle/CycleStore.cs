using System.Text.Json;

namespace Noctis.Plugins.TrueShuffle.Shuffle;

/// <summary>The remembered cycle: ids of the songs started since the cycle began, and the last one.</summary>
public sealed record CycleData(int Version, string[] Seen, string? Last);

/// <summary>
/// cycle.json in the plugin's data folder (ported from True Shuffle's CycleStore). Only this file is
/// written: never Noctis's settings, history or library. Writes are serialized, staged and swapped in,
/// and a file another program holds for a moment (a virus scanner, a backup tool) is waited out.
///
/// The original capped the cycle at 50,000 songs and the file at 4 MB: on a larger library a No
/// repeats cycle past 50,000 heard songs could no longer be saved ("Shuffle history is invalid or
/// exceeds its limit") and persistent modes refused to run. The caps now fit a 250,000-song library.
/// </summary>
public sealed class CycleStore
{
    public const int MaxKeys = 250_000;
    private const long MaxBytes = 16_000_000;
    private const int MoveAttempts = 5;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CycleStore(string folder) => _path = Path.Combine(folder, "cycle.json");

    public string FilePath => _path;

    public async Task<CycleData> LoadAsync()
    {
        if (!File.Exists(_path)) return new(1, Array.Empty<string>(), null);
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A linked shuffle history file was left untouched.");
        if (new FileInfo(_path).Length > MaxBytes) throw new IOException("The shuffle history is too large; it was left untouched.");
        var bytes = await File.ReadAllBytesAsync(_path).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<CycleData>(bytes) ?? throw new IOException("The shuffle history is empty or damaged.");
        if (data.Version != 1 || data.Seen == null || data.Seen.Length > MaxKeys || data.Seen.Any(k => !ValidKey(k))
            || (data.Last != null && !ValidKey(data.Last)))
            throw new IOException("The shuffle history has an unknown format; it was left untouched.");
        return data with { Seen = data.Seen.Distinct(StringComparer.Ordinal).ToArray() };
    }

    public async Task SaveAsync(CycleData data)
    {
        if (data.Version != 1 || data.Seen.Length > MaxKeys || data.Seen.Any(k => !ValidKey(k)) || (data.Last != null && !ValidKey(data.Last)))
            throw new IOException("The shuffle history is invalid or too long.");
        await _gate.WaitAsync().ConfigureAwait(false);
        string? temp = null;
        try
        {
            var folder = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(folder);
            if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A linked shuffle history file was left untouched.");
            temp = Path.Combine(folder, ".cycle-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
            await using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await f.WriteAsync(bytes).ConfigureAwait(false);
                f.Flush(true);
            }
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(temp, _path, overwrite: true); break; }
                catch (Exception ex) when (attempt < MoveAttempts && ex is IOException or UnauthorizedAccessException)
                { await Task.Delay(50 * attempt).ConfigureAwait(false); }
            }
        }
        finally
        {
            if (temp != null && File.Exists(temp)) File.Delete(temp);
            _gate.Release();
        }
    }

    /// <summary>Noctis track ids are GUIDs.</summary>
    private static bool ValidKey(string? k) => k is { Length: 36 } && Guid.TryParseExact(k, "D", out _);
}

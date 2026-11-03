using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noctis.Plugins.Chords.Analysis;

/// <summary>
/// Finished analyses, one small JSON file per song in the plugin's data folder (cache/). A song is
/// known by its path, size and last-write time, so an edited or replaced file is analysed again;
/// results of another <see cref="ChordAnalyzer.EngineVersion"/> or the other Sevenths setting are
/// not used. At most <see cref="MaxEntries"/> files: the least recently used go first.
/// </summary>
public sealed class ChordCache
{
    public const int MaxEntries = 1000;

    private readonly string? _folder;
    private readonly Action<string>? _log;

    /// <param name="dataDirectory">The plugin's data folder; null = nothing is kept.</param>
    public ChordCache(string? dataDirectory, Action<string>? log = null)
    {
        _folder = string.IsNullOrEmpty(dataDirectory) ? null : Path.Combine(dataDirectory, "cache");
        _log = log;
    }

    public string? Folder => _folder;

    /// <summary>The cache file for this song as it is now, or null when it cannot be read.</summary>
    public string? FileFor(string songPath, bool sevenths)
    {
        if (_folder is null) return null;
        try
        {
            var info = new FileInfo(songPath);
            if (!info.Exists) return null;
            var path = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? songPath.ToUpperInvariant() : songPath;
            var identity = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{ChordAnalyzer.EngineVersion}|{(sevenths ? 7 : 3)}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32].ToLowerInvariant();
            return Path.Combine(_folder, hash + ".json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public ChordAnalysis? TryLoad(string songPath, bool sevenths)
    {
        var file = FileFor(songPath, sevenths);
        if (file is null || !File.Exists(file)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(file));
            var analysis = data?.ToAnalysis(sevenths);
            if (analysis is null) return null;
            try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { /* best effort, for pruning */ }
            return analysis;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Invoke("could not read a cached analysis: " + ex.Message);
            return null;
        }
    }

    public void Save(string songPath, ChordAnalysis analysis)
    {
        var file = FileFor(songPath, analysis.Sevenths);
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(_folder!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(CacheFile.From(analysis)));
            File.Move(temp, file, overwrite: true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("could not save the analysis: " + ex.Message);
        }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(_folder!).GetFiles("*.json");
        if (files.Length <= MaxEntries) return;
        foreach (var old in files.OrderBy(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc).Take(files.Length - MaxEntries))
        {
            try { old.Delete(); } catch { /* in use: next time */ }
        }
    }

    /// <summary>On-disk shape: short names, chords as text ("Bbm7"), times in seconds.</summary>
    private sealed class CacheFile
    {
        public int Engine { get; set; }
        public bool Sevenths { get; set; }
        public double Duration { get; set; }
        public double Tuning { get; set; }
        public string? Key { get; set; }
        public List<double[]>? Times { get; set; }   // [start, end, confidence]
        public List<string>? Chords { get; set; }

        public static CacheFile From(ChordAnalysis a) => new()
        {
            Engine = ChordAnalyzer.EngineVersion,
            Sevenths = a.Sevenths,
            Duration = a.Duration,
            Tuning = Math.Round(a.TuningCents, 1),
            Key = a.Key is { } k ? NoteNames.NameOf(k.Tonic, false) + (k.Minor ? "m" : "") : null,
            Times = a.Segments.Select(s => new[] { s.Start, s.End, s.Confidence }).ToList(),
            Chords = a.Segments.Select(s => s.Chord.Name()).ToList(),
        };

        public ChordAnalysis? ToAnalysis(bool sevenths)
        {
            if (Engine != ChordAnalyzer.EngineVersion || Sevenths != sevenths || Times is null || Chords is null || Times.Count != Chords.Count) return null;
            if (!double.IsFinite(Duration) || Duration < 0) return null;
            var segments = new List<ChordSegment>(Times.Count);
            var last = 0.0;
            for (var i = 0; i < Times.Count; i++)
            {
                var t = Times[i];
                if (t is not { Length: 3 } || !t.All(double.IsFinite) || t[0] < last - 1e-6 || t[1] <= t[0]) return null;
                if (!Chord.TryParse(Chords[i], out var chord)) return null;
                segments.Add(new ChordSegment(t[0], t[1], chord, Math.Clamp(t[2], 0, 1)));
                last = t[1];
            }
            MusicalKey? key = null;
            if (!string.IsNullOrEmpty(Key))
            {
                var minor = Key.EndsWith('m');
                if (!NoteNames.TryParse(minor ? Key[..^1] : Key, out var tonic, out var used) || used != Key.Length - (minor ? 1 : 0)) return null;
                key = new MusicalKey(tonic, minor);
            }
            return new ChordAnalysis(segments, key, Duration, double.IsFinite(Tuning) ? Tuning : 0, Sevenths);
        }
    }
}

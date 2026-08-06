using System.Globalization;
using System.Text;

namespace Noctis.Services;

/// <summary>
/// Disk mirror for <see cref="DebugLog"/> that survives the process.
///
/// The session log lived only in memory, so the one session whose log actually
/// mattered — the one that crashed — was exactly the one whose log vanished on
/// restart. Every line <see cref="DebugLog"/> accepts is appended to session.log
/// in the app data folder, flushed per line so the tail is on disk when the
/// process dies without warning. A clean exit stamps a marker and deletes the
/// file, so a session.log found at the NEXT startup means the previous run died:
/// managed crashes are stamped by <see cref="MarkFatal"/> before the exception
/// is logged, and a file with neither marker means the process was killed
/// outright — a native fault, a task-manager kill, or power loss, which are
/// indistinguishable from inside the process, so the surfaced wording for that
/// case stays neutral rather than claiming a crash. The dead session's file is
/// preserved as crashlog-*.log (newest few kept) and Settings → About shows it
/// above the live log, across restarts, until the user presses Clear.
/// </summary>
public static class CrashJournal
{
    /// <summary>What the previous run's session.log said about how it ended.</summary>
    public enum SessionEnd
    {
        /// <summary>No file, an empty file, or a clean-shutdown marker.</summary>
        Clean,
        /// <summary>A fatal marker: an unhandled managed exception was recorded.</summary>
        Crashed,
        /// <summary>No marker: the process died without running any handler.</summary>
        Killed
    }

    private const string SessionFileName = "session.log";
    private const string PreservedPrefix = "crashlog-";
    private const string PreservedStampFormat = "yyyyMMdd-HHmmss";
    private const string CleanMarker = "=== clean shutdown ===";
    private const string FatalMarkerPrefix = "=== FATAL: ";

    /// <summary>Preserved crash files kept on disk; older ones are pruned.</summary>
    private const int PreservedKept = 5;

    /// <summary>
    /// Hard cap on lines streamed per session so a pathological write loop cannot
    /// grow session.log without bound (the in-memory ring trims itself; the file
    /// deliberately keeps everything up to this cap for post-mortems).
    /// </summary>
    private const int MaxStreamedLines = 5000;

    /// <summary>Lines of a preserved log surfaced in the pane / Copy Logs; the
    /// full file stays in the data folder for anything longer.</summary>
    private const int SurfacedLines = 400;

    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static string? _dataRoot;
    private static int _streamed;
    private static bool _capNoticeWritten;
    private static string? _preservedBlock;
    private static bool _preservedBlockLoaded;

    /// <summary>
    /// Detects how the previous run ended, preserves its log if it died, and
    /// starts streaming this session's log. Call once, first thing in Main,
    /// before anything can write to <see cref="DebugLog"/>. Never throws; on any
    /// IO trouble (typically a second live instance holding session.log) this
    /// session simply runs memory-only, exactly as every session did before.
    /// </summary>
    public static void Initialize(string dataRoot)
    {
        lock (Lock)
        {
            if (_writer != null) return;
            _dataRoot = dataRoot;
            var sessionPath = Path.Combine(dataRoot, SessionFileName);

            try
            {
                Directory.CreateDirectory(dataRoot);

                if (File.Exists(sessionPath))
                {
                    var content = File.ReadAllText(sessionPath);
                    if (Classify(content) == SessionEnd.Clean)
                    {
                        // Marker present (or nothing written): the delete at
                        // shutdown didn't happen, but the run ended cleanly.
                        File.Delete(sessionPath);
                    }
                    else
                    {
                        var stamp = File.GetLastWriteTime(sessionPath)
                            .ToString(PreservedStampFormat, CultureInfo.InvariantCulture);
                        var target = Path.Combine(dataRoot, PreservedPrefix + stamp + ".log");
                        // A same-second crash loop lands on an existing name.
                        File.Move(sessionPath, target, overwrite: true);

                        foreach (var name in SelectPruneVictims(
                            Directory.GetFiles(dataRoot, PreservedPrefix + "*.log")
                                .Select(Path.GetFileName)!, PreservedKept))
                        {
                            try { File.Delete(Path.Combine(dataRoot, name)); }
                            catch { /* retention is best effort */ }
                        }
                    }
                }

                // FileShare.Read: users can peek via Open Folder, but a second
                // Noctis instance cannot open this for write — its Initialize
                // fails right here (or on the Move above) and it runs
                // memory-only instead of destroying the live journal.
                _writer = new StreamWriter(
                    new FileStream(sessionPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                    Encoding.UTF8)
                { AutoFlush = true };
            }
            catch
            {
                _writer = null; // memory-only session
                return;
            }
        }

        DebugLog.AttachSink(AppendLine, ResetSessionFile);
    }

    /// <summary>
    /// Stamps the journal fatal so the preserved file reads as a crash, not a
    /// kill. Call from the fatal handlers BEFORE logging the exception, so the
    /// marker precedes the stack trace in the file.
    /// </summary>
    public static void MarkFatal(string source)
    {
        lock (Lock)
        {
            try { _writer?.WriteLine(FatalMarkerPrefix + source + " ==="); }
            catch { /* the exception itself still reaches crash.log */ }
        }
    }

    /// <summary>
    /// Stamps a clean shutdown and removes the journal — the next launch must
    /// not carry this session's log forward. Idempotent; call at the end of
    /// Main once the lifetime has exited normally.
    /// </summary>
    public static void MarkCleanShutdown()
    {
        lock (Lock)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine(CleanMarker);
                _writer.Dispose();
                if (_dataRoot != null)
                    File.Delete(Path.Combine(_dataRoot, SessionFileName));
            }
            catch { /* the marker alone makes the next launch discard it */ }
            _writer = null;
        }
    }

    /// <summary>
    /// The newest preserved crash log as a bannered block for the Settings pane
    /// and Copy Logs, or null when none is preserved. Loaded lazily off the
    /// startup path and cached; <see cref="ClearPreserved"/> resets it.
    /// </summary>
    public static string? PreservedBlock
    {
        get
        {
            lock (Lock)
            {
                if (_preservedBlockLoaded) return _preservedBlock;
                _preservedBlockLoaded = true;
                try
                {
                    var newest = _dataRoot == null
                        ? null
                        : Directory.GetFiles(_dataRoot, PreservedPrefix + "*.log")
                            .Select(Path.GetFileName)
                            .OrderByDescending(n => n, StringComparer.Ordinal)
                            .FirstOrDefault();
                    _preservedBlock = newest == null
                        ? null
                        : BuildPreservedBlock(newest!,
                            File.ReadAllText(Path.Combine(_dataRoot!, newest!)));
                }
                catch
                {
                    _preservedBlock = null;
                }
                return _preservedBlock;
            }
        }
    }

    /// <summary>One-line banner for the Settings UI, or null when nothing is preserved.</summary>
    public static string? PreservedBanner
    {
        get
        {
            var block = PreservedBlock;
            if (block == null) return null;
            var end = block.StartsWith(FatalMarkerBanner(SessionEnd.Crashed), StringComparison.Ordinal)
                ? SessionEnd.Crashed
                : SessionEnd.Killed;
            return end == SessionEnd.Crashed
                ? "Previous session crashed — its log is preserved below until you press Clear."
                : "Previous session did not shut down cleanly — its log is preserved below until you press Clear.";
        }
    }

    /// <summary>Deletes every preserved crash log. Wired to the Clear button.</summary>
    public static void ClearPreserved()
    {
        lock (Lock)
        {
            _preservedBlock = null;
            _preservedBlockLoaded = true;
            if (_dataRoot == null) return;
            try
            {
                foreach (var file in Directory.GetFiles(_dataRoot, PreservedPrefix + "*.log"))
                    File.Delete(file);
            }
            catch { /* leftover files re-surface next launch; better than throwing in a command */ }
        }
    }

    // ── streaming (called by DebugLog under its own lock) ─────────────

    private static void AppendLine(string line)
    {
        lock (Lock)
        {
            if (_writer == null) return;
            if (_streamed >= MaxStreamedLines)
            {
                if (_capNoticeWritten) return;
                _capNoticeWritten = true;
                try { _writer.WriteLine($"(session file line cap of {MaxStreamedLines} reached — further lines are memory-only)"); }
                catch { }
                return;
            }
            _streamed++;
            try { _writer.WriteLine(line); }
            catch { _writer = null; /* disk went away; stay memory-only */ }
        }
    }

    private static void ResetSessionFile()
    {
        lock (Lock)
        {
            if (_writer == null) return;
            try
            {
                _writer.Flush();
                _writer.BaseStream.SetLength(0);
                _streamed = 0;
                _capNoticeWritten = false;
            }
            catch { }
        }
    }

    // ── pure decision logic (unit-tested) ─────────────────────────────

    /// <summary>
    /// How the run that wrote <paramref name="sessionFileContent"/> ended. A
    /// clean-shutdown marker as the last non-blank line (or nothing written at
    /// all) is clean; a fatal marker anywhere is a managed crash; anything else
    /// is a process that died with no handler running.
    /// </summary>
    public static SessionEnd Classify(string? sessionFileContent)
    {
        if (string.IsNullOrWhiteSpace(sessionFileContent)) return SessionEnd.Clean;

        string? lastNonBlank = null;
        foreach (var line in EnumerateLines(sessionFileContent))
            if (!string.IsNullOrWhiteSpace(line))
                lastNonBlank = line;

        if (lastNonBlank == CleanMarker) return SessionEnd.Clean;

        foreach (var line in EnumerateLines(sessionFileContent))
            if (line.StartsWith(FatalMarkerPrefix, StringComparison.Ordinal))
                return SessionEnd.Crashed;

        return SessionEnd.Killed;
    }

    /// <summary>
    /// Which preserved files to delete so only the newest <paramref name="keep"/>
    /// remain. Names sort by their embedded timestamp (zero-padded, so ordinal
    /// order is chronological order).
    /// </summary>
    public static IReadOnlyList<string> SelectPruneVictims(IEnumerable<string?> fileNames, int keep)
        => fileNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();

    /// <summary>
    /// The bannered block Settings shows above the live log: a header naming the
    /// file and how the session ended, the preserved lines (bounded to the last
    /// <see cref="SurfacedLines"/>; the file keeps everything), and a footer
    /// separating it from the current session.
    /// </summary>
    public static string BuildPreservedBlock(string fileName, string content)
    {
        var end = Classify(content) == SessionEnd.Crashed ? SessionEnd.Crashed : SessionEnd.Killed;

        var lines = EnumerateLines(content).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var sb = new StringBuilder();
        sb.AppendLine(FatalMarkerBanner(end));
        var stampLabel = TryParseStamp(fileName, out var stamp)
            ? stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "unknown time";
        sb.AppendLine($"({stampLabel} — {fileName}, kept until Clear is pressed)");
        if (lines.Count > SurfacedLines)
        {
            sb.AppendLine($"(showing the last {SurfacedLines} of {lines.Count} lines — full file is in the data folder)");
            lines.RemoveRange(0, lines.Count - SurfacedLines);
        }
        foreach (var line in lines)
            sb.AppendLine(line);
        sb.Append("═══ end of previous session — current session below ═══");
        return sb.ToString();
    }

    /// <summary>Parses the timestamp out of a crashlog-yyyyMMdd-HHmmss.log name.</summary>
    public static bool TryParseStamp(string fileName, out DateTime stamp)
    {
        stamp = default;
        if (!fileName.StartsWith(PreservedPrefix, StringComparison.Ordinal)) return false;
        var core = Path.GetFileNameWithoutExtension(fileName)[PreservedPrefix.Length..];
        return DateTime.TryParseExact(core, PreservedStampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp);
    }

    private static string FatalMarkerBanner(SessionEnd end) => end == SessionEnd.Crashed
        ? "═══ Previous session CRASHED — log preserved ═══"
        : "═══ Previous session did not shut down cleanly — log preserved ═══";

    private static IEnumerable<string> EnumerateLines(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}

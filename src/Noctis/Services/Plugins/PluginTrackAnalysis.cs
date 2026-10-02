using Avalonia.Threading;
using Noctis.Plugins;

namespace Noctis.Services.Plugins;

/// <summary>
/// "library.write.analysis" (API 1.2): a plugin fills in BPM and musical key the way the
/// background tempo/key analysis does (AudioAnalysisCoordinator): values set on the live
/// tracks on the UI thread, then one library save and one metadata refresh. Library only:
/// file tags are never written and no other field can be set. Writes from a stopped plugin
/// are dropped.
/// </summary>
internal sealed class PluginTrackAnalysisWriter : ITrackAnalysisWriter
{
    /// <summary>Highest BPM accepted (Track.Bpm is a whole number; Mixxx caps at 500).</summary>
    internal const int MaxBpm = 999;
    /// <summary>Longest key accepted; real ones are "C# minor", "12A", "Abm".</summary>
    internal const int MaxKeyLength = 32;

    private readonly ILibraryService? _library;
    private readonly Func<bool> _isActive;
    private readonly string _pluginId;

    public PluginTrackAnalysisWriter(ILibraryService? library, Func<bool> isActive, string pluginId)
    {
        _library = library;
        _isActive = isActive;
        _pluginId = pluginId;
    }

    public async Task<int> SetTrackAnalysisAsync(IReadOnlyList<TrackAnalysisUpdate> updates, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (_library is not { } library || updates.Count == 0 || !_isActive()) return 0;
        var batch = updates.ToArray(); // the caller may reuse its list while this hops threads
        // Track.Bpm/MusicalKey are observable and bound by the Songs BPM column: change them where the bindings live.
        var changed = Dispatcher.UIThread.CheckAccess()
            ? Apply(library, batch, overwrite)
            : await Dispatcher.UIThread.InvokeAsync(() => _isActive() ? Apply(library, batch, overwrite) : 0);
        if (changed == 0) return 0;

        try { await library.SaveAsync(); }
        catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"{_pluginId}: library save after BPM/key update: {ex.Message}"); }
        // SaveAsync does not raise LibraryUpdated: this refreshes the views and the SQLite index.
        try { library.NotifyMetadataChanged(); } catch { /* best effort, as in the backfill */ }
        DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"{_pluginId} set BPM/key on {changed} of {batch.Length} tracks (overwrite {overwrite})");
        return changed;
    }

    /// <summary>Applies what is valid and wanted; returns the number of tracks that changed.</summary>
    internal static int Apply(ILibraryService library, IReadOnlyList<TrackAnalysisUpdate> updates, bool overwrite)
    {
        var changed = 0;
        foreach (var update in updates)
        {
            if (update is null || !Guid.TryParse(update.TrackId, out var id) || library.GetTrackById(id) is not { } track) continue;
            var any = false;
            if (update.Bpm is { } bpm && bpm is >= 1 and <= MaxBpm && bpm != track.Bpm && (overwrite || track.Bpm <= 0))
            {
                track.Bpm = bpm;
                any = true;
            }
            if (CleanKey(update.MusicalKey) is { } key && !string.Equals(key, track.MusicalKey, StringComparison.Ordinal)
                && (overwrite || string.IsNullOrWhiteSpace(track.MusicalKey)))
            {
                track.MusicalKey = key;
                any = true;
            }
            if (any) changed++;
        }
        return changed;
    }

    /// <summary>The trimmed key, or null when it is blank, too long or holds control characters.</summary>
    internal static string? CleanKey(string? key)
    {
        var trimmed = key?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxKeyLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }
}

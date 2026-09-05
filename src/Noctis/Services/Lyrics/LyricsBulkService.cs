using Noctis.Models;

namespace Noctis.Services.Lyrics;

public sealed record LyricsBulkProgress(int Done, int Total, string CurrentTitle, string Outcome);

public sealed record LyricsBulkSummary(int Synced, int PlainOnly, int NotFound, int Skipped, int Failed)
{
    public int Total => Synced + PlainOnly + NotFound + Skipped + Failed;
}

public interface ILyricsBulkService
{
    /// <summary>Fetches LRCLIB lyrics for tracks that have no synced lyrics yet and saves them.</summary>
    Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct);

    /// <summary>Removes app-written lyrics from the tracks.</summary>
    Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Bulk lyrics operations over a selection: fetch from LRCLIB (two requests in flight,
/// polite to the community server) and remove. Persists through <see cref="LyricsWriter"/>
/// and asks the library for one save at the end so the lyrics store is committed.
/// </summary>
public sealed class LyricsBulkService : ILyricsBulkService
{
    private readonly ILrcLibService _lrcLib;
    private readonly LyricsWriter _writer;
    private readonly ILibraryService _library;
    private readonly Func<AppSettings> _settings;

    public LyricsBulkService(ILrcLibService lrcLib, LyricsWriter writer, ILibraryService library, Func<AppSettings> settings)
    {
        _lrcLib = lrcLib;
        _writer = writer;
        _library = library;
        _settings = settings;
    }

    public async Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
    {
        int synced = 0, plainOnly = 0, notFound = 0, skipped = 0, failed = 0, done = 0;
        var embed = Safe(() => _settings().LyricsStudioEmbedTags);
        var gate = new SemaphoreSlim(2, 2);
        var total = tracks.Count;

        var work = tracks.Select(async track =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string outcome;
                if (!string.IsNullOrWhiteSpace(track.SyncedLyrics))
                {
                    Interlocked.Increment(ref skipped);
                    outcome = "already synced";
                }
                else
                {
                    try
                    {
                        var result = await _lrcLib.GetLyricsAsync(track.Artist ?? string.Empty, track.Title ?? string.Empty, track.Duration.TotalSeconds, ct).ConfigureAwait(false);
                        if (result is null || !result.HasLyrics)
                        {
                            Interlocked.Increment(ref notFound);
                            outcome = "not found";
                        }
                        else if (result.HasSyncedLyrics)
                        {
                            _writer.Save(track, result.PlainLyrics, result.SyncedLyrics, embed);
                            Interlocked.Increment(ref synced);
                            outcome = "synced";
                        }
                        else
                        {
                            _writer.Save(track, result.PlainLyrics, null, embed);
                            Interlocked.Increment(ref plainOnly);
                            outcome = "plain lyrics";
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        outcome = "failed";
                        DebugLogger.Warn(DebugLogger.Category.Lyrics, "Bulk.FetchFailed", $"{track.Title}: {ex.Message}");
                    }
                }
                progress?.Report(new LyricsBulkProgress(Interlocked.Increment(ref done), total, track.Title ?? string.Empty, outcome));
            }
            finally
            {
                gate.Release();
            }
        });

        try { await Task.WhenAll(work).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* partial results below are still valid */ }

        if (synced + plainOnly > 0)
            await CommitAsync().ConfigureAwait(false);
        return new LyricsBulkSummary(synced, plainOnly, notFound, skipped, failed);
    }

    public async Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
    {
        var removed = 0;
        var embed = Safe(() => _settings().LyricsStudioEmbedTags);
        await Task.Run(() =>
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var t = tracks[i];
                var had = !string.IsNullOrWhiteSpace(t.Lyrics) || !string.IsNullOrWhiteSpace(t.SyncedLyrics)
                          || (!string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(Path.ChangeExtension(t.FilePath, ".lrc")));
                _writer.Remove(t, clearTags: embed);
                if (had) removed++;
                progress?.Report(new LyricsBulkProgress(i + 1, tracks.Count, t.Title ?? string.Empty, had ? "removed" : "nothing to remove"));
            }
        }, CancellationToken.None).ConfigureAwait(false);
        if (removed > 0) await CommitAsync().ConfigureAwait(false);
        return removed;
    }

    private async Task CommitAsync()
    {
        try { await _library.SaveAsync().ConfigureAwait(false); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "Bulk.SaveFailed", ex.Message); }
    }

    private static bool Safe(Func<bool> read)
    {
        try { return read(); } catch { return false; }
    }
}

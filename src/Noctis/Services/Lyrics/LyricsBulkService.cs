using Noctis.Models;
using Noctis.Services.LyricsStudio;

namespace Noctis.Services.Lyrics;

public sealed record LyricsBulkProgress(int Done, int Total, string CurrentTitle, string Outcome)
{
    /// <summary>The track this report is about (rows are matched by it, not by title).</summary>
    public Guid TrackId { get; init; }
}

public sealed record LyricsBulkSummary(int Synced, int PlainOnly, int NotFound, int Skipped, int Failed)
{
    public int Total => Synced + PlainOnly + NotFound + Skipped + Failed;

    /// <summary>Of <see cref="Synced"/>: saved with word timings.</summary>
    public int WordSynced { get; init; }
    /// <summary>Saved over lyrics the song already had (plain → synced, line → word).</summary>
    public int Upgraded { get; init; }
    /// <summary>Of <see cref="Skipped"/>: looked up in an earlier run and nothing better was found then.</summary>
    public int CheckedBefore { get; init; }
    /// <summary>Lyrics were found but the user's own lyrics file next to the song was left in place.</summary>
    public int KeptOwnFile { get; init; }
    /// <summary>A source said it was busy too often and was left alone for the rest of the run.</summary>
    public IReadOnlyList<string> BusySources { get; init; } = Array.Empty<string>();
    /// <summary>Songs that end the run with plain lyrics only: candidates for Lyrics Studio's timing.</summary>
    public IReadOnlyList<Track> PlainOnlyTracks { get; init; } = Array.Empty<Track>();
    /// <summary>A review run (<see cref="LyricsBulkOptions.ReviewOnly"/>): what was found and not saved yet.</summary>
    public IReadOnlyList<LyricsBulkFind> Finds { get; init; } = Array.Empty<LyricsBulkFind>();
}

/// <summary>Lyrics a review run found for a song, richer than what it has, waiting for the user's Save (GitHub #195).</summary>
/// <param name="Result">The find, already reduced to the preferred lyrics type.</param>
/// <param name="Rank">Its format: 3 word-synced, 2 line-synced, 1 plain.</param>
/// <param name="HadRank">What the song had when it was looked up (0 = no lyrics).</param>
public sealed record LyricsBulkFind(Track Track, LrcLibResult Result, string Source, int Rank, int HadRank);

/// <summary>How a fetch run treats songs that already have lyrics.</summary>
/// <param name="Upgrade">Also look up songs whose lyrics are plain or line-synced, and save a
/// richer find (word-synced over line-synced over plain). Off: only songs without synced lyrics.</param>
/// <param name="Recheck">Ask again about songs an earlier run already looked up.</param>
/// <param name="SyncedOnly">Save only synced finds: a plain find counts as "nothing better" even for a
/// song without lyrics (the lyrics page's "Upgrade plain lyrics", GitHub #194, keeps the plain lyrics it shows).</param>
/// <param name="ReviewOnly">Look up but save nothing: the finds come back in <see cref="LyricsBulkSummary.Finds"/>
/// for the user to pick from, and <see cref="ILyricsBulkService.ApplyAsync"/> saves the picked ones (GitHub #195).</param>
public sealed record LyricsBulkOptions(bool Upgrade = false, bool Recheck = false, bool SyncedOnly = false, bool ReviewOnly = false);

/// <summary>One online source a bulk run can ask, in the lyrics page's Auto order.</summary>
public sealed record LyricsBulkSource(
    string Name,
    Func<AppSettings, bool> IsEnabled,
    Func<string, string, double, CancellationToken, Task<LrcLibResult?>> SearchAsync);

public interface ILyricsBulkService
{
    /// <summary>Fetches LRCLIB lyrics for tracks that have no synced lyrics yet and saves them.</summary>
    Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct);

    /// <summary>Fetches with <paramref name="options"/>: every enabled source, richest format first, upgrades and resume.</summary>
    Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, LyricsBulkOptions options, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        => FetchAsync(tracks, progress, ct);

    /// <summary>Saves the finds of a review run the user kept (GitHub #195), as a normal run would have.</summary>
    Task<LyricsBulkSummary> ApplyAsync(IReadOnlyList<LyricsBulkFind> finds, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        => Task.FromResult(new LyricsBulkSummary(0, 0, 0, 0, 0));

    /// <summary>Removes app-written lyrics from the tracks.</summary>
    Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Bulk lyrics over a selection or the whole library (Discord, Cosmo 11-02: "a mass search of
/// all your songs … finding word-synced lyrics for all"). Each song is looked up in the sources
/// switched on in Settings, in the lyrics page's Auto order, stopping at the first answer with
/// the format the user prefers (Settings ▸ Lyrics ▸ Preferred lyrics type) — so most songs cost
/// one or two requests, not one per source. The richest answer is saved through
/// <see cref="LyricsWriter"/> (word timings as .elrc + a line-level .lrc), never over the user's
/// own lyrics file. Polite to the servers: two songs in flight, each source paced and backed off
/// on 429/503 (<see cref="PoliteGate"/>). Resumable: what was looked up is kept in a ledger, so a
/// stopped run continues where it left off. One library save at the end commits the store.
/// </summary>
public sealed class LyricsBulkService : ILyricsBulkService
{
    private readonly ILrcLibService _lrcLib;
    private readonly LyricsWriter _writer;
    private readonly ILibraryService _library;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<LyricsBulkSource>>? _sources;
    private readonly string? _ledgerPath;

    /// <summary>Spacing between two requests to one source.</summary>
    internal TimeSpan SourceInterval { get; init; } = TimeSpan.FromMilliseconds(350);
    /// <summary>One request's time box.</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Test seam: the gates' clock and wait.</summary>
    internal Func<DateTime>? Clock { get; init; }
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    /// <summary>Retries of one song at one source after a busy answer.</summary>
    internal const int BusyRetries = 2;

    public LyricsBulkService(ILrcLibService lrcLib, LyricsWriter writer, ILibraryService library, Func<AppSettings> settings,
        Func<IReadOnlyList<LyricsBulkSource>>? sources = null, string? ledgerPath = null)
    {
        _lrcLib = lrcLib;
        _writer = writer;
        _library = library;
        _settings = settings;
        _sources = sources;
        _ledgerPath = ledgerPath;
    }

    public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        => FetchAsync(tracks, new LyricsBulkOptions(), progress, ct);

    public async Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, LyricsBulkOptions options, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
    {
        int synced = 0, word = 0, plainOnly = 0, notFound = 0, skipped = 0, failed = 0, done = 0, upgraded = 0, before = 0, keptOwn = 0;
        var settings = SafeSettings();
        var embed = settings.LyricsStudioEmbedTags;
        var preferred = LyricsSaveOptions.ParsePreferredType(settings.LyricsPreferredType);
        // The format worth stopping at: the preferred one (word-synced by default).
        var target = preferred switch
        {
            LyricsPreferredType.LineSynced => 2,
            LyricsPreferredType.Plain => 1,
            _ => LyricsSearchSelector.WordSyncedRank,
        };
        var sources = EnabledSources(settings);
        var gates = sources.ToDictionary(s => s.Name, _ => new PoliteGate(SourceInterval, Clock, Delay));
        var ledger = new BulkRunLedger(_ledgerPath, Clock);
        var plainLeft = new System.Collections.Concurrent.ConcurrentBag<Track>();
        var finds = new System.Collections.Concurrent.ConcurrentBag<(int Index, LyricsBulkFind Find)>();
        var total = tracks.Count;

        // What each song has now, sidecars included (a .elrc next to the file is word-synced even
        // when the store holds nothing) — read off the caller's thread, once for the whole list.
        var formats = await Task.Run(() => ExistingLyricsLoader.DetectFormats(tracks, ct), ct).ConfigureAwait(false);

        var gate = new SemaphoreSlim(2, 2);
        var work = tracks.Select(async (track, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var have = Rank(formats[i]);
                string outcome;
                if (track.NoLyrics)
                {
                    // Marked as having no lyrics (GitHub #162): not searched, whatever it has.
                    Interlocked.Increment(ref skipped);
                    outcome = "marked as no lyrics";
                }
                else if (have >= target || (!options.Upgrade && have >= 2))
                {
                    Interlocked.Increment(ref skipped);
                    outcome = have >= LyricsSearchSelector.WordSyncedRank ? "already word-synced" : have >= 2 ? "already synced" : "already has lyrics";
                }
                else if (!options.Recheck && ledger.CheckedRecently(track.Id, have))
                {
                    Interlocked.Increment(ref skipped);
                    Interlocked.Increment(ref before);
                    outcome = "checked before";
                    if (have == 1) plainLeft.Add(track);
                }
                else
                {
                    try
                    {
                        var (best, bestSource, complete) = await FindBestAsync(track, sources, gates, target, ct).ConfigureAwait(false);
                        var found = LyricsSearchSelector.FormatRank(best);
                        if (best is null || found == 0)
                        {
                            if (have == 0) Interlocked.Increment(ref notFound); else Interlocked.Increment(ref skipped);
                            outcome = have == 0 ? (complete ? "not found" : "sources busy") : "nothing better";
                            if (have == 1) plainLeft.Add(track);
                        }
                        else if (found <= have || (options.SyncedOnly && found < 2))
                        {
                            Interlocked.Increment(ref skipped);
                            outcome = "nothing better";
                            if (have == 1) plainLeft.Add(track);
                        }
                        else if (options.ReviewOnly)
                        {
                            // Nothing is written: the user picks from the finds, then ApplyAsync saves.
                            var kept = ViewModels.LyricsViewModel.ApplyPreferredType(best, preferred);
                            var rank = KeptRank(kept, found, preferred);
                            finds.Add((i, new LyricsBulkFind(track, kept, bestSource, rank, have)));
                            outcome = rank >= LyricsSearchSelector.WordSyncedRank ? "found word-synced" : rank >= 2 ? "found synced" : "found plain";
                        }
                        else
                        {
                            var r = Save(track, best, found, have, preferred, embed);
                            if (r.KeptOwn) Interlocked.Increment(ref keptOwn);
                            if (have > 0) Interlocked.Increment(ref upgraded);
                            if (r.Synced) Interlocked.Increment(ref synced);
                            else { Interlocked.Increment(ref plainOnly); plainLeft.Add(track); }
                            if (r.Word) Interlocked.Increment(ref word);
                            outcome = r.Outcome;
                        }
                        // Only a complete answer (no source busy or down) counts as looked up. A review
                        // run saved nothing: its songs are looked up again unless their finds get saved.
                        if (complete && !options.ReviewOnly) ledger.Record(track.Id, Math.Max(have, found));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        outcome = "failed";
                        DebugLogger.Warn(DebugLogger.Category.Lyrics, "Bulk.FetchFailed", $"{track.Title}: {ex.Message}");
                    }
                }
                var n = Interlocked.Increment(ref done);
                progress?.Report(new LyricsBulkProgress(n, total, track.Title ?? string.Empty, outcome) { TrackId = track.Id });
                if (n % 25 == 0) ledger.Save();
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try { await Task.WhenAll(work).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* partial results below are still valid */ }

        ledger.Save();
        if (synced + plainOnly > 0)
            await CommitAsync().ConfigureAwait(false);
        return new LyricsBulkSummary(synced, plainOnly, notFound, skipped, failed)
        {
            WordSynced = word,
            Upgraded = upgraded,
            CheckedBefore = before,
            KeptOwnFile = keptOwn,
            BusySources = gates.Where(g => g.Value.GaveUp).Select(g => g.Key).ToList(),
            PlainOnlyTracks = plainLeft.Distinct().ToList(),
            Finds = finds.OrderBy(f => f.Index).Select(f => f.Find).ToList(),
        };
    }

    public async Task<LyricsBulkSummary> ApplyAsync(IReadOnlyList<LyricsBulkFind> finds, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
    {
        int synced = 0, word = 0, plainOnly = 0, upgraded = 0, keptOwn = 0, failed = 0;
        var settings = SafeSettings();
        var embed = settings.LyricsStudioEmbedTags;
        var preferred = LyricsSaveOptions.ParsePreferredType(settings.LyricsPreferredType);
        var ledger = new BulkRunLedger(_ledgerPath, Clock);
        var plainLeft = new List<Track>();
        await Task.Run(() =>
        {
            for (var i = 0; i < finds.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var f = finds[i];
                string outcome;
                try
                {
                    // The find is already the preferred type: reducing it again changes nothing.
                    var r = Save(f.Track, f.Result, f.Rank, f.HadRank, preferred, embed);
                    if (r.KeptOwn) keptOwn++;
                    if (f.HadRank > 0) upgraded++;
                    if (r.Synced) synced++;
                    else { plainOnly++; plainLeft.Add(f.Track); }
                    if (r.Word) word++;
                    outcome = r.Outcome;
                    ledger.Record(f.Track.Id, Math.Max(f.HadRank, f.Rank));
                }
                catch (Exception ex)
                {
                    failed++;
                    outcome = "failed";
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "Bulk.ApplyFailed", $"{f.Track.Title}: {ex.Message}");
                }
                progress?.Report(new LyricsBulkProgress(i + 1, finds.Count, f.Track.Title ?? string.Empty, outcome) { TrackId = f.Track.Id });
            }
        }, CancellationToken.None).ConfigureAwait(false);
        ledger.Save();
        if (synced + plainOnly > 0)
            await CommitAsync().ConfigureAwait(false);
        return new LyricsBulkSummary(synced, plainOnly, 0, 0, failed)
        {
            WordSynced = word,
            Upgraded = upgraded,
            KeptOwnFile = keptOwn,
            PlainOnlyTracks = plainLeft,
        };
    }

    /// <summary>Saves <paramref name="best"/> (format <paramref name="found"/>) for a song that had format
    /// <paramref name="have"/>, reduced to the preferred type, through <see cref="LyricsWriter"/> (so per
    /// Settings ▸ Lyrics ▸ Save lyrics to, never over the user's own lyrics file).</summary>
    private (string Outcome, bool Synced, bool Word, bool KeptOwn) Save(
        Track track, LrcLibResult best, int found, int have, LyricsPreferredType preferred, bool embed)
    {
        var kept = ViewModels.LyricsViewModel.ApplyPreferredType(best, preferred);
        var syncedText = SyncedToWrite(kept);
        var saved = _writer.SaveDetailed(track, kept.PlainLyrics, syncedText, embed, replaceForeignSidecar: false);
        string outcome;
        bool isSynced = false, isWord = false;
        if (found >= 2 && syncedText is not null)
        {
            isSynced = true;
            if (found >= LyricsSearchSelector.WordSyncedRank && preferred == LyricsPreferredType.WordSynced)
            {
                isWord = true;
                outcome = have > 0 ? "upgraded to word-synced" : "word-synced";
            }
            else outcome = have > 0 ? "upgraded to synced" : "synced";
        }
        else outcome = "plain lyrics";
        if (saved.KeptForeignSidecar) outcome += " (your lyrics file kept)";
        return (outcome, isSynced, isWord, saved.KeptForeignSidecar);
    }

    /// <summary>The format a find keeps once reduced to the preferred type, as <see cref="Save"/> counts it.</summary>
    private static int KeptRank(LrcLibResult kept, int found, LyricsPreferredType preferred)
    {
        if (found >= 2 && SyncedToWrite(kept) is not null)
            return found >= LyricsSearchSelector.WordSyncedRank && preferred == LyricsPreferredType.WordSynced
                ? LyricsSearchSelector.WordSyncedRank : 2;
        return 1;
    }

    private static int Rank(LyricsFormat f) => f switch
    {
        LyricsFormat.Elrc => LyricsSearchSelector.WordSyncedRank,
        LyricsFormat.Lrc => 2,
        LyricsFormat.Plain => 1,
        _ => 0,
    };

    /// <summary>
    /// Asks the sources in order until one has the target format; the richest answer wins, an
    /// earlier source a tie. <c>Complete</c> is false when a source that might have done better
    /// was busy or failed, so the song is looked up again next run.
    /// </summary>
    private async Task<(LrcLibResult? Best, string Source, bool Complete)> FindBestAsync(
        Track track, IReadOnlyList<LyricsBulkSource> sources, Dictionary<string, PoliteGate> gates, int target, CancellationToken ct)
    {
        LrcLibResult? best = null;
        var bestRank = 0;
        var bestSource = string.Empty;
        var complete = true;
        var artist = track.Artist ?? string.Empty;
        var title = track.Title ?? string.Empty;
        var duration = track.Duration.TotalSeconds;
        foreach (var source in sources)
        {
            if (bestRank >= target) break;
            var gate = gates[source.Name];
            if (gate.GaveUp) { complete = false; continue; }
            for (var attempt = 0; ; attempt++)
            {
                await gate.WaitTurnAsync(ct).ConfigureAwait(false);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(RequestTimeout);
                    var result = await source.SearchAsync(artist, title, duration, timeout.Token).ConfigureAwait(false);
                    gate.ReportOk();
                    var rank = result is { Instrumental: true } ? 0 : LyricsSearchSelector.FormatRank(result);
                    if (rank > bestRank) { best = result; bestRank = rank; bestSource = source.Name; }
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsBusy(ex))
                {
                    gate.ReportBusy();
                    if (attempt >= BusyRetries || gate.GaveUp) { complete = false; break; }
                }
                catch (Exception ex)
                {
                    // Down, timed out or a bad answer: this source has nothing for this song now.
                    complete = false;
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "Bulk.SourceFailed", $"{source.Name} / {title}: {ex.Message}");
                    break;
                }
            }
        }
        return (best, bestSource, complete);
    }

    private static bool IsBusy(Exception ex) => ex switch
    {
        LyricsProviderException p => p.IsBusy,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable } => true,
        _ => false,
    };

    /// <summary>The sources switched on in Settings; LRCLIB alone when the app wired none.</summary>
    private IReadOnlyList<LyricsBulkSource> EnabledSources(AppSettings settings)
    {
        IReadOnlyList<LyricsBulkSource> all;
        try { all = _sources?.Invoke() ?? Array.Empty<LyricsBulkSource>(); }
        catch { all = Array.Empty<LyricsBulkSource>(); }
        if (all.Count == 0)
            all = new[] { new LyricsBulkSource("LRCLIB", s => s.LrcLibEnabled, (a, t, d, c) => LrcLibLookup.FetchAsync(_lrcLib, a, t, d, c)) };
        return all.Where(s => { try { return s.IsEnabled(settings); } catch { return false; } }).ToList();
    }

    /// <summary>The synced text a result is saved as: its Lyricsfile's word timings as ELRC when it
    /// has them, else its synced lyrics (as the lyrics page saves, LyricsViewModel.SyncedToWrite).</summary>
    internal static string? SyncedToWrite(LrcLibResult result) =>
        (result.HasLyricsfile ? LyricsfileParser.ToElrc(result.Lyricsfile) : null)
        ?? (string.IsNullOrWhiteSpace(result.SyncedLyrics) ? null : result.SyncedLyrics);

    public async Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
    {
        var removed = 0;
        var embed = Safe(() => _settings().LyricsStudioEmbedTags);
        // Lyrics saved inside the audio files (GitHub #135) live in the tags: clear those too.
        var tags = embed || Safe(() => LyricsSaveOptions.ParseTarget(_settings().LyricsSaveTarget).WritesTags());
        await Task.Run(() =>
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var t = tracks[i];
                var had = !string.IsNullOrWhiteSpace(t.Lyrics) || !string.IsNullOrWhiteSpace(t.SyncedLyrics)
                          || (!string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(Path.ChangeExtension(t.FilePath, ".lrc")));
                _writer.Remove(t, clearTags: tags);
                if (had) removed++;
                progress?.Report(new LyricsBulkProgress(i + 1, tracks.Count, t.Title ?? string.Empty, had ? "removed" : "nothing to remove") { TrackId = t.Id });
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

    private AppSettings SafeSettings()
    {
        try { return _settings(); } catch { return new AppSettings(); }
    }

    private static bool Safe(Func<bool> read)
    {
        try { return read(); } catch { return false; }
    }
}

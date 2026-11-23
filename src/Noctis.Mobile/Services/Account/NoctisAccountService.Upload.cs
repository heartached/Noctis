using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// "Send phone songs to computer": the phone's own songs (not the desktop's) go to the desktop's
/// library, one way. First the desktop is asked which it already has (noctisHasTracks: title,
/// artist and length, 100 songs a call), because a byte hash would never match the same song in
/// another format (a FLAC on the PC, an MP3 of it on the phone), and hashing every phone file
/// first would read the whole collection before sending anything. The rest are sent one at a
/// time (noctisUploadTrack), each read straight from its file as it goes out; the desktop still
/// refuses an identical file by hash. Every song sent or found is remembered per account
/// (uploads.json), so a cancelled or failed send resumes on the next press without asking again.
/// </summary>
public sealed partial class NoctisAccountService
{
    /// <summary>Opens a phone song for sending (Android: the SAF document, read through its file
    /// descriptor). Null, or a null answer for a path: the path is opened as a plain file.</summary>
    public ITrackFileAccess? LocalFiles { get; init; }

    private readonly object _uploadGate = new();
    private Task<NoctisUploadResult>? _uploadTask;
    private CancellationTokenSource? _uploadCts;
    private volatile bool _isUploading;

    public bool IsUploading => _isUploading;

    /// <summary>Songs sent between writes of the sent-songs record (tests shorten it). Writing it after
    /// every song rewrote a record that grows with each one: N songs, N² bytes written.</summary>
    internal int UploadSaveEvery { get; init; } = 25;

    /// <summary>The record is also written once this long has passed since the last write, so a slow
    /// send (large files) loses no more than this much to an app that is killed.</summary>
    private static readonly TimeSpan UploadSaveInterval = TimeSpan.FromSeconds(5);

    /// <summary>Tests: each write of the sent-songs record.</summary>
    internal event Action? UploadRecordSaved;

    public event EventHandler<NoctisUploadProgress>? UploadProgress;

    /// <summary>A song of the phone's own library: a scanned file, not a desktop song.</summary>
    private bool IsPhoneSong(Track track) =>
        track is { SourceType: SourceType.Local } && !IsRemote(track) && !string.IsNullOrEmpty(track.FilePath);

    public Task<NoctisUploadResult> SendLocalSongsAsync(CancellationToken ct = default)
    {
        lock (_uploadGate)
        {
            if (_uploadTask is { IsCompleted: false } running)
            {
                // The second caller (the Account page reopened mid-send) can stop it too.
                var cts = _uploadCts;
                if (ct.CanBeCanceled && cts is not null)
                    ct.Register(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
                return running;
            }
            if (_account is null) return Task.FromException<NoctisUploadResult>(new NoctisServerException(NoctisErrorKind.SignedOut, "Not signed in."));
            _uploadCts?.Dispose();
            _uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _uploadCts.Token;
            _isUploading = true;
            _uploadTask = Task.Run(() => RunUploadAsync(token), CancellationToken.None);
            return _uploadTask;
        }
    }

    /// <summary>Sign-out: stops a running send and waits for it to wind down.</summary>
    private async Task StopUploadsAsync()
    {
        Task? running;
        lock (_uploadGate)
        {
            _uploadCts?.Cancel();
            running = _uploadTask;
        }
        if (running is null) return;
        try { await running.ConfigureAwait(false); } catch { /* cancelled or failed: irrelevant now */ }
    }

    private async Task<NoctisUploadResult> RunUploadAsync(CancellationToken ct)
    {
        RaiseStateChanged();
        try
        {
            return await UploadCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is NoctisServerException { Kind: NoctisErrorKind.SignedOut }) ForgetAccountLocally();
            DebugLog.Write("Account", $"sending songs stopped: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
            throw;
        }
        finally
        {
            _isUploading = false;
            RaiseStateChanged();
        }
    }

    private async Task<NoctisUploadResult> UploadCoreAsync(CancellationToken ct)
    {
        var client = GetClient();
        var account = _account ?? throw new NoctisServerException(NoctisErrorKind.SignedOut, "Not signed in.");
        await LibraryReady.WaitAsync(ct).ConfigureAwait(false);

        var record = _store.LoadUploads();
        if (!SameIdentity(record.ServerUrl, record.UserName, account))
            record = new NoctisUploadState { ServerUrl = account.ServerUrl, UserName = account.UserName };
        var phoneSongs = _library.Tracks.Where(IsPhoneSong).DistinctBy(t => t.Id).ToList();
        var previously = phoneSongs.Count(t => record.Sent.Contains(Hex(t)));
        var todo = phoneSongs.Where(t => !record.Sent.Contains(Hex(t))).ToList();
        if (todo.Count == 0)
        {
            DebugLog.Write("Account", $"send songs: nothing new ({previously} sent before)");
            return new NoctisUploadResult(0, 0, 0, previously);
        }

        // 1. What the computer already has (Total 0 = still asking).
        RaiseUploadProgress(0, 0);
        var already = 0;
        var toSend = new List<Track>();
        foreach (var chunk in todo.Chunk(NoctisServerClient.HasTracksBatch))
        {
            var have = await client.HasTracksAsync(chunk.Select(IdentityOf).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                if (have[i]) { record.Sent.Add(Hex(chunk[i])); already++; }
                else toSend.Add(chunk[i]);
            }
        }
        if (already > 0) SaveUploads(record);

        // 2. The rest, one at a time. A song that fails is not remembered: the next press retries it.
        // What was sent is written in batches (every UploadSaveEvery songs or UploadSaveInterval), and
        // whatever is left when the send ends, fails or is stopped: an app killed mid-send sends at
        // most the last batch again, which the desktop finds by its bytes ("already there").
        var sent = 0;
        var failed = 0;
        var done = 0;
        var unsaved = 0;
        var lastSave = Environment.TickCount64;
        RaiseUploadProgress(0, toSend.Count);
        try
        {
            foreach (var track in toSend)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await UploadOneAsync(client, track, ct).ConfigureAwait(false) == UploadOutcome.AlreadyThere) already++;
                    else sent++;
                    record.Sent.Add(Hex(track));
                    if (++unsaved >= UploadSaveEvery || Environment.TickCount64 - lastSave >= UploadSaveInterval.TotalMilliseconds)
                    {
                        SaveUploads(record);
                        unsaved = 0;
                        lastSave = Environment.TickCount64;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (NoctisServerException ex) when (ex.Kind is NoctisErrorKind.SignedOut or NoctisErrorKind.CertificateChanged
                                                           or NoctisErrorKind.SyncDisabled or NoctisErrorKind.Unreachable)
                {
                    // Every other song would fail the same way; what was sent stays remembered.
                    DebugLog.Write("Account", $"send songs: {sent} sent, {already} already there, {failed} failed, stopped ({ex.Kind})");
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    DebugLog.Write("Account", $"send song failed: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
                }
                RaiseUploadProgress(++done, toSend.Count);
            }
        }
        finally
        {
            if (unsaved > 0) SaveUploads(record);
        }
        DebugLog.Write("Account", $"send songs: {sent} sent, {already} already there, {failed} failed, {previously} sent before");
        return new NoctisUploadResult(sent, already, failed, previously);
    }

    /// <summary>One song: opened through <see cref="LocalFiles"/> (or by path), checked against the
    /// desktop's limits, sent under its own file name.</summary>
    private async Task<UploadOutcome> UploadOneAsync(NoctisServerClient client, Track track, CancellationToken ct)
    {
        var name = UploadNameOf(track.FilePath);
        if (!MetadataService.SupportedExtensions.Contains(Path.GetExtension(name)))
            throw new InvalidDataException("Not a file the computer plays.");
        await using var stream = OpenPhoneSong(track.FilePath) ?? throw new FileNotFoundException("The song could not be opened.");
        long length = -1;
        try { if (stream.CanSeek) length = stream.Length - stream.Position; } catch (NotSupportedException) { }
        if (length > NoctisServerClient.MaxUploadBytes || (length < 0 && track.FileSize > NoctisServerClient.MaxUploadBytes))
            throw new InvalidDataException("Larger than the computer takes.");
        return await client.UploadTrackAsync(stream, length, name, ct).ConfigureAwait(false);
    }

    private Stream? OpenPhoneSong(string path)
    {
        if (LocalFiles?.OpenAudio(path) is { } opened) return opened;
        if (path.Contains("://", StringComparison.Ordinal) || !File.Exists(path)) return null;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    /// <summary>The file name the desktop stores it under: the source's own (a SAF document URI ends
    /// in its encoded path, as the lyrics loader reads it), the server makes it safe.</summary>
    private string UploadNameOf(string path) =>
        LocalFiles?.AudioFileName(path) ?? Path.GetFileName(Uri.UnescapeDataString(path).Replace('\\', '/').TrimEnd('/'));

    private static SongIdentity IdentityOf(Track t) => new(t.Title ?? string.Empty, t.Artist ?? string.Empty, (int)t.Duration.TotalSeconds);

    private static string Hex(Track t) => t.Id.ToString("N");

    private void SaveUploads(NoctisUploadState record)
    {
        try
        {
            _store.SaveUploads(record);
            UploadRecordSaved?.Invoke();
        }
        catch (Exception ex) { DebugLog.Write("Account", $"uploads record save failed ({ex.GetType().Name})"); }
    }

    private void RaiseUploadProgress(int done, int total) => UploadProgress?.Invoke(this, new NoctisUploadProgress(done, total));
}

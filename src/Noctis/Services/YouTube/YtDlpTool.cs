using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Noctis.Services.YouTube;

/// <summary>Result of a yt-dlp update check.</summary>
public sealed record YtDlpUpdateStatus(string? InstalledVersion, string? LatestVersion, bool IsAppInstalled, bool Updated)
{
    /// <summary>A newer release exists than the copy in use (only possible for a copy Noctis may not replace, or a failed update).</summary>
    public bool UpdateAvailable => YtDlpParsing.IsNewer(LatestVersion, InstalledVersion);
}

/// <summary>A yt-dlp run that failed; <see cref="Stderr"/> keeps the full output for classification and the log.</summary>
public sealed class YtDlpRunException : InvalidOperationException
{
    public int ExitCode { get; }
    public string Stderr { get; }

    public YtDlpRunException(int exitCode, string stderr, string message) : base(message)
    {
        ExitCode = exitCode;
        Stderr = stderr ?? string.Empty;
    }
}

/// <summary>
/// yt-dlp as an external tool, the way ffmpeg already is: a user-set path wins, then the
/// copy Noctis installs under its data folder, then PATH. Installing fetches the official
/// release asset from GitHub; the same call updates it.
/// YouTube changes break old yt-dlp builds (HTTP 403, 09-24 report on 1.5.2), so the
/// app-installed copy is kept current: a quiet check once per session (throttled to 24 h),
/// and a forced check + one retry when a run is blocked. A user-set or PATH copy is never
/// replaced; for those the UI only says an update is available.
/// </summary>
public sealed class YtDlpTool
{
    private const string UpdateStateFileName = "yt-dlp-update.json";

    private readonly HttpClient _http;
    private readonly Func<string> _overridePath;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly object _sessionLock = new();
    private Task<YtDlpUpdateStatus>? _sessionCheck;
    private (string Path, DateTime Stamp, string? Version)? _versionCache;

    public string ToolsDirectory { get; }

    /// <summary>Newest release seen by an update check (this session or the persisted one), or null.</summary>
    public string? LatestKnownVersion { get; private set; }

    // ── Test seams (internal): process runner, GitHub lookup, installer, clock, JS runtimes ──
    internal Func<string, IReadOnlyList<string>, Action<string>?, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> Runner { get; set; }
    internal Func<CancellationToken, Task<string?>> LatestVersionFetcher { get; set; }
    internal Func<CancellationToken, Task> Updater { get; set; }
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    /// <summary>Null = detect from PATH on first use.</summary>
    internal (bool HasDeno, bool HasNode)? JsRuntimes { get; set; }
    /// <summary>Null = <see cref="ResumableDownload.Options.Default"/>; tests shorten the retry delays.</summary>
    internal ResumableDownload.Options? DownloadOptions { get; set; }

    public YtDlpTool(HttpClient http, string dataRoot, Func<string> overridePath)
    {
        _http = http;
        _overridePath = overridePath;
        ToolsDirectory = Path.Combine(dataRoot, "tools");
        Runner = RunProcessAsync;
        LatestVersionFetcher = FetchLatestVersionAsync;
        Updater = ct => InstallCoreAsync(null, ct);
    }

    /// <summary>Where the app-installed copy lives.</summary>
    public string InstalledPath => Path.Combine(ToolsDirectory, YtDlpParsing.ReleaseAssetName());

    private string UpdateStatePath => Path.Combine(ToolsDirectory, UpdateStateFileName);

    /// <summary>Absolute path of a usable yt-dlp, or null.</summary>
    public string? Resolve()
    {
        var configured = SafeTrim(_overridePath);
        if (configured.Length > 0 && File.Exists(configured)) return configured;
        if (File.Exists(InstalledPath)) return InstalledPath;
        return FindOnPath(OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
    }

    public bool IsAvailable => Resolve() != null;

    /// <summary>True when <paramref name="path"/> is the copy Noctis installed (the only one it may replace).</summary>
    public bool IsAppInstalled(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledPath), cmp);
        }
        catch { return false; }
    }

    private static string SafeTrim(Func<string> read)
    {
        try { return (read() ?? string.Empty).Trim(); } catch { return string.Empty; }
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Downloads the latest release asset into the tools folder (replacing an older copy).</summary>
    public async Task<string> InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await InstallCoreAsync(progress, ct).ConfigureAwait(false); }
        finally { _updateGate.Release(); }
    }

    private async Task<string> InstallCoreAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var url = YtDlpParsing.ReleaseDownloadUrl();
        var temp = InstalledPath + ".part";
        // Retried/resumed within this call only: "latest" can change between calls, so a
        // leftover .part from an earlier run is never continued.
        try
        {
            await ResumableDownload.DownloadAsync(
                _http,
                () => new HttpRequestMessage(HttpMethod.Get, url),
                temp,
                resumeExisting: false,
                (done, total) => { if (total > 0) progress?.Report(Math.Min(0.99, done / (double)total)); },
                "YtDlp.Install",
                ct,
                DownloadOptions).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
        File.Move(temp, InstalledPath, overwrite: true);
        _versionCache = null;
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(InstalledPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
            catch { }
        }
        progress?.Report(1);
        Log("YtDlp.Installed", InstalledPath);
        return InstalledPath;
    }

    /// <summary>"2026.08.12"-style version, or null when the tool cannot run.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        var exe = Resolve();
        return exe is null ? null : await GetVersionAsync(exe, ct).ConfigureAwait(false);
    }

    /// <summary>Version of one binary, cached per path + write time so each download can log it cheaply.</summary>
    private async Task<string?> GetVersionAsync(string exe, CancellationToken ct)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(exe); } catch { stamp = default; }
        if (_versionCache is { } c && c.Path == exe && c.Stamp == stamp && c.Version is not null) return c.Version;
        try
        {
            var (code, stdout, _) = await Runner(exe, YtDlpParsing.VersionArgs(), null, ct).ConfigureAwait(false);
            var line = stdout.Split('\n').FirstOrDefault()?.Trim();
            var version = code == 0 && !string.IsNullOrEmpty(line) ? line : null;
            _versionCache = (exe, stamp, version);
            return version;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // ── Updates ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The quiet once-per-session check (first YouTube use / Settings row). Runs off the UI
    /// thread, never throws; the 24 h throttle still applies to the network lookup.
    /// </summary>
    public Task<YtDlpUpdateStatus> EnsureSessionUpdateCheckAsync()
    {
        lock (_sessionLock)
        {
            return _sessionCheck ??= Task.Run(() => SafeCheckAsync(force: false, CancellationToken.None));
        }
    }

    private async Task<YtDlpUpdateStatus> SafeCheckAsync(bool force, CancellationToken ct)
    {
        try { return await CheckForUpdateAsync(force, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogWarn("YtDlp.UpdateCheckFailed", ex.Message);
            return new YtDlpUpdateStatus(null, LatestKnownVersion, false, false);
        }
    }

    /// <summary>
    /// Looks up the latest release (at most once per 24 h unless <paramref name="force"/>) and,
    /// when the copy in use is the app-installed one and older, replaces it. Network errors are
    /// logged and swallowed.
    /// </summary>
    public async Task<YtDlpUpdateStatus> CheckForUpdateAsync(bool force, CancellationToken ct)
    {
        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exe = Resolve();
            if (exe is null) return new YtDlpUpdateStatus(null, LatestKnownVersion, false, false);
            var appInstalled = IsAppInstalled(exe);
            var installed = await GetVersionAsync(exe, ct).ConfigureAwait(false);

            var (lastCheck, latest) = ReadUpdateState();
            if (YtDlpParsing.ShouldCheckForUpdate(lastCheck, Clock(), force))
            {
                try
                {
                    var fetched = await LatestVersionFetcher(ct).ConfigureAwait(false);
                    if (fetched is not null)
                    {
                        latest = fetched;
                        WriteUpdateState(Clock(), latest);
                    }
                    Log("YtDlp.UpdateCheck",
                        $"installed={installed ?? "?"} latest={fetched ?? "?"} appInstalled={appInstalled} force={force}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    LogWarn("YtDlp.UpdateCheckFailed", ex.Message);
                }
            }
            if (latest is not null) LatestKnownVersion = latest;

            var updated = false;
            if (appInstalled && YtDlpParsing.IsNewer(latest, installed))
            {
                try
                {
                    await Updater(ct).ConfigureAwait(false);
                    _versionCache = null;
                    var before = installed;
                    installed = await GetVersionAsync(exe, ct).ConfigureAwait(false);
                    updated = true;
                    Log("YtDlp.Updated", $"{before ?? "?"} → {installed ?? "?"}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    LogWarn("YtDlp.UpdateFailed", ex.Message);
                }
            }
            return new YtDlpUpdateStatus(installed, latest, appInstalled, updated);
        }
        finally { _updateGate.Release(); }
    }

    private (DateTime? LastCheckUtc, string? Latest) ReadUpdateState()
    {
        try
        {
            if (!File.Exists(UpdateStatePath)) return (null, LatestKnownVersion);
            using var doc = JsonDocument.Parse(File.ReadAllText(UpdateStatePath));
            var root = doc.RootElement;
            DateTime? last = root.TryGetProperty("lastCheckUtc", out var l) && l.TryGetDateTime(out var dt)
                ? DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc)
                : null;
            var latest = root.TryGetProperty("latestVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (last, YtDlpParsing.ParseVersion(latest) is null ? LatestKnownVersion : latest);
        }
        catch { return (null, LatestKnownVersion); }
    }

    private void WriteUpdateState(DateTime nowUtc, string? latest)
    {
        try
        {
            Directory.CreateDirectory(ToolsDirectory);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("lastCheckUtc", nowUtc);
                if (latest is null) w.WriteNull("latestVersion"); else w.WriteString("latestVersion", latest);
                w.WriteEndObject();
            }
            File.WriteAllBytes(UpdateStatePath, ms.ToArray());
        }
        catch (Exception ex)
        {
            LogWarn("YtDlp.UpdateStateWriteFailed", ex.Message);
        }
    }

    private async Task<string?> FetchLatestVersionAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, YtDlpParsing.LatestReleaseApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return YtDlpParsing.ParseLatestTag(json);
    }

    // ── JS runtime ────────────────────────────────────────────────────────────

    private async Task<string?> JsRuntimeForAsync(string exe, CancellationToken ct)
    {
        var (hasDeno, hasNode) = JsRuntimes ??= DetectJsRuntimes();
        if (hasDeno || !hasNode) return null;
        return YtDlpParsing.PickJsRuntime(hasDeno, hasNode, await GetVersionAsync(exe, ct).ConfigureAwait(false));
    }

    private static (bool HasDeno, bool HasNode) DetectJsRuntimes()
    {
        var win = OperatingSystem.IsWindows();
        var result = (FindOnPath(win ? "deno.exe" : "deno") is not null, FindOnPath(win ? "node.exe" : "node") is not null);
        Log("YtDlp.JsRuntimes", $"deno={result.Item1} node={result.Item2}");
        return result;
    }

    // ── Operations ────────────────────────────────────────────────────────────

    public async Task<List<YouTubeTrackInfo>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var exe = Resolve() ?? throw new InvalidOperationException("yt-dlp is not installed.");
        var js = await JsRuntimeForAsync(exe, ct).ConfigureAwait(false);
        var (code, stdout, stderr) = await RunLoggedAsync("Search", exe, YtDlpParsing.SearchArgs(query, limit, js), null, ct).ConfigureAwait(false);
        var list = YtDlpParsing.ParseSearch(stdout);
        if (list.Count == 0 && code != 0) throw new YtDlpRunException(code, stderr, Tail(stderr) ?? "Search failed.");
        return list;
    }

    public Task<YouTubeTrackInfo?> GetInfoAsync(string url, CancellationToken ct) =>
        RunWithBlockedRetryAsync("Info", async (exe, js) =>
        {
            var (code, stdout, stderr) = await RunLoggedAsync("Info", exe, YtDlpParsing.InfoArgs(url, js), null, ct).ConfigureAwait(false);
            var info = YtDlpParsing.ParseInfo(stdout.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('{')) ?? string.Empty);
            if (info is null && code != 0) throw new YtDlpRunException(code, stderr, Tail(stderr) ?? "Could not read this video.");
            return info;
        }, null, ct);

    /// <summary>
    /// Downloads the best audio for <paramref name="url"/> into a fresh temp folder under
    /// <paramref name="targetDir"/> and returns the one file it produced.
    /// </summary>
    public Task<string> DownloadAsync(string url, string targetDir, string? ffmpegPath, IProgress<double>? progress, CancellationToken ct, Action<string>? onStatus = null)
    {
        var ffmpegDir = ffmpegPath is null ? null : Path.GetDirectoryName(ffmpegPath);
        return RunWithBlockedRetryAsync("Download", (exe, js) =>
            DownloadToScratchAsync("Download", exe, targetDir, template => YtDlpParsing.DownloadArgs(url, template, ffmpegDir, js), progress, ct),
            onStatus, ct);
    }

    /// <summary>
    /// Downloads the video stream (no audio) for a lyrics backdrop into a fresh temp folder under
    /// <paramref name="targetDir"/>, capped at <paramref name="maxHeight"/> (0 = best), and returns the file.
    /// </summary>
    public Task<string> DownloadVideoAsync(string url, string targetDir, string? ffmpegPath, int maxHeight, IProgress<double>? progress, CancellationToken ct, Action<string>? onStatus = null)
    {
        var ffmpegDir = ffmpegPath is null ? null : Path.GetDirectoryName(ffmpegPath);
        return RunWithBlockedRetryAsync("VideoDownload", (exe, js) =>
            DownloadToScratchAsync("VideoDownload", exe, targetDir, template => YtDlpParsing.VideoDownloadArgs(url, template, ffmpegDir, maxHeight, js), progress, ct),
            onStatus, ct);
    }

    private async Task<string> DownloadToScratchAsync(string operation, string exe, string targetDir,
        Func<string, IReadOnlyList<string>> buildArgs, IProgress<double>? progress, CancellationToken ct)
    {
        var scratch = Path.Combine(targetDir, ".noctis-download-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var template = Path.Combine(scratch, "%(id)s.%(ext)s");
            var (code, _, stderr) = await RunLoggedAsync(operation, exe, buildArgs(template), line =>
            {
                if (YtDlpParsing.ParseProgressPercent(line) is { } pct) progress?.Report(pct / 100.0);
            }, ct).ConfigureAwait(false);

            var produced = Directory.EnumerateFiles(scratch)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
            if (produced is null || code != 0 && new FileInfo(produced).Length == 0)
                throw new YtDlpRunException(code, stderr, Tail(stderr) ?? "Download produced no file.");
            progress?.Report(1);
            return produced;
        }
        catch
        {
            try { Directory.Delete(scratch, true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="attempt"/>; when YouTube blocks it (403 / bot check / formats
    /// withheld) and the copy is app-installed, forces an update check, updates if newer and
    /// retries once. A still-blocked run ends with a plain-language message instead of the raw
    /// ERROR line. User-set / PATH copies are never replaced and not retried.
    /// </summary>
    private async Task<T> RunWithBlockedRetryAsync<T>(string operation, Func<string, string?, Task<T>> attempt, Action<string>? onStatus, CancellationToken ct)
    {
        var exe = Resolve() ?? throw new InvalidOperationException("yt-dlp is not installed.");
        try
        {
            return await attempt(exe, await JsRuntimeForAsync(exe, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (YtDlpRunException first) when (YtDlpParsing.IsBlockedError(first.Stderr) && !ct.IsCancellationRequested)
        {
            if (!IsAppInstalled(exe))
            {
                var status = await SafeCheckAsync(force: false, ct).ConfigureAwait(false);
                var version = status.InstalledVersion ?? await GetVersionAsync(exe, ct).ConfigureAwait(false);
                throw new InvalidOperationException(YtDlpParsing.BlockedMessage(version, status.LatestVersion ?? LatestKnownVersion), first);
            }

            Log("YtDlp.BlockedRetry", operation);
            try { onStatus?.Invoke(Localization.Loc.T("YtDlp.UpdatingRetrying")); } catch { }
            var check = await SafeCheckAsync(force: true, ct).ConfigureAwait(false);
            var retryExe = Resolve() ?? exe;
            try
            {
                return await attempt(retryExe, await JsRuntimeForAsync(retryExe, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (YtDlpRunException second) when (YtDlpParsing.IsBlockedError(second.Stderr) && !ct.IsCancellationRequested)
            {
                var version = await GetVersionAsync(retryExe, ct).ConfigureAwait(false) ?? check.InstalledVersion;
                throw new InvalidOperationException(YtDlpParsing.BlockedMessage(version, check.LatestVersion ?? LatestKnownVersion), second);
            }
        }
    }

    /// <summary>Runs yt-dlp, logging version + args up front and the stderr tail of a failing run.</summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunLoggedAsync(string operation, string exe, IReadOnlyList<string> args, Action<string>? onStdoutLine, CancellationToken ct)
    {
        var version = await GetVersionAsync(exe, ct).ConfigureAwait(false);
        Log("YtDlp.Run",
            $"{operation} yt-dlp {version ?? "?"} ({(IsAppInstalled(exe) ? "app-installed" : exe)}) args: {string.Join(' ', args)}");
        var result = await Runner(exe, args, onStdoutLine, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            LogWarn("YtDlp.Failed",
                $"{operation} exit={result.ExitCode} yt-dlp {version ?? "?"} stderr: {YtDlpParsing.StderrTail(result.Stderr)}");
        }
        return result;
    }

    // DebugLogger is off unless the debug panel is on, and only Playback reaches "Copy Logs";
    // yt-dlp lines also go to the session log so a bug report carries them (09-24 403 report).
    private static void Log(string action, string? message)
    {
        DebugLogger.Info(DebugLogger.Category.State, action, message);
        DebugLog.Write("YouTube", $"{action}: {message}");
    }

    private static void LogWarn(string action, string? message)
    {
        DebugLogger.Warn(DebugLogger.Category.State, action, message);
        DebugLog.Write("YouTube", $"Warn: {action}: {message}");
    }

    /// <summary>Removes the temp folder a download left behind once its file has been moved out.</summary>
    public static void CleanupScratch(string producedFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(producedFile);
            if (dir is not null && Path.GetFileName(dir).StartsWith(".noctis-download-", StringComparison.Ordinal) && Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch { }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string exe, IReadOnlyList<string> args, Action<string>? onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp could not be started");
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

        var stdout = new StringBuilder();
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            stdout.Append(line).Append('\n');
            onStdoutLine?.Invoke(line);
        }
        var stderr = await stderrTask.ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, stdout.ToString(), stderr);
    }

    private static string? Tail(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault();
        return string.IsNullOrWhiteSpace(last) ? null : last.Length > 240 ? last[..240] : last;
    }
}

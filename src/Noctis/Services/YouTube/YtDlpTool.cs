using System.Diagnostics;
using System.Text;

namespace Noctis.Services.YouTube;

/// <summary>
/// yt-dlp as an external tool, the way ffmpeg already is: a user-set path wins, then the
/// copy Noctis installs under its data folder, then PATH. Installing fetches the official
/// release asset from GitHub; the same call updates it.
/// </summary>
public sealed class YtDlpTool
{
    private readonly HttpClient _http;
    private readonly Func<string> _overridePath;

    public string ToolsDirectory { get; }

    public YtDlpTool(HttpClient http, string dataRoot, Func<string> overridePath)
    {
        _http = http;
        _overridePath = overridePath;
        ToolsDirectory = Path.Combine(dataRoot, "tools");
    }

    /// <summary>Where the app-installed copy lives.</summary>
    public string InstalledPath => Path.Combine(ToolsDirectory, YtDlpParsing.ReleaseAssetName());

    /// <summary>Absolute path of a usable yt-dlp, or null.</summary>
    public string? Resolve()
    {
        var configured = SafeTrim(_overridePath);
        if (configured.Length > 0 && File.Exists(configured)) return configured;
        if (File.Exists(InstalledPath)) return InstalledPath;
        return FindOnPath(OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
    }

    public bool IsAvailable => Resolve() != null;

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
        Directory.CreateDirectory(ToolsDirectory);
        var url = YtDlpParsing.ReleaseDownloadUrl();
        var temp = InstalledPath + ".part";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Min(0.99, done / (double)total));
            }
        }
        File.Move(temp, InstalledPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(InstalledPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
            catch { }
        }
        progress?.Report(1);
        DebugLogger.Info(DebugLogger.Category.State, "YtDlp.Installed", InstalledPath);
        return InstalledPath;
    }

    /// <summary>"2026.08.12"-style version, or null when the tool cannot run.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        var exe = Resolve();
        if (exe is null) return null;
        try
        {
            var (code, stdout, _) = await RunAsync(exe, YtDlpParsing.VersionArgs(), null, ct).ConfigureAwait(false);
            var line = stdout.Split('\n').FirstOrDefault()?.Trim();
            return code == 0 && !string.IsNullOrEmpty(line) ? line : null;
        }
        catch { return null; }
    }

    public async Task<List<YouTubeTrackInfo>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var exe = Resolve() ?? throw new InvalidOperationException("yt-dlp is not installed.");
        var (code, stdout, stderr) = await RunAsync(exe, YtDlpParsing.SearchArgs(query, limit), null, ct).ConfigureAwait(false);
        var list = YtDlpParsing.ParseSearch(stdout);
        if (list.Count == 0 && code != 0) throw new InvalidOperationException(Tail(stderr) ?? "Search failed.");
        return list;
    }

    public async Task<YouTubeTrackInfo?> GetInfoAsync(string url, CancellationToken ct)
    {
        var exe = Resolve() ?? throw new InvalidOperationException("yt-dlp is not installed.");
        var (code, stdout, stderr) = await RunAsync(exe, YtDlpParsing.InfoArgs(url), null, ct).ConfigureAwait(false);
        var info = YtDlpParsing.ParseInfo(stdout.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('{')) ?? string.Empty);
        if (info is null && code != 0) throw new InvalidOperationException(Tail(stderr) ?? "Could not read this video.");
        return info;
    }

    /// <summary>
    /// Downloads the best audio for <paramref name="url"/> into a fresh temp folder under
    /// <paramref name="targetDir"/> and returns the one file it produced.
    /// </summary>
    public async Task<string> DownloadAsync(string url, string targetDir, string? ffmpegPath, IProgress<double>? progress, CancellationToken ct)
    {
        var exe = Resolve() ?? throw new InvalidOperationException("yt-dlp is not installed.");
        var scratch = Path.Combine(targetDir, ".noctis-download-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var template = Path.Combine(scratch, "%(id)s.%(ext)s");
            var ffmpegDir = ffmpegPath is null ? null : Path.GetDirectoryName(ffmpegPath);
            var (code, _, stderr) = await RunAsync(exe, YtDlpParsing.DownloadArgs(url, template, ffmpegDir), line =>
            {
                if (YtDlpParsing.ParseProgressPercent(line) is { } pct) progress?.Report(pct / 100.0);
            }, ct).ConfigureAwait(false);

            var produced = Directory.EnumerateFiles(scratch)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
            if (produced is null || code != 0 && new FileInfo(produced).Length == 0)
                throw new InvalidOperationException(Tail(stderr) ?? "Download produced no file.");
            progress?.Report(1);
            return produced;
        }
        catch
        {
            try { Directory.Delete(scratch, true); } catch { }
            throw;
        }
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string exe, IReadOnlyList<string> args, Action<string>? onStdoutLine, CancellationToken ct)
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

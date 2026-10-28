using Noctis.PluginCheck;

// noctis-plugin-check check --form <issue body.md> [--zip <file>] [--index plugins/index.json]
//                           [--report <out.md>] [--offline] [--apply --issue <n> --pr-title <out> --pr-body <out>]
//
// Without --zip the release zip named in the form is downloaded (GitHub only, size-capped).
// --offline skips the GitHub API reads. --apply writes the entry into --index when nothing
// blocks it (exit 2 otherwise). A plain check exits 0 whatever it finds: the report says it.
// GITHUB_TOKEN, when set, is sent to api.github.com only (rate limits).

var opts = ParseArgs(args);
if (args.Length == 0 || args[0] != "check" || !opts.TryGetValue("form", out var formPath))
{
    Console.Error.WriteLine("usage: check --form <body.md> [--zip <file>] [--index <index.json>] [--report <out.md>] [--offline] [--apply --issue <n> --pr-title <out> --pr-body <out>]");
    return 64;
}

var form = SubmissionForm.Parse(File.ReadAllText(formPath));
var offline = opts.ContainsKey("offline");
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
using var http = GitHubFetch.CreateClient();

string? zip = opts.GetValueOrDefault("zip");
string? downloadError = null;
string? tempDir = null;
if (zip is null)
{
    tempDir = Path.Combine(Path.GetTempPath(), "noctis-plugin-check-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    var target = Path.Combine(tempDir, "submission.zip");
    downloadError = offline ? "Offline run without --zip: nothing to check." : await GitHubFetch.DownloadZipAsync(http, form[SubmissionForm.Download], target);
    zip = downloadError is null ? target : null;
}

try
{
    var remote = offline ? null : await GitHubFetch.FetchAsync(http, form, token);
    var indexPath = opts.GetValueOrDefault("index");
    var indexJson = indexPath is not null && File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;

    var result = SubmissionCheck.Run(form, zip, downloadError, indexJson, remote);
    var report = SubmissionReport.Render(form, result);
    if (opts.GetValueOrDefault("report") is { } reportPath) File.WriteAllText(reportPath, report);
    else Console.WriteLine(report);
    Console.Error.WriteLine($"problems: {result.Count(CheckLevel.Problem)}, red flags: {result.Count(CheckLevel.RedFlag)}, listable: {result.CanBeListed}");

    if (!opts.ContainsKey("apply")) return 0;
    if (!result.CanBeListed || indexPath is null || indexJson is null || !int.TryParse(opts.GetValueOrDefault("issue"), out var issue))
    {
        Console.Error.WriteLine("Not applied: the check found problems, or --index/--issue is missing.");
        return 2;
    }
    File.WriteAllText(indexPath, PluginIndexFile.Upsert(indexJson, result.Entry!, out var replaced));
    Console.Error.WriteLine(replaced ? "Updated the existing entry." : "Added a new entry.");
    if (opts.GetValueOrDefault("pr-title") is { } titlePath) File.WriteAllText(titlePath, SubmissionReport.PullRequestTitle(result));
    if (opts.GetValueOrDefault("pr-body") is { } bodyPath) File.WriteAllText(bodyPath, SubmissionReport.PullRequestBody(result, issue));
    return 0;
}
finally
{
    if (tempDir is not null)
    {
        try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = a[i][2..];
        if (i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal)) d[key] = a[++i];
        else d[key] = "";
    }
    return d;
}

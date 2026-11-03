using System.Text;

namespace Noctis.PluginCheck;

/// <summary>The issue comment for a check: verdict, findings, what the code uses, the
/// plugins/index.json entry, and the reviewer's manual checklist.</summary>
public static class SubmissionReport
{
    /// <summary>First line of every report: the workflow finds its own comment by it and edits it.</summary>
    public const string Marker = "<!-- noctis-plugin-check -->";

    public static string Render(SubmissionForm form, SubmissionResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Marker).Append('\n');
        var title = r.Manifest is { } m ? $"{m.Name} {m.Version}" : form[SubmissionForm.Name];
        sb.Append("## Plugin check: ").Append(SubmissionCheck.Code(title)).Append("\n\n");

        var problems = r.Count(CheckLevel.Problem);
        var flags = r.Count(CheckLevel.RedFlag);
        sb.Append(problems > 0
            ? $"**Needs changes:** {problems} problem(s) to fix" + (flags > 0 ? $", {flags} red flag(s) to explain" : "") +
              ". Edit the issue (or publish a new release and update the form) and this check runs again.\n\n"
            : flags > 0
                ? $"**Ready for review, with {flags} red flag(s).** A reviewer will look at each one; explaining them in a comment speeds that up.\n\n"
                : "**Ready for review.** The automatic checks passed; a reviewer still reads the source before it's listed.\n\n");

        foreach (var level in new[] { CheckLevel.Problem, CheckLevel.RedFlag, CheckLevel.Note, CheckLevel.Pass })
        {
            var items = r.Items.Where(i => i.Level == level).ToList();
            if (items.Count == 0) continue;
            sb.Append("### ").Append(level switch
            {
                CheckLevel.Problem => "Problems",
                CheckLevel.RedFlag => "Red flags",
                CheckLevel.Note => "Notes",
                _ => "Passed",
            }).Append('\n');
            foreach (var i in items)
                sb.Append(level == CheckLevel.Pass ? "- [x] " : "- [ ] ").Append(i.Text).Append('\n');
            sb.Append('\n');
        }

        if (r.Scan is { Assemblies.Count: > 0 } scan)
        {
            sb.Append("### What the code references\n");
            sb.Append("Read from the assemblies' metadata; the plugin was not run.\n\n");
            sb.Append("| Assembly | Uses |\n|---|---|\n");
            foreach (var a in scan.Assemblies)
                sb.Append("| ").Append(SubmissionCheck.Code(a.Path)).Append(" | ")
                  .Append(a.Uses.Count == 0 ? "nothing notable" : string.Join(", ", a.Uses.Order().Select(Describe)))
                  .Append(" |\n");
            sb.Append('\n');
        }

        if (r.Entry is not null)
        {
            sb.Append("### Entry for `plugins/index.json`\n");
            sb.Append(r.CanBeListed
                ? "Added by the approval pull request when a maintainer applies the `approved` label.\n\n"
                : "Not ready yet: fix the problems above first.\n\n");
            // In the file's own layout, ready to paste. A backtick can only sit inside a JSON string,
            // where ` means the same: with none left, nothing in the entry can close the fence.
            var json = PluginIndexFile.FormatEntry(r.Entry).Replace("`", "\\u0060");
            sb.Append("```json\n").Append(json).Append("\n```\n\n");
        }

        sb.Append("### Reviewer checklist\n");
        sb.Append("- [ ] The source repository builds the released zip (same files; for .NET, a local Release build of the tag gives the same DLL names and sizes).\n");
        sb.Append("- [ ] What it does with the internet and files matches the form's answers and plugin.json's permissions.\n");
        sb.Append("- [ ] No telemetry, ads, miners, credential or token collection, and no downloading of copyrighted music or DRM workarounds.\n");
        sb.Append("- [ ] Every red flag above has a good reason.\n");
        sb.Append("- [ ] The licence lets people use it, and the name and description are plain and accurate.\n");
        sb.Append("- [ ] Installed with **Install from file** in a dev build: it loads, does what it says, and turns off cleanly.\n\n");
        sb.Append("When all of that holds, apply the `approved` label. A pull request with the entry above follows; merging it makes the plugin show up in Settings → Plugins.\n");
        return sb.ToString();
    }

    /// <summary>The pull request's title.</summary>
    public static string PullRequestTitle(SubmissionResult r)
        => $"plugins: list {SubmissionCheck.Text((string?)r.Entry?["name"])} {SubmissionCheck.Text((string?)r.Entry?["version"])}";

    /// <summary>The pull request's body: what is added, credited to whom, and the issue it closes.</summary>
    public static string PullRequestBody(SubmissionResult r, int issue)
    {
        var e = r.Entry!;
        var sb = new StringBuilder();
        sb.Append("Adds ").Append(SubmissionCheck.Code((string?)e["name"])).Append(' ').Append(SubmissionCheck.Code((string?)e["version"]))
          .Append(" by ").Append(SubmissionCheck.Code((string?)e["author"])).Append(" to `plugins/index.json`.\n\n");
        sb.Append("- Source: ").Append(SubmissionCheck.Code((string?)e["homepage"])).Append('\n');
        sb.Append("- Download: ").Append(SubmissionCheck.Code((string?)e["download"])).Append('\n');
        sb.Append("- SHA-256: ").Append(SubmissionCheck.Code((string?)e["sha256"])).Append(", ").Append((long?)e["size"]).Append(" bytes (checked again when this PR was made)\n\n");
        sb.Append("Reviewed in #").Append(issue).Append(". Merging makes it live in Settings → Plugins (GitHub's cache can take about 5 minutes).\n\n");
        sb.Append("Closes #").Append(issue).Append('\n');
        return sb.ToString();
    }

    private static string Describe(ApiUse use) => use switch
    {
        ApiUse.Network => "network",
        ApiUse.Process => "**starts programs**",
        ApiUse.DynamicCode => "**loads code at run time**",
        ApiUse.NativeInterop => "**native calls**",
        ApiUse.Registry => "**registry**",
        _ => "files",
    };
}

using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>Which group a release note bullet belongs to (Settings › About › What's new).</summary>
public enum ReleaseNoteKind { Added, Improved, Fixed, Other }

/// <summary>
/// One change from the release notes, as plain text. <see cref="Title"/> and
/// <see cref="Detail"/> are set only when the bullet reads "Title: detail" or "**Title** detail";
/// otherwise <see cref="Title"/> is the whole line and <see cref="Detail"/> is null.
/// <see cref="Issues"/> are the "(#123)" references taken off its end.
/// </summary>
public sealed record ReleaseNoteItem(string Title, string? Detail, IReadOnlyList<int> Issues);

public sealed record ReleaseNoteSection(ReleaseNoteKind Kind, IReadOnlyList<ReleaseNoteItem> Items);

/// <summary>
/// Reads a GitHub release body (the small markdown subset the release notes use) into
/// Added / Improved / Fixed / Other groups. Pure: no I/O, no UI.
/// <list type="bullet">
/// <item>"##"/"###" headings or a whole-line bold "**Added**" start a section; Added, Improved and
/// Fixed (also New / Features, Changed / Changes / Improvements, Fixes / Bug fixes) are known,
/// any other heading ("What's Changed", "Thanks", "Download") collects into Other.</item>
/// <item>"-", "*" and "+" bullets are the changes; everything else (the download table, the
/// macOS note, code blocks, "**Full Changelog**", paragraphs) is left out.</item>
/// <item>Emphasis, inline code and links become plain text (a link keeps its text, an image
/// goes); GitHub's generated "by @name in #12" tails go (the number stays as a reference) and
/// "made their first contribution" lines are dropped.</item>
/// </list>
/// </summary>
public static class ReleaseNotesParser
{
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(?<t>.*?)(\s+#+)?\s*$", RegexOptions.Compiled);
    private static readonly Regex BoldLine = new(@"^\s*(\*\*|__)(?<t>[^*_]+?)(\*\*|__)\s*:?\s*$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(?<indent>\s*)[-*+]\s+(?<t>.+)$", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^\s*(```|~~~)", RegexOptions.Compiled);

    private static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[(?<t>[^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Html = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"(\*\*|__)(?<t>.+?)\1", RegexOptions.Compiled);
    private static readonly Regex StarItalic = new(@"(?<![\w*])\*(?<t>[^*\s][^*]*?)\*(?![\w*])", RegexOptions.Compiled);
    private static readonly Regex UnderscoreItalic = new(@"(?<![\w_])_(?<t>[^_\s][^_]*?)_(?![\w_])", RegexOptions.Compiled);
    private static readonly Regex Code = new(@"`(?<t>[^`]*)`", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuation = new(@"\s+(?=[.,;:!?)](\s|$))", RegexOptions.Compiled);

    /// <summary>"**Title** detail" / "**Title:** detail" / "**Title** — detail", before cleaning.</summary>
    private static readonly Regex BoldTitle = new(@"^(\*\*|__)(?<title>.+?)\1\s*[:—–-]?\s*(?<detail>\S.*)$", RegexOptions.Compiled);

    /// <summary>GitHub's generated "by @name in https://…/pull/12" or "by @name in #12".</summary>
    private static readonly Regex ContributorTail = new(
        @"\s+by\s+@[\w-]+(\s+in\s+(https?://\S*/(pull|issues)/(?<n>\d+)|#(?<n>\d+)))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IssueTail = new(@"\s*\((?<refs>#\d+(\s*,\s*#\d+)*)\)\s*\.?\s*$", RegexOptions.Compiled);
    private static readonly Regex IssueNumber = new(@"#(?<n>\d+)", RegexOptions.Compiled);

    private static readonly ReleaseNoteKind[] Order =
        { ReleaseNoteKind.Added, ReleaseNoteKind.Improved, ReleaseNoteKind.Fixed, ReleaseNoteKind.Other };

    /// <summary>The groups that have changes, in the order Added, Improved, Fixed, Other.</summary>
    public static IReadOnlyList<ReleaseNoteSection> Parse(string? body)
    {
        var groups = Order.ToDictionary(k => k, _ => new List<ReleaseNoteItem>());
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<ReleaseNoteSection>();

        var current = ReleaseNoteKind.Other;
        var inFence = false;
        // The raw text of the bullet being read (continuation lines join it) and its group.
        string? pending = null;
        var pendingKind = ReleaseNoteKind.Other;

        void Flush()
        {
            if (pending is not null && ToItem(pending) is { } item) groups[pendingKind].Add(item);
            pending = null;
        }

        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (Fence.IsMatch(raw))
            {
                Flush();
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            if (string.IsNullOrWhiteSpace(raw))
            {
                Flush();
                continue;
            }

            var heading = Heading.Match(raw);
            var bold = heading.Success ? Match.Empty : BoldLine.Match(raw);
            if (heading.Success || bold.Success)
            {
                Flush();
                current = Classify((heading.Success ? heading : bold).Groups["t"].Value);
                continue;
            }

            var bullet = Bullet.Match(raw);
            if (bullet.Success)
            {
                Flush();
                pending = bullet.Groups["t"].Value;
                pendingKind = current;
                continue;
            }

            // An indented line under a bullet continues it; any other text ends it.
            if (pending is not null && char.IsWhiteSpace(raw[0]))
                pending += " " + raw.Trim();
            else
                Flush();
        }
        Flush();

        return Order.Where(k => groups[k].Count > 0)
            .Select(k => new ReleaseNoteSection(k, groups[k]))
            .ToList();
    }

    /// <summary>The group a heading names, or Other for headings that aren't a change type.</summary>
    internal static ReleaseNoteKind Classify(string heading)
    {
        var name = Spaces.Replace(Clean(heading), " ").Trim().TrimEnd(':').Trim().ToLowerInvariant();
        return name switch
        {
            "added" or "new" or "what's new" or "new features" or "features" => ReleaseNoteKind.Added,
            "improved" or "improvements" or "changed" or "changes" or "enhancements" => ReleaseNoteKind.Improved,
            "fixed" or "fixes" or "bug fixes" or "bugfixes" or "bug fix" => ReleaseNoteKind.Fixed,
            _ => ReleaseNoteKind.Other,
        };
    }

    /// <summary>One bullet's raw markdown as an item, or null for boilerplate / empty lines.</summary>
    internal static ReleaseNoteItem? ToItem(string markdown)
    {
        var text = markdown.Trim();
        if (text.Length == 0) return null;
        if (text.Contains("made their first contribution", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.StartsWith("**Full Changelog**", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase)) return null;

        var issues = new List<int>();

        string? title = null, detail = null;
        var boldTitle = BoldTitle.Match(text);
        if (boldTitle.Success)
        {
            title = Clean(boldTitle.Groups["title"].Value).TrimEnd(':').Trim();
            detail = TakeTails(Clean(boldTitle.Groups["detail"].Value), issues);
            if (title.Length == 0) title = null;
        }

        if (title is null)
        {
            var plain = TakeTails(Clean(text), issues);
            if (plain.Length == 0) return null;
            (title, detail) = SplitColon(plain);
        }
        else if (string.IsNullOrEmpty(detail))
        {
            detail = null;
        }

        return new ReleaseNoteItem(title, detail, issues);
    }

    /// <summary>"Title: detail" when the part before the colon reads like a short title.</summary>
    private static (string Title, string? Detail) SplitColon(string text)
    {
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 2 || colon > 60) return (text, null);
        var head = text[..colon].Trim();
        var tail = text[(colon + 1)..].Trim();
        if (tail.Length == 0 || head.Contains('.') || head.Split(' ').Length > 8) return (text, null);
        return (head, tail);
    }

    /// <summary>Takes the contributor tail and the "(#12)" references off the end.</summary>
    private static string TakeTails(string text, List<int> issues)
    {
        var contributor = ContributorTail.Match(text);
        if (contributor.Success)
        {
            text = text[..contributor.Index].TrimEnd();
            foreach (Capture n in contributor.Groups["n"].Captures)
                AddIssue(issues, n.Value);
        }

        var refs = IssueTail.Match(text);
        if (refs.Success)
        {
            text = text[..refs.Index].TrimEnd();
            // Inserted ahead of a contributor's PR number: the note's own issue reads first.
            var at = 0;
            foreach (Match n in IssueNumber.Matches(refs.Groups["refs"].Value))
                if (int.TryParse(n.Groups["n"].Value, out var id) && !issues.Contains(id))
                    issues.Insert(at++, id);
        }
        return text;
    }

    private static void AddIssue(List<int> issues, string value)
    {
        if (int.TryParse(value, out var id) && !issues.Contains(id)) issues.Add(id);
    }

    /// <summary>Markdown emphasis, links, code and tags to plain text, spaces collapsed.</summary>
    internal static string Clean(string markdown)
    {
        var s = Image.Replace(markdown, "");
        s = Link.Replace(s, m => m.Groups["t"].Value);
        s = Html.Replace(s, " ");
        s = Code.Replace(s, m => m.Groups["t"].Value);
        s = Bold.Replace(s, m => m.Groups["t"].Value);
        s = StarItalic.Replace(s, m => m.Groups["t"].Value);
        s = UnderscoreItalic.Replace(s, m => m.Groups["t"].Value);
        // A removed image or tag can leave "the docs ." behind.
        return SpaceBeforePunctuation.Replace(Spaces.Replace(s, " "), "").Trim();
    }
}

namespace Noctis.PluginCheck;

/// <summary>One field of .github/ISSUE_TEMPLATE/3-plugin-submission.yml: its id and its label,
/// which is the "### Label" heading GitHub writes into the issue body.</summary>
public sealed record FormField(string Id, string Label);

/// <summary>
/// The answers of a "Plugin submission" issue, read from the issue body. GitHub renders an issue
/// form as one "### Label" heading per field followed by the answer; an empty optional field
/// reads "_No response_", a dropdown its option text, a multi-select its options joined by ", ",
/// and checkboxes "- [X] label" lines. The body is untrusted text: it is only ever read here,
/// never passed to a shell.
/// </summary>
public sealed class SubmissionForm
{
    public static readonly FormField Name = new("plugin-name", "Plugin name");
    public static readonly FormField Id = new("plugin-id", "Plugin id");
    public static readonly FormField Version = new("version", "Version");
    public static readonly FormField Description = new("description", "Short description");
    public static readonly FormField WhatItDoes = new("what-it-does", "What it does");
    public static readonly FormField HowToTry = new("how-to-try", "How to try it");
    public static readonly FormField Screenshots = new("screenshots", "Screenshots");
    public static readonly FormField Author = new("author", "Author credit");
    public static readonly FormField AuthorUrl = new("author-url", "Author link");
    public static readonly FormField Contact = new("contact", "How to reach you");
    public static readonly FormField Source = new("source-repo", "Source code");
    public static readonly FormField License = new("license", "Licence");
    public static readonly FormField Download = new("download", "Release zip");
    public static readonly FormField Sha256 = new("sha256", "SHA-256");
    public static readonly FormField Type = new("type", "Type");
    public static readonly FormField Platforms = new("platforms", "Platforms");
    public static readonly FormField MinAppVersion = new("min-app-version", "Minimum Noctis version");
    public static readonly FormField Network = new("network", "Internet access");
    public static readonly FormField NetworkDetails = new("network-details", "What it connects to");
    public static readonly FormField Files = new("files", "Files outside its folder");
    public static readonly FormField FilesDetails = new("files-details", "Which files and why");
    public static readonly FormField Notes = new("notes", "Anything else");
    public static readonly FormField Agreements = new("agreements", "Before you submit");

    public static IReadOnlyList<FormField> All { get; } = new[]
    {
        Name, Id, Version, Description, WhatItDoes, HowToTry, Screenshots, Author, AuthorUrl, Contact, Source, License,
        Download, Sha256, Type, Platforms, MinAppVersion, Network, NetworkDetails, Files, FilesDetails, Notes, Agreements,
    };

    /// <summary>Answered fields, every one but <see cref="Agreements"/>.</summary>
    public static IReadOnlyList<FormField> Required { get; } = new[]
    {
        Name, Id, Version, Description, WhatItDoes, HowToTry, Author, Source, License, Download, Sha256, Type, Platforms, MinAppVersion, Network, Files,
    };

    /// <summary>The three boxes of <see cref="Agreements"/>, all required.</summary>
    public static IReadOnlyList<string> AgreementLabels { get; } = new[]
    {
        "I wrote this plugin, or I have the right to submit it.",
        "It has no telemetry, ads or malware.",
        "It doesn't download copyrighted music or get around DRM.",
    };

    /// <summary>Dropdown answers that mean "yes" for <see cref="Network"/> and <see cref="Files"/>.</summary>
    public const string YesAnswerPrefix = "Yes";

    /// <summary>Longest answer kept per field; a form answer is never legitimately longer.</summary>
    internal const int MaxAnswerLength = 4000;

    private readonly Dictionary<string, string> _answers;
    private readonly HashSet<string> _ticked;

    private SubmissionForm(Dictionary<string, string> answers, HashSet<string> ticked)
    {
        _answers = answers;
        _ticked = ticked;
    }

    /// <summary>The answer to <paramref name="field"/>, trimmed; "" when missing or "_No response_".</summary>
    public string this[FormField field] => _answers.TryGetValue(field.Id, out var v) ? v : "";

    /// <summary>True when the agreement box with this label is ticked.</summary>
    public bool IsTicked(string agreementLabel) => _ticked.Contains(agreementLabel);

    public bool IsYes(FormField field) => this[field].StartsWith(YesAnswerPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"dotnet" or "content" from the Type answer ("Code plugin (dotnet)"), or "" when unknown.</summary>
    public string TypeValue
    {
        get
        {
            var t = this[Type].ToLowerInvariant();
            return t.Contains("(content)") || t == "content" ? "content"
                : t.Contains("(dotnet)") || t == "dotnet" ? "dotnet" : "";
        }
    }

    /// <summary>Lowercase platform names from the Platforms answer ("Windows, macOS" → windows, macos).</summary>
    public IReadOnlyList<string> PlatformValues
    {
        get
        {
            var list = new List<string>();
            foreach (var part in this[Platforms].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = part.ToLowerInvariant() switch { "windows" => "windows", "macos" => "macos", "linux" => "linux", _ => null };
                if (p is not null && !list.Contains(p)) list.Add(p);
            }
            return list;
        }
    }

    public static SubmissionForm Parse(string? body)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var ticked = new HashSet<string>(StringComparer.Ordinal);
        var lines = (body ?? "").ReplaceLineEndings("\n").Split('\n');

        FormField? current = null;
        var buffer = new List<string>();
        void Flush()
        {
            // First heading wins: a later "### Version" typed inside an answer can't replace the real one.
            if (current is null || answers.ContainsKey(current.Id)) return;
            var text = string.Join("\n", buffer).Trim();
            if (text == "_No response_") text = "";
            if (text.Length > MaxAnswerLength) text = text[..MaxAnswerLength];
            answers[current.Id] = text;
            if (current == Agreements)
                foreach (var l in buffer)
                {
                    var line = l.Trim();
                    if (line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase)) ticked.Add(line[6..].Trim());
                }
        }

        foreach (var raw in lines)
        {
            if (raw.StartsWith("### ", StringComparison.Ordinal))
            {
                var label = raw[4..].Trim();
                var field = All.FirstOrDefault(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase));
                if (field is not null)
                {
                    Flush();
                    current = field;
                    buffer.Clear();
                    continue;
                }
            }
            buffer.Add(raw);
        }
        Flush();
        return new SubmissionForm(answers, ticked);
    }
}

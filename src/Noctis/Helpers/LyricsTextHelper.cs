using System.Text.RegularExpressions;

namespace Noctis.Helpers;

public static class LyricsTextHelper
{
    private static readonly Regex TimestampRegex =
        new(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]\s*", RegexOptions.Compiled);

    public static bool ContainsTimestamps(string? text) =>
        !string.IsNullOrWhiteSpace(text) && TimestampRegex.IsMatch(text);

    public static string StripTimestamps(string? lrcContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent)) return string.Empty;

        var lines = lrcContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var plainLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) { plainLines.Add(""); continue; }

            var text = TimestampRegex.Replace(trimmed, "");

            if (text.StartsWith('[') && text.Contains(':')) continue;

            if (!string.IsNullOrWhiteSpace(text))
                plainLines.Add(text);
        }

        return string.Join(Environment.NewLine, plainLines).Trim();
    }
}

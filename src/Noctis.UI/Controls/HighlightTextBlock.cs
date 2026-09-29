using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Noctis.Controls;

public class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string> DisplayTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string>(nameof(DisplayText), string.Empty);

    public static readonly StyledProperty<string> HighlightTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string>(nameof(HighlightText), string.Empty);

    public static readonly StyledProperty<IBrush> HighlightForegroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush>(
            nameof(HighlightForeground),
            new SolidColorBrush(Color.Parse("#FFE066")));

    public static readonly StyledProperty<bool> IsExplicitProperty =
        AvaloniaProperty.Register<HighlightTextBlock, bool>(nameof(IsExplicit));

    public string DisplayText
    {
        get => GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    public string HighlightText
    {
        get => GetValue(HighlightTextProperty);
        set => SetValue(HighlightTextProperty, value);
    }

    public IBrush HighlightForeground
    {
        get => GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    public bool IsExplicit
    {
        get => GetValue(IsExplicitProperty);
        set => SetValue(IsExplicitProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DisplayTextProperty ||
            change.Property == HighlightTextProperty ||
            change.Property == HighlightForegroundProperty ||
            change.Property == IsExplicitProperty)
        {
            UpdateInlines();
        }
        else if ((change.Property == FontFamilyProperty ||
                  change.Property == ForegroundProperty ||
                  change.Property == FontWeightProperty) && Inlines is { Count: > 0 })
        {
            // Runs copy the font properties, so only a block that actually holds runs has to
            // rebuild them. On the plain-Text path these fire once each as styles land during
            // realization and used to rebuild the inlines three times per tile for nothing.
            UpdateInlines();
        }
    }

    private void UpdateInlines()
    {
        var text = DisplayText ?? string.Empty;
        var query = HighlightText;
        var needsInlines = IsExplicit ||
            (!string.IsNullOrWhiteSpace(query) && FindMatchRanges(text, query.Trim()).Count > 0);

        if (!needsInlines)
        {
            // Plain Text, no runs. Inline layout costs several times a plain run and every
            // grid tile / list row paid it with no query active: 3.4ms of a realized Artists
            // row of seven, a third of the hitch the wheel glide showed on each new row (09-12).
            if (Inlines is { Count: > 0 }) Inlines.Clear();
            if (Text != text) Text = text;
            return;
        }

        // Leaving the plain path: the fast path above wrote the title into Text, and a
        // TextBlock renders Text AND its Inlines when both are set — Favorites showed
        // "VolvíVolví" for every explicit single until a later relayout
        // (HighlightTextBlockExplicitTests pins it). Clear Text before the runs go in.
        if (!string.IsNullOrEmpty(Text)) Text = string.Empty;
        Inlines?.Clear();

        foreach (var segment in BuildSegments(text, query))
        {
            Inlines?.Add(new Run
            {
                Text = segment.Text,
                FontFamily = FontFamily,
                Foreground = segment.IsMatch ? HighlightForeground : Foreground,
                FontWeight = segment.IsMatch ? FontWeight.Bold : FontWeight
            });
        }

        if (IsExplicit)
            Inlines?.Add(CreateExplicitBadge());
    }

    private static InlineUIContainer CreateExplicitBadge()
    {
        var badgeText = new TextBlock
        {
            Text = "E",
            FontSize = 8,
            Opacity = 0.9
        };
        badgeText.Classes.Add("explicit-badge-text");
        badgeText.Classes.Add("compact");

        var badge = new Border
        {
            Margin = new Thickness(4, 0, 0, 0),
            Child = badgeText
        };
        badge.Classes.Add("explicit-badge");
        badge.Classes.Add("compact");
        ToolTip.SetTip(badge, "Explicit");

        return new InlineUIContainer
        {
            BaselineAlignment = BaselineAlignment.Center,
            Child = badge
        };
    }

    private static IEnumerable<(string Text, bool IsMatch)> BuildSegments(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
        {
            yield return (text, false);
            yield break;
        }

        var ranges = FindMatchRanges(text, query.Trim());
        if (ranges.Count == 0)
        {
            yield return (text, false);
            yield break;
        }

        var position = 0;
        foreach (var range in ranges)
        {
            if (range.Start > position)
                yield return (text.Substring(position, range.Start - position), false);

            yield return (text.Substring(range.Start, range.Length), true);
            position = range.Start + range.Length;
        }

        if (position < text.Length)
            yield return (text.Substring(position), false);
    }

    private static List<(int Start, int Length)> FindMatchRanges(string text, string query)
    {
        var candidates = new List<(int Start, int Length)>();
        AddMatches(candidates, text, query);

        // Term values without their search tag ("artist:madonna" highlights "madonna", GitHub #107).
        foreach (var term in Noctis.Helpers.SearchQuery.Parse(query).HighlightTerms)
        {
            if (term.Length >= 2)
                AddMatches(candidates, text, term);
        }

        return candidates
            .OrderBy(range => range.Start)
            .ThenByDescending(range => range.Length)
            .Aggregate(new List<(int Start, int Length)>(), (merged, range) =>
            {
                if (merged.Count == 0)
                {
                    merged.Add(range);
                    return merged;
                }

                var previous = merged[^1];
                var previousEnd = previous.Start + previous.Length;
                if (range.Start < previousEnd)
                {
                    if (range.Start + range.Length > previousEnd)
                        merged[^1] = (previous.Start, range.Start + range.Length - previous.Start);
                    return merged;
                }

                merged.Add(range);
                return merged;
            });
    }

    private static void AddMatches(List<(int Start, int Length)> ranges, string text, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return;

        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return;

            ranges.Add((index, needle.Length));
            start = index + needle.Length;
        }
    }
}

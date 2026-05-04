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
            change.Property == IsExplicitProperty ||
            change.Property == FontFamilyProperty ||
            change.Property == ForegroundProperty ||
            change.Property == FontWeightProperty)
        {
            UpdateInlines();
        }
    }

    private void UpdateInlines()
    {
        Inlines?.Clear();

        foreach (var segment in BuildSegments(DisplayText ?? string.Empty, HighlightText))
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
            FontSize = 9,
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

        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Noctis.Helpers;

/// <summary>
/// Settings search without a hand-maintained keyword list: every <c>Border.setting-card</c>
/// in every tab is indexed by the text of the TextBlocks it contains (card titles,
/// descriptions, row labels). A query hides non-matching cards by adding the
/// <see cref="HiddenClass"/> class — a style, not a local IsVisible value, so cards whose
/// visibility is bound to a toggle keep their binding intact.
/// </summary>
public sealed class SettingsSearchIndex
{
    public const string CardClass = "setting-card";
    public const string HiddenClass = "search-hidden";

    public sealed record Entry(string Text, string Tab, Border Card, Control Panel)
    {
        /// <summary>
        /// Visible for reasons other than the search itself: a card gated off by a
        /// binding (platform, Developer Mode, a parent toggle) must neither count as a
        /// hit nor be jumped to, or the badge points at something the user cannot see.
        /// </summary>
        public bool IsNaturallyVisible
        {
            get
            {
                for (ILogical? v = Card; v != null && !ReferenceEquals(v, Panel); v = v.LogicalParent)
                {
                    if (v is not Visual visual) continue;
                    if (visual.IsVisible) continue;
                    // Hidden by us, not by the page: still a real card.
                    if (ReferenceEquals(v, Card) && Card.Classes.Contains(HiddenClass)) continue;
                    return false;
                }
                return true;
            }
        }
    }

    private readonly List<Entry> _entries;

    private SettingsSearchIndex(List<Entry> entries) => _entries = entries;

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Walk each tab panel's logical tree for setting cards and their text.</summary>
    public static SettingsSearchIndex Build(IEnumerable<(string Tab, Control Panel)> tabPanels)
    {
        var entries = new List<Entry>();
        foreach (var (tab, panel) in tabPanels)
        {
            foreach (var card in panel.GetLogicalDescendants().OfType<Border>())
            {
                if (!card.Classes.Contains(CardClass)) continue;
                // The section name is part of the text so "audio" or "about" finds the
                // section's cards, the way a person would expect.
                var text = tab + ' ' + string.Join(' ',
                    card.GetLogicalDescendants().OfType<TextBlock>()
                        .Select(t => t.Text)
                        .Where(t => !string.IsNullOrWhiteSpace(t)));
                entries.Add(new Entry(text, tab, card, panel));
            }
        }
        return new SettingsSearchIndex(entries);
    }

    private static string[] Tokens(string query)
        => query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Matches(Entry e, string[] tokens)
        => tokens.All(t => e.Text.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cards matching every word of the query; empty query → nothing.</summary>
    public IReadOnlyList<Entry> Query(string query)
    {
        var tokens = Tokens(query);
        if (tokens.Length == 0) return Array.Empty<Entry>();
        return _entries.Where(e => e.IsNaturallyVisible && Matches(e, tokens)).ToList();
    }

    /// <summary>Hide non-matching cards; an empty query shows everything again.</summary>
    public void Apply(string query)
    {
        var tokens = Tokens(query);
        foreach (var e in _entries)
        {
            // Only cards the page shows get the class: it must mean "hidden by search"
            // and nothing else, or IsNaturallyVisible could not tell the two apart.
            var hide = tokens.Length > 0 && e.IsNaturallyVisible && !Matches(e, tokens);
            if (hide) e.Card.Classes.Add(HiddenClass);
            else e.Card.Classes.Remove(HiddenClass);
        }
    }

    public IReadOnlyDictionary<string, int> CountByTab(string query)
        => Query(query).GroupBy(e => e.Tab).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>First hit, preferring the tab the user is already on.</summary>
    public Entry? FirstMatch(string query, string preferTab)
    {
        var hits = Query(query);
        return hits.FirstOrDefault(e => e.Tab == preferTab) ?? hits.FirstOrDefault();
    }
}

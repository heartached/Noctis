using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>One line of a page's sort menu: its label, the key the page sorts by, and whether it is the one in use.</summary>
public sealed partial class SortChoice : ObservableObject
{
    /// <param name="label">A Strings.resx key ("Mobile.Sort.Title"), read again each time the
    /// menu opens so it follows a language switch; text that is no key shows as given.</param>
    public SortChoice(string label, object key)
    {
        _label = label;
        Key = key;
    }

    private readonly string _label;
    public string Label => Loc.T(_label);
    public object Key { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>A page with a sort menu (Search, a song list); the view builds the menu from this.</summary>
public interface ISortable
{
    IReadOnlyList<SortChoice> SortChoices { get; }

    void ChooseSort(SortChoice choice);

    /// <summary>Whether the menu also offers Ascending / Descending.</summary>
    bool HasSortDirection { get; }

    bool IsDescending { get; }

    void ChooseDirection(bool descending);
}

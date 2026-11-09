using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// A short list of choices on the glass pop menu (PopMenu), Apple-style: a sort menu (the order in
/// use ticked, a group break, then Ascending / Descending), a filter menu, or a note with no
/// choices at all (an album's quality). Built from the page's state each time it opens, so the
/// ticks always match what the page shows. Each row closes the menu before it acts.
/// </summary>
public sealed class OptionsMenuViewModel
{
    public OptionsMenuViewModel(IReadOnlyList<OptionsMenuItem> items, string? title = null, string? detail = null, string? note = null)
    {
        Items = items;
        Title = title;
        Detail = detail;
        Note = note;
    }

    public IReadOnlyList<OptionsMenuItem> Items { get; }

    /// <summary>A heading over the rows (a note's title); none on a sort or filter menu.</summary>
    public string? Title { get; }
    public string? Detail { get; }
    public string? Note { get; }
    public bool HasTitle => !string.IsNullOrEmpty(Title);
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool HasItems => Items.Count > 0;

    /// <summary>A page's sort menu: its orders, the one in use ticked; then, when it has one, the
    /// direction after a group break.</summary>
    public static OptionsMenuViewModel ForSort(ShellViewModel shell, ISortable page)
    {
        var items = new List<OptionsMenuItem>();
        foreach (var choice in page.SortChoices)
            items.Add(new OptionsMenuItem(shell, choice.Label, choice.IsSelected, () => page.ChooseSort(choice)));
        if (page.HasSortDirection)
        {
            items.Add(new OptionsMenuItem(shell, Loc.T("Mobile.Sort.Ascending"), !page.IsDescending, () => page.ChooseDirection(false)) { StartsGroup = true });
            items.Add(new OptionsMenuItem(shell, Loc.T("Mobile.Sort.Descending"), page.IsDescending, () => page.ChooseDirection(true)));
        }
        return Lined(items);
    }

    /// <summary>A song list's filter menu: favourites, and (with an account) the desktop songs
    /// kept offline; Show All after a group break while one is on.</summary>
    public static OptionsMenuViewModel ForFilter(ShellViewModel shell, SongListPageViewModel list)
    {
        var items = new List<OptionsMenuItem>();
        if (list.OffersFavouritesFilter)
            items.Add(new OptionsMenuItem(shell, Loc.T("Nav.Favorites"), list.OnlyFavourites, () => list.OnlyFavourites = !list.OnlyFavourites));
        if (list.CanFilterDownloaded)
            items.Add(new OptionsMenuItem(shell, Loc.T("Mobile.Downloaded"), list.OnlyDownloaded, () => list.OnlyDownloaded = !list.OnlyDownloaded));
        if (list.IsFiltered)
            items.Add(new OptionsMenuItem(shell, Loc.T("Mobile.ShowAll"), false, () => { list.OnlyFavourites = false; list.OnlyDownloaded = false; }) { StartsGroup = true });
        return Lined(items);
    }

    /// <summary>
    /// Now Playing's Sleep Timer (the desktop player bar's): 15, 30, 45 minutes, 1 hour and End
    /// of Current Song, the one running ticked; Turn Off Timer after a group break while one is
    /// set. Headed "Sleep Timer", with what is left under it.
    /// </summary>
    public static OptionsMenuViewModel ForSleepTimer(ShellViewModel shell, NowPlayingViewModel player)
    {
        var items = new List<OptionsMenuItem>();
        foreach (var minutes in new[] { 15, 30, 45 })
            items.Add(new OptionsMenuItem(shell, Loc.T("Mobile.SleepTimer.Minutes", minutes), player.SleepTimerMinutes == minutes,
                () => player.StartSleepTimer(minutes)));
        items.Add(new OptionsMenuItem(shell, Loc.T("Mobile.SleepTimer.OneHour"), player.SleepTimerMinutes == 60, () => player.StartSleepTimer(60)));
        if (player.HasTrack)
            items.Add(new OptionsMenuItem(shell, Loc.T("Mobile.SleepTimer.EndOfSong"), player.StopAfterCurrentSong, player.StopAfterThisSong));
        if (player.HasSleepTimer)
            items.Add(new OptionsMenuItem(shell, Loc.T("Mobile.SleepTimer.TurnOff"), false, player.CancelSleepTimer) { StartsGroup = true });
        var lined = Lined(items);
        return new OptionsMenuViewModel(lined.Items, Loc.T("Mobile.SleepTimer"), player.HasSleepTimer ? player.SleepTimerStatus : null);
    }

    /// <summary>Any short list of choices (Settings' Theme menu): the items as given, with the usual hairlines.</summary>
    public static OptionsMenuViewModel ForItems(List<OptionsMenuItem> items) => Lined(items);

    /// <summary>A note with no choices: a heading, a detail line and a paragraph.</summary>
    public static OptionsMenuViewModel ForNote(string title, string? detail, string? note) =>
        new(Array.Empty<OptionsMenuItem>(), title, detail, note);

    // Apple's inset hairline over every row but the first of each group.
    private static OptionsMenuViewModel Lined(List<OptionsMenuItem> items)
    {
        for (var i = 1; i < items.Count; i++) items[i].HasLine = !items[i].StartsGroup;
        return new OptionsMenuViewModel(items);
    }
}

/// <summary>One row of an <see cref="OptionsMenuViewModel"/>: its label, whether it is ticked,
/// and what picking it does (after the menu closes).</summary>
public sealed partial class OptionsMenuItem
{
    private readonly ShellViewModel _shell;
    private readonly Action _pick;

    public OptionsMenuItem(ShellViewModel shell, string label, bool isChecked, Action pick)
    {
        _shell = shell;
        Label = label;
        IsChecked = isChecked;
        _pick = pick;
    }

    public string Label { get; }
    public bool IsChecked { get; }

    /// <summary>What a screen reader says: the label, and that it is the choice in use.</summary>
    public string AccessibleName => IsChecked ? Loc.T("Mobile.SelectedName", Label) : Label;

    /// <summary>The first row of a new group (Ascending, Show All): a band above it, no hairline.</summary>
    public bool StartsGroup { get; init; }

    /// <summary>The inset hairline over the row.</summary>
    public bool HasLine { get; internal set; }

    [RelayCommand]
    private void Pick()
    {
        _shell.CloseSheet();
        _pick();
    }
}

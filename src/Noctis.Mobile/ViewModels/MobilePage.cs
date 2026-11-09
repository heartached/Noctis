using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>The tabs of the phone UI, in the bar's order (Apple Music's: Home first, Search last).
/// Home is the start tab (Back from the others returns to it).</summary>
public enum MobileTab { Home, Favorites, Playlists, Library, Search }

/// <summary>How the page stack last changed: a page pushed (or presented), the top one popped,
/// or a jump straight to the root (a tab tap) that no page slides through.</summary>
public enum PageMove { Push, Pop, Reset }

/// <summary>Settings › Library › Show in Song Rows: what a song row's small mark shows
/// (Views/RowMark), the saved names in settings.json (MobileSongRowMark).</summary>
public enum SongRowMark { Off, Ratings, Favorites }

/// <summary>
/// A page pushed over a tab's root (Songs, an album, an artist, Settings…). ShellView shows
/// the top one through its PageHost with a DataTemplate per concrete type, so a page's view
/// is re-created when it comes back to the top; <see cref="ScrollOffset"/> is how it keeps
/// its place (see ScrollMemory).
/// </summary>
public abstract partial class MobilePage : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Shown as a tab's root under the tab's own title (Playlists, Favorites) rather
    /// than pushed: the view hides its own back/title header.</summary>
    public bool IsEmbedded { get; init; }

    /// <summary>Presented as a sheet (Settings): it rises over the tabs and the tab bar rather
    /// than sliding in over the tab, and the pages pushed after it open inside it. Set by
    /// <see cref="ShellViewModel.Present"/>.</summary>
    public bool IsSheet { get; internal set; }

    [ObservableProperty] private Vector _scrollOffset;

    /// <summary>The page this one was pushed over (null: the tab's root) and the tab it was
    /// pushed on; set by <see cref="ShellViewModel.Navigate"/>. The header's back label names it.</summary>
    internal MobilePage? PushedOver { get; set; }
    internal MobileTab PushedOnTab { get; set; }

    /// <summary>The header's "‹ Library" (PageHeader): the page Back returns to, by its title,
    /// in the current language (a language switch re-reads it through <see cref="Relabel"/>).</summary>
    public string BackTitle => PushedOver?.Title ?? TabTitle(PushedOnTab);

    /// <summary>A tab root's large title (the tab headers': Nav.Home, Tab.Library, …).</summary>
    public static string TabTitle(MobileTab tab) => Loc.T(tab switch
    {
        MobileTab.Favorites => "Nav.Favorites",
        MobileTab.Playlists => "Nav.Playlists",
        MobileTab.Library => "Tab.Library",
        MobileTab.Search => "Mobile.Search",
        _ => "Nav.Home",
    });

    /// <summary>The page left the stack for good (Back, a tab switch): drop
    /// event subscriptions so a closed page stops rebuilding on every library refresh.</summary>
    public virtual void OnClosed() { }

    /// <summary>Android Back, before the page itself is popped: a page with pages of its own
    /// (Settings' Music Folders, Appearance, Lyrics) returns to its top level and says so.</summary>
    public virtual bool TryGoBackWithin() => false;

    /// <summary>The page's view is kept, hidden, when the page closes, and handed the next page
    /// of the same kind (<see cref="Views.PageStack"/>): for a page whose view is costly to build
    /// and that is opened again and again (Settings).</summary>
    public virtual bool KeepsView => false;

    public override string ToString() => Title;

    /// <summary>The UI language changed: every binding on the page re-reads (titles, counts,
    /// row values built in code). Pages with child view models add theirs.</summary>
    public virtual void Relabel() => OnPropertyChanged(string.Empty);

    /// <summary>A page title given as a Strings.resx key ("Mobile.RecentlyAdded", "Nav.Songs") reads
    /// in the current language, so an open page follows a language switch; any other text (an
    /// artist's or playlist's name) shows as given.</summary>
    protected static string Localized(string titleOrKey) =>
        titleOrKey.StartsWith("Mobile.", StringComparison.Ordinal) || titleOrKey.StartsWith("Nav.", StringComparison.Ordinal)
            ? Loc.T(titleOrKey) : titleOrKey;
}

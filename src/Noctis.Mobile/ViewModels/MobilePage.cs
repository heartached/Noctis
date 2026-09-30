using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Mobile.ViewModels;

/// <summary>The three tabs of the phone UI. Library is the start tab (Back from the others returns to it).</summary>
public enum MobileTab { Home, Library, Search }

/// <summary>
/// A page pushed over a tab's root (Songs, an album, an artist, Settings…). ShellView shows
/// the top one through its PageHost with a DataTemplate per concrete type, so a page's view
/// is re-created when it comes back to the top; <see cref="ScrollOffset"/> is how it keeps
/// its place (see ScrollMemory).
/// </summary>
public abstract partial class MobilePage : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Shown inside the Library page under a filter chip rather than pushed:
    /// the view hides its own back/title header.</summary>
    public bool IsEmbedded { get; init; }

    [ObservableProperty] private Vector _scrollOffset;

    /// <summary>The page left the stack for good (Back, a tab switch, a chip change): drop
    /// event subscriptions so a closed page stops rebuilding on every library refresh.</summary>
    public virtual void OnClosed() { }

    public override string ToString() => Title;
}

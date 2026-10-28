using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Mobile.ViewModels;

/// <summary>Settings → Listening History (stub: the History agent replaces this).</summary>
public sealed partial class ListeningHistoryViewModel : ObservableObject
{
    public ListeningHistoryViewModel(ShellViewModel shell)
    {
        Shell = shell;
    }

    public ShellViewModel Shell { get; }

    /// <summary>The Settings row's value.</summary>
    public string Summary => "";

    /// <summary>Settings → Pick Up on Computer: the computer offers to carry on from the song playing here.</summary>
    [ObservableProperty] private bool _pickUpOnComputer;
}

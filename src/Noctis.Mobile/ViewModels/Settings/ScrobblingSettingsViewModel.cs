using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Mobile.ViewModels;

/// <summary>Settings → Scrobbling (stub: the Scrobbling agent replaces this).</summary>
public sealed partial class ScrobblingSettingsViewModel : ObservableObject
{
    public ScrobblingSettingsViewModel(ShellViewModel shell)
    {
        Shell = shell;
    }

    public ShellViewModel Shell { get; }

    /// <summary>The Settings row's value.</summary>
    public string Summary => "Off";
}

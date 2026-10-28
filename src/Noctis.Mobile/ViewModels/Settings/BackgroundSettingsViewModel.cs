using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Mobile.ViewModels;

/// <summary>Settings → Background (stub: the Background agent replaces this).</summary>
public sealed partial class BackgroundSettingsViewModel : ObservableObject
{
    public BackgroundSettingsViewModel(ShellViewModel shell)
    {
        Shell = shell;
    }

    public ShellViewModel Shell { get; }

    /// <summary>The Settings row's value: the background in use.</summary>
    public string Summary => "Ash";

    /// <summary>The dot beside <see cref="Summary"/>.</summary>
    public IBrush SummarySwatch { get; } = new ImmutableSolidColorBrush(Color.Parse("#5D4B46"));
}

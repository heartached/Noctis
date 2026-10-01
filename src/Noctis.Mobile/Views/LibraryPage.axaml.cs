using System.ComponentModel;
using Avalonia.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class LibraryPage : UserControl
{
    private ShellViewModel? _shell;

    public LibraryPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
            _shell = DataContext as ShellViewModel;
            if (_shell != null) _shell.PropertyChanged += OnShellChanged;
        };
    }

    /// <summary>A chip switch: the view it shows settles in rather than snapping.</summary>
    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.LibraryChip) || _shell == null) return;
        Appear.Play(_shell.IsAllMusicChip ? LibraryScroll : ChipHost);
    }
}

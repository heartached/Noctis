using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Noctis.Mobile.Views;

public partial class SongRow : UserControl
{
    /// <summary>What the row's ⋯ opens, given the song: the list's own song menu (a playlist's
    /// page adds Remove from Playlist), or the shell's.</summary>
    public static readonly StyledProperty<ICommand?> MenuCommandProperty =
        AvaloniaProperty.Register<SongRow, ICommand?>(nameof(MenuCommand));

    /// <summary>Whether the row's ⋯ shows (not in its list's Select mode).</summary>
    public static readonly StyledProperty<bool> IsMenuVisibleProperty =
        AvaloniaProperty.Register<SongRow, bool>(nameof(IsMenuVisible), true);

    public bool IsMenuVisible { get => GetValue(IsMenuVisibleProperty); set => SetValue(IsMenuVisibleProperty, value); }

    public SongRow()
    {
        InitializeComponent();
    }

    public ICommand? MenuCommand { get => GetValue(MenuCommandProperty); set => SetValue(MenuCommandProperty, value); }
}

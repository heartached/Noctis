using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class TopBarView : UserControl
{
    private static readonly IBrush ActiveToggleBg = new SolidColorBrush(Color.Parse("#30FFFFFF"));
    private static readonly IBrush InactiveToggleBg = Brushes.Transparent;

    public TopBarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChangedForToggle;
    }

    private void OnDataContextChangedForToggle(object? sender, EventArgs e)
    {
        if (DataContext is TopBarViewModel vm)
        {
            vm.PropertyChanged += OnTopBarPropertyChanged;
            UpdateAlbumsToggleVisuals(vm.IsAlbumsCoverFlowMode);
        }
    }

    private void OnTopBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TopBarViewModel.IsAlbumsCoverFlowMode) && sender is TopBarViewModel vm)
        {
            UpdateAlbumsToggleVisuals(vm.IsAlbumsCoverFlowMode);
        }
    }

    private void UpdateAlbumsToggleVisuals(bool isCoverFlow)
    {
        if (AlbumsLibraryModeBtn != null)
        {
            AlbumsLibraryModeBtn.Background = isCoverFlow ? InactiveToggleBg : ActiveToggleBg;
            AlbumsLibraryModeBtn.Opacity = isCoverFlow ? 0.5 : 1.0;
        }
        if (AlbumsUpNextModeBtn != null)
        {
            AlbumsUpNextModeBtn.Background = isCoverFlow ? ActiveToggleBg : InactiveToggleBg;
            AlbumsUpNextModeBtn.Opacity = isCoverFlow ? 1.0 : 0.5;
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Listen for pointer presses on the top-level window so we can
        // clear focus from the search box when the user clicks outside it.
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
        }
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox == null || !searchBox.IsFocused) return;

        // The pill Border is: TextBox → Grid → pill Border
        var pillBorder = (searchBox.Parent as Visual)?.GetVisualParent();

        if (e.Source is Visual source && pillBorder != null)
        {
            // Walk up from click source — if we hit the pill, the click is inside it
            Visual? v = source;
            while (v != null)
            {
                if (ReferenceEquals(v, pillBorder)) return;
                v = v.GetVisualParent();
            }
        }

        // Click was outside the pill — steal focus away from the TextBox.
        // TopBarView has Focusable="True" so this reliably clears TextBox focus.
        this.Focus(NavigationMethod.Pointer);
    }
}

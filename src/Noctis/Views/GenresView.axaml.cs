using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>Genres page (GitHub #220): a click or Enter on a row opens the genre.</summary>
public partial class GenresView : UserControl
{
    public GenresView()
    {
        InitializeComponent();
        GenreList.AddHandler(KeyDownEvent, OnGenreListKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        // The row's Play / Shuffle buttons run their own commands.
        for (var v = e.Source as Visual; v != null && !ReferenceEquals(v, sender); v = v.GetVisualParent())
            if (v is Button) return;
        if (sender is Control { DataContext: GenreRow row } && DataContext is GenresViewModel vm)
            vm.OpenGenreCommand.Execute(row);
    }

    private void OnGenreListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || GenreList.SelectedItem is not GenreRow row || DataContext is not GenresViewModel vm) return;
        vm.OpenGenreCommand.Execute(row);
        e.Handled = true;
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>GitHub #141: pick the right Deezer account for an artist (see <see cref="ArtistInfoSearchViewModel"/>).</summary>
public partial class ArtistInfoSearchDialog : Window
{
    /// <summary>Room kept above and below the card when the window is shorter than it.</summary>
    private const double CardWindowMargin = 48;

    public ArtistInfoSearchDialog()
    {
        InitializeComponent();
        // Fixed card height, capped to a short window (as Search Lyrics).
        SizeChanged += (_, e) => CardContent.MaxHeight = Math.Max(0, e.NewSize.Height - CardWindowMargin);
    }

    public ArtistInfoSearchDialog(ArtistInfoSearchViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns Close() into the animated close; the pick reaches the page
        // through the view model's apply callback before it raises Closed.
        vm.Closed += (_, _) => Close();
        Closed += (_, _) => vm.StopSearch();
        Opened += (_, _) => _ = vm.SearchCommand.ExecuteAsync(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ArtistInfoSearchViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}

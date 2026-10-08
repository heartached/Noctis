using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsSearchDialog : Window
{
    /// <summary>Room kept above and below the card when the window is shorter than it.</summary>
    private const double CardWindowMargin = 48;

    public LyricsSearchDialog()
    {
        InitializeComponent();
        // The card is a fixed 560 px so picking another source never resizes it; a short
        // window (the main window's MinHeight is 500) caps it instead of cutting it off.
        // MaxHeight wins over Height in layout, so the rows just get less room.
        SizeChanged += (_, e) => CardContent.MaxHeight = Math.Max(0, e.NewSize.Height - CardWindowMargin);
    }

    public LyricsSearchDialog(LyricsSearchViewModel vm) : this()
    {
        DataContext = vm;
        // Owner 10-08: Search Lyrics in the pill dialog. PillDialogHost turns this into the
        // animated close. Nothing is returned through Close(result) (the deferred close would
        // lose it): Use Lyrics hands the pick to the lyrics page through the view model's
        // apply callback before it raises Closed.
        vm.Closed += (_, _) => Close();
        // Alt+F4 closes the window without the view model's Close: stop the search anyway,
        // so it doesn't keep asking every source after the dialog is gone.
        Closed += (_, _) => vm.StopSearch();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape is Cancel (and cancels a running search).
        if (e.Key == Key.Escape && DataContext is LyricsSearchViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}

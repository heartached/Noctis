using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>"Music Video from YouTube" for one song (see <see cref="MusicVideoPickerViewModel"/>).</summary>
public partial class MusicVideoPickerDialog : Window
{
    /// <summary>Room kept above and below the card when the window is shorter than it.</summary>
    private const double CardWindowMargin = 48;

    public MusicVideoPickerDialog()
    {
        InitializeComponent();
        SizeChanged += (_, e) => CardContent.MaxHeight = Math.Max(0, e.NewSize.Height - CardWindowMargin);
    }

    public MusicVideoPickerDialog(MusicVideoPickerViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
        Closed += (_, _) => vm.Stop();
        // The search runs as the dialog opens: the list is the "Wrong video?" answer.
        Opened += (_, _) =>
        {
            _ = vm.SearchCommand.ExecuteAsync(null);
            LinkBox.Focus();
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MusicVideoPickerViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}

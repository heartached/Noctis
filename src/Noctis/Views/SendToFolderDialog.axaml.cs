using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SendToFolderDialog : Window
{
    // PillDialogHost's open curve (OpenEase: a long ease-out) and timings, so the list, the
    // errors and the progress bar come in the way the card itself does.
    private static readonly CubicBezierEase RevealEase = new(0.16, 1.0, 0.3, 1.0);
    private static readonly TimeSpan RevealFade = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RevealMove = TimeSpan.FromMilliseconds(260);

    public SendToFolderDialog()
    {
        InitializeComponent();
        AttachReveal(PlanList);
        AttachReveal(ErrorPanel);
        AttachReveal(CopyProgress);
    }

    public SendToFolderDialog(SendToFolderViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close; nothing is returned through
        // Close(result), so the deferred close loses nothing.
        vm.Closed += (_, _) => Close();
    }

    /// <summary>The stf-reveal styles set the poses; these carry the change between them.</summary>
    private static void AttachReveal(Control control)
    {
        control.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = RevealFade, Easing = RevealEase },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = RevealMove, Easing = RevealEase },
        };
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localization.Loc.T("SendTo.Title"),
                AllowMultiple = false,
            });
            // Cancelled picker keeps what was there; a picked folder without a local path (a
            // portal URI on Linux) can't be copied to.
            if (folders.Count > 0 && DataContext is SendToFolderViewModel { IsIdle: true } vm
                && folders[0].TryGetLocalPath() is { Length: > 0 } path)
                vm.Destination = path;
        }
        catch (Exception ex)
        {
            DebugLog.Write("SendToFolder", $"Folder pick failed: {ex.Message}");
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape is Cancel: it stops a running copy first, and closes when idle (the converter's).
        if (e.Key == Key.Escape && DataContext is SendToFolderViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Stops a running copy when the window closes by any route (Alt+F4, the owner
    /// going away), not only through Stop; the half-written file is removed.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        (DataContext as SendToFolderViewModel)?.CancelForClose();
        base.OnClosing(e);
    }
}

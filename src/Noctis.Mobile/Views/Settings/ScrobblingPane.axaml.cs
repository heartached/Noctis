using Avalonia.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>Settings → Scrobbling's content. The view's one job: open Last.fm's approval page in the
/// phone's browser when the view model asks for it.</summary>
public partial class ScrobblingPane : UserControl
{
    private ScrobblingSettingsViewModel? _vm;

    public ScrobblingPane()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.OpenUrlRequested -= OnOpenUrl;
        _vm = DataContext as ScrobblingSettingsViewModel;
        if (_vm != null) _vm.OpenUrlRequested += OnOpenUrl;
    }

    private void OnOpenUrl(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            _ = launcher.LaunchUriAsync(uri);
    }
}

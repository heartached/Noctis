using Avalonia.Controls;
using Avalonia.Input;

namespace Noctis.Views;

/// <summary>The metadata editor's Find online panel (see MetadataSearchPanelViewModel).
/// MetadataWindow shows/hides and animates it; this only handles focus and keys.</summary>
public partial class MetadataSearchPanel : UserControl
{
    public MetadataSearchPanel()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in the first query box, so typing refines the search at once
    /// and Enter re-runs it.</summary>
    public void FocusQuery()
    {
        var box = FirstQueryBox.IsEffectivelyVisible ? FirstQueryBox : FirstAlbumQueryBox;
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0;
    }

    /// <summary>Down from a query box moves into the results, so the whole panel is
    /// keyboard-drivable: type, Enter, arrows to compare, Tab to Apply.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.None && e.Source is TextBox
            && ResultsList.IsEffectivelyVisible && ResultsList.ItemCount > 0)
        {
            if (ResultsList.SelectedIndex < 0) ResultsList.SelectedIndex = 0;
            (ResultsList.ContainerFromIndex(ResultsList.SelectedIndex) as Control ?? ResultsList).Focus(NavigationMethod.Directional);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}

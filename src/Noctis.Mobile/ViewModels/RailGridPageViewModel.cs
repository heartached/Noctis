using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Helpers;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// A Library section's › page: every tile of a rail (Pinned, Recently Played) as a three-column
/// grid. Taps and long-presses go through the shell's rail commands, like the rail itself.
/// </summary>
public sealed partial class RailGridPageViewModel : MobilePage
{
    private readonly string _title;
    private readonly Func<IEnumerable<RailItem>> _source;

    public RailGridPageViewModel(ShellViewModel shell, string title, Func<IEnumerable<RailItem>> source)
    {
        Shell = shell;
        _title = title;
        _source = source;
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    public ShellViewModel Shell { get; }

    public override string Title => _title;

    public BulkObservableCollection<RailItem> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private int _itemCount;

    public bool HasItems => ItemCount > 0;

    public void Refresh()
    {
        Items.ReplaceAll(_source().ToList());
        ItemCount = Items.Count;
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}

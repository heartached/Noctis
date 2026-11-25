using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Helpers;

namespace Noctis.Mobile.ViewModels;

/// <summary>One line of a <see cref="RailGridPageViewModel"/>'s grid. Equal when it holds the same
/// tiles, so a refresh that changes nothing here leaves the grid alone.</summary>
public sealed record RailGridRow(IReadOnlyList<RailItem> Items)
{
    public bool Equals(RailGridRow? other) => other != null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Count == 0 ? 0 : Items[0].GetHashCode();
}

/// <summary>
/// A Library section's › page: every tile of a rail (Pinned, Recently Played) as a three-column
/// grid. Taps and long-presses go through the shell's rail commands, like the rail itself.
/// </summary>
public sealed partial class RailGridPageViewModel : MobilePage
{
    internal const int Columns = 3;

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

    public override string Title => Localized(_title);

    public BulkObservableCollection<RailItem> Items { get; } = new();

    /// <summary><see cref="Items"/> three to a row, what the grid shows: rows virtualise (the
    /// Albums page's rule). Over a plain three-column panel every tile was built at once, cover
    /// decode and arrival fade included: 400 tiles and ~1.9 s headless for a 400-album Downloaded
    /// page, or an artist's long release list.</summary>
    public BulkObservableCollection<RailGridRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private int _itemCount;

    public bool HasItems => ItemCount > 0;

    public void Refresh()
    {
        MobileLibrary.ReplaceIfChanged(Items, _source().ToList());
        MobileLibrary.ReplaceIfChanged(Rows, Items.Chunk(Columns).Select(chunk => new RailGridRow(chunk)).ToList());
        ItemCount = Items.Count;
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}

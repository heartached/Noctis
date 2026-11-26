using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Noctis.Controls;

/// <summary>
/// The Edit Info artwork chooser card: one design for the Artwork and the Animated Artwork
/// search pop-ups (owner 11-04: "make sure the pop up for animated artwork and artwork are the
/// same"). Header, a search box to refine and search again in place, the searching / nothing
/// found state, a grid of album tiles (<see cref="ViewModels.IArtworkChooserItem"/>: cover,
/// title, artist, detail, a pill per quality) and the checking-more / downloading / error
/// footers. Each tab binds its own view-model state to it.
/// </summary>
public partial class ArtworkChooserCard : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(Subtitle));

    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(Query), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The box's watermark: the query the tags made.</summary>
    public static readonly StyledProperty<string?> QueryHintProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(QueryHint));

    /// <summary>What the box accepts (its tooltip and accessible name).</summary>
    public static readonly StyledProperty<string?> SearchBoxHintProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(SearchBoxHint));

    public static readonly StyledProperty<ICommand?> SearchCommandProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, ICommand?>(nameof(SearchCommand));

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<bool> HasResultsProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, bool>(nameof(HasResults));

    public static readonly StyledProperty<bool> IsSearchingProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, bool>(nameof(IsSearching));

    public static readonly StyledProperty<string?> StatusProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(Status));

    /// <summary>Second line of the nothing-found state: why nothing is offered.</summary>
    public static readonly StyledProperty<string?> StatusDetailProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(StatusDetail));

    /// <summary>More albums are still being looked at while some tiles already show.</summary>
    public static readonly StyledProperty<bool> IsCheckingMoreProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, bool>(nameof(IsCheckingMore));

    public static readonly StyledProperty<bool> IsDownloadingProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, bool>(nameof(IsDownloading));

    /// <summary>GitHub #185: the running download's bar (percent, speed, time left, Cancel).</summary>
    public static readonly StyledProperty<ViewModels.ArtworkDownloadProgress?> DownloadProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, ViewModels.ArtworkDownloadProgress?>(nameof(Download));

    /// <summary>Run with the clicked quality pill's choice.</summary>
    public static readonly StyledProperty<ICommand?> PickCommandProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, ICommand?>(nameof(PickCommand));

    /// <summary>Run with the clicked tile when its poster previews (animated artwork).</summary>
    public static readonly StyledProperty<ICommand?> PreviewCommandProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, ICommand?>(nameof(PreviewCommand));

    /// <summary>Owner 11-26: the results in titled sections (<see cref="ViewModels.ArtworkResultSection"/>);
    /// while it holds any, they show instead of the flat <see cref="Items"/> grid.</summary>
    public static readonly StyledProperty<IEnumerable?> SectionsProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, IEnumerable?>(nameof(Sections));

    /// <summary>The artist whose artwork library is shown (the header); empty for none.</summary>
    public static readonly StyledProperty<string?> LibraryTitleProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(LibraryTitle));

    /// <summary>Under the header: "36 releases".</summary>
    public static readonly StyledProperty<string?> LibraryDetailProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, string?>(nameof(LibraryDetail));

    /// <summary>The wider, taller library card (five tiles a row) rather than the four-tile one.</summary>
    public static readonly StyledProperty<bool> IsWideProperty =
        AvaloniaProperty.Register<ArtworkChooserCard, bool>(nameof(IsWide));

    public static readonly DirectProperty<ArtworkChooserCard, bool> ShowsSectionsProperty =
        AvaloniaProperty.RegisterDirect<ArtworkChooserCard, bool>(nameof(ShowsSections), c => c.ShowsSections);

    public static readonly DirectProperty<ArtworkChooserCard, bool> ShowsFlatResultsProperty =
        AvaloniaProperty.RegisterDirect<ArtworkChooserCard, bool>(nameof(ShowsFlatResults), c => c.ShowsFlatResults);

    public static readonly DirectProperty<ArtworkChooserCard, bool> ShowsLibraryHeaderProperty =
        AvaloniaProperty.RegisterDirect<ArtworkChooserCard, bool>(nameof(ShowsLibraryHeader), c => c.ShowsLibraryHeader);

    private bool _showsSections;
    private bool _showsFlatResults;
    private bool _showsLibraryHeader;
    private System.Collections.Specialized.INotifyCollectionChanged? _watchedSections;

    public ArtworkChooserCard()
    {
        InitializeComponent();
    }

    public IEnumerable? Sections { get => GetValue(SectionsProperty); set => SetValue(SectionsProperty, value); }
    public string? LibraryTitle { get => GetValue(LibraryTitleProperty); set => SetValue(LibraryTitleProperty, value); }
    public string? LibraryDetail { get => GetValue(LibraryDetailProperty); set => SetValue(LibraryDetailProperty, value); }
    public bool IsWide { get => GetValue(IsWideProperty); set => SetValue(IsWideProperty, value); }

    /// <summary>Results showing, in sections.</summary>
    public bool ShowsSections { get => _showsSections; private set => SetAndRaise(ShowsSectionsProperty, ref _showsSections, value); }

    /// <summary>Results showing, as the flat grid (no sections given).</summary>
    public bool ShowsFlatResults { get => _showsFlatResults; private set => SetAndRaise(ShowsFlatResultsProperty, ref _showsFlatResults, value); }

    public bool ShowsLibraryHeader { get => _showsLibraryHeader; private set => SetAndRaise(ShowsLibraryHeaderProperty, ref _showsLibraryHeader, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SectionsProperty)
        {
            if (_watchedSections != null) _watchedSections.CollectionChanged -= OnSectionsChanged;
            _watchedSections = change.NewValue as System.Collections.Specialized.INotifyCollectionChanged;
            if (_watchedSections != null) _watchedSections.CollectionChanged += OnSectionsChanged;
            UpdateResultsLayout();
        }
        else if (change.Property == HasResultsProperty || change.Property == LibraryTitleProperty)
        {
            UpdateResultsLayout();
        }
    }

    private void OnSectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateResultsLayout();

    private void UpdateResultsLayout()
    {
        var sections = Sections?.GetEnumerator().MoveNext() == true;
        ShowsSections = HasResults && sections;
        ShowsFlatResults = HasResults && !sections;
        ShowsLibraryHeader = ShowsSections && !string.IsNullOrEmpty(LibraryTitle);
    }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? Query { get => GetValue(QueryProperty); set => SetValue(QueryProperty, value); }
    public string? QueryHint { get => GetValue(QueryHintProperty); set => SetValue(QueryHintProperty, value); }
    public string? SearchBoxHint { get => GetValue(SearchBoxHintProperty); set => SetValue(SearchBoxHintProperty, value); }
    public ICommand? SearchCommand { get => GetValue(SearchCommandProperty); set => SetValue(SearchCommandProperty, value); }
    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public bool HasResults { get => GetValue(HasResultsProperty); set => SetValue(HasResultsProperty, value); }
    public bool IsSearching { get => GetValue(IsSearchingProperty); set => SetValue(IsSearchingProperty, value); }
    public string? Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string? StatusDetail { get => GetValue(StatusDetailProperty); set => SetValue(StatusDetailProperty, value); }
    public bool IsCheckingMore { get => GetValue(IsCheckingMoreProperty); set => SetValue(IsCheckingMoreProperty, value); }
    public bool IsDownloading { get => GetValue(IsDownloadingProperty); set => SetValue(IsDownloadingProperty, value); }
    public ViewModels.ArtworkDownloadProgress? Download { get => GetValue(DownloadProperty); set => SetValue(DownloadProperty, value); }
    public ICommand? PickCommand { get => GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    public ICommand? PreviewCommand { get => GetValue(PreviewCommandProperty); set => SetValue(PreviewCommandProperty, value); }
}

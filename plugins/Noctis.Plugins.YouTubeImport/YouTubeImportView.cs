using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// The "Add from YouTube" window, built in code (a plugin ships no XAML). The layout of the
/// app's old YouTubeDownloadDialog: header with the destination, one search row, then setup /
/// skeleton / results / empty hint, and a footer with the status and the yt-dlp version; plus
/// the format picker, a folder you can change, and the recent downloads. It borrows Noctis's
/// pill styles (PillDialog.axaml) when they load, and looks plain but works when they do not.
/// </summary>
public sealed class YouTubeImportView : UserControl
{
    internal const string PillStylesUri = "avares://Noctis.UI/Assets/PillDialog.axaml";

    private static readonly IBrush Coral = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0xFF, 0x7A, 0x6B), 0), new GradientStop(Color.FromRgb(0xF0, 0x44, 0x4E), 1) },
    };
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
    private static readonly Geometry PlayGlyph = Geometry.Parse("M 8,5 L 19,12 L 8,19 Z");
    private static readonly Geometry SearchGlyph = Geometry.Parse("M 10.5,3 A 7.5,7.5 0 1 0 15.6,16.1 L 20.3,20.8 L 21.7,19.4 L 17,14.7 A 7.5,7.5 0 0 0 10.5,3 Z M 10.5,5 A 5.5,5.5 0 1 1 10.5,16 A 5.5,5.5 0 1 1 10.5,5 Z");
    private static readonly Geometry CheckGlyph = Geometry.Parse("M 4,12.5 L 9.5,18 L 20,7.5 L 18.6,6.1 L 9.5,15.2 L 5.4,11.1 Z");

    public YouTubeImportViewModel ViewModel { get; }

    internal TextBox QueryBox { get; }
    internal ComboBox FormatBox { get; }
    internal ItemsControl ResultsList { get; }
    internal ItemsControl RecentList { get; }

    public YouTubeImportView(YouTubeImportViewModel vm)
    {
        ViewModel = vm;
        DataContext = vm;
        TryAddPillStyles();
        vm.PickFolder = PickFolderAsync;

        // ── Header: icon, title, where downloads go (Change / Use default). ──
        var title = new TextBlock { Text = "Add from YouTube", FontSize = 18, FontWeight = FontWeight.Bold };
        var savesTo = new TextBlock { FontSize = 13, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        savesTo.Bind(TextBlock.TextProperty, new Binding(nameof(vm.DestinationFolder)) { StringFormat = "Saves to {0}" });
        savesTo.Bind(ToolTip.TipProperty, new Binding(nameof(vm.DestinationFolder)));
        savesTo.Bind(IsVisibleProperty, new Binding(nameof(vm.HasDestination)));
        var change = SmallButton("Change…", nameof(vm.ChangeFolderCommand));
        AutomationName(change, "Change download folder");
        var reset = SmallButton("Use default", nameof(vm.ResetFolderCommand));
        reset.Bind(IsVisibleProperty, new Binding(nameof(vm.HasCustomFolder)));
        var folderRow = new DockPanel { LastChildFill = true };
        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 0, 0, 0), Children = { change, reset } };
        DockPanel.SetDock(folderButtons, Dock.Right);
        folderRow.Children.Add(folderButtons);
        folderRow.Children.Add(savesTo);

        var badge = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(16), Background = Coral,
            Child = new Avalonia.Controls.Shapes.Path { Data = PlayGlyph, Fill = Brushes.White, Width = 22, Height = 22, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var headerText = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { title, folderRow } };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("56,16,*") };
        headerGrid.Children.Add(badge);
        Grid.SetColumn(headerText, 2);
        headerGrid.Children.Add(headerText);

        // ── Search or paste a link + the round search button + the format. ──
        QueryBox = new TextBox { Name = "QueryBox", PlaceholderText = "Search or paste a link", MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center };
        QueryBox.Classes.Add("pill-field");
        QueryBox.Bind(TextBox.TextProperty, new Binding(nameof(vm.Query)) { Mode = BindingMode.TwoWay });
        QueryBox.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = vm.SearchCommand });
        AutomationName(QueryBox, "Search or paste a link");
        var searchButton = new Button
        {
            Width = 40, Height = 40, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
            Command = vm.SearchCommand,
            Content = new Avalonia.Controls.Shapes.Path { Data = SearchGlyph, Width = 14, Height = 14, Stretch = Stretch.Uniform },
        };
        searchButton.Classes.Add("pill-primary");
        searchButton.Bind(IsEnabledProperty, new Binding("!" + nameof(vm.IsSearching)));
        ToolTip.SetTip(searchButton, "Search");
        AutomationName(searchButton, "Search");
        if (searchButton.Content is Avalonia.Controls.Shapes.Path glyph) glyph.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, searchButton.GetResourceObservable("AccentForegroundBrush"));
        FormatBox = new ComboBox { Name = "FormatBox", MinWidth = 196, MinHeight = 40, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Stretch, ItemsSource = vm.Formats };
        FormatBox.Classes.Add("pill-field");
        FormatBox.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.SelectedFormat)) { Mode = BindingMode.TwoWay });
        ToolTip.SetTip(FormatBox, vm.Formats.Count > 1 ? "Format and quality" : "Format (MP3 and Opus need ffmpeg)");
        AutomationName(FormatBox, "Format and quality");
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        searchRow.Bind(IsEnabledProperty, new Binding("!" + nameof(vm.ShowSetup)));
        searchRow.Children.Add(QueryBox);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);
        Grid.SetColumn(FormatBox, 2);
        searchRow.Children.Add(FormatBox);

        var header = new StackPanel { Spacing = 16, Margin = new Thickness(0, 0, 0, 14), Children = { headerGrid, searchRow } };

        // ── Body: setup, skeleton, empty hint, results. ──
        ResultsList = new ItemsControl { Name = "ResultsList", ItemTemplate = new FuncDataTemplate<YouTubeImportViewModel.ResultRow>((_, _) => BuildRow(), supportsRecycling: false) };
        ResultsList.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Results)));
        var results = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = ResultsList, Padding = new Thickness(0, 0, 12, 0) };
        results.Bind(IsVisibleProperty, new Binding(nameof(vm.ShowResults)));

        var body = new Panel { MinHeight = 220, Children = { BuildSetup(vm), BuildSkeleton(vm), BuildEmpty(vm), results } };

        // ── Recent downloads. ──
        RecentList = new ItemsControl { Name = "RecentList", ItemTemplate = new FuncDataTemplate<RecentDownload>((_, _) => BuildRecentRow(), supportsRecycling: false) };
        RecentList.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Recent)));
        var recentHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var clear = SmallButton("Clear", nameof(vm.ClearRecentCommand));
        AutomationName(clear, "Clear recent downloads");
        DockPanel.SetDock(clear, Dock.Right);
        recentHeader.Children.Add(clear);
        recentHeader.Children.Add(new TextBlock { Text = "Recent downloads", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var recent = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Children = { recentHeader, new ScrollViewer { MaxHeight = 150, Content = RecentList, Padding = new Thickness(0, 0, 12, 0) } },
        };
        recent.Bind(IsVisibleProperty, new Binding(nameof(vm.HasRecent)));

        // ── Footer: status, then the yt-dlp version note (and Update for a copy it may not replace). ──
        var status = new TextBlock { FontSize = 12.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(vm.StatusMessage)));
        status.Bind(ToolTip.TipProperty, new Binding(nameof(vm.StatusMessage)));
        status.Bind(IsVisibleProperty, new Binding(nameof(vm.HasStatus)));
        var version = new TextBlock { FontSize = 11, Opacity = 0.4, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        version.Bind(TextBlock.TextProperty, new Binding(nameof(vm.ToolVersionText)));
        var update = new Button { Name = "UpdateToolButton", Content = "Update", MinHeight = 0, Padding = new Thickness(10, 2), FontSize = 11, Command = vm.InstallToolCommand };
        update.Classes.Add("pill-secondary");
        update.Bind(IsVisibleProperty, new Binding(nameof(vm.ShowToolUpdate)));
        ToolTip.SetTip(update, "Install an up-to-date copy of yt-dlp for Noctis.");
        AutomationName(update, "Update yt-dlp");
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { version, update } };
        versionRow.Bind(IsVisibleProperty, new Binding(nameof(vm.HasToolVersion)));
        var footer = new Border
        {
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel { Spacing = 4, Children = { status, versionRow } },
        };
        footer.Bind(Border.BorderBrushProperty, footer.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            MaxWidth = 760,
            Margin = new Thickness(28, 24, 28, 18),
        };
        root.Children.Add(header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(recent, 2);
        root.Children.Add(recent);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;

        ActualThemeVariantChanged += (_, _) => ApplyBackground();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyBackground();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        QueryBox.Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ViewModel.Detach();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the window, as it closed the dialog.
        if (e.Key == Key.Escape && TopLevel.GetTopLevel(this) is Window window)
        {
            e.Handled = true;
            window.Close();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>The plugin window is dark whatever the theme: paint Noctis's page colour so text stays readable in light mode.</summary>
    private void ApplyBackground()
    {
        var variant = ActualThemeVariant;
        var dark = variant != ThemeVariant.Light;
        Color page = this.TryFindResource("AppMainBackgroundColor", variant, out var p) && p is Color c
            ? c
            : dark ? Color.FromRgb(0x1C, 0x1C, 0x1F) : Color.FromRgb(0xF2, 0xF2, 0xF5);
        Background = new SolidColorBrush(page);
    }

    private void TryAddPillStyles()
    {
        try
        {
            var include = new StyleInclude(new Uri("avares://Noctis.Plugins.YouTubeImport/")) { Source = new Uri(PillStylesUri) };
            _ = include.Loaded; // throws now, not at first layout, when the app has no such file
            Styles.Add(include);
            PillStylesLoaded = true;
        }
        catch
        {
            PillStylesLoaded = false;
        }
    }

    /// <summary>Tests: whether Noctis's pill styles were found.</summary>
    internal bool PillStylesLoaded { get; private set; }

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage) return null;
        var picks = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for YouTube downloads", AllowMultiple = false });
        return picks.Count > 0 ? picks[0].TryGetLocalPath() : null;
    }

    private static void AutomationName(Control control, string name) => Avalonia.Automation.AutomationProperties.SetName(control, name);

    private static Button SmallButton(string text, string command)
    {
        var b = new Button { Content = text, MinHeight = 0, Padding = new Thickness(12, 4), FontSize = 12 };
        b.Classes.Add("pill-secondary");
        b.Bind(Button.CommandProperty, new Binding(command));
        return b;
    }

    private static Control BuildSetup(YouTubeImportViewModel vm)
    {
        var install = new Button { Name = "InstallButton", MinWidth = 160, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Command = vm.InstallToolCommand };
        install.Classes.Add("pill-primary");
        var label = new TextBlock();
        label.Bind(TextBlock.TextProperty, new Binding(nameof(vm.IsInstallingTool)) { Converter = new FuncValueConverter<bool, string>(busy => busy ? "Installing…" : "Install yt-dlp") });
        install.Content = label;
        install.Bind(IsEnabledProperty, new Binding("!" + nameof(vm.IsInstallingTool)));
        AutomationName(install, "Install yt-dlp");
        var bar = new ProgressBar { Width = 160, Height = 4, MinHeight = 0, MinWidth = 0, Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Center };
        bar.Bind(RangeBase.ValueProperty, new Binding(nameof(vm.InstallProgress)));
        bar.Bind(IsVisibleProperty, new Binding(nameof(vm.IsInstallingTool)));
        var panel = new StackPanel
        {
            Name = "SetupPanel", Spacing = 10, Margin = new Thickness(0, 30), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "One-time setup", FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Downloads need yt-dlp. Noctis installs it once and keeps it updated.", FontSize = 11.5, Opacity = 0.55, MaxWidth = 320, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                install,
                bar,
            },
        };
        panel.Bind(IsVisibleProperty, new Binding(nameof(vm.ShowSetup)));
        return panel;
    }

    private static Control BuildSkeleton(YouTubeImportViewModel vm)
    {
        var panel = new StackPanel { Spacing = 8, Opacity = 0.7 };
        foreach (var (top, bottom) in new[] { (220, 140), (180, 168), (248, 120) })
        {
            var lines = new StackPanel
            {
                Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                Children = { Bone(top, 10), Bone(bottom, 8) },
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("56,14,*") };
            grid.Children.Add(Bone(56, 56, 12));
            Grid.SetColumn(lines, 2);
            grid.Children.Add(lines);
            var card = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(10), Child = grid };
            card.Classes.Add("pill-well");
            panel.Children.Add(card);
        }
        panel.Bind(IsVisibleProperty, new Binding(nameof(vm.ShowSkeleton)));
        return panel;

        static Border Bone(double width, double height, double radius = -1) => new()
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(radius < 0 ? height / 2 : radius),
            HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)),
        };
    }

    private static Control BuildEmpty(YouTubeImportViewModel vm)
    {
        var text = new TextBlock
        {
            Text = "Find a song, then download it into your library.", FontSize = 12.5, Opacity = 0.5, MaxWidth = 320,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        text.Bind(IsVisibleProperty, new Binding(nameof(vm.ShowEmpty)));
        return text;
    }

    /// <summary>One result card: thumbnail, title, channel · duration, Download (or progress / done) on the right.</summary>
    private static Control BuildRow()
    {
        var thumb = new Image { Stretch = Stretch.UniformToFill };
        thumb.Bind(Image.SourceProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.Thumbnail)));
        var art = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Child = thumb, VerticalAlignment = VerticalAlignment.Center };

        var title = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.Title)));
        title.Bind(ToolTip.TipProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.Title)));
        var subtitle = new TextBlock { FontSize = 11.5, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
        subtitle.Bind(TextBlock.TextProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.Subtitle)));
        var bar = new ProgressBar { Height = 4, MinHeight = 0, MinWidth = 0, Minimum = 0, Maximum = 1 };
        bar.Bind(RangeBase.ValueProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.Progress)));
        var stage = new TextBlock { FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis };
        stage.Bind(TextBlock.TextProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.StatusText)));
        var busy = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0), Children = { bar, stage } };
        busy.Bind(IsVisibleProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.IsBusy)));
        var failed = new TextBlock { Foreground = FailedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        failed.Bind(TextBlock.TextProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.StatusText)));
        failed.Bind(IsVisibleProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.Failed)));
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle, busy, failed } };

        var download = new Button { Content = "Download", MinHeight = 34, Padding = new Thickness(16, 0), FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center };
        download.Classes.Add("pill-secondary");
        download.Bind(Button.CommandProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.DownloadCommand)));
        download.Bind(IsVisibleProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.ShowDownload)));
        var percent = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        percent.Bind(TextBlock.TextProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.ProgressText)));
        percent.Bind(IsVisibleProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.IsBusy)));
        var check = new Avalonia.Controls.Shapes.Path { Data = CheckGlyph, Width = 13, Height = 13, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        check.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, check.GetResourceObservable("AccentColorBrush"));
        var doneText = new TextBlock { FontSize = 12.5, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        doneText.Bind(TextBlock.TextProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.StatusText)));
        var done = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { check, doneText } };
        done.Bind(IsVisibleProperty, new Binding(nameof(YouTubeImportViewModel.ResultRow.IsDone)));
        var actions = new Panel { VerticalAlignment = VerticalAlignment.Center, Children = { download, percent, done } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("56,14,*,12,Auto") };
        grid.Children.Add(art);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        Grid.SetColumn(actions, 4);
        grid.Children.Add(actions);

        // A downloaded card gets the accent ring; the 1.5 px ring is always there (transparent
        // otherwise) so finishing only recolours it and nothing shifts.
        var card = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(10, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Child = grid };
        card.Classes.Add("pill-well");
        card.Classes.Add("yt-row");
        card.Bind(Border.BorderBrushProperty, new MultiBinding
        {
            Bindings = { new Binding(nameof(YouTubeImportViewModel.ResultRow.IsDone)), card.GetResourceObservable("AccentColorBrush").ToBinding() },
            Converter = new FuncMultiValueConverter<object?, IBrush?>(values =>
            {
                var list = values.ToList();
                return list.Count == 2 && list[0] is true && list[1] is IBrush accent ? accent : Brushes.Transparent;
            }),
        });
        return card;
    }

    /// <summary>One recent download: title, then artist · where it went · when.</summary>
    private static Control BuildRecentRow()
    {
        var title = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(RecentDownload.Title)));
        var detail = new TextBlock { FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.Bind(TextBlock.TextProperty, new Binding(".") { Converter = new FuncValueConverter<RecentDownload?, string>(r => r is null ? "" : RecentDetail(r, DateTimeOffset.Now)) });
        var row = new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 0, 8), Children = { title, detail } };
        row.Bind(ToolTip.TipProperty, new Binding(nameof(RecentDownload.FilePath)));
        return row;
    }

    /// <summary>"Artist · In your library · 5 min ago".</summary>
    internal static string RecentDetail(RecentDownload r, DateTimeOffset now)
    {
        var age = now - r.When;
        var when = age < TimeSpan.FromMinutes(1) ? "just now"
            : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
            : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago"
            : r.When.LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Artist)) parts.Add(r.Artist);
        parts.Add(r.InLibrary ? "In your library" : "Saved to folder");
        parts.Add(when);
        return string.Join(" · ", parts);
    }
}

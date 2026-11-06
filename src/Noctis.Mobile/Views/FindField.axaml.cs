using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace Noctis.Mobile.Views;

/// <summary>The list pages' shared "Find in …" field: <see cref="Text"/> binds two-way to the
/// page's query; the clear button empties it.</summary>
public partial class FindField : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<FindField, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<FindField, string?>(nameof(Placeholder));

    public FindField()
    {
        InitializeComponent();
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    private void OnClear(object? sender, RoutedEventArgs e) => Text = string.Empty;
}

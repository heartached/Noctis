using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Noctis.Mobile.Views;

/// <summary>The list pages' shared "Find in …" field: <see cref="Text"/> binds two-way to the
/// page's query; the clear button empties it.</summary>
public partial class FindField : UserControl
{
    /// <summary>
    /// Typing: a keystroke within this long of the one before waits until the typing pauses for
    /// it before it reaches <see cref="Text"/> (the first letter after a pause, and an emptied
    /// field, at once), so a fast word or a held backspace filters once, not per letter: each
    /// filter re-ran the page's search over the library and re-laid the list on the UI thread
    /// (Songs, Albums, Artists; tester 11-06 "typing or backspacing fast lags"). Zero (the
    /// default, tests) hands on every keystroke; the phone sets it, as Search's own.
    /// </summary>
    public static TimeSpan TypingDebounce { get; set; }

    private DispatcherTimer? _typingTimer;
    private long _lastKeystrokeTicks = long.MinValue / 2;
    private bool _syncing;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<FindField, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<FindField, string?>(nameof(Placeholder));

    public FindField()
    {
        InitializeComponent();
        FindBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) OnTyped();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The page set its query itself (a clear, a new page): the box shows it, and a keystroke
        // still waiting is dropped.
        if (change.Property != TextProperty || _syncing || FindBox is null || FindBox.Text == Text) return;
        _typingTimer?.Stop();
        _syncing = true;
        try { FindBox.Text = Text; }
        finally { _syncing = false; }
    }

    private void OnTyped()
    {
        if (_syncing) return;
        var debounce = TypingDebounce;
        var sinceLast = System.Diagnostics.Stopwatch.GetElapsedTime(_lastKeystrokeTicks);
        _lastKeystrokeTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (debounce <= TimeSpan.Zero || string.IsNullOrEmpty(FindBox.Text)
            || (sinceLast >= debounce && _typingTimer?.IsEnabled != true))
        {
            HandOn();
            return;
        }
        if (_typingTimer == null)
        {
            _typingTimer = new DispatcherTimer();
            _typingTimer.Tick += (_, _) => HandOn();
        }
        _typingTimer.Stop();
        _typingTimer.Interval = debounce;
        _typingTimer.Start();
    }

    private void HandOn()
    {
        _typingTimer?.Stop();
        _syncing = true;
        try { Text = FindBox.Text; }
        finally { _syncing = false; }
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    private void OnClear(object? sender, RoutedEventArgs e) => Text = string.Empty;
}

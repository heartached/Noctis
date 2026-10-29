using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Plugins.ABLoop.Looping;

namespace Noctis.Plugins.ABLoop.Views;

/// <summary>
/// A-B Loop's slim window: the song's title, the strip with draggable A and B markers and the
/// looped stretch shaded, [A] [B] [Loop] [count], nudge arrows, and the song's saved loops.
/// The loop itself lives in <see cref="LoopSession"/> and keeps running when the window closes.
/// </summary>
public sealed class ABLoopView : UserControl
{
    public const double WindowWidth = 460;
    public const double WindowHeight = 500;
    public const double MinWindowWidth = 380;
    public const double MinWindowHeight = 320;

    private readonly LoopSession _session;
    private readonly LoopStrip _strip = new();
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _aTime;
    private readonly TextBlock _bTime;
    private readonly TextBlock _posTime;
    private readonly Button _aButton;
    private readonly Button _bButton;
    private readonly Button _loopButton;
    private readonly TextBlock _loopLabel;
    private readonly Button _countButton;
    private readonly TextBlock _countLabel;
    private readonly Button _stepButton;
    private readonly TextBlock _stepLabel;
    private readonly Button _gapButton;
    private readonly TextBlock _gapLabel;
    private readonly Button _pinButton;
    private readonly TextBlock _pinLabel;
    private readonly Button _saveButton;
    private readonly StackPanel _list;
    private readonly TextBlock _listEmpty;
    private readonly TextBlock _savedHeader;
    private readonly TextBlock _noSong;
    private readonly Grid _body;
    private readonly List<(Button Button, TextBlock Label)> _buttons = new();
    private readonly List<TextBlock> _faintTexts = new();
    private readonly DispatcherTimer _frame;
    private LoopTheme _theme = LoopTheme.Dark;
    private SavedLoop[] _shownLoops = Array.Empty<SavedLoop>();
    private string _shownPos = "";
    private bool _shift;

    public ABLoopView(LoopSession session)
    {
        _session = session;

        _title = new TextBlock { FontSize = 17, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        _artist = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        (_pinButton, _pinLabel) = Pill("Keep on top", TogglePin);
        _pinButton.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titles = new StackPanel { Children = { _title, _artist }, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(_pinButton, 1);
        header.Children.Add(titles);
        header.Children.Add(_pinButton);

        _strip.Margin = new Thickness(-LoopStrip.Pad + 2, 10, -LoopStrip.Pad + 2, 0);
        _strip.MarkerMoved += (_, m) =>
        {
            if (m.IsA) _session.Engine.SetA(m.At);
            else _session.Engine.SetB(m.At);
        };
        _strip.SeekRequested += (_, at) => _session.Engine.SeekHost(at);

        _aTime = Faint();
        _bTime = Faint();
        _bTime.HorizontalAlignment = HorizontalAlignment.Right;
        _posTime = Faint();
        _posTime.HorizontalAlignment = HorizontalAlignment.Center;
        var times = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new Thickness(2, 2, 2, 0) };
        Grid.SetColumn(_posTime, 1);
        Grid.SetColumn(_bTime, 2);
        times.Children.Add(_aTime);
        times.Children.Add(_posTime);
        times.Children.Add(_bTime);

        (_aButton, _) = Pill("A", () => _session.Engine.CaptureA());
        (_bButton, _) = Pill("B", () => _session.Engine.CaptureB());
        (_loopButton, _loopLabel) = Pill("Loop", () => _session.ToggleLoop());
        (_countButton, _countLabel) = Pill("∞", () => _session.CycleCount());
        ToolTip.SetTip(_aButton, "Set A to where the song is now");
        ToolTip.SetTip(_bButton, "Set B to where the song is now");
        ToolTip.SetTip(_countButton, "How many times the part plays before the song plays on");
        foreach (var b in new[] { _aButton, _bButton, _loopButton, _countButton })
        {
            b.MinWidth = 64;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0),
            Children = { _aButton, _bButton, _loopButton, _countButton },
        };

        (_stepButton, _stepLabel) = Pill("0.1 s", ToggleStep);
        ToolTip.SetTip(_stepButton, "How far the arrows move A or B (hold Shift for the other step)");
        (_gapButton, _gapLabel) = Pill("No gap", () => _session.CycleGap());
        ToolTip.SetTip(_gapButton, "A pause at B before each repeat");
        var nudges = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                NudgeLabel("A"), Arrow("◀", "Move A back", () => _session.Nudge(true, -1, _shift)), Arrow("▶", "Move A forward", () => _session.Nudge(true, 1, _shift)),
                Spacer(), _stepButton, _gapButton, Spacer(),
                Arrow("◀", "Move B back", () => _session.Nudge(false, -1, _shift)), Arrow("▶", "Move B forward", () => _session.Nudge(false, 1, _shift)), NudgeLabel("B"),
            },
        };

        _savedHeader = new TextBlock { Text = "Saved loops", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        (_saveButton, _) = Pill("Save loop", () => _session.SaveCurrent());
        var savedRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 18, 0, 6) };
        Grid.SetColumn(_saveButton, 1);
        savedRow.Children.Add(_savedHeader);
        savedRow.Children.Add(_saveButton);
        _list = new StackPanel { Spacing = 4 };
        _listEmpty = Faint();
        _listEmpty.Text = "Set A and B, then save the loop to keep it with this song.";
        _listEmpty.TextWrapping = TextWrapping.Wrap;
        var scroller = new ScrollViewer { Content = new StackPanel { Children = { _listEmpty, _list } } };

        _body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*") };
        var rows = new Control[] { header, _strip, times, actions, nudges, savedRow, scroller };
        for (var i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i], i);
            _body.Children.Add(rows[i]);
        }
        _noSong = new TextBlock
        {
            Text = "Play a song to loop a part of it.", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
        };
        Content = new Grid { Children = { _body, _noSong }, Margin = new Thickness(18, 14, 18, 16) };

        // Shift on a nudge arrow takes the other step: read it as the press goes down.
        AddHandler(PointerPressedEvent, (_, e) => _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel, handledEventsToo: true);
        _frame = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => UpdatePosition());
        _session.Changed += OnSessionChanged;
        ApplyTheme(LoopTheme.Dark);
        Refresh();
    }

    // ── For tests and the preview ──
    public LoopStrip Strip => _strip;
    public string LoopText => _loopLabel.Text ?? "";
    public string CountText => _countLabel.Text ?? "";
    public bool LoopOn => _session.Engine.Enabled;
    public int SavedRows => _list.Children.Count;
    public bool NoSongShown => _noSong.IsVisible;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme(LoopTheme.From(this));
        ActualThemeVariantChanged += OnThemeChanged;
        if (TopLevel.GetTopLevel(this) is Window w) w.Topmost = _session.Store.OnTop;
        _frame.Start();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _frame.Stop();
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The window closed: stop listening (the loop itself carries on).</summary>
    public void Release()
    {
        _frame.Stop();
        _session.Changed -= OnSessionChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme(LoopTheme.From(this));

    private void OnSessionChanged(object? sender, EventArgs e) => Refresh();

    private void TogglePin()
    {
        var on = !_session.Store.OnTop;
        _session.Store.SetOnTop(on);
        if (TopLevel.GetTopLevel(this) is Window w) w.Topmost = on;
        Refresh();
    }

    private void ToggleStep()
    {
        _session.NudgeStep = _session.NudgeStep >= TimeSpan.FromSeconds(1) ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        Refresh();
    }

    /// <summary>The playhead and the time under it (a 33 ms timer while the window is open; idle when minimized).</summary>
    public void UpdatePosition()
    {
        if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized }) return;
        if (!_session.HasTrack) return;
        var pos = _session.Engine.Predicted;
        if (!_strip.IsDragging) _strip.Position = pos;
        var text = LoopSession.Time(pos);
        if (text != _shownPos)
        {
            _shownPos = text;
            _posTime.Text = text;
        }
    }

    /// <summary>Everything but the playhead, after a change.</summary>
    public void Refresh()
    {
        var engine = _session.Engine;
        var has = _session.HasTrack;
        _noSong.IsVisible = !has;
        _body.IsVisible = has;
        _title.Text = string.IsNullOrWhiteSpace(_session.TrackTitle) ? "A-B Loop" : _session.TrackTitle;
        _artist.Text = _session.TrackArtist;
        _artist.IsVisible = _session.TrackArtist.Length > 0;
        _strip.Duration = _session.Duration;
        _strip.A = engine.A;
        _strip.B = engine.B;
        _strip.InvalidateVisual();
        _aTime.Text = engine.A is { } a ? "A  " + LoopSession.Time(a) : "A  not set";
        _bTime.Text = engine.B is { } b ? LoopSession.Time(b) + "  B" : "not set  B";
        _loopButton.IsEnabled = engine.HasPart;
        _loopLabel.Text = engine.Enabled ? "Looping" : "Loop";
        _countLabel.Text = LoopSession.CountText(engine.Count);
        _stepLabel.Text = _session.NudgeStep >= TimeSpan.FromSeconds(1) ? "Step 1 s" : "Step 0.1 s";
        _gapLabel.Text = LoopSession.GapText(engine.Gap);
        _saveButton.IsEnabled = engine.HasPart && has;
        _pinLabel.Text = _session.Store.OnTop ? "On top" : "Keep on top";
        // These keep their own background, so a disabled one says so by fading.
        _loopButton.Opacity = _loopButton.IsEnabled ? 1 : 0.4;
        _saveButton.Opacity = _saveButton.IsEnabled ? 1 : 0.4;
        Paint();

        var saved = _session.Saved;
        if (!saved.SequenceEqual(_shownLoops))
        {
            _shownLoops = saved.ToArray();
            _list.Children.Clear();
            foreach (var loop in _shownLoops) _list.Children.Add(Row(loop));
        }
        _listEmpty.IsVisible = _shownLoops.Length == 0;
        UpdatePosition();
    }

    private Control Row(SavedLoop loop)
    {
        var name = new TextBox
        {
            Text = loop.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(2, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Foreground = _theme.Text,
        };
        Avalonia.Automation.AutomationProperties.SetName(name, "Loop name");
        void Commit()
        {
            if (!string.IsNullOrWhiteSpace(name.Text) && name.Text != loop.Name) _session.Rename(loop, name.Text);
        }
        name.LostFocus += (_, _) => Commit();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        var span = new TextBlock
        {
            Text = LoopSession.Time(loop.Start) + " to " + LoopSession.Time(loop.End), FontSize = 12, Foreground = _theme.Muted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0),
        };
        var play = new Button
        {
            Content = new TextBlock { Text = "Loop it", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = _theme.ChipOnText },
            Background = _theme.ChipOn, CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(0), Padding = new Thickness(10, 4), MinHeight = 0,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(play, "Loop " + loop.Name);
        play.Click += (_, _) => _session.Jump(loop);
        var delete = new Button
        {
            Content = new TextBlock { Text = "×", FontSize = 15, Foreground = _theme.Muted },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 0), MinHeight = 0,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
        };
        Avalonia.Automation.AutomationProperties.SetName(delete, "Delete " + loop.Name);
        delete.Click += (_, _) => _session.Delete(loop);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        Grid.SetColumn(span, 1);
        Grid.SetColumn(play, 2);
        Grid.SetColumn(delete, 3);
        grid.Children.Add(name);
        grid.Children.Add(span);
        grid.Children.Add(play);
        grid.Children.Add(delete);
        return new Border { Child = grid, Background = _theme.Row, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 5) };
    }

    public void ApplyTheme(LoopTheme theme)
    {
        _theme = theme;
        _strip.Palette = theme;
        Background = theme.Page;
        Foreground = theme.Text;
        _title.Foreground = theme.Text;
        _artist.Foreground = theme.Muted;
        _savedHeader.Foreground = theme.Text;
        _noSong.Foreground = theme.Muted;
        foreach (var t in _faintTexts) t.Foreground = theme.Faint;
        Paint();
        _shownLoops = Array.Empty<SavedLoop>(); // rows rebuilt in the new colours
    }

    private void Paint()
    {
        foreach (var (button, label) in _buttons)
        {
            var on = (button == _loopButton && _session.Engine.Enabled) || (button == _pinButton && _session.Store.OnTop);
            button.Background = on ? _theme.AccentBrush : _theme.Chip;
            label.Foreground = on ? _theme.OnAccent : _theme.Text;
        }
    }

    private (Button, TextBlock) Pill(string text, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold };
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(0),
            MinHeight = 0,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        b.Click += (_, _) => click();
        _buttons.Add((b, label));
        return (b, label);
    }

    private Button Arrow(string glyph, string name, Action click)
    {
        var (b, label) = Pill(glyph, click);
        label.FontSize = 10;
        b.Padding = new Thickness(10, 7);
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        ToolTip.SetTip(b, name);
        return b;
    }

    private TextBlock NudgeLabel(string text)
    {
        var t = Faint();
        t.Text = text;
        t.FontWeight = FontWeight.Bold;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(2, 0);
        return t;
    }

    private static Control Spacer() => new Border { Width = 6 };

    private TextBlock Faint()
    {
        var t = new TextBlock { FontSize = 12 };
        _faintTexts.Add(t);
        return t;
    }
}

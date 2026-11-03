using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Plugins.Chords.Analysis;

namespace Noctis.Plugins.Chords.Views;

/// <summary>
/// The Chords window: the song, the chord playing now (large) with the next ones beside it, a lane of
/// the chords around the playhead (click one to jump there), the key, and a line that says these
/// are estimates. Before a song is analysed: a "Find chords" button; while it runs: progress and Cancel.
/// </summary>
public sealed class ChordsView : UserControl
{
    public const double WindowWidth = 520;
    public const double WindowHeight = 400;
    public const double MinWindowWidth = 380;
    public const double MinWindowHeight = 320;
    public const string EstimateNote = "Estimated from the song's audio on this computer, so it can be wrong (voices, effects and unusual chords make it harder).";

    private readonly ChordsSession _session;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _badge;
    private readonly Border _badgeBox;
    private readonly TextBlock _now;
    private readonly TextBlock _nowCaption;
    private readonly TextBlock _nextCaption;
    private readonly StackPanel _upcoming;
    private readonly ChordLane _lane = new();
    private readonly TextBlock _key;
    private readonly TextBlock _message;
    private readonly ProgressBar _progress;
    private readonly Button _action;
    private readonly TextBlock _actionLabel;
    private readonly Button _again;
    private readonly TextBlock _againLabel;
    private readonly TextBlock _note;
    private readonly Grid _chordsPanel;
    private readonly StackPanel _statePanel;
    private readonly DispatcherTimer _frame;
    private ChordsTheme _theme = ChordsTheme.Dark;
    private int _shownIndex = -2;
    private ChordAnalysis? _shownAnalysis;

    public ChordsView(ChordsSession session)
    {
        _session = session;

        _title = new TextBlock { FontSize = 17, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        _artist = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        _badge = new TextBlock { Text = "Estimate", FontSize = 11, FontWeight = FontWeight.SemiBold };
        _badgeBox = new Border { Child = _badge, CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_badgeBox, EstimateNote);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titles = new StackPanel { Children = { _title, _artist }, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(_badgeBox, 1);
        header.Children.Add(titles);
        header.Children.Add(_badgeBox);

        _now = new TextBlock { FontSize = 64, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.None };
        Avalonia.Automation.AutomationProperties.SetName(_now, "Chord now");
        _nowCaption = new TextBlock { FontSize = 12, Text = "Now" };
        var nowStack = new StackPanel { Children = { _nowCaption, _now }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 170 };
        _nextCaption = new TextBlock { FontSize = 12, Text = "Next" };
        _upcoming = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var nextStack = new StackPanel { Children = { _nextCaption, _upcoming }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetColumn(nextStack, 1);
        top.Children.Add(nowStack);
        top.Children.Add(nextStack);

        _lane.Margin = new Thickness(0, 14, 0, 0);
        _lane.SeekRequested += (_, at) => _session.Seek(at);
        ToolTip.SetTip(_lane, "Click a chord to play from there");

        _key = new TextBlock { FontSize = 13, Margin = new Thickness(0, 10, 0, 0) };
        (_again, _againLabel) = Pill("Analyze again", () => _session.Find(force: true));
        _again.HorizontalAlignment = HorizontalAlignment.Right;
        _again.VerticalAlignment = VerticalAlignment.Bottom;
        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_again, 1);
        keyRow.Children.Add(_key);
        keyRow.Children.Add(_again);

        _chordsPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        Grid.SetRow(_lane, 1);
        Grid.SetRow(keyRow, 2);
        _chordsPanel.Children.Add(top);
        _chordsPanel.Children.Add(_lane);
        _chordsPanel.Children.Add(keyRow);

        _message = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 380 };
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, Width = 260, IsVisible = false };
        (_action, _actionLabel) = Pill("Find chords", OnAction);
        _action.HorizontalAlignment = HorizontalAlignment.Center;
        _action.Padding = new Thickness(18, 8);
        _statePanel = new StackPanel
        {
            Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { _message, _progress, _action },
        };
        _progress.HorizontalAlignment = HorizontalAlignment.Center;

        _note = new TextBlock { Text = EstimateNote, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var middle = new Grid { Children = { _chordsPanel, _statePanel } };
        Grid.SetRow(middle, 1);
        Grid.SetRow(_note, 2);
        body.Children.Add(header);
        body.Children.Add(middle);
        body.Children.Add(_note);
        Content = new Border { Child = body, Padding = new Thickness(18, 14, 18, 14) };

        _frame = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => UpdatePosition());
        _session.Changed += OnSessionChanged;
        ApplyTheme(ChordsTheme.Dark);
        Refresh();
    }

    // ── For tests ──
    public string NowText => _now.Text ?? "";
    public IReadOnlyList<string> UpcomingTexts => _upcoming.Children.OfType<Border>().Select(b => (b.Child as TextBlock)?.Text ?? "").ToList();
    public string KeyText => _key.Text ?? "";
    public string MessageText => _message.Text ?? "";
    public string ActionText => _actionLabel.Text ?? "";
    public bool ActionShown => _action.IsVisible;
    public bool ChordsShown => _chordsPanel.IsVisible;
    public bool ProgressShown => _progress.IsVisible;
    public ChordLane Lane => _lane;
    public string BadgeText => _badge.Text ?? "";
    public void ClickAction() => OnAction();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme(ChordsTheme.From(this));
        ActualThemeVariantChanged += OnThemeChanged;
        _session.ViewOpened();
        _frame.Start();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _frame.Stop();
        _session.ViewClosed();
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The window closed: stop listening (the session keeps its result).</summary>
    public void Release()
    {
        _frame.Stop();
        _session.Changed -= OnSessionChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme(ChordsTheme.From(this));

    private void OnSessionChanged(object? sender, EventArgs e) => Refresh();

    private void OnAction()
    {
        switch (_session.State)
        {
            case ChordsState.Finding: _session.Cancel(); break;
            case ChordsState.Idle or ChordsState.Failed or ChordsState.NoDecoder: _session.Find(force: true); break;
        }
    }

    /// <summary>The lane's playhead every 50 ms; the big chord and the upcoming ones when the chord changes. Idle while minimized.</summary>
    public void UpdatePosition()
    {
        if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized }) return;
        if (_session.State != ChordsState.Ready || _session.Analysis is not { } a) return;
        var pos = _session.PredictedPosition.TotalSeconds;
        _lane.Position = pos;
        _lane.InvalidateVisual();
        var index = a.IndexAt(pos);
        if (index == _shownIndex && ReferenceEquals(a, _shownAnalysis)) return;
        _shownIndex = index;
        _shownAnalysis = a;
        var chord = index >= 0 ? a.Segments[index].Chord : Chord.NoChord;
        _now.Text = chord.IsNone ? "–" : chord.Name(a.Flats);
        Avalonia.Automation.AutomationProperties.SetName(_now, chord.IsNone ? "No chord now" : "Chord now: " + chord.Name(a.Flats));
        _upcoming.Children.Clear();
        var start = index >= 0 ? index + 1 : NextAfter(a, pos);
        for (var i = start; i >= 0 && i < a.Segments.Count && _upcoming.Children.Count < 3; i++)
        {
            var s = a.Segments[i];
            if (s.Chord.IsNone) continue;
            var label = new TextBlock
            {
                Text = s.Chord.Name(a.Flats), FontSize = _upcoming.Children.Count == 0 ? 26 : 18, FontWeight = FontWeight.SemiBold,
                Foreground = _upcoming.Children.Count == 0 ? _theme.Text : _theme.Muted,
            };
            _upcoming.Children.Add(new Border { Child = label, Background = _theme.Chip, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 4), VerticalAlignment = VerticalAlignment.Center });
        }
    }

    private static int NextAfter(ChordAnalysis a, double seconds)
    {
        for (var i = 0; i < a.Segments.Count; i++) if (a.Segments[i].Start > seconds) return i;
        return -1;
    }

    /// <summary>Everything but the playhead, after a change of state.</summary>
    public void Refresh()
    {
        var state = _session.State;
        var hasSong = state != ChordsState.NoSong;
        _title.Text = hasSong && !string.IsNullOrWhiteSpace(_session.TrackTitle) ? _session.TrackTitle : "Chords";
        _artist.Text = hasSong ? _session.TrackArtist : "";
        _artist.IsVisible = _artist.Text.Length > 0;

        var ready = state == ChordsState.Ready && _session.Analysis is not null;
        _chordsPanel.IsVisible = ready;
        _statePanel.IsVisible = !ready;
        _progress.IsVisible = state == ChordsState.Finding;
        _action.IsVisible = state is ChordsState.Idle or ChordsState.Finding or ChordsState.Failed or ChordsState.NoDecoder;
        _again.IsVisible = ready;
        _progress.IsIndeterminate = state == ChordsState.Finding && _session.Progress < 0;
        _progress.Value = Math.Max(0, _session.Progress);
        _actionLabel.Text = state switch
        {
            ChordsState.Finding => "Cancel",
            ChordsState.Failed or ChordsState.NoDecoder => "Try again",
            _ => "Find chords",
        };
        Avalonia.Automation.AutomationProperties.SetName(_action, _actionLabel.Text);
        _message.Text = state switch
        {
            ChordsState.NoSong => "Play a song to see its chords.",
            ChordsState.Idle => "Find this song's chords: Chords listens to the file on this computer. It takes a few seconds.",
            ChordsState.Finding => _session.Progress < 0 ? "Reading the song…" : $"Listening for chords… {(int)(_session.Progress * 100)}%",
            ChordsState.Failed => "Could not work out the chords. " + (_session.Error ?? ""),
            ChordsState.NoDecoder => "Chords needs ffmpeg to read songs. Noctis normally brings it along; set it under Settings → Advanced → Helper programs.",
            _ => "",
        };

        if (ready && _session.Analysis is { } a)
        {
            _lane.Analysis = a;
            var noChords = a.Segments.All(s => s.Chord.IsNone);
            _key.Text = noChords ? "No chords found in this song." : a.Key is { } k ? $"Key: {k.Name} (estimated)" : "";
            _shownIndex = -2; // redraw the chord now
            UpdatePosition();
        }
        else
        {
            _lane.Analysis = null;
            _now.Text = "";
            _upcoming.Children.Clear();
            _shownIndex = -2;
        }
        Paint();
    }

    public void ApplyTheme(ChordsTheme theme)
    {
        _theme = theme;
        _lane.Palette = theme;
        Background = theme.Page;
        Foreground = theme.Text;
        _title.Foreground = theme.Text;
        _artist.Foreground = theme.Muted;
        _now.Foreground = theme.Text;
        _nowCaption.Foreground = theme.Faint;
        _nextCaption.Foreground = theme.Faint;
        _key.Foreground = theme.Muted;
        _message.Foreground = theme.Muted;
        _note.Foreground = theme.Faint;
        _badge.Foreground = theme.AccentText;
        _badgeBox.Background = theme.BlockNow;
        _progress.Foreground = theme.AccentBrush;
        _progress.Background = theme.Chip;
        _shownIndex = -2;
        Paint();
        UpdatePosition();
    }

    private void Paint()
    {
        var primary = _session.State is ChordsState.Idle or ChordsState.Failed or ChordsState.NoDecoder;
        _action.Background = primary ? _theme.AccentBrush : _theme.Chip;
        _actionLabel.Foreground = primary ? _theme.OnAccent : _theme.Text;
        _again.Background = _theme.Chip;
        _againLabel.Foreground = _theme.Text;
    }

    private static (Button, TextBlock) Pill(string text, Action click)
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
        return (b, label);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Noctis.Plugins.TrueShuffle.Shuffle;

namespace Noctis.Plugins.TrueShuffle.Views;

/// <summary>
/// The True Shuffle window (Settings → Plugins → True Shuffle → "Open True Shuffle", or a song's
/// menu → True Shuffle): pick a mode and what to shuffle, then Shuffle. Opening it changes nothing.
/// </summary>
public sealed class TrueShuffleView : UserControl
{
    private readonly ShuffleController _controller;
    private readonly IPluginHost _host;
    private readonly ComboBox _mode = new() { MinWidth = 220 };
    private readonly ComboBox _source = new() { MinWidth = 220 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly Button _reset = new() { Content = "Forget heard songs" };
    private List<ShuffleSource> _sources = new();
    private bool _confirmReset;

    public TrueShuffleView(ShuffleController controller, IPluginHost host)
    {
        _controller = controller;
        _host = host;
        foreach (var name in ShuffleModes.Names) _mode.Items.Add(name);
        _mode.SelectedIndex = (int)ShuffleModes.Parse(Setting("mode"));
        AutomationName(_mode, "Shuffle mode");
        AutomationName(_source, "What to shuffle");

        var shuffle = new Button { Content = "Shuffle", Classes = { "accent" }, IsDefault = true };
        shuffle.Click += async (_, _) => await ShuffleAsync();
        var end = new Button { Content = "End True Shuffle" };
        end.Click += (_, _) => _controller.End();
        _reset.Click += async (_, _) => await ResetAsync();
        _reset.LostFocus += (_, _) => { _confirmReset = false; _reset.Content = "Forget heard songs"; };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                MaxWidth = 560,
                Children =
                {
                    new TextBlock { Text = "True Shuffle", FontSize = 22, FontWeight = FontWeight.Bold },
                    Row("Mode", _mode),
                    Row("Shuffle", _source),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { shuffle, end, _reset } },
                    _status,
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12,
                        Text = "Equal chance plays every song once in a fair random order. No repeats waits with the songs you heard "
                             + "until the rest have played. Discovery plays the least played first. Never played plays only songs you "
                             + "never played, and stops when none are left. For you picks from how you listen on this computer.\n\n"
                             + "Songs you add with Play Next or Add to Queue play first, in your order. Playing an album or a list "
                             + "yourself ends True Shuffle; the queue stays as it is. By Alfamin.",
                    },
                },
            },
        };
        AttachedToVisualTree += (_, _) =>
        {
            FillSources();
            _controller.Changed += OnChanged;
            OnChanged(null, EventArgs.Empty);
        };
        DetachedFromVisualTree += (_, _) => _controller.Changed -= OnChanged;
    }

    private static void AutomationName(Control c, string name) => Avalonia.Automation.AutomationProperties.SetName(c, name);

    private static Control Row(string label, Control field) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 12,
        Children = { new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center }, field },
    };

    private string? Setting(string key)
    {
        try { return _host.Settings.GetString(key); } catch { return null; }
    }

    private void FillSources()
    {
        _sources = new List<ShuffleSource> { ShuffleSource.Queue, ShuffleSource.Library };
        try
        {
            foreach (var p in _host.Library.GetPlaylists())
                _sources.Add(new ShuffleSource(SourceKind.Playlist, p.Id, "Playlist: " + p.Name));
        }
        catch (Exception ex) { _host.Log("playlists: " + ex.Message); }
        _source.Items.Clear();
        foreach (var s in _sources) _source.Items.Add(s.Name);
        var wanted = string.Equals(Setting("source"), "Whole library", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _source.SelectedIndex = wanted;
    }

    private async Task ShuffleAsync()
    {
        var mode = (ShuffleMode)Math.Max(0, _mode.SelectedIndex);
        var source = _source.SelectedIndex >= 0 && _source.SelectedIndex < _sources.Count ? _sources[_source.SelectedIndex] : ShuffleSource.Queue;
        bool skip;
        try { skip = _host.Settings.GetBool("skipDisliked"); } catch { skip = false; }
        await _controller.StartAsync(mode, source, skip, Setting("forYouNew"));
    }

    private async Task ResetAsync()
    {
        if (!_confirmReset)
        {
            _confirmReset = true;
            _reset.Content = "Click again to forget them";
            return;
        }
        _confirmReset = false;
        _reset.Content = "Forget heard songs";
        await _controller.ResetCycleAsync();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        var s = _controller.Current;
        _status.Text = (s is null ? "" : $"Playing {ShuffleModes.Name(s.Mode)} on {s.Source.Name}. ") + _controller.Status;
    }
}

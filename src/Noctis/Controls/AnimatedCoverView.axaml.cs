using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Avalonia;

namespace Noctis.Controls;

public partial class AnimatedCoverView : UserControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AnimatedCoverView, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AnimatedCoverView, bool>(nameof(IsActive), defaultValue: false);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private MediaPlayer? _player;
    private VideoView? _videoHost;
    private Panel? _hostPanel;
    private bool _attached;

    public AnimatedCoverView()
    {
        InitializeComponent();
        _hostPanel = this.FindControl<Panel>("HostPanel");
        // Only drive the VideoView while we're in the visual tree. A VideoView whose
        // native window hasn't been created yet makes libVLC spawn its own standalone
        // output window ("VLC (Direct3D11 output)"). Rebuild on (re-)attach because
        // Source/IsActive don't change across a parent view show/hide.
        AttachedToVisualTree += (_, _) => { _attached = true; Refresh(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; Teardown(); };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsActiveProperty)
            Refresh();
    }

    private void Refresh()
    {
        var shouldPlay = _attached && IsActive && !string.IsNullOrEmpty(Source) && File.Exists(Source);
        if (!shouldPlay)
        {
            Teardown();
            return;
        }

        EnsurePlayer();

        // Start playback after a layout pass so the freshly-attached VideoView has
        // created its native window — otherwise libVLC opens a standalone window.
        var player = _player!;
        var source = Source!;
        Dispatcher.UIThread.Post(() =>
        {
            if (player != _player) return; // torn down or replaced in the meantime
            try
            {
                using var media = new Media(SharedLibVlc.Instance, source, FromType.FromPath,
                    ":no-audio", ":input-repeat=65535");
                player.Play(media);
            }
            catch
            {
                Teardown();
            }
        }, DispatcherPriority.Loaded);
    }

    private void EnsurePlayer()
    {
        if (_player == null)
        {
            _player = new MediaPlayer(SharedLibVlc.Instance) { EnableHardwareDecoding = true, Mute = true };
        }
        if (_videoHost == null && _hostPanel != null)
        {
            _videoHost = new VideoView { MediaPlayer = _player };
            _hostPanel.Children.Add(_videoHost);
        }
    }

    private void Teardown()
    {
        if (_videoHost != null && _hostPanel != null)
        {
            try { _hostPanel.Children.Remove(_videoHost); } catch { }
            try { _videoHost.MediaPlayer = null; } catch { }
            _videoHost = null;
        }

        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;

        // NEVER dispose the shared LibVLC — it's reused across surfaces.
    }
}

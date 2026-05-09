using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
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

    // One shared LibVLC instance for the whole app. Multiple LibVLC instances
    // fight over global VLC subsystem state (notably the audio device, which
    // breaks the main audio player). All animated-cover players share this.
    private static LibVLC? _sharedLibVlc;
    private static readonly object _libVlcLock = new();

    private static LibVLC GetSharedLibVlc()
    {
        if (_sharedLibVlc != null) return _sharedLibVlc;
        lock (_libVlcLock)
        {
            if (_sharedLibVlc == null)
            {
                Core.Initialize();
                // --aout=none guarantees this LibVLC never opens an audio device.
                _sharedLibVlc = new LibVLC("--quiet", "--no-video-title-show", "--aout=none");
            }
            return _sharedLibVlc;
        }
    }

    private MediaPlayer? _player;
    private VideoView? _videoHost;
    private Panel? _hostPanel;

    public AnimatedCoverView()
    {
        InitializeComponent();
        _hostPanel = this.FindControl<Panel>("HostPanel");
        DetachedFromVisualTree += (_, _) => Teardown();
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
        var shouldPlay = IsActive && !string.IsNullOrEmpty(Source) && File.Exists(Source);
        if (!shouldPlay)
        {
            Teardown();
            return;
        }

        try
        {
            EnsurePlayer();
            using var media = new Media(GetSharedLibVlc(), Source!, FromType.FromPath,
                ":no-audio", ":input-repeat=65535");
            _player!.Play(media);
        }
        catch
        {
            Teardown();
        }
    }

    private void EnsurePlayer()
    {
        var libVlc = GetSharedLibVlc();
        if (_player == null)
        {
            _player = new MediaPlayer(libVlc) { EnableHardwareDecoding = true, Mute = true };
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

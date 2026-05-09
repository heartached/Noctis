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

    private LibVLC? _libVlc;
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
            using var media = new Media(_libVlc!, Source!, FromType.FromPath,
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
        if (_libVlc == null)
        {
            Core.Initialize();
            _libVlc = new LibVLC("--quiet", "--no-video-title-show");
        }
        if (_player == null)
        {
            _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true, Mute = true };
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

        try { _libVlc?.Dispose(); } catch { }
        _libVlc = null;
    }
}

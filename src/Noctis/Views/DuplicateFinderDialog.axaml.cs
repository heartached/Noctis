using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class DuplicateFinderDialog : Window
{
    /// <summary>The Rescan arrow's rotation (the Viewbox's RotateTransform in the XAML).</summary>
    private Avalonia.Media.RotateTransform Spin => (Avalonia.Media.RotateTransform)RescanIcon.RenderTransform!;

    /// <summary>One turn of the Rescan arrow while scanning.</summary>
    internal static readonly TimeSpan SpinPeriod = TimeSpan.FromMilliseconds(850);

    private DuplicateFinderViewModel? _vm;
    private bool _spinning;
    private TimeSpan? _lastFrame;
    /// <summary>Set when the scan ends: the arrow then eases to rest at this angle (the next
    /// full turn), instead of snapping back from wherever it was.</summary>
    private double? _restAt;

    public DuplicateFinderDialog()
    {
        InitializeComponent();
    }

    public DuplicateFinderDialog(DuplicateFinderViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close; nothing is returned through
        // Close(result), so the deferred close loses nothing.
        vm.Closed += (_, _) => Close();
        _vm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        Opened += (_, _) => { if (vm.IsBusy) StartSpin(); };
        Closed += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DuplicateFinderViewModel.IsBusy) || _vm is null) return;
        if (_vm.IsBusy) StartSpin();
        else if (_spinning) _restAt = Math.Ceiling((Spin.Angle + 1) / 360) * 360; // at least a little more turn
    }

    /// <summary>The Rescan arrow turns while a scan runs (owner 10-08 "fix the rescan
    /// animation": it never moved). Driven by the frame clock so it stays smooth, and when the
    /// scan ends it finishes its turn with an ease-out — a quick scan still shows one turn.</summary>
    private void StartSpin()
    {
        _restAt = null;
        if (_spinning) return;
        _spinning = true;
        _lastFrame = null;
        RequestAnimationFrame(OnSpinFrame);
    }

    /// <summary>Degrees per second at full speed.</summary>
    private static double FullSpeed => 360 / SpinPeriod.TotalSeconds;

    private void OnSpinFrame(TimeSpan now)
    {
        if (!_spinning) return;
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 0;
        _lastFrame = now;
        var angle = Spin.Angle;

        if (_restAt is { } rest)
        {
            // Ease out: speed proportional to what is left, floored so it always arrives.
            var left = rest - angle;
            var step = Math.Max(left * 6 * dt, 40 * dt);
            if (step >= left || left <= 0.5)
            {
                Spin.Angle = 0; // a whole number of turns: the same pose
                _spinning = false;
                _restAt = null;
                return;
            }
            Spin.Angle = angle + Math.Min(step, FullSpeed * dt);
        }
        else
        {
            angle += FullSpeed * dt;
            Spin.Angle = angle >= 360 * 1000 ? angle % 360 : angle;
        }
        RequestAnimationFrame(OnSpinFrame);
    }

    /// <summary>Tests: whether the arrow is turning, and its angle.</summary>
    internal bool IsRescanSpinning => _spinning;
    internal double RescanAngle => Spin.Angle;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way the footer Close does.
        if (e.Key == Key.Escape && DataContext is DuplicateFinderViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}

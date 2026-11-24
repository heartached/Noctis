using System;
using Android.Content;
using Android.OS;
using Noctis.Mobile.Services;

namespace Noctis.Android.Services;

/// <summary>Android's Battery Saver (PowerManager.IsPowerSaveMode), asked when the background's
/// Slow Motion starts and every few seconds while it runs. Turning it on or off raises
/// <see cref="Changed"/> (ACTION_POWER_SAVE_MODE_CHANGED, on the main thread); the receiver is
/// registered with the app's context only while something listens.</summary>
internal sealed class AndroidBatterySaver : IBatterySaver
{
    private readonly Context _context;
    private readonly PowerManager? _power;
    private Receiver? _receiver;
    private EventHandler? _changed;

    public AndroidBatterySaver(Context context)
    {
        _context = context.ApplicationContext ?? context;
        _power = context.GetSystemService(Context.PowerService) as PowerManager;
    }

    public bool IsOn => _power?.IsPowerSaveMode ?? false;

    public event EventHandler? Changed
    {
        add
        {
            _changed += value;
            if (_changed is null || _receiver is not null) return;
            _receiver = new Receiver(this);
            var filter = new IntentFilter(PowerManager.ActionPowerSaveModeChanged);
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(33)) _context.RegisterReceiver(_receiver, filter, ReceiverFlags.NotExported);
                else _context.RegisterReceiver(_receiver, filter);
            }
            catch (Exception)
            {
                // Not watched: the loop then follows Battery Saver at the next pause, song or menu.
                _receiver = null;
            }
        }
        remove
        {
            _changed -= value;
            if (_changed is not null || _receiver is null) return;
            try { _context.UnregisterReceiver(_receiver); }
            catch (Exception) { }
            _receiver = null;
        }
    }

    private sealed class Receiver : BroadcastReceiver
    {
        private readonly AndroidBatterySaver _owner;

        public Receiver(AndroidBatterySaver owner) => _owner = owner;

        public override void OnReceive(Context? context, Intent? intent) => _owner._changed?.Invoke(_owner, EventArgs.Empty);
    }
}

using System;

namespace Noctis.Mobile.Services;

/// <summary>Whether the phone's Battery Saver is on (AndroidBatterySaver); Slow Motion in
/// Settings › Background stands still while it is. Null in tests unless injected.</summary>
public interface IBatterySaver
{
    bool IsOn { get; }

    /// <summary>Battery Saver was turned on or off (raised on the UI thread). Android watches for
    /// it only while someone listens. Hosts that cannot tell never raise it.</summary>
    event EventHandler? Changed { add { } remove { } }
}

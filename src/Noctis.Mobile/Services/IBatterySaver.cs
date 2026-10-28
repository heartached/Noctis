namespace Noctis.Mobile.Services;

/// <summary>Whether the phone's Battery Saver is on (AndroidBatterySaver); Slow Motion in
/// Settings › Background stands still while it is. Null in tests unless injected.</summary>
public interface IBatterySaver
{
    bool IsOn { get; }
}

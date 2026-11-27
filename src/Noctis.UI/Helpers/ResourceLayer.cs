using Avalonia.Controls;
using Avalonia.Styling;

namespace Noctis.Helpers;

/// <summary>
/// One merged-dictionary entry made of swappable parts, for resources that change together
/// (the theme overlay and the accent overlay; the Liquid Glass surfaces). Every Add or Remove
/// in a MergedDictionaries walks the whole tree under its owner and re-resolves every
/// DynamicResource on the way, hidden kept pages included, and a theme switch made three or
/// four of them (theme overlay out, in; accent overlay out, in), each landing a half-switched
/// state first (11-27). Here parts change silently between <see cref="BeginUpdate"/> and
/// <see cref="EndUpdate"/> and the owner is told once at the end; lookups see every change at
/// once, so code inside the update that reads the resources (the accent window tint reads the
/// theme's surfaces) gets the new state. A part setter outside an update notifies at once; a
/// part set to the one it already holds changes nothing. A lookup reads the parts from the last
/// to the first, as merged dictionaries added in that order were read. The parts get no owner
/// of their own; nothing may change them once they are set.
/// </summary>
public sealed class ResourceLayer : ResourceProvider
{
    private readonly IResourceProvider?[] _parts;
    private int _updating;
    private bool _changed;

    public ResourceLayer(int parts) => _parts = new IResourceProvider?[parts];

    public IResourceProvider? this[int index]
    {
        get => _parts[index];
        set
        {
            var old = _parts[index];
            if (ReferenceEquals(old, value)) return;
            _parts[index] = value;
            if (old?.HasResources == true || value?.HasResources == true) Changed();
        }
    }

    public override bool HasResources
    {
        get
        {
            foreach (var part in _parts)
                if (part?.HasResources == true) return true;
            return false;
        }
    }

    public override bool TryGetResource(object key, ThemeVariant? theme, out object? value)
    {
        for (var i = _parts.Length - 1; i >= 0; i--)
        {
            if (_parts[i] is { } part && part.TryGetResource(key, theme, out value))
                return true;
        }
        value = null;
        return false;
    }

    /// <summary>Holds the owner's notification until the matching <see cref="EndUpdate"/>.</summary>
    public void BeginUpdate() => _updating++;

    /// <summary>Ends an update; the outermost one tells the owner once if any part changed.</summary>
    public void EndUpdate()
    {
        if (_updating == 0 || --_updating > 0 || !_changed) return;
        _changed = false;
        Notify();
    }

    private void Changed()
    {
        if (_updating > 0)
            _changed = true;
        else
            Notify();
    }

    private void Notify() => Owner?.NotifyHostedResourcesChanged(ResourcesChangedEventArgs.Create());
}

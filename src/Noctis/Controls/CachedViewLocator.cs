using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Noctis.Controls;

/// <summary>
/// An IDataTemplate that caches previously-created views by ViewModel type.
/// When a ViewModel of the same type is shown again, the cached view instance
/// is reused instead of creating a new one. This prevents expensive visual tree
/// recreation for views with heavy content (virtualized ListBoxes, album grids).
///
/// Views that should NOT be cached (e.g., PlaylistViewModel where multiple instances
/// exist) should use a regular DataTemplate instead.
/// </summary>
public class CachedViewLocator : IDataTemplate
{
    private readonly Dictionary<Type, Control> _cache = new();
    private readonly Dictionary<Type, Func<Control>> _factories;

    public CachedViewLocator(Dictionary<Type, Func<Control>> factories)
    {
        _factories = factories;
    }

    public Control Build(object? data)
    {
        if (data == null)
            return new TextBlock { Text = "(null)" };

        var vmType = data.GetType();

        if (_cache.TryGetValue(vmType, out var cached))
            return cached;

        if (_factories.TryGetValue(vmType, out var factory))
        {
            var view = factory();
            _cache[vmType] = view;
            return view;
        }

        return new TextBlock { Text = $"No view for {vmType.Name}" };
    }

    public bool Match(object? data)
    {
        if (data == null) return false;
        return _factories.ContainsKey(data.GetType());
    }
}

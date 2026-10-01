using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Photo asks for the artist circles a list shows (the Artists grid, Search's artists): a circle
/// coming on screen asks for its artist's photo, one going off screen drops its ask (the source
/// stops a lookup nobody waits for), and a photo that arrives lands on the item, which repaints
/// its circle. Only names reach the source, and only for circles actually shown.
/// </summary>
internal sealed class ArtistPhotoRequests
{
    private readonly Func<IArtistPhotoSource?> _source;
    private readonly Dictionary<ArtistListItem, (CancellationTokenSource Cts, Task Task)> _asks = new(ReferenceEqualityComparer.Instance);

    /// <param name="source">The shell's photo source, read at each ask (the host sets it after
    /// the pages that use it are made).</param>
    public ArtistPhotoRequests(Func<IArtistPhotoSource?> source) => _source = source;

    /// <summary>Puts the photo the phone already has on <paramref name="item"/> (no network).</summary>
    public ArtistListItem Fill(ArtistListItem item)
    {
        item.PhotoPath ??= _source()?.CachedPhoto(item.Name);
        return item;
    }

    /// <summary>Asks for <paramref name="item"/>'s photo unless it has one or an ask is running;
    /// <paramref name="delay"/> waits first (a search result may be gone at the next keystroke).</summary>
    public void Request(ArtistListItem item, TimeSpan delay = default)
    {
        if (item.PhotoPath != null || _asks.ContainsKey(item) || _source() is not { } source) return;
        var cts = new CancellationTokenSource();
        var task = LoadAsync(source, item, delay, cts);
        // An answer already on hand finished the ask before it could be recorded.
        if (!task.IsCompleted) _asks[item] = (cts, task);
    }

    /// <summary>The circle left the screen: its ask stops.</summary>
    public void Cancel(ArtistListItem item)
    {
        if (_asks.Remove(item, out var ask)) ask.Cts.Cancel();
    }

    public void CancelAll()
    {
        foreach (var (cts, _) in _asks.Values) cts.Cancel();
        _asks.Clear();
    }

    /// <summary>The asks still running; tests await them.</summary>
    internal Task Pending => Task.WhenAll(_asks.Values.Select(a => a.Task));

    /// <summary>The items with an ask running (tests).</summary>
    internal IReadOnlyCollection<ArtistListItem> Asking => _asks.Keys;

    private async Task LoadAsync(IArtistPhotoSource source, ArtistListItem item, TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
            var path = await source.GetPhotoAsync(item.Name, cts.Token);
            if (path != null && !cts.IsCancellationRequested) item.PhotoPath = path;
        }
        catch (OperationCanceledException)
        {
            // Scrolled away, or a new search.
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Artist photo failed: {ex.Message}");
        }
        finally
        {
            if (_asks.TryGetValue(item, out var ask) && ReferenceEquals(ask.Cts, cts)) _asks.Remove(item);
        }
    }
}

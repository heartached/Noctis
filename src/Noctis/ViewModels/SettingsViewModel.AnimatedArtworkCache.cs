using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Artwork › Fetch animated artwork when opening an album (GitHub #167) and the
/// size of the cache those loops go into. Loaded and saved with the Built-in Features
/// settings, so Reset Everything restores them too.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>Look up the album's loop when its page opens (clear matches only).</summary>
    [ObservableProperty] private bool _animatedArtworkAutoFetch;

    /// <summary>The cache cap in MB, one of <see cref="AppSettings.AnimatedArtworkCacheSizeChoices"/>.</summary>
    [ObservableProperty] private int _animatedArtworkCacheMb = 1024;

    /// <summary>How much the cache holds now ("312.4 MB").</summary>
    [ObservableProperty] private string _animatedArtworkCacheUsage = string.Empty;

    /// <summary>One cache size in the picker.</summary>
    public sealed record CacheSizeOption(int Megabytes, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<CacheSizeOption> AnimatedArtworkCacheSizeOptions { get; } =
        AppSettings.AnimatedArtworkCacheSizeChoices
            .Select(mb => new CacheSizeOption(mb, mb < 1024 ? $"{mb} MB" : $"{mb / 1024} GB"))
            .ToList();

    public CacheSizeOption? SelectedAnimatedArtworkCacheSizeOption
    {
        get => AnimatedArtworkCacheSizeOptions.FirstOrDefault(o => o.Megabytes == AnimatedArtworkCacheMb);
        set
        {
            // A ComboBox nulls its selection while items are rebuilt; ignore that, keep the state.
            if (value is null) return;
            AnimatedArtworkCacheMb = value.Megabytes;
        }
    }

    /// <summary>The cap in bytes, as the fetcher wants it.</summary>
    internal long AnimatedArtworkCacheBytes => AnimatedArtworkCacheMb * 1024L * 1024L;

    private AnimatedArtworkCache? _animatedArtworkCache;

    /// <summary>The cache's files; the block list is not needed here.</summary>
    private AnimatedArtworkCache AnimatedArtworkCacheFiles => _animatedArtworkCache ??= new AnimatedArtworkCache(_persistence, null);

    partial void OnAnimatedArtworkAutoFetchChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnAnimatedArtworkCacheMbChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedAnimatedArtworkCacheSizeOption));
        if (!_settingsLoaded) return;
        _ = SaveAsync();
        // A smaller cap applies now, not at the next fetch.
        var cap = AnimatedArtworkCacheBytes;
        _ = RefreshAnimatedArtworkCacheUsageAsync(() => AnimatedArtworkCacheFiles.Trim(cap));
    }

    /// <summary>Settings › Artwork › Clear: deletes every fetched loop (saved ones stay).</summary>
    [RelayCommand]
    private Task ClearAnimatedArtworkCache()
        => RefreshAnimatedArtworkCacheUsageAsync(() => AnimatedArtworkCacheFiles.Clear());

    /// <summary>Re-reads the cache size off the UI thread, after <paramref name="first"/> if given.</summary>
    internal async Task RefreshAnimatedArtworkCacheUsageAsync(Action? first = null)
    {
        var files = AnimatedArtworkCacheFiles;
        var bytes = await Task.Run(() =>
        {
            first?.Invoke();
            return files.SizeBytes();
        });
        AnimatedArtworkCacheUsage = FormatBytes(bytes);
    }

    private void LoadAnimatedArtworkCacheSettings()
    {
        AnimatedArtworkAutoFetch = _settings.AnimatedArtworkAutoFetch;
        AnimatedArtworkCacheMb = AppSettings.SnapAnimatedArtworkCacheMb(_settings.AnimatedArtworkCacheMb);
        _ = RefreshAnimatedArtworkCacheUsageAsync();
    }

    private void SaveAnimatedArtworkCacheSettings()
    {
        _settings.AnimatedArtworkAutoFetch = AnimatedArtworkAutoFetch;
        _settings.AnimatedArtworkCacheMb = AppSettings.SnapAnimatedArtworkCacheMb(AnimatedArtworkCacheMb);
    }
}

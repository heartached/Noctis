using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
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

    /// <summary>How much the cache holds against its cap ("312.4 MB of 1 GB used").</summary>
    [ObservableProperty] private string _animatedArtworkCacheUsage = string.Empty;

    /// <summary>The same as a 0-100 fill for the usage bar.</summary>
    [ObservableProperty] private double _animatedArtworkCacheUsedPercent;

    /// <summary>Anything to clear: Clear Cache is greyed out on an empty cache.</summary>
    [ObservableProperty] private bool _hasAnimatedArtworkCache;

    private long _animatedArtworkCacheSizeBytes;

    /// <summary>GitHub #185: the loop quality fetched loops (on opening an album, Find Animated
    /// Artwork) are downloaded at, one of <see cref="AnimatedArtworkQualities.All"/>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AnimatedArtworkPrefersMax), nameof(SelectedAnimatedArtworkQualityOption))]
    private string _animatedArtworkQuality = AnimatedArtworkQualities.Default;

    /// <summary>True when fetched loops should be the 2160p ones (falling back to 1080p).</summary>
    public bool AnimatedArtworkPrefersMax => AnimatedArtworkQualities.PrefersMax(AnimatedArtworkQuality);

    /// <summary>One quality in the picker: the setting's value and what the list shows.</summary>
    public sealed record AnimatedArtworkQualityOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<AnimatedArtworkQualityOption> AnimatedArtworkQualityOptions { get; } = new[]
    {
        new AnimatedArtworkQualityOption(AnimatedArtworkQualities.P1080, AnimatedArtworkQualities.P1080),
        new AnimatedArtworkQualityOption(AnimatedArtworkQualities.P2160, Loc.T("Settings.AnimArtQuality2160")),
    };

    public AnimatedArtworkQualityOption? SelectedAnimatedArtworkQualityOption
    {
        get => AnimatedArtworkQualityOptions.FirstOrDefault(o => o.Value == AnimatedArtworkQualities.Normalize(AnimatedArtworkQuality));
        set
        {
            // A ComboBox nulls its selection while items are rebuilt; ignore that, keep the state.
            if (value is null) return;
            AnimatedArtworkQuality = value.Value;
        }
    }

    partial void OnAnimatedArtworkQualityChanged(string value)
    {
        if (_settingsLoaded) _ = SaveAsync();
    }

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
        _animatedArtworkCacheSizeBytes = bytes;
        UpdateAnimatedArtworkCacheUsageText();
    }

    private void UpdateAnimatedArtworkCacheUsageText()
    {
        var bytes = _animatedArtworkCacheSizeBytes;
        var cap = AnimatedArtworkCacheBytes;
        var capLabel = SelectedAnimatedArtworkCacheSizeOption?.Label ?? FormatBytes(cap);
        AnimatedArtworkCacheUsage = Loc.T("Settings.AnimArtCacheUsedOf", FormatBytes(bytes), capLabel);
        AnimatedArtworkCacheUsedPercent = cap > 0 ? Math.Min(100, bytes * 100.0 / cap) : 0;
        HasAnimatedArtworkCache = bytes > 0;
    }

    private void LoadAnimatedArtworkCacheSettings()
    {
        AnimatedArtworkAutoFetch = _settings.AnimatedArtworkAutoFetch;
        AnimatedArtworkCacheMb = AppSettings.SnapAnimatedArtworkCacheMb(_settings.AnimatedArtworkCacheMb);
        AnimatedArtworkQuality = AnimatedArtworkQualities.Normalize(_settings.AnimatedArtworkQuality);
        _ = RefreshAnimatedArtworkCacheUsageAsync();
    }

    private void SaveAnimatedArtworkCacheSettings()
    {
        _settings.AnimatedArtworkAutoFetch = AnimatedArtworkAutoFetch;
        _settings.AnimatedArtworkCacheMb = AppSettings.SnapAnimatedArtworkCacheMb(AnimatedArtworkCacheMb);
        _settings.AnimatedArtworkQuality = AnimatedArtworkQualities.Normalize(AnimatedArtworkQuality);
    }
}

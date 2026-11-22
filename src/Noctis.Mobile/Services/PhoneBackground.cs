using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Converters;
using Noctis.Models;
using Noctis.Services;
using Noctis.Localization;

namespace Noctis.Mobile.Services;

/// <summary>
/// Settings › Background, applied: the page background (Home, Library, pushed pages, the Settings
/// sheet) as a soft mesh instead of the flat theme colour. It puts one more resource dictionary
/// over the theme's, after it, that replaces the page brush with a <see cref="MeshBrush"/> and
/// makes the grouped and card surfaces translucent glass over it; "Off" (the default) removes the
/// dictionary and the theme shows as it always did. The mesh is built once; a new song's cover, the
/// Strength slider and Slow Motion only recolour or move parts of it (<see cref="MeshBrush"/>).
/// The choice, motion and strength are saved to settings.json (only those three fields).
/// Colour Wash (Discord #mobile-beta 11-22) paints Now Playing's own wash of the cover there
/// instead (<see cref="WashBrush"/>); without a cover the theme's page shows.
/// </summary>
public sealed class PhoneBackground : ObservableObject
{
    public const string Off = "Off";
    public const string Artwork = "Artwork";
    public const string ColourWash = "Wash";

    /// <summary>Strength's range: below 0.2 the mesh barely shows.</summary>
    public const double MinStrength = 0.2;
    public const double DefaultStrength = 0.6;

    /// <summary>The drift's frame rate: slow colours need no more, and each frame repaints every
    /// surface showing the mesh.</summary>
    internal static readonly TimeSpan MotionInterval = TimeSpan.FromMilliseconds(125);

    /// <summary>How many frames pass between asking whether Battery Saver came on.</summary>
    private const int SaverCheckEvery = 40;

    private readonly IPersistenceService? _persistence;
    private readonly IBatterySaver? _saver;
    private MeshBrush? _dark;
    private MeshBrush? _light;
    private WashBrush? _washDark;
    private WashBrush? _washLight;
    private ResourceDictionary? _layer;
    private DispatcherTimer? _timer;
    private readonly Stopwatch _clock = new();
    private double _driftBase;
    private int _frames;
    private bool _saverOn;
    private bool _loaded;
    // Which of the three the user set before the file was read: the load keeps those and reads the rest.
    private readonly HashSet<string> _setBeforeLoad = new();
    private Task _saveChain = Task.CompletedTask;

    public PhoneBackground(IPersistenceService? persistence = null, IBatterySaver? saver = null)
    {
        _persistence = persistence;
        _saver = saver;
    }

    private string _choice = Off;
    private bool _motion;
    private double _strength = DefaultStrength;
    private Color? _cover;
    private bool _suspended;

    /// <summary><see cref="Off"/>, <see cref="Artwork"/> or the id of a <see cref="BackgroundCatalog"/> choice.</summary>
    public string Choice
    {
        get => _choice;
        set
        {
            value = BackgroundCatalog.Normalize(value);
            if (_choice == value) return;
            _choice = value;
            Changed(nameof(Choice), nameof(Summary), nameof(IsActive), nameof(MotionRuns));
        }
    }

    public bool Motion
    {
        get => _motion;
        set
        {
            if (_motion == value) return;
            _motion = value;
            Changed(nameof(Motion), nameof(MotionRuns));
        }
    }

    public double Strength
    {
        get => _strength;
        set
        {
            value = Math.Clamp(value, MinStrength, 1);
            if (Math.Abs(_strength - value) < 0.0001) return;
            _strength = value;
            Changed(nameof(Strength));
        }
    }

    /// <summary>The playing cover's colour (the mini player's tint), for <see cref="Artwork"/>.</summary>
    public Color? Cover
    {
        get => _cover;
        set
        {
            if (_cover == value) return;
            _cover = value;
            if (Choice == Artwork) Refresh(save: false);
        }
    }

    private Bitmap? _coverArt;
    private int _washGeneration;

    /// <summary>The playing cover as the mini player decoded it (ShellView hands it over), for
    /// <see cref="ColourWash"/>; null with nothing playing or no cover.</summary>
    public Bitmap? CoverArt
    {
        get => _coverArt;
        set
        {
            if (ReferenceEquals(_coverArt, value)) return;
            _coverArt = value;
            _washFor = null;
            if (Choice == ColourWash) BuildWash(fade: true);
            else
            {
                ++_washGeneration;   // one still being made is for the old cover
                WashImage = null;    // made when Colour Wash (or its Settings tile) needs it
            }
        }
    }

    // The cover the last wash was started for: each cover is washed once, a failed one too.
    private Bitmap? _washFor;

    /// <summary>Makes the playing cover's wash if it is not made yet (the Settings tile, Colour Wash chosen).</summary>
    internal void EnsureWash()
    {
        if (_coverArt != null && !ReferenceEquals(_washFor, _coverArt)) BuildWash(fade: false);
    }

    private IImage? _washImage;
    private WashBrush.Extremes _washExtremes;

    /// <summary>The cover's wash (PreBlurredArtworkConverter's "wash", the one Now Playing paints),
    /// once made; the Settings tile shows it.</summary>
    public IImage? WashImage
    {
        get => _washImage;
        private set => SetProperty(ref _washImage, value);
    }

    /// <summary>The last wash on its way; tests await it.</summary>
    internal Task PendingWash { get; private set; } = Task.CompletedTask;

    private static readonly PreBlurredArtworkConverter WashConverter = new();

    /// <summary>
    /// The cover's wash and its extremes, made off the UI thread (BlurredCoverBackdrop's way: the
    /// cover is held while the pool reads it). The converter caches the wash per decoded cover, so a
    /// cover already washed is not washed again, and every page shares the one brush: no decode
    /// and no blur per page.
    /// </summary>
    private async void BuildWash(bool fade)
    {
        var generation = ++_washGeneration;
        _washFor = _coverArt;
        if (_coverArt is not { } source)
        {
            WashImage = null;
            if (Choice == ColourWash) Refresh(save: false);
            return;
        }
        ArtworkCache.Acquire(source);
        (IImage Wash, WashBrush.Extremes Extremes)? made = null;
        try
        {
            var build = Task.Run(() => WashConverter.Convert(source, typeof(IImage), PreBlurredArtworkConverter.WashParameter, CultureInfo.InvariantCulture) is IImage wash
                && WashBrush.Measure(wash) is { } extremes ? (wash, extremes) : ((IImage, WashBrush.Extremes)?)null);
            PendingWash = build;
            made = await build;
        }
        catch (Exception ex)
        {
            // No wash beats a crash: the theme's page shows.
            DebugLog.Write("Settings", $"Colour Wash failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ArtworkCache.Release(source);
        }
        if (generation != _washGeneration) return;   // a newer cover came in meanwhile
        _washExtremes = made?.Extremes ?? default;
        WashImage = made?.Wash;
        if (Choice != ColourWash) return;
        _fadeNext = fade;
        Refresh(save: false);
    }

    /// <summary>The app is in the background (Android's pause): the drift stops.</summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            UpdateTimer();
        }
    }

    public bool IsActive => Choice != Off;

    /// <summary>The layer is over the theme: a choice, and for Colour Wash a cover's wash to paint.</summary>
    private bool Paints => IsActive && (Choice != ColourWash || WashImage != null);

    /// <summary>Slow Motion is on, a mesh is showing, and Battery Saver is off (Colour Wash
    /// stands still: it is the cover's own colours, nothing drifts).</summary>
    public bool MotionRuns => IsActive && Choice != ColourWash && Motion && !BatterySaverOn;

    public bool BatterySaverOn => _saver?.IsOn ?? false;

    /// <summary>The current choice's name, as the Settings row shows it.</summary>
    public string Summary => Choice == Off ? Loc.T("Mobile.Off")
        : Choice == Artwork ? Loc.T("Mobile.NowPlaying")
        : Choice == ColourWash ? Loc.T("Mobile.NowPlayingBackground.Wash")
        : BackgroundCatalog.Find(Choice)?.Name ?? Loc.T("Mobile.Off");

    /// <summary>Raised after a change that Settings shows (choice, motion, strength).</summary>
    public event EventHandler? ChoiceChanged;

    private void Changed(string property, params string[] also)
    {
        OnPropertyChanged(property);
        foreach (var other in also) OnPropertyChanged(other);
        if (!_loaded) _setBeforeLoad.Add(property);
        // A slider drags through many values: the file is written once it settles.
        if (property == nameof(Strength)) SaveWhenSettled();
        Refresh(save: property != nameof(Strength));
        ChoiceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How long Strength rests before it is written (a drag is one save, not one per step).</summary>
    internal static readonly TimeSpan SaveSettleDelay = TimeSpan.FromMilliseconds(400);
    private int _settleGeneration;

    private void SaveWhenSettled()
    {
        var generation = ++_settleGeneration;
        PendingSave = Task.Run(async () =>
        {
            await Task.Delay(SaveSettleDelay);
            if (generation != Volatile.Read(ref _settleGeneration)) return;   // a later step saves
            if (_loaded) await SaveNow();
        });
    }

    // ── Settings file ──

    /// <summary>The saved values. A settings file without the fields reads as Off, motion off.</summary>
    public void ApplySettings(AppSettings settings)
    {
        if (_loaded) return;
        _loaded = true;
        // A value set while the load ran stays; the others come from the file (saving the defaults
        // for them put Motion off and Strength back to 0.6 when only the choice was picked).
        if (!_setBeforeLoad.Contains(nameof(Choice))) _choice = BackgroundCatalog.Normalize(settings.MobileBackground);
        if (!_setBeforeLoad.Contains(nameof(Motion))) _motion = settings.MobileBackgroundMotion;
        if (!_setBeforeLoad.Contains(nameof(Strength)))
            _strength = double.IsFinite(settings.MobileBackgroundStrength)
                ? Math.Clamp(settings.MobileBackgroundStrength, MinStrength, 1) : DefaultStrength;
        OnPropertyChanged(string.Empty);
        // The user's early change is saved now, beside the values just read.
        Refresh(save: _setBeforeLoad.Count > 0);
        ChoiceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAsync()
    {
        if (_persistence == null) { _loaded = true; return; }
        try
        {
            ApplySettings(await _persistence.LoadSettingsAsync());
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"Background load failed: {ex.Message}");
        }
        if (!_loaded)
        {
            // The file could not be read: keep what is showing, and save it.
            _loaded = true;
            Save();
        }
    }

    /// <summary>The tail of the saves; tests await it.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    private void Save() => _ = SaveNow();

    private Task SaveNow()
    {
        if (_persistence == null) return Task.CompletedTask;
        string choice = Choice;
        bool motion = Motion;
        double strength = Strength;
        PendingSave = _saveChain = _saveChain.ContinueWith(async _ =>
        {
            try
            {
                // Only our own three fields, in one update: another save landing in between survives.
                await _persistence.UpdateSettingsAsync(settings =>
                {
                    settings.MobileBackground = choice;
                    settings.MobileBackgroundMotion = motion;
                    settings.MobileBackgroundStrength = strength;
                });
            }
            catch (Exception ex)
            {
                DebugLog.Write("Settings", $"Background save failed: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
        return PendingSave;
    }

    // ── The mesh on the app ──

    /// <summary>The mesh as a light or dark page wears it; for the swatches and the tests.</summary>
    internal MeshBrush? MeshFor(bool light) => light ? _light : _dark;

    /// <summary>The Colour Wash as a light or dark page wears it; for the tests.</summary>
    internal WashBrush? WashFor(bool light) => light ? _washLight : _washDark;

    // The next wash painted is a new song's: it fades in over the last one.
    private bool _fadeNext;

    private void Refresh(bool save)
    {
        if (save && _loaded) Save();
        var fade = _fadeNext;
        _fadeNext = false;
        if (Choice == ColourWash) EnsureWash();
        if (!Paints)
        {
            StopFade();
            Detach();
            UpdateTimer();
            return;
        }
        if (Choice == ColourWash)
        {
            _washDark ??= new WashBrush(light: false);
            _washLight ??= new WashBrush(light: true);
            // Into the theme's page, or a Strength step: at once; a new cover: a fade.
            fade &= _attached && !BatterySaverOn;
            _washDark.Set(WashImage!, WashBrush.VeilFor(_washExtremes, false, Strength), fade);
            _washLight.Set(WashImage!, WashBrush.VeilFor(_washExtremes, true, Strength), fade);
            if (fade) StartFade();
            else StopFade();
            Attach(_washDark.Brush, _washLight.Brush);
        }
        else
        {
            StopFade();
            _dark ??= new MeshBrush();
            _light ??= new MeshBrush();
            var choice = BackgroundCatalog.Find(Choice);
            _dark.Set(choice != null ? BackgroundCatalog.For(choice, false) : BackgroundCatalog.ForCover(Cover, false), Strength);
            _light.Set(choice != null ? BackgroundCatalog.For(choice, true) : BackgroundCatalog.ForCover(Cover, true), Strength);
            Attach(_dark.Brush, _light.Brush);
        }
        UpdateTimer();
    }

    // ── Colour Wash's fade ──

    /// <summary>A new cover's wash fades in as long as Now Playing's Blurred Cover does
    /// (BlurredCoverBackdrop.FadeDuration), a few steps a second like Slow Motion's: the brush
    /// repaints every surface showing it, so it runs only for the fade, never at rest.</summary>
    internal static readonly TimeSpan FadeInterval = TimeSpan.FromMilliseconds(33);

    private DispatcherTimer? _fadeTimer;
    private readonly Stopwatch _fadeClock = new();

    internal bool FadeRunning => _fadeTimer != null;

    private void StartFade()
    {
        _fadeClock.Restart();
        if (_fadeTimer != null) return;
        _fadeTimer = new DispatcherTimer(FadeInterval, DispatcherPriority.Background, (_, _) => FadeTick());
        _fadeTimer.Start();
    }

    /// <summary>One step of the fade (the timer's; the tests call it).</summary>
    internal void FadeTick(double? progress = null)
    {
        var t = progress ?? _fadeClock.Elapsed.TotalMilliseconds / Mobile.Views.BlurredCoverBackdrop.FadeDuration.TotalMilliseconds;
        if (t >= 1) StopFade();
        else
        {
            _washDark?.Step(t);
            _washLight?.Step(t);
        }
    }

    private void StopFade()
    {
        var was = _fadeTimer != null;
        _fadeTimer?.Stop();
        _fadeTimer = null;
        _fadeClock.Stop();
        if (!was) return;
        _washDark?.Step(1);
        _washLight?.Step(1);
    }

    /// <summary>The dictionary over the theme's: the page brush is the mesh, the surfaces on it
    /// translucent. A sheet's own page (Settings, Account: PhoneSheetBackgroundBrush) is the mesh
    /// too, never the translucent grouped grey: that showed the app under the sheet through it.
    /// The dark glass is the theme's own grouped and card colours made translucent (a colour
    /// theme's cards are tinted glass on its mesh, not the neutral grey: beta feedback 11-04).
    /// Dark stays near black and Light near white under the washes (<see cref="BackgroundCatalog"/>),
    /// so the text on the glass keeps its contrast.</summary>
    private ResourceDictionary Layer(IBrush darkPage, IBrush lightPage)
    {
        if (_layer == null)
        {
            static SolidColorBrush B(string hex) => new(Color.Parse(hex));
            _layer = new ResourceDictionary();
            _layer.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary();
            _layer.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
            {
                ["PhoneGroupedBackgroundBrush"] = B("#99FFFFFF"),
                ["PhoneCardBrush"] = B("#CCFFFFFF"),
            };
        }
        var dark = (ResourceDictionary)_layer.ThemeDictionaries[ThemeVariant.Dark];
        // The page brush is the mesh's or the Colour Wash's; set again only when it changes.
        void Page(ResourceDictionary set, IBrush brush)
        {
            if (set.TryGetValue("PhoneBackgroundBrush", out var old) && ReferenceEquals(old, brush)) return;
            set["PhoneBackgroundBrush"] = brush;
            set["PhoneSheetBackgroundBrush"] = brush;
        }
        Page(dark, darkPage);
        Page((ResourceDictionary)_layer.ThemeDictionaries[ThemeVariant.Light], lightPage);
        var theme = PhoneThemes.DarkOrInk(_darkTheme);
        // Only a new theme replaces them: a Strength drag re-attaches the layer at every step.
        void Put(string key, Color c)
        {
            if (dark.TryGetValue(key, out var old) && old is ISolidColorBrush b && b.Color == c) return;
            dark[key] = new SolidColorBrush(c);
        }
        Put("PhoneGroupedBackgroundBrush", Glass(theme.Grouped, GroupedGlassAlpha));
        Put("PhoneCardBrush", Glass(theme.Card, CardGlassAlpha));
        return _layer;
    }

    /// <summary>The dark glass's alphas over the mesh: the grouped rows and the cards.</summary>
    internal const byte GroupedGlassAlpha = 0xB3, CardGlassAlpha = 0xE0;

    private static Color Glass(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    // The dark theme the glass is drawn from (Settings › Theme); Ink until the host says.
    private string? _darkTheme;

    /// <summary>The dark theme's surfaces as the glass on the mesh (the light ones are white).</summary>
    internal Color DarkGlass(bool card) => Glass(card ? PhoneThemes.DarkOrInk(_darkTheme).Card : PhoneThemes.DarkOrInk(_darkTheme).Grouped,
        card ? CardGlassAlpha : GroupedGlassAlpha);

    // The page brushes the layer last carried (Reassert puts the same ones back).
    private IBrush? _darkPage, _lightPage;
    private bool _attached;

    private void Attach(IBrush darkPage, IBrush lightPage)
    {
        _darkPage = darkPage;
        _lightPage = lightPage;
        if (Application.Current is not { } app) return;
        var layer = Layer(darkPage, lightPage);
        app.Resources.MergedDictionaries.Remove(layer);
        app.Resources.MergedDictionaries.Add(layer);   // last: it must win over the theme's palette
        _attached = true;
    }

    private void Detach()
    {
        _attached = false;
        if (_layer != null) Application.Current?.Resources.MergedDictionaries.Remove(_layer);
    }

    /// <summary>The theme was applied again (it rebuilds the palette): put the mesh back on top,
    /// its glass in <paramref name="darkTheme"/>'s colours (null keeps the last one).</summary>
    public void Reassert(string? darkTheme = null)
    {
        if (darkTheme != null) _darkTheme = darkTheme;
        if (Paints && _darkPage != null && _lightPage != null) Attach(_darkPage, _lightPage);
    }

    // ── Slow Motion ──

    private void UpdateTimer()
    {
        _saverOn = BatterySaverOn;
        var run = MotionRuns && !Suspended;
        if (run && _timer == null)
        {
            _frames = 0;
            _driftBase = 0;
            _clock.Restart();
            _timer = new DispatcherTimer(MotionInterval, DispatcherPriority.Background, (_, _) => Tick());
            _timer.Start();
        }
        else if (!run && _timer != null)
        {
            _timer.Stop();
            _timer = null;
            _clock.Stop();
            // Resting washes (a still background must not freeze mid-drift).
            _dark?.Rest();
            _light?.Rest();
        }
    }

    /// <summary>One frame of the drift (the timer's; the tests call it).</summary>
    internal void Tick()
    {
        if (++_frames % SaverCheckEvery == 0 && BatterySaverOn != _saverOn)
        {
            UpdateTimer();   // Battery Saver came on (or off): stop (or carry on)
            if (_timer == null) return;
        }
        var t = _driftBase + _clock.Elapsed.TotalSeconds;
        _dark?.Drift(t);
        _light?.Drift(t);
    }

    internal bool MotionTimerRunning => _timer != null;
}

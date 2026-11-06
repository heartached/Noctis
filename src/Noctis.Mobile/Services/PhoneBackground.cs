using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// </summary>
public sealed class PhoneBackground : ObservableObject
{
    public const string Off = "Off";
    public const string Artwork = "Artwork";

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

    /// <summary>Slow Motion is on, a background is showing, and Battery Saver is off.</summary>
    public bool MotionRuns => IsActive && Motion && !BatterySaverOn;

    public bool BatterySaverOn => _saver?.IsOn ?? false;

    /// <summary>The current choice's name, as the Settings row shows it.</summary>
    public string Summary => Choice == Off ? Loc.T("Mobile.Off")
        : Choice == Artwork ? Loc.T("Mobile.NowPlaying")
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

    private void Refresh(bool save)
    {
        if (save && _loaded) Save();
        if (!IsActive)
        {
            Detach();
            UpdateTimer();
            return;
        }
        _dark ??= new MeshBrush();
        _light ??= new MeshBrush();
        var choice = BackgroundCatalog.Find(Choice);
        _dark.Set(choice != null ? BackgroundCatalog.For(choice, false) : BackgroundCatalog.ForCover(Cover, false), Strength);
        _light.Set(choice != null ? BackgroundCatalog.For(choice, true) : BackgroundCatalog.ForCover(Cover, true), Strength);
        Attach();
        UpdateTimer();
    }

    /// <summary>The dictionary over the theme's: the page brush is the mesh, the surfaces on it
    /// translucent. A sheet's own page (Settings, Account: PhoneSheetBackgroundBrush) is the mesh
    /// too, never the translucent grouped grey: that showed the app under the sheet through it.
    /// The dark glass is the theme's own grouped and card colours made translucent (a colour
    /// theme's cards are tinted glass on its mesh, not the neutral grey: beta feedback 11-04).
    /// Dark stays near black and Light near white under the washes (<see cref="BackgroundCatalog"/>),
    /// so the text on the glass keeps its contrast.</summary>
    private ResourceDictionary Layer()
    {
        if (_layer == null)
        {
            static SolidColorBrush B(string hex) => new(Color.Parse(hex));
            _layer = new ResourceDictionary();
            _layer.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
            {
                ["PhoneBackgroundBrush"] = _dark!.Brush,
                ["PhoneSheetBackgroundBrush"] = _dark.Brush,
            };
            _layer.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
            {
                ["PhoneBackgroundBrush"] = _light!.Brush,
                ["PhoneSheetBackgroundBrush"] = _light.Brush,
                ["PhoneGroupedBackgroundBrush"] = B("#99FFFFFF"),
                ["PhoneCardBrush"] = B("#CCFFFFFF"),
            };
        }
        var dark = (ResourceDictionary)_layer.ThemeDictionaries[ThemeVariant.Dark];
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

    private void Attach()
    {
        if (Application.Current is not { } app) return;
        var layer = Layer();
        app.Resources.MergedDictionaries.Remove(layer);
        app.Resources.MergedDictionaries.Add(layer);   // last: it must win over the theme's palette
    }

    private void Detach()
    {
        if (_layer != null) Application.Current?.Resources.MergedDictionaries.Remove(_layer);
    }

    /// <summary>The theme was applied again (it rebuilds the palette): put the mesh back on top,
    /// its glass in <paramref name="darkTheme"/>'s colours (null keeps the last one).</summary>
    public void Reassert(string? darkTheme = null)
    {
        if (darkTheme != null) _darkTheme = darkTheme;
        if (IsActive) Attach();
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

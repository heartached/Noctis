using Noctis.Plugins.LyricMotion.Motion;

namespace Noctis.Plugins.LyricMotion;

/// <summary>
/// Lyric Motion, by Alfamin (MIT; https://github.com/Alfamin/LyricMotion): word-synced lyrics
/// move letter by letter with the singer. Noctis draws the words, the highlight and the glow; this
/// plugin only says how each letter sits (plugin API 1.5, <see cref="IPluginHost.RegisterLyricsEffect"/>)
/// and which lines read right to left. It never changes the lyrics, their timing or any file.
/// </summary>
public sealed class LyricMotionPlugin : INoctisPlugin
{
    public const string Id = "dev.noctis.plugins.lyricmotion";
    public const string Author = "Alfamin";
    public const string Version = "1.1.0";
    public const string About = "Lyrics that grow letter by letter as they are sung.";

    private IPluginHost? _host;
    private IDisposable? _registration;
    private MotionEffect? _effect;

    public PluginInfo Info { get; } = new(Id, "Lyric Motion", Version, Author, About);

    /// <summary>Tests: the effect in use.</summary>
    public MotionEffect? Effect => _effect;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.Settings.Changed += OnSettingsChanged;
        Register();
    }

    /// <summary>
    /// (Re)registers the effect with the current settings. A new registration makes Noctis lay the
    /// words out again and ask the line directions again, so a change applies to the line being
    /// sung too.
    /// </summary>
    private void Register()
    {
        if (_host is not { } host) return;
        _registration?.Dispose();
        _effect = new MotionEffect(Tuning.From(host.Settings));
        _registration = host.RegisterLyricsEffect(_effect);
    }

    private void OnSettingsChanged(object? sender, string key) => Register();

    public void Shutdown()
    {
        if (_host is { } host) host.Settings.Changed -= OnSettingsChanged;
        _registration?.Dispose();
        _registration = null;
        _effect = null;
        _host = null;
    }
}

/// <summary>The effect Noctis calls while it draws a word: pure, allocation-free (see <see cref="LetterMotion"/>).</summary>
public sealed class MotionEffect : ILyricsEffect
{
    public MotionEffect(Tuning tuning) => Tuning = tuning;

    public Tuning Tuning { get; }

    public string Name => "Lyric Motion";

    public void GetLineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft)
    {
        if (Tuning.RightToLeft) Script.Directions(lines, rightToLeft);
    }

    public LyricsWordMode GetMode(in LyricsWord word) => LetterMotion.Mode(Tuning, word);

    public LyricsLetterPose GetPose(in LyricsWord word, int letter) => LetterMotion.Pose(Tuning, word, letter);
}

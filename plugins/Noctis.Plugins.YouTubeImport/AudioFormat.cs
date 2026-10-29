namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// A format/quality choice in the window. M4A is what "Add from YouTube" always produced before
/// it became a plugin; MP3 and Opus are converted by ffmpeg (bundled with Noctis), so they are
/// only offered when ffmpeg is found.
/// </summary>
/// <param name="Key">Stored in the plugin's state file.</param>
/// <param name="Label">Shown in the format picker.</param>
/// <param name="Selector">yt-dlp's <c>-f</c> format selector.</param>
/// <param name="Codec">yt-dlp's <c>--audio-format</c> (with ffmpeg).</param>
/// <param name="Quality">yt-dlp's <c>--audio-quality</c> (0 = best VBR, or a bitrate like 320K).</param>
/// <param name="NeedsFfmpeg">True when the format only exists after a conversion.</param>
public sealed record AudioFormat(string Key, string Label, string Selector, string Codec, string Quality, bool NeedsFfmpeg)
{
    public static readonly AudioFormat M4a = new("m4a", "M4A (AAC), best quality", "bestaudio[ext=m4a]/bestaudio/best", "m4a", "0", false);
    public static readonly AudioFormat Mp3High = new("mp3-320", "MP3, 320 kbps", "bestaudio/best", "mp3", "320K", true);
    public static readonly AudioFormat Mp3Standard = new("mp3-192", "MP3, 192 kbps", "bestaudio/best", "mp3", "192K", true);
    public static readonly AudioFormat Opus = new("opus", "Opus, best quality", "bestaudio[acodec=opus]/bestaudio/best", "opus", "0", true);

    public static AudioFormat Default => M4a;

    public static IReadOnlyList<AudioFormat> All { get; } = new[] { M4a, Mp3High, Mp3Standard, Opus };

    /// <summary>The format stored under <paramref name="key"/>, or the default.</summary>
    public static AudioFormat FromKey(string? key) =>
        All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public override string ToString() => Label;
}

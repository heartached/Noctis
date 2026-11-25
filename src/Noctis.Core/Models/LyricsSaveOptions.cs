namespace Noctis.Models;

/// <summary>Where the app puts lyrics it saves (GitHub #135).</summary>
public enum LyricsSaveTarget
{
    /// <summary>Lyrics files beside the song (.lrc, .elrc for word timings, .ttml on request): the behaviour before #135.</summary>
    Sidecar,

    /// <summary>Inside the audio file's tags only; no lyrics file is written.</summary>
    Embedded,

    /// <summary>Lyrics files beside the song and the audio file's tags.</summary>
    Both,
}

/// <summary>Which form of online lyrics an automatic pick keeps (GitHub #135).</summary>
public enum LyricsPreferredType
{
    /// <summary>The richest the sources have: word-synced, then line-synced, then plain (the pick before #135).</summary>
    WordSynced,

    /// <summary>Line timings only: word timings are dropped from what is kept.</summary>
    LineSynced,

    /// <summary>Plain text only, no timings.</summary>
    Plain,
}

/// <summary>The lyrics files Lyrics Studio saves for word-timed lyrics (owner 11-24: "choose LRC or ELRC
/// instead of saving both").</summary>
public enum LyricsStudioFiles
{
    /// <summary>The .elrc with the word timings and a line-timed .lrc beside it, for players that only read LRC
    /// (the behaviour before the choice).</summary>
    ElrcAndLrc,

    /// <summary>Only the .elrc; an old .lrc beside the song is removed.</summary>
    ElrcOnly,

    /// <summary>Only a line-timed .lrc (the word timings are not saved); an old .elrc is removed.</summary>
    LrcOnly,
}

/// <summary>Parsing for the string-valued lyrics save settings in <see cref="AppSettings"/>.</summary>
public static class LyricsSaveOptions
{
    /// <summary>The stored setting as a target; anything unknown is the default, <see cref="LyricsSaveTarget.Sidecar"/>.</summary>
    public static LyricsSaveTarget ParseTarget(string? value) =>
        Enum.TryParse<LyricsSaveTarget>(value, ignoreCase: true, out var target) && Enum.IsDefined(target)
            ? target : LyricsSaveTarget.Sidecar;

    /// <summary>The stored setting as a type; anything unknown is the default, <see cref="LyricsPreferredType.WordSynced"/>.</summary>
    public static LyricsPreferredType ParsePreferredType(string? value) =>
        Enum.TryParse<LyricsPreferredType>(value, ignoreCase: true, out var type) && Enum.IsDefined(type)
            ? type : LyricsPreferredType.WordSynced;

    /// <summary>The stored setting as a choice; anything unknown is the default, <see cref="LyricsStudioFiles.ElrcAndLrc"/>.</summary>
    public static LyricsStudioFiles ParseStudioFiles(string? value) =>
        Enum.TryParse<LyricsStudioFiles>(value, ignoreCase: true, out var files) && Enum.IsDefined(files)
            ? files : LyricsStudioFiles.ElrcAndLrc;

    /// <summary>Lyrics files are written for this target.</summary>
    public static bool WritesSidecars(this LyricsSaveTarget target) => target != LyricsSaveTarget.Embedded;

    /// <summary>The audio file's tags are written for this target.</summary>
    public static bool WritesTags(this LyricsSaveTarget target) => target != LyricsSaveTarget.Sidecar;
}

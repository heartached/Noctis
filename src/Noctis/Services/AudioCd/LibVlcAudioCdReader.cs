using LibVLCSharp.Shared;
using Noctis.Controls;

namespace Noctis.Services.AudioCd;

/// <summary>
/// Reads a disc's table of contents through libvlc's cdda access: parsing
/// <c>cdda:///D:/</c> yields one sub-item per track with its length and, when the
/// disc carries CD-Text (or libvlc's CDDB lookup succeeds), title/artist/album.
/// Uses the shared parse-only LibVLC instance (no audio output), never the player's.
/// </summary>
public sealed class LibVlcAudioCdReader : IAudioCdReader
{
    private const int ParseTimeoutMs = 20000; // a cold drive can take several seconds to spin up

    public async Task<AudioCdDisc?> ReadAsync(string driveRoot, string mrl, CancellationToken ct = default)
    {
        Media? media = null;
        try
        {
            media = new Media(SharedLibVlc.Instance, mrl, FromType.FromLocation);
            var status = await media.Parse(MediaParseOptions.ParseLocal, timeout: ParseTimeoutMs, cancellationToken: ct);
            if (status != MediaParsedStatus.Done)
            {
                DebugLogger.Info(DebugLogger.Category.Playback, "AudioCd.Parse", $"status={status}");
                return null;
            }

            using var subItems = media.SubItems;
            var count = subItems.Count;
            if (count == 0) return null; // data disc, empty tray, or not an audio CD

            var tracks = new List<AudioCdTrackInfo>(count);
            for (var i = 0; i < count; i++)
            {
                using var item = subItems[i];
                tracks.Add(new AudioCdTrackInfo(
                    Number: i + 1,
                    Title: Clean(item.Meta(MetadataType.Title)),
                    Artist: Clean(item.Meta(MetadataType.Artist)),
                    Album: Clean(item.Meta(MetadataType.Album)),
                    Duration: TimeSpan.FromMilliseconds(Math.Max(0, item.Duration)),
                    Mrl: item.Mrl));
            }

            // The sub-item MRL form is the one thing that differs between libvlc builds;
            // log it once so a "plays the wrong track" report can be read straight off the log.
            DebugLogger.Info(DebugLogger.Category.Playback, "AudioCd.Parse",
                $"tracks={count}, firstMrl={tracks[0].Mrl}, cdText={tracks.Count(t => t.Title != null)}");

            return new AudioCdDisc(driveRoot, mrl, tracks,
                Clean(media.Meta(MetadataType.Album)) ?? Clean(media.Meta(MetadataType.Title)),
                Clean(media.Meta(MetadataType.Artist)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "AudioCd.Parse", ex.Message);
            return null;
        }
        finally
        {
            media?.Dispose();
        }
    }

    /// <summary>
    /// libvlc names untitled tracks "Audio CD - Track NN" / "Track NN"; those are not
    /// CD-Text and must not masquerade as it, so the service can apply its own fallback.
    /// </summary>
    internal static string? Clean(string? meta)
    {
        if (string.IsNullOrWhiteSpace(meta)) return null;
        var t = meta.Trim();
        if (t.StartsWith("Audio CD", StringComparison.OrdinalIgnoreCase) && t.Contains("Track", StringComparison.OrdinalIgnoreCase))
            return null;
        if (t.StartsWith("Track ", StringComparison.OrdinalIgnoreCase) && t.Length <= 9 && t[6..].All(char.IsDigit))
            return null;
        return t;
    }
}

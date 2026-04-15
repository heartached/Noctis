using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// TagLib#-based metadata reader. Extracts ID3v2, Vorbis, FLAC, and other tag formats.
/// </summary>
public class MetadataService : IMetadataService
{
    private static readonly Lazy<bool> FfprobeAvailable = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return false;

            if (!proc.WaitForExit(1500))
            {
                try { proc.Kill(true); } catch { }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    /// Audio file extensions we consider valid.
    /// Includes lossless (FLAC, ALAC, AIFF, WAV, APE, WavPack),
    /// lossy (MP3, AAC, OGG, Opus, WMA), and container formats (M4A).
    /// .m4a can contain either AAC (lossy) or ALAC (lossless) — both are supported.
    /// </summary>
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".m4a", ".wav", ".wma", ".aac",
        ".opus", ".aiff", ".aif", ".aifc", ".ape", ".wv", ".alac", ".mp4"
    };

    public Track? ReadTrackMetadata(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        // Reject paths with invalid characters
        if (filePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;

        // Skip very large files that are unlikely to be audio (>2GB)
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Length > 2L * 1024 * 1024 * 1024)
                return null;
        }
        catch
        {
            return null;
        }

        try
        {
            using var file = TagLib.File.Create(filePath);

            var tag = file.Tag;
            var props = file.Properties;
            var fileInfo = new FileInfo(filePath);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            var codec = DetermineCodec(file, ext);
            var sampleRate = NormalizeSampleRate(props.AudioSampleRate);
            var bitsPerSample = NormalizeBitsPerSample(props.BitsPerSample, file, ext, sampleRate, codec);
            var bitrate = NormalizeBitrate(props.AudioBitrate, fileInfo.Length, props.Duration);

            // ALAC fallback: TagLib# often returns 0 for sample rate on M4A/ALAC files.
            // Read it directly from the ALAC decoder config atom.
            if (sampleRate < 8000 && ext is ".m4a" or ".mp4" or ".alac")
            {
                var codecLower = (codec ?? "").ToLowerInvariant();
                if (codecLower.Contains("alac") || codecLower.Contains("lossless"))
                {
                    var (_, sr) = TryReadAlacConfigFromFile(filePath);
                    if (sr >= 8000) sampleRate = sr;
                }
            }

            if (NeedsFfprobeFallback(sampleRate, bitsPerSample, bitrate))
                TryPopulateAudioMetricsWithFfprobe(filePath, ref sampleRate, ref bitsPerSample, ref bitrate);

            // Preserve all credited performers so featured artists show in track rows.
            var artist = FirstNonEmpty(JoinTagValues(tag.Performers), tag.FirstPerformer, tag.FirstAlbumArtist, "Unknown Artist");
            var albumArtist = FirstNonEmpty(JoinTagValues(tag.AlbumArtists), tag.FirstAlbumArtist, tag.FirstPerformer, "Unknown Artist");
            var album = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown Album" : tag.Album;
            var title = string.IsNullOrWhiteSpace(tag.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : tag.Title;

            // Merge featured artists from title into artist field when Performers tag is incomplete
            artist = EnrichArtistFromTitle(artist, title);

            var track = new Track
            {
                FilePath = filePath,
                Title = title,
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = album,
                Genre = tag.FirstGenre ?? string.Empty,
                TrackNumber = (int)tag.Track,
                TrackCount = (int)tag.TrackCount,
                DiscNumber = tag.Disc > 0 ? (int)tag.Disc : 1,
                DiscCount = tag.DiscCount > 0 ? (int)tag.DiscCount : 1,
                Bpm = (int)tag.BeatsPerMinute,
                Year = (int)tag.Year,
                Duration = props.Duration,
                AlbumId = Track.ComputeAlbumId(albumArtist, album),
                FileSize = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                DateAdded = DateTime.UtcNow,
                IsExplicit = DetectExplicit(file),
                Composer = tag.FirstComposer ?? string.Empty,
                Lyrics = tag.Lyrics ?? string.Empty,
                Comment = tag.Comment ?? string.Empty,
                Copyright = ReadCopyright(file),
                ReleaseDate = ReadReleaseDate(file, tag),
                IsCompilation = ExtendedTagIO.ReadIsCompilation(file),
                Grouping = tag.Grouping ?? string.Empty,
                ShowComposerInAllViews = ExtendedTagIO.ReadShowComposer(file),
                UseWorkAndMovement = ExtendedTagIO.ReadUseWorkAndMovement(file),
                WorkName = ExtendedTagIO.ReadWorkName(file),
                MovementName = ExtendedTagIO.ReadMovementName(file),
                MovementNumber = ExtendedTagIO.ReadMovementNumber(file),
                MovementCount = ExtendedTagIO.ReadMovementCount(file),
                // Audio quality properties
                Bitrate = bitrate,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                Codec = codec ?? string.Empty
            };

            return track;
        }
        catch (Exception)
        {
            // File is corrupted, unsupported, or locked — skip it
            return null;
        }
    }

    public byte[]? ExtractAlbumArt(string filePath)
    {
        // 1. Try embedded artwork first (most reliable).
        // Prefer FrontCover if present; within each bucket pick largest payload.
        try
        {
            using var file = TagLib.File.Create(filePath);
            var bestEmbedded = SelectBestEmbeddedPicture(file.Tag.Pictures);
            if (bestEmbedded != null)
                return bestEmbedded;
        }
        catch
        {
            // Fall through to folder-level search
        }

        // 2. Fall back to artwork files in the same directory.
        // Keep preferred names order, but choose the largest file among matches.
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir == null) return null;

            string[] artworkNames = { "cover", "folder", "album", "front", "art", "artwork" };
            string[] imageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

            var candidates = new List<FileInfo>();
            foreach (var name in artworkNames)
            {
                foreach (var ext in imageExtensions)
                {
                    var artPath = Path.Combine(dir, name + ext);
                    if (File.Exists(artPath))
                        candidates.Add(new FileInfo(artPath));
                }
            }

            var bestFile = candidates
                .OrderByDescending(f => f.Length)
                .FirstOrDefault();

            if (bestFile != null && bestFile.Exists)
                return File.ReadAllBytes(bestFile.FullName);
        }
        catch
        {
            // Non-critical — return null
        }

        return null;
    }

    private static byte[]? SelectBestEmbeddedPicture(TagLib.IPicture[]? pictures)
    {
        if (pictures == null || pictures.Length == 0)
            return null;

        TagLib.IPicture? bestFrontCover = null;
        TagLib.IPicture? bestAny = null;
        var bestFrontCoverBytes = -1;
        var bestAnyBytes = -1;

        foreach (var picture in pictures)
        {
            var bytes = picture?.Data?.Data;
            if (bytes == null || bytes.Length == 0)
                continue;

            if (bytes.Length > bestAnyBytes)
            {
                bestAny = picture;
                bestAnyBytes = bytes.Length;
            }

            if (picture!.Type == TagLib.PictureType.FrontCover && bytes.Length > bestFrontCoverBytes)
            {
                bestFrontCover = picture;
                bestFrontCoverBytes = bytes.Length;
            }
        }

        return bestFrontCover?.Data?.Data ?? bestAny?.Data?.Data;
    }

    public bool WriteTrackMetadata(Track track)
    {
        try
        {
            using var file = TagLib.File.Create(track.FilePath);
            var tag = file.Tag;

            tag.Title = track.Title;
            tag.Performers = SplitArtistList(track.Artist);
            tag.AlbumArtists = SplitArtistList(track.AlbumArtist);
            tag.Album = track.Album;
            tag.Genres = string.IsNullOrWhiteSpace(track.Genre) ? Array.Empty<string>() : new[] { track.Genre };
            tag.Track = (uint)Math.Max(0, track.TrackNumber);
            tag.TrackCount = (uint)Math.Max(0, track.TrackCount);
            tag.Disc = (uint)Math.Max(0, track.DiscNumber);
            tag.DiscCount = (uint)Math.Max(0, track.DiscCount);
            tag.BeatsPerMinute = (uint)Math.Max(0, track.Bpm);
            tag.Year = (uint)Math.Max(0, track.Year);
            tag.Composers = string.IsNullOrWhiteSpace(track.Composer) ? Array.Empty<string>() : new[] { track.Composer };
            tag.Lyrics = string.IsNullOrWhiteSpace(track.Lyrics) ? null : track.Lyrics;
            tag.Comment = string.IsNullOrWhiteSpace(track.Comment) ? null : track.Comment;
            tag.Grouping = string.IsNullOrWhiteSpace(track.Grouping) ? null : track.Grouping;
            tag.Copyright = string.IsNullOrWhiteSpace(track.Copyright) ? null : track.Copyright;

            ExtendedTagIO.WriteIsCompilation(file, track.IsCompilation);
            ExtendedTagIO.WriteShowComposer(file, track.ShowComposerInAllViews);
            ExtendedTagIO.WriteUseWorkAndMovement(file, track.UseWorkAndMovement);
            ExtendedTagIO.WriteWorkName(file, track.WorkName);
            ExtendedTagIO.WriteMovementName(file, track.MovementName);
            ExtendedTagIO.WriteMovementNumber(file, track.MovementNumber);
            ExtendedTagIO.WriteMovementCount(file, track.MovementCount);

            file.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool WriteAlbumArt(string filePath, byte[]? imageData)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            if (imageData == null || imageData.Length == 0)
            {
                file.Tag.Pictures = Array.Empty<TagLib.IPicture>();
            }
            else
            {
                // Detect MIME type from image header bytes
                var mimeType = "image/jpeg";
                if (imageData.Length >= 4 &&
                    imageData[0] == 0x89 && imageData[1] == 0x50 &&
                    imageData[2] == 0x4E && imageData[3] == 0x47)
                {
                    mimeType = "image/png";
                }

                var pic = new TagLib.Picture(new TagLib.ByteVector(imageData))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mimeType
                };
                file.Tag.Pictures = new TagLib.IPicture[] { pic };
            }
            file.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public AudioFileInfo? ReadFileInfo(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var props = file.Properties;
            var fileInfo = new FileInfo(filePath);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            var codec = DetermineCodec(file, ext);
            var sampleRate = NormalizeSampleRate(props.AudioSampleRate);
            var bitsPerSample = NormalizeBitsPerSample(props.BitsPerSample, file, ext, sampleRate, codec);
            var isLossless = IsLosslessFormat(codec, ext);
            var bitrate = NormalizeBitrate(props.AudioBitrate, fileInfo.Length, props.Duration);

            // ALAC fallback: TagLib# often returns 0 for sample rate on M4A/ALAC files.
            if (sampleRate < 8000 && ext is ".m4a" or ".mp4" or ".alac")
            {
                var codecLower = (codec ?? "").ToLowerInvariant();
                if (codecLower.Contains("alac") || codecLower.Contains("lossless"))
                {
                    var (_, sr) = TryReadAlacConfigFromFile(filePath);
                    if (sr >= 8000) sampleRate = sr;
                }
            }

            if (NeedsFfprobeFallback(sampleRate, bitsPerSample, bitrate))
                TryPopulateAudioMetricsWithFfprobe(filePath, ref sampleRate, ref bitsPerSample, ref bitrate);

            return new AudioFileInfo
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                FileFormat = GetFileFormat(ext),
                Codec = codec ?? string.Empty,
                IsLossless = isLossless,
                Bitrate = bitrate,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                Channels = props.AudioChannels,
                ChannelDescription = props.AudioChannels switch
                {
                    1 => "Mono",
                    2 => "Stereo",
                    6 => "5.1 Surround",
                    8 => "7.1 Surround",
                    _ => $"{props.AudioChannels} channels"
                },
                Duration = props.Duration,
                FileSize = fileInfo.Length,
                DateModified = fileInfo.LastWriteTime,
                DateAdded = fileInfo.CreationTime
            };
        }
        catch
        {
            return null;
        }
    }

    private static int NormalizeSampleRate(int sampleRate)
    {
        if (sampleRate <= 0)
            return 0;

        // Already in Hz (normal case).
        if (sampleRate >= 8000)
            return sampleRate;

        // Some containers report kHz-like integers (e.g., 96 instead of 96000).
        return sampleRate switch
        {
            8 => 8000,
            11 => 11025,
            12 => 12000,
            16 => 16000,
            22 => 22050,
            24 => 24000,
            32 => 32000,
            44 => 44100,
            48 => 48000,
            64 => 64000,
            88 => 88200,
            96 => 96000,
            176 => 176400,
            192 => 192000,
            352 => 352800,
            384 => 384000,
            // Placeholder values (e.g., 1 kHz) should be treated as unknown.
            _ => 0
        };
    }

    private static int NormalizeBitsPerSample(int bits, TagLib.File file, string ext, int sampleRate, string codec)
    {
        if (bits > 0)
            return bits;

        // Try to read from codec details if available.
        foreach (var codecInfo in file.Properties.Codecs)
        {
            if (codecInfo is TagLib.IAudioCodec audio && audio.Description != null)
            {
                var desc = audio.Description.ToLowerInvariant();
                if (desc.Contains("24"))
                    return 24;
                if (desc.Contains("16"))
                    return 16;
            }
        }

        // TagLib# doesn't expose BitsPerSample for ALAC in M4A containers
        // (the MPEG4 codec class doesn't implement ILosslessAudioCodec).
        // Read the bit depth directly from the ALAC decoder config atom.
        if (ext is ".m4a" or ".mp4" or ".alac")
        {
            var codecLower = (codec ?? "").ToLowerInvariant();
            if (codecLower.Contains("alac") || codecLower.Contains("lossless"))
            {
                var (bd, _) = TryReadAlacConfigFromFile(file.Name);
                if (bd > 0) return bd;
            }
        }

        // Do NOT guess bit depth from sample rate — a 48 kHz FLAC/ALAC file can
        // be 16-bit or 24-bit. Guessing would mislabel files and corrupt
        // Hi-Res Lossless badges. Leave as 0 and let ffprobe fallback handle it.

        return 0;
    }

    /// <summary>
    /// Reads ALAC bit depth and sample rate directly from the M4A/MP4 container
    /// by scanning for the inner 'alac' decoder config box.
    /// Layout after the box type:
    /// [version+flags 4B][frameLength 4B][compatVersion 1B][bitDepth 1B]
    /// [tuningParams 3B][numChannels 1B][maxRun 2B][maxFrameBytes 4B]
    /// [avgBitRate 4B][sampleRate 4B]
    /// </summary>
    private static (int BitDepth, int SampleRate) TryReadAlacConfigFromFile(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var scanSize = (int)Math.Min(fs.Length, 1024 * 1024);
            var data = new byte[scanSize];
            int bytesRead = fs.Read(data, 0, scanSize);

            for (int i = 4; i < bytesRead - 32; i++)
            {
                if (data[i] != 'a' || data[i + 1] != 'l' || data[i + 2] != 'a' || data[i + 3] != 'c')
                    continue;

                // Box size is the 4 bytes before the type (big-endian).
                int boxSize = (data[i - 4] << 24) | (data[i - 3] << 16) | (data[i - 2] << 8) | data[i - 1];

                // Inner ALAC config box is small (typically 36 bytes).
                // Outer ALAC sample entry is much larger (300+ bytes). Skip it.
                if (boxSize < 28 || boxSize > 64)
                    continue;

                int configStart = i + 4; // past 'alac' type bytes

                // bitDepth at config offset 9: version(4) + frameLength(4) + compatVersion(1)
                int bdOffset = configStart + 9;
                // sampleRate at config offset 24: +tuning(3) +channels(1) +maxRun(2) +maxFrameBytes(4) +avgBitRate(4)
                int srOffset = configStart + 24;

                if (srOffset + 3 >= bytesRead)
                    continue;

                int bd = data[bdOffset];
                int sr = (data[srOffset] << 24) | (data[srOffset + 1] << 16) |
                         (data[srOffset + 2] << 8) | data[srOffset + 3];

                if (bd is 16 or 24 or 32)
                    return (bd, sr >= 8000 ? sr : 0);
            }
        }
        catch
        {
            // Best-effort fallback only; return zeros if anything goes wrong.
        }

        return (0, 0);
    }

    private static bool IsLikelyLossyCodec(string codec, string ext)
    {
        var codecLower = (codec ?? string.Empty).ToLowerInvariant();
        if (codecLower.Contains("mp3") || codecLower.Contains("mpeg") ||
            codecLower.Contains("aac") || codecLower.Contains("vorbis") ||
            codecLower.Contains("opus") || codecLower.Contains("wma") ||
            codecLower.Contains("mp4a"))
            return true;

        // Don't classify .m4a/.mp4 as lossy by extension — these containers
        // can hold ALAC (lossless). The codec string check above already
        // catches AAC. Only classify unambiguously lossy extensions.
        return ext is ".mp3" or ".aac" or ".ogg" or ".opus" or ".wma";
    }

    private static bool IsLikelyLosslessCodec(string codec, string ext)
    {
        var codecLower = (codec ?? string.Empty).ToLowerInvariant();
        if (codecLower.Contains("flac") || codecLower.Contains("alac") ||
            codecLower.Contains("lossless") || codecLower.Contains("pcm") ||
            codecLower.Contains("wavpack") || codecLower.Contains("monkey"))
            return true;

        return ext is ".flac" or ".alac" or ".wav" or ".aiff" or ".aif" or ".aifc" or ".ape" or ".wv";
    }

    private static int NormalizeBitrate(int bitrate, long fileSize, TimeSpan duration)
    {
        if (bitrate > 0)
            return bitrate;

        if (fileSize <= 0 || duration.TotalSeconds <= 0)
            return 0;

        var estimated = (int)Math.Round((fileSize * 8d) / duration.TotalSeconds / 1000d);
        return estimated > 0 ? estimated : 0;
    }

    private static bool NeedsFfprobeFallback(int sampleRate, int bitsPerSample, int bitrate)
    {
        return sampleRate < 8000 || bitsPerSample <= 0 || bitrate <= 0;
    }

    private static void TryPopulateAudioMetricsWithFfprobe(string filePath, ref int sampleRate, ref int bitsPerSample, ref int bitrate)
    {
        if (!FfprobeAvailable.Value)
            return;

        try
        {
            var escapedPath = filePath.Replace("\"", "\\\"");
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -select_streams a:0 -show_entries stream=sample_rate,bits_per_sample,bits_per_raw_sample,bit_rate:format=bit_rate -of json \"{escapedPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return;

            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2500))
            {
                try { proc.Kill(true); } catch { }
                return;
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (root.TryGetProperty("streams", out var streams) &&
                streams.ValueKind == JsonValueKind.Array &&
                streams.GetArrayLength() > 0)
            {
                var stream = streams[0];
                if (sampleRate < 8000)
                {
                    var streamRate = ParseJsonInt(stream, "sample_rate");
                    var normalizedStreamRate = NormalizeSampleRate(streamRate);
                    if (normalizedStreamRate >= 8000)
                        sampleRate = normalizedStreamRate;
                }

                if (bitsPerSample <= 0)
                {
                    var rawBits = ParseJsonInt(stream, "bits_per_raw_sample");
                    var bits = rawBits > 0 ? rawBits : ParseJsonInt(stream, "bits_per_sample");
                    if (bits > 0)
                        bitsPerSample = bits;
                }

                if (bitrate <= 0)
                {
                    var streamBitrate = ParseJsonInt(stream, "bit_rate");
                    if (streamBitrate > 0)
                        bitrate = (int)Math.Round(streamBitrate / 1000d);
                }
            }

            if (bitrate <= 0 &&
                root.TryGetProperty("format", out var format))
            {
                var formatBitrate = ParseJsonInt(format, "bit_rate");
                if (formatBitrate > 0)
                    bitrate = (int)Math.Round(formatBitrate / 1000d);
            }
        }
        catch
        {
            // Optional fallback only; keep existing values on failure.
        }
    }

    private static int ParseJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0
        };
    }

    private static string DetermineCodec(TagLib.File file, string ext)
    {
        // Try to get codec from TagLib properties
        foreach (var codec in file.Properties.Codecs)
        {
            if (codec is TagLib.IAudioCodec audioCodec)
            {
                var desc = audioCodec.Description;
                if (!string.IsNullOrWhiteSpace(desc))
                    return desc;
            }
        }

        // Fallback: determine from file extension
        return ext switch
        {
            ".mp3" => "MPEG Audio Layer 3",
            ".flac" => "FLAC",
            ".ogg" => "Vorbis",
            ".m4a" or ".mp4" or ".aac" => "AAC",
            ".alac" => "Apple Lossless (ALAC)",
            ".wav" => "PCM (WAV)",
            ".aiff" or ".aif" or ".aifc" => "PCM (AIFF)",
            ".opus" => "Opus",
            ".wma" => "Windows Media Audio",
            ".ape" => "Monkey's Audio",
            ".wv" => "WavPack",
            _ => "Unknown"
        };
    }

    private static string JoinTagValues(string[]? values)
    {
        if (values == null || values.Length == 0)
            return string.Empty;

        var normalized = values
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? string.Empty : string.Join(", ", normalized);
    }

    /// <summary>
    /// If the title contains "feat."/"ft." artists not already present in the artist field,
    /// merge them in so collaboration tracks always show the full artist credit.
    /// </summary>
    internal static string EnrichArtistFromTitle(string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return artist;

        // Extract "feat. X" / "ft. X" / "featuring X" from parentheses or brackets
        var match = Regex.Match(title,
            @"[\(\[]\s*(?:feat\.?|ft\.?|featuring)\s+(.+?)[\)\]]",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return artist;

        var featuredRaw = match.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(featuredRaw))
            return artist;

        // Split featured part by common separators
        var featNames = Regex.Split(featuredRaw, @"\s*(?:,|;|&|\band\b)\s*", RegexOptions.IgnoreCase)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        if (featNames.Length == 0)
            return artist;

        // Find featured artists not already mentioned in the artist string
        var missing = featNames
            .Where(f => artist.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0)
            .ToArray();

        if (missing.Length == 0)
            return artist;

        return artist + " & " + string.Join(" & ", missing);
    }

    private static string[] SplitArtistList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        return value
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsLosslessFormat(string codec, string ext)
    {
        var codecLower = codec.ToLowerInvariant();
        if (codecLower.Contains("flac") || codecLower.Contains("alac") ||
            codecLower.Contains("lossless") || codecLower.Contains("pcm") ||
            codecLower.Contains("wavpack") || codecLower.Contains("monkey"))
            return true;

        return ext is ".flac" or ".alac" or ".wav" or ".aiff" or ".aif" or ".aifc" or ".ape" or ".wv";
    }

    private static string GetFileFormat(string ext)
    {
        return ext switch
        {
            ".mp3" => "MP3",
            ".flac" => "FLAC",
            ".ogg" => "OGG Vorbis",
            ".m4a" => "Apple Audio (M4A)",
            ".mp4" => "MPEG-4 Audio",
            ".aac" => "AAC",
            ".alac" => "Apple Lossless",
            ".wav" => "WAV",
            ".aiff" or ".aif" or ".aifc" => "AIFF",
            ".opus" => "Opus",
            ".wma" => "Windows Media Audio",
            ".ape" => "Monkey's Audio",
            ".wv" => "WavPack",
            _ => ext.TrimStart('.').ToUpperInvariant()
        };
    }

    /// <summary>
    /// Reads the copyright notice explicitly from format-specific tag fields.
    /// ID3v2: TCOP frame. Apple: cprt atom. Xiph: COPYRIGHT field.
    /// Does NOT fall back to tag.Comment.
    /// </summary>
    private static string ReadCopyright(TagLib.File file)
    {
        // ID3v2 (MP3): TCOP frame
        try
        {
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2)
            {
                var tcop = id3v2.GetFrames<TagLib.Id3v2.TextInformationFrame>()
                    .FirstOrDefault(f => f.FrameId == TagLib.ByteVector.FromString("TCOP", TagLib.StringType.Latin1));
                if (tcop != null)
                {
                    var val = tcop.Text?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                }

                // Also check TXXX:COPYRIGHT custom frame
                foreach (var frame in id3v2.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    if (string.Equals(frame.Description, "COPYRIGHT", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = frame.Text?.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                    }
                }
            }
        }
        catch { }

        // Apple tags (M4A/MP4): cprt atom, then freeform COPYRIGHT
        try
        {
            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
            {
                // cprt atom (standard copyright)
                var cprt = apple.GetText(TagLib.ByteVector.FromString("cprt", TagLib.StringType.Latin1));
                if (cprt != null)
                {
                    var val = cprt.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                }

                // Freeform COPYRIGHT box
                var freeform = apple.GetDashBox("com.apple.iTunes", "COPYRIGHT");
                if (!string.IsNullOrWhiteSpace(freeform)) return freeform.Trim();
            }
        }
        catch { }

        // Xiph comments (FLAC, OGG, Opus): COPYRIGHT field
        try
        {
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var fields = xiph.GetField("COPYRIGHT");
                var val = fields?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
        }
        catch { }

        return string.Empty;
    }

    /// <summary>
    /// Reads the full release date from RELEASETIME, YEAR, TDRL/TDRC, or date tags across formats.
    /// Returns raw date string (e.g., "2014-10-27" or "2014/10/27" or "2014-10-27T00:00:00Z"), or empty.
    /// Only returns values that contain more than just a year (length > 4).
    /// </summary>
    private static string ReadReleaseDate(TagLib.File file, TagLib.Tag tag)
    {
        // ID3v2 (MP3): check TXXX custom frames first (RELEASETIME, RELEASEDATE, YEAR)
        try
        {
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2)
            {
                // TXXX custom text frames — taggers like MusicBrainz Picard, Mp3tag, etc.
                // store full dates here as "RELEASETIME", "RELEASEDATE", or even "YEAR"
                foreach (var frame in id3v2.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    var desc = frame.Description;
                    if (string.Equals(desc, "RELEASETIME", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(desc, "RELEASEDATE", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(desc, "YEAR", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = frame.Text?.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(val) && val.Trim().Length > 4) return val.Trim();
                    }
                }

                // Standard ID3v2.4 date frames: TDRL (release date), TDRC (recording date)
                foreach (var frameId in new[] { "TDRL", "TDRC" })
                {
                    var frame = id3v2.GetFrames<TagLib.Id3v2.TextInformationFrame>()
                        .FirstOrDefault(f => f.FrameId == TagLib.ByteVector.FromString(frameId, TagLib.StringType.Latin1));
                    if (frame != null)
                    {
                        var val = frame.Text?.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(val) && val.Trim().Length > 4) return val.Trim();
                    }
                }
            }
        }
        catch { }

        // Apple tags (M4A/MP4/AAC/ALAC): freeform boxes + ©day atom
        try
        {
            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
            {
                // Freeform boxes: ----:com.apple.iTunes:RELEASETIME, RELEASEDATE, YEAR
                foreach (var boxName in new[] { "RELEASETIME", "RELEASEDATE", "YEAR" })
                {
                    var val = apple.GetDashBox("com.apple.iTunes", boxName);
                    if (!string.IsNullOrWhiteSpace(val) && val.Trim().Length > 4) return val.Trim();
                }

                // ©day atom often contains full date like "2014-10-27T07:00:00Z"
                var dayAtom = apple.GetText(TagLib.ByteVector.FromString("\u00A9day", TagLib.StringType.Latin1));
                if (dayAtom != null)
                {
                    var val = dayAtom.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(val) && val.Trim().Length > 4) return val.Trim();
                }
            }
        }
        catch { }

        // Xiph comments (FLAC, OGG, Opus): check RELEASETIME, RELEASEDATE, DATE, YEAR
        try
        {
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                foreach (var fieldName in new[] { "RELEASETIME", "RELEASEDATE", "DATE", "YEAR" })
                {
                    var fields = xiph.GetField(fieldName);
                    var val = fields?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(val) && val.Trim().Length > 4) return val.Trim();
                }
            }
        }
        catch { }

        return string.Empty;
    }

    /// <summary>
    /// Detects explicit content from common advisory fields across tag formats.
    /// Supports ITUNESADVISORY, iTunEXTC, and MP4 rtng/rating values.
    /// </summary>
    private static bool DetectExplicit(TagLib.File file)
    {
        try
        {
            // ID3v2 (MP3, etc.): TXXX:ITUNESADVISORY
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2)
            {
                foreach (var frame in id3v2.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    if (!string.Equals(frame.Description, "ITUNESADVISORY", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (TryParseExplicitFromValues(frame.Text, out var isExplicit))
                    {
                        if (isExplicit) return true;
                    }
                }
            }
        }
        catch { }

        try
        {
            // Xiph comments (FLAC, OGG, Opus)
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var fields = xiph.GetField("ITUNESADVISORY");
                if (TryParseExplicitFromValues(fields, out var isExplicit))
                {
                    if (isExplicit) return true;
                }
            }
        }
        catch { }

        try
        {
            // Apple tags (M4A/MP4/AAC/ALAC)
            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
            {
                // Freeform advisory: ----:com.apple.iTunes:ITUNESADVISORY
                if (TryParseExplicitValue(apple.GetDashBox("com.apple.iTunes", "ITUNESADVISORY"), out var advisory))
                {
                    if (advisory) return true;
                }

                // Alternate advisory in iTunes extension field.
                if (TryParseExplicitValue(apple.GetDashBox("com.apple.iTunes", "iTunEXTC"), out var itunExtc))
                {
                    if (itunExtc) return true;
                }

                // Standard MP4 rating atom that often appears as "rating=1" in ffprobe.
                if (TryParseExplicitFromValues(apple.GetText(TagLib.ByteVector.FromString("rtng", TagLib.StringType.Latin1)), out var rating))
                {
                    if (rating) return true;
                }

                // Some files store the same value under "rate".
                if (TryParseExplicitFromValues(apple.GetText(TagLib.ByteVector.FromString("rate", TagLib.StringType.Latin1)), out var rate))
                {
                    if (rate) return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static bool TryParseExplicitFromValues(IEnumerable<string>? values, out bool isExplicit)
    {
        if (values != null)
        {
            foreach (var value in values)
            {
                if (TryParseExplicitValue(value, out isExplicit))
                    return true;
            }
        }

        isExplicit = false;
        return false;
    }

    private static bool TryParseExplicitValue(string? raw, out bool isExplicit)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var normalized = raw.Trim().Trim('\0');
            if (TryParseAdvisoryNumeric(normalized, out var numeric))
            {
                // 1 = explicit, 0/2 = clean per iTunes advisory conventions.
                if (numeric == 1)
                {
                    isExplicit = true;
                    return true;
                }

                if (numeric == 0 || numeric == 2)
                {
                    isExplicit = false;
                    return true;
                }
            }

            if (normalized.Contains("Explicit", StringComparison.OrdinalIgnoreCase))
            {
                isExplicit = true;
                return true;
            }

            if (normalized.Contains("Clean", StringComparison.OrdinalIgnoreCase))
            {
                isExplicit = false;
                return true;
            }
        }

        isExplicit = false;
        return false;
    }

    private static bool TryParseAdvisoryNumeric(string value, out int numeric)
    {
        if (int.TryParse(value, out numeric))
            return true;

        // MP4 "rtng" can be returned as a single control character (0x00/0x01/0x02).
        if (value.Length == 1)
        {
            var code = (int)value[0];
            if (code is 0 or 1 or 2)
            {
                numeric = code;
                return true;
            }
        }

        numeric = -1;
        return false;
    }

    /// <summary>Returns the first non-null, non-whitespace string, or the fallback.</summary>
    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return values[^1] ?? "Unknown";
    }
}

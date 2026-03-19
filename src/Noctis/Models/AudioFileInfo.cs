namespace Noctis.Models;

/// <summary>
/// Read-only technical information about an audio file on disk.
/// </summary>
public class AudioFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileFormat { get; set; } = string.Empty;
    public string Codec { get; set; } = string.Empty;
    public bool IsLossless { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int BitsPerSample { get; set; }
    public int Channels { get; set; }
    public string ChannelDescription { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime DateAdded { get; set; }
    public DateTime DateModified { get; set; }

    public string FileSizeFormatted
    {
        get
        {
            if (FileSize >= 1_073_741_824)
                return $"{FileSize / 1_073_741_824.0:F1} GB";
            if (FileSize >= 1_048_576)
                return $"{FileSize / 1_048_576.0:F1} MB";
            if (FileSize >= 1024)
                return $"{FileSize / 1024.0:F1} KB";
            return $"{FileSize} bytes";
        }
    }

    public string BitrateFormatted => Bitrate > 0 ? $"{Bitrate} kbps" : "N/A";
    public string SampleRateFormatted => SampleRate > 0 ? $"{SampleRate / 1000.0:#.###} kHz" : "N/A";
    public string BitsPerSampleFormatted => BitsPerSample > 0 ? $"{BitsPerSample} bit" : "N/A";

    public string DurationFormatted =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");
}

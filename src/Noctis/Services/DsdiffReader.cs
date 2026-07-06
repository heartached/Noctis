using System;
using System.IO;
using System.Text;

namespace Noctis.Services;

/// <summary>
/// Minimal reader for DSDIFF (.dff) audio files. TagLib# ships no DSDIFF parser, so
/// without this the library scan drops every .dff file. This pulls the sample rate,
/// channel count and duration — and, when present, the edited-master title/artist —
/// straight from the DSDIFF chunk tree so the files show up in the library.
///
/// Best-effort: any malformed or unexpected input returns null so the scan simply
/// skips the file (the prior behaviour) instead of throwing.
///
/// DSDIFF is an IFF-style container, but unlike classic IFF every chunk size is an
/// 8-byte (64-bit) big-endian value and the top-level form id is "FRM8".
/// Reference: "DSDIFF File Format Specification" v1.5 (Philips).
/// </summary>
internal static class DsdiffReader
{
    internal sealed record DsdiffInfo(int SampleRate, int Channels, TimeSpan Duration, string? Title, string? Artist);

    internal static DsdiffInfo? TryRead(Stream stream)
    {
        try
        {
            if (!TryReadId(stream, out var formId) || formId != "FRM8")
                return null;
            if (!TryReadU64(stream, out _))            // form size — ignored; we walk to EOF
                return null;
            if (!TryReadId(stream, out var formType) || formType != "DSD ")
                return null;

            int sampleRate = 0, channels = 0;
            long dsdDataBytes = -1;
            var dstDuration = TimeSpan.Zero;
            string? title = null, artist = null;

            // Walk the top-level local chunks of the FRM8 form.
            while (TryReadChunkHeader(stream, out var id, out var size))
            {
                var dataStart = stream.Position;
                switch (id)
                {
                    case "PROP":
                        ReadProp(stream, dataStart + size, ref sampleRate, ref channels);
                        break;
                    case "DSD ":           // uncompressed sound data — size gives the duration
                        dsdDataBytes = size;
                        break;
                    case "DST ":           // compressed sound data — duration lives in its FRTE chunk
                        dstDuration = ReadDstDuration(stream, dataStart + size);
                        break;
                    case "DIIN":           // edited master info (title / artist)
                        ReadDiin(stream, dataStart + size, ref title, ref artist);
                        break;
                }

                // Chunks pad to an even byte boundary.
                if (!SeekTo(stream, dataStart + size + (size & 1)))
                    break;
            }

            if (sampleRate <= 0 || channels <= 0)
                return null;

            var duration = dsdDataBytes > 0
                ? TimeSpan.FromSeconds(dsdDataBytes * 8.0 / ((double)sampleRate * channels))
                : dstDuration;

            return new DsdiffInfo(sampleRate, channels, duration, Clean(title), Clean(artist));
        }
        catch
        {
            return null;
        }
    }

    private static void ReadProp(Stream stream, long end, ref int sampleRate, ref int channels)
    {
        // PROP data starts with a property type ("SND ") then nested chunks.
        if (!TryReadId(stream, out var propType) || propType != "SND ")
            return;

        while (stream.Position + 12 <= end && TryReadChunkHeader(stream, out var id, out var size))
        {
            var dataStart = stream.Position;
            switch (id)
            {
                case "FS  ":
                    if (TryReadU32(stream, out var fs)) sampleRate = (int)fs;
                    break;
                case "CHNL":
                    if (TryReadU16(stream, out var ch)) channels = ch;
                    break;
            }
            if (!SeekTo(stream, dataStart + size + (size & 1)))
                break;
        }
    }

    private static TimeSpan ReadDstDuration(Stream stream, long end)
    {
        // DST (compressed) sound data is a container; FRTE holds the frame count
        // and frame rate, from which the duration follows directly.
        while (stream.Position + 12 <= end && TryReadChunkHeader(stream, out var id, out var size))
        {
            var dataStart = stream.Position;
            if (id == "FRTE"
                && TryReadU32(stream, out var numFrames)
                && TryReadU16(stream, out var frameRate)
                && frameRate > 0)
            {
                return TimeSpan.FromSeconds((double)numFrames / frameRate);
            }
            if (!SeekTo(stream, dataStart + size + (size & 1)))
                break;
        }
        return TimeSpan.Zero;
    }

    private static void ReadDiin(Stream stream, long end, ref string? title, ref string? artist)
    {
        while (stream.Position + 12 <= end && TryReadChunkHeader(stream, out var id, out var size))
        {
            var dataStart = stream.Position;
            switch (id)
            {
                case "DITI":
                    title = ReadCountedText(stream, size);
                    break;
                case "DIAR":
                    artist = ReadCountedText(stream, size);
                    break;
            }
            if (!SeekTo(stream, dataStart + size + (size & 1)))
                break;
        }
    }

    // DITI/DIAR data is a 4-byte big-endian length followed by that many text bytes.
    // Cap the allocation: the count is untrusted file data, and a corrupt value could
    // otherwise ask for up to 2 GB for what is a title/artist string.
    private const int MaxTextBytes = 4096;

    private static string? ReadCountedText(Stream stream, long chunkSize)
    {
        if (chunkSize < 4 || !TryReadU32(stream, out var count))
            return null;
        var len = (int)Math.Min(Math.Min(count, chunkSize - 4), MaxTextBytes);
        if (len <= 0)
            return null;
        var buf = new byte[len];
        if (!ReadExact(stream, buf, len))
            return null;
        return Encoding.Latin1.GetString(buf);
    }

    private static bool TryReadChunkHeader(Stream stream, out string id, out long size)
    {
        size = 0;
        if (!TryReadId(stream, out id) || !TryReadU64(stream, out var raw))
            return false;
        if (raw > long.MaxValue)            // absurd / corrupt size
            return false;
        size = (long)raw;
        return size >= 0;
    }

    private static bool TryReadId(Stream stream, out string id)
    {
        id = string.Empty;
        var buf = new byte[4];
        if (!ReadExact(stream, buf, 4))
            return false;
        id = Encoding.ASCII.GetString(buf);
        return true;
    }

    private static bool TryReadU16(Stream stream, out ushort value)
    {
        value = 0;
        var buf = new byte[2];
        if (!ReadExact(stream, buf, 2))
            return false;
        value = (ushort)((buf[0] << 8) | buf[1]);
        return true;
    }

    private static bool TryReadU32(Stream stream, out uint value)
    {
        value = 0;
        var buf = new byte[4];
        if (!ReadExact(stream, buf, 4))
            return false;
        value = ((uint)buf[0] << 24) | ((uint)buf[1] << 16) | ((uint)buf[2] << 8) | buf[3];
        return true;
    }

    private static bool TryReadU64(Stream stream, out ulong value)
    {
        value = 0;
        var buf = new byte[8];
        if (!ReadExact(stream, buf, 8))
            return false;
        for (int i = 0; i < 8; i++)
            value = (value << 8) | buf[i];
        return true;
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }

    private static bool SeekTo(Stream stream, long position)
    {
        if (position < 0)
            return false;
        stream.Seek(position, SeekOrigin.Begin);
        return true;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.TrimEnd('\0').Trim();
    }
}

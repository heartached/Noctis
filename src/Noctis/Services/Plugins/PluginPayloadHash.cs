using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Noctis.Services.Plugins;

/// <summary>
/// One SHA-256 over an installed plugin's files, recorded when its zip passed the official
/// list's check and compared every time the plugin loads (docs/PLUGINS.md, "How the app
/// decides"). The zip hash alone said nothing about the folder afterwards: a build copied by
/// hand over a verified install, at the same version, still counted as verified.
///
/// <para><b>The payload</b> is every file under the plugin folder, at any depth: the loader
/// reads code from the entry DLL and anything its .deps.json names (managed or native, in any
/// subfolder), and a plugin may run or read any file it ships next to it (API 1.1
/// <c>PluginDirectory</c>). Left out, because none is code the plugin ships:</para>
/// <list type="bullet">
/// <item>the uninstall marker Noctis itself leaves (<see cref="PluginInstaller.UninstallMarker"/>);</item>
/// <item>files a file manager writes when the folder is opened (<c>.DS_Store</c>, <c>Thumbs.db</c>,
/// <c>desktop.ini</c>), since Settings offers to open the plugin folder;</item>
/// <item>for a plugin built for API 1.0 only, its top-level <c>data</c> folder: that was its data
/// folder, moved out on first load.</item>
/// </list>
/// <para>A plugin's own data never lives here (<c>&lt;data&gt;/plugin-data/&lt;id&gt;/</c> since API 1.1),
/// so a plugin writing its data or cache does not change the hash.</para>
///
/// <para><b>The hash</b>: files sorted by relative path ('/'-separated, ordinal); for each, the
/// UTF-8 path's length (4 bytes, little-endian), the path, the file's length (8 bytes,
/// little-endian) and its bytes. The same function runs over a zip's entries, so a downloaded
/// zip can be compared with an installed folder.</para>
/// </summary>
public static class PluginPayloadHash
{
    private static readonly string[] OsFiles = { ".DS_Store", "Thumbs.db", "desktop.ini" };

    /// <summary>Not part of the payload (see the class summary).</summary>
    internal static bool IsIgnored(string relativePath, bool keepsDataInFolder)
    {
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        if (string.Equals(name, PluginInstaller.UninstallMarker, StringComparison.OrdinalIgnoreCase)) return true;
        if (OsFiles.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase))) return true;
        return keepsDataInFolder && relativePath.StartsWith("data/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a plugin built against API 1.0 (or with no plugin.json): its top-level
    /// <c>data</c> folder is data, moved out of the plugin folder on first load.</summary>
    internal static bool KeepsDataInFolder(PluginManifest? manifest, bool isLegacy)
        => isLegacy || (manifest is not null && PluginManifest.TryParseApiVersion(manifest.ApiVersion, out var major, out var minor)
                                              && major == 1 && minor == 0);

    // Per process: folder → (what its files looked like when hashed, the hash), for the checks
    // between two loads (each Activate, the cards, the Community switch). Every load and every
    // install hashes afresh (fresh: true): extracted files carry their zip entry's time, so two
    // builds zipped in the same two seconds with same-size files look alike by stamp. Nothing is
    // kept across launches either, so a copy that keeps the old timestamps is still read.
    private static readonly ConcurrentDictionary<string, (string Stamp, string Hash)> FolderCache = new(StringComparer.Ordinal);

    /// <summary>The payload hash of an installed plugin folder; an empty or missing folder hashes as
    /// no files. Re-reads the files whenever any one's size or write time (or the file set) changed
    /// since this process last hashed it.</summary>
    public static string OfFolder(string directory, bool keepsDataInFolder, bool fresh = false)
    {
        var root = Path.GetFullPath(directory);
        var files = new List<(string Relative, FileInfo Info)>();
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!IsIgnored(relative, keepsDataInFolder)) files.Add((relative, new FileInfo(path)));
            }
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));

        var stamp = new StringBuilder(keepsDataInFolder ? "1|" : "0|");
        foreach (var (relative, info) in files)
            stamp.Append(relative).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks)
                .Append('|').Append(info.CreationTimeUtc.Ticks).Append('\n');
        var key = stamp.ToString();
        if (!fresh && FolderCache.TryGetValue(root, out var cached) && cached.Stamp == key) return cached.Hash;

        var hash = Compute(files.Select(f => (f.Relative, f.Info.Length, (Func<Stream>)(() => f.Info.OpenRead()))));
        FolderCache[root] = (key, hash);
        return hash;
    }

    /// <summary>The payload hash of the plugin a zip installs: the files under its root folder (the
    /// ones <see cref="PluginInstaller.Extract"/> writes), as they will be named in the folder.</summary>
    /// <exception cref="PluginInstallException">Not a usable plugin package.</exception>
    public static string OfZip(string zipPath)
    {
        var package = PluginInstaller.Inspect(zipPath);
        var keepsData = KeepsDataInFolder(package.Manifest, isLegacy: false);
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries
            .Select(e => (Entry: e, Name: e.FullName.Replace('\\', '/')))
            .Where(e => !e.Name.EndsWith('/') && e.Name.Length > package.RootPrefix.Length
                        && e.Name.StartsWith(package.RootPrefix, StringComparison.Ordinal))
            .Select(e => (Relative: e.Name[package.RootPrefix.Length..], e.Entry))
            .Where(e => !IsIgnored(e.Relative, keepsData))
            .OrderBy(e => e.Relative, StringComparer.Ordinal)
            .ToList();
        return Compute(entries.Select(e => (e.Relative, e.Entry.Length, (Func<Stream>)e.Entry.Open)));
    }

    private static string Compute(IEnumerable<(string Relative, long Length, Func<Stream> Open)> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[8];
        var buffer = new byte[81920];
        foreach (var (relative, length, open) in files)
        {
            var name = Encoding.UTF8.GetBytes(relative);
            BinaryPrimitives.WriteInt32LittleEndian(number, name.Length);
            sha.AppendData(number[..4]);
            sha.AppendData(name);
            BinaryPrimitives.WriteInt64LittleEndian(number, length);
            sha.AppendData(number);
            using var stream = open();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Tests: forget what this process hashed.</summary>
    internal static void ClearCacheForTests() => FolderCache.Clear();
}

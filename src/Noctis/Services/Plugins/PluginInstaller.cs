using System.IO.Compression;

namespace Noctis.Services.Plugins;

/// <summary>A plugin .zip could not be installed; the message is shown to the user as is.</summary>
public sealed class PluginInstallException : Exception
{
    public PluginInstallException(string message) : base(message) { }
}

/// <summary>What a plugin .zip holds, read without extracting anything.</summary>
/// <param name="RootPrefix">"" when plugin.json is at the top of the zip, else "folder/".</param>
public sealed record PluginPackage(string ZipPath, PluginManifest Manifest, string RootPrefix, int FileCount, long TotalBytes);

/// <summary>
/// Reads and extracts plugin packages. A package is a .zip with plugin.json either at its
/// top or inside a single top-level folder. Every entry is checked before anything is
/// written: absolute paths, drive letters and ".." segments (zip-slip) reject the whole
/// file, as do oversized archives.
/// </summary>
public static class PluginInstaller
{
    internal const int MaxEntries = 2000;
    internal const long MaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>Validates the zip and its plugin.json.</summary>
    /// <exception cref="PluginInstallException">Not a usable plugin package.</exception>
    public static PluginPackage Inspect(string zipPath)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PluginInstallException("The file is not a readable .zip: " + ex.Message);
        }

        using (zip)
        {
            if (zip.Entries.Count > MaxEntries) throw new PluginInstallException($"The zip has more than {MaxEntries} files.");
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (!IsSafeEntryName(e.FullName))
                    throw new PluginInstallException($"The zip contains an unsafe path (\"{e.FullName}\") and was rejected.");
                total += e.Length;
                if (total > MaxTotalBytes) throw new PluginInstallException("The zip unpacks to more than 256 MB.");
            }

            var prefix = FindRootPrefix(zip);
            var manifestEntry = zip.GetEntry(prefix + PluginManifest.FileName)
                ?? zip.Entries.First(e => Normalize(e.FullName) == prefix + PluginManifest.FileName);
            if (manifestEntry.Length > PluginManifest.MaxBytes) throw new PluginInstallException("plugin.json is larger than 256 KB.");

            PluginManifest manifest;
            try
            {
                using var reader = new StreamReader(manifestEntry.Open());
                manifest = PluginManifest.Parse(reader.ReadToEnd());
            }
            catch (PluginManifestException ex) { throw new PluginInstallException(ex.Message); }

            var files = zip.Entries.Where(e => IsFileUnder(e, prefix)).ToList();
            if (manifest.IsDotnet && !files.Any(e => string.Equals(Normalize(e.FullName), prefix + manifest.Entry, StringComparison.OrdinalIgnoreCase)))
                throw new PluginInstallException($"plugin.json names \"{manifest.Entry}\" as its entry, but the zip has no such file next to it.");
            if (manifest.IsContent) CheckContentPackage(files, prefix, manifest);

            return new PluginPackage(zipPath, manifest, prefix, files.Count, total);
        }
    }

    /// <summary>
    /// Extracts the package's files (those under <see cref="PluginPackage.RootPrefix"/>) into
    /// <paramref name="destination"/>, which must not exist yet. Every target path is resolved
    /// and checked to stay inside the destination before it is written.
    /// </summary>
    public static void Extract(PluginPackage package, string destination)
    {
        if (Directory.Exists(destination)) throw new PluginInstallException("The install folder already exists: " + destination);
        var root = Path.GetFullPath(destination);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        try
        {
            using var zip = ZipFile.OpenRead(package.ZipPath);
            foreach (var entry in zip.Entries)
            {
                if (!IsSafeEntryName(entry.FullName))
                    throw new PluginInstallException($"The zip contains an unsafe path (\"{entry.FullName}\") and was rejected.");
                if (!IsFileUnder(entry, package.RootPrefix)) continue;
                var relative = Normalize(entry.FullName)[package.RootPrefix.Length..];
                var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootWithSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new PluginInstallException($"The zip entry \"{entry.FullName}\" points outside the plugin folder and was rejected.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch
        {
            TryDeleteDirectory(root);
            throw;
        }
    }

    /// <summary>A content pack's zip: data files only, within the limits, and every listed file present.
    /// The folder is checked again (and every file parsed) when the pack loads.</summary>
    private static void CheckContentPackage(List<ZipArchiveEntry> files, string prefix, PluginManifest manifest)
    {
        if (files.Count > ContentPackRules.MaxFiles)
            throw new PluginInstallException($"A content pack may hold at most {ContentPackRules.MaxFiles} files.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in files)
        {
            var rel = Normalize(e.FullName)[prefix.Length..];
            names.Add(rel);
            total += e.Length;
            if (total > ContentPackRules.MaxTotalBytes) throw new PluginInstallException("A content pack may unpack to at most 20 MB.");
            if (rel == PluginManifest.FileName) continue;
            if (ContentPackRules.CheckFile(rel, e.Length) is { } why) throw new PluginInstallException(why);
        }
        var contents = manifest.Contents!;
        foreach (var listed in contents.Themes.Concat(contents.LyricsPresets).Concat(contents.Languages))
            if (!names.Contains(listed))
                throw new PluginInstallException($"plugin.json lists \"{listed}\", but the zip has no such file.");
    }

    /// <summary>
    /// False for anything that could land outside the target folder: rooted paths, drive
    /// letters or colons (also NTFS alternate streams), ".." segments, and control characters.
    /// </summary>
    internal static bool IsSafeEntryName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return false;
        var name = fullName.Replace('\\', '/');
        if (name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl)) return false;
        foreach (var segment in name.Split('/'))
            if (segment is ".." or ".") return false;
        return true;
    }

    /// <summary>Deletes a folder, retrying briefly (a just-unloaded plugin may still hold a file for a moment).</summary>
    internal static bool TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception) when (attempt < 4)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100 * (attempt + 1));
            }
            catch (Exception ex)
            {
                DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"delete {path}: {ex.Message}");
                return false;
            }
        }
        return !Directory.Exists(path);
    }

    private static string Normalize(string name) => name.Replace('\\', '/');

    private static bool IsFileUnder(ZipArchiveEntry e, string prefix)
    {
        var n = Normalize(e.FullName);
        return !n.EndsWith('/') && n.Length > prefix.Length && n.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string FindRootPrefix(ZipArchive zip)
    {
        var names = zip.Entries.Select(e => Normalize(e.FullName)).ToList();
        if (names.Contains(PluginManifest.FileName)) return "";
        var nested = names
            .Where(n => n.EndsWith("/" + PluginManifest.FileName, StringComparison.Ordinal) && n.Count(c => c == '/') == 1)
            .ToList();
        if (nested.Count == 1) return nested[0][..^PluginManifest.FileName.Length];
        if (nested.Count > 1) throw new PluginInstallException("The zip holds more than one plugin.json; install one plugin at a time.");
        throw new PluginInstallException("No plugin.json at the top of the zip (or inside a single top-level folder).");
    }
}

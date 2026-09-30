using System.Diagnostics.CodeAnalysis;

namespace Noctis.Services;

/// <summary>The desktop scan source: a recursive directory walk (moved out of LibraryService unchanged).</summary>
public sealed class LocalFileSystemSource : IFileSystemSource
{
    public bool RootExists(string root) => Directory.Exists(root);

    public IEnumerable<ScanEntry> EnumerateAudioFiles(
        string root,
        IReadOnlyCollection<string> excludedRoots,
        IReadOnlySet<string> ignoredFolderNames,
        Action<string> reportFailedDirectory)
    {
        var stack = new Stack<string>();
        // Cycle guard keyed on the RESOLVED path: a junction/symlink pointing at
        // an ancestor re-enters the tree under an ever-growing logical path, so
        // the walked path alone never repeats and the DFS loops forever.
        var visited = new HashSet<string>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (IsUnderAnyRoot(current, excludedRoots)) continue;
            if (!visited.Add(ResolveRealPath(current))) continue;

            List<string> directories;
            List<string> files;
            try
            {
                // Materialized inside the try: the enumerables are lazy, so an I/O error
                // surfacing mid-listing (not just at open) would otherwise escape this
                // catch and abort the entire scan pipeline.
                directories = Directory.EnumerateDirectories(current).ToList();
                files = Directory.EnumerateFiles(current).ToList();
            }
            catch
            {
                // "Couldn't list" is not "doesn't exist": LibraryService keeps the known
                // tracks under a reported directory instead of dropping them.
                reportFailedDirectory(current);
                continue;
            }

            foreach (var dir in directories)
            {
                var name = Path.GetFileName(dir);
                if (ignoredFolderNames.Contains(name.ToLowerInvariant())) continue;
                if (IsUnderAnyRoot(dir, excludedRoots)) continue;
                stack.Push(dir);
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (!MetadataService.SupportedExtensions.Contains(ext)) continue;

                // A file deleted or renamed between the listing above and now (NoBuffering
                // pulls files lazily, so that window can be seconds to minutes on a large
                // tree) is a per-file skip, same as every other file failure here.
                if (TryCreateEntry(file, out var entry))
                    yield return entry;
            }
        }
    }

    /// <summary>
    /// The scan entry for one file, or false when it can't be stat'ed (gone, renamed, no
    /// access). Also used for files added to the library on their own (GitHub #108).
    /// </summary>
    internal static bool TryCreateEntry(string file, [NotNullWhen(true)] out ScanEntry? entry)
    {
        // FileInfo's constructor does no I/O (it only normalizes the path); the actual stat
        // happens the first time Length/LastWriteTimeUtc/Name are read, which throws for a
        // missing file. Reading them here — instead of in an iterator's yield return argument
        // list, where a throw would escape and abort the whole scan — keeps it a skip.
        try
        {
            var fi = new FileInfo(file);
            var length = fi.Length;
            var lastWrite = fi.LastWriteTimeUtc;
            entry = new ScanEntry(file, fi.Name, length, lastWrite, file, () => File.OpenRead(file));
            return true;
        }
        catch
        {
            entry = null;
            return false;
        }
    }

    // Symlinked/junctioned directories are followed (symlinked music libraries are
    // legitimate); resolving to the final target is what makes the visited-set
    // above detect a loop regardless of the logical path it was reached through.
    private static string ResolveRealPath(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
            return info.FullName;
        }
        catch
        {
            return dir;
        }
    }

    private static bool IsUnderAnyRoot(string path, IReadOnlyCollection<string> roots)
    {
        var normalized = LibraryService.NormalizePath(path);
        foreach (var root in roots)
        {
            if (LibraryService.IsUnderRoot(normalized, root))
                return true;
        }
        return false;
    }
}

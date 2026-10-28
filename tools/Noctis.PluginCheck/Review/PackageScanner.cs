using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Noctis.PluginCheck;

/// <summary>What a plugin's code reaches for, as seen in its assemblies' references.</summary>
public enum ApiUse
{
    /// <summary>HttpClient, sockets, WebSockets, DNS, WebClient, ….</summary>
    Network,
    /// <summary>Starts other programs (System.Diagnostics.Process).</summary>
    Process,
    /// <summary>Loads or emits code at run time (Assembly.Load*, AssemblyLoadContext, Reflection.Emit).</summary>
    DynamicCode,
    /// <summary>Calls native code (P/Invoke, NativeLibrary, function pointers).</summary>
    NativeInterop,
    /// <summary>The Windows registry.</summary>
    Registry,
    /// <summary>Reads or writes files and folders (System.IO.File, Directory, FileStream, …).</summary>
    Files,
}

/// <summary>One managed assembly in the zip, read as metadata only (never loaded).</summary>
public sealed record AssemblyScan(
    string Path,
    IReadOnlySet<ApiUse> Uses,
    IReadOnlyList<string> NativeLibraries,
    IReadOnlyList<string> ObfuscationMarkers,
    IReadOnlyList<string> EmbeddedBinaries,
    bool IsMixedMode);

/// <summary>Everything in a plugin zip that a reviewer should look at twice.</summary>
public sealed record PackageScan(
    IReadOnlyList<string> Executables,
    IReadOnlyList<string> Scripts,
    IReadOnlyList<string> NativeBinaries,
    IReadOnlyList<string> Archives,
    IReadOnlyList<string> HostAssemblies,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<AssemblyScan> Assemblies)
{
    public IEnumerable<ApiUse> AllUses => Assemblies.SelectMany(a => a.Uses).Distinct().Order();
    public IEnumerable<string> UsersOf(ApiUse use) => Assemblies.Where(a => a.Uses.Contains(use)).Select(a => a.Path);
}

/// <summary>
/// Looks through every file of a plugin zip for red flags: programs and scripts, native
/// binaries, nested archives, obfuscated assemblies, and which risky .NET APIs the code
/// references. Assemblies are parsed with System.Reflection.Metadata; nothing is loaded or run.
/// </summary>
public static class PackageScanner
{
    /// <summary>Largest single file read for inspection. Bigger ones are listed as skipped.</summary>
    internal const long MaxEntryBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".msi", ".msix", ".appx", ".app", ".bin", ".run", ".apk", ".jar", ".deb", ".rpm", ".dmg", ".pkg", ".cpl", ".sys",
    };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".mjs", ".wsf", ".wsh", ".hta", ".sh", ".bash", ".zsh",
        ".command", ".py", ".pyw", ".rb", ".pl", ".lua", ".applescript", ".scpt", ".reg", ".lnk", ".url", ".desktop", ".csx",
    };

    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".so", ".dylib", ".a", ".lib", ".node" };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".tar", ".xz", ".bz2", ".zst", ".nupkg", ".cab", ".iso",
    };

    /// <summary>Assemblies Noctis already provides; docs/PLUGINS.md asks plugins not to ship them.</summary>
    private static readonly string[] HostAssemblyPrefixes = { "Noctis.Plugins.Abstractions", "Avalonia", "SkiaSharp", "HarfBuzzSharp" };

    /// <summary>Attribute and type names obfuscators and anti-decompiler tools leave behind.</summary>
    private static readonly HashSet<string> ObfuscatorMarkers = new(StringComparer.Ordinal)
    {
        "ConfusedByAttribute", "DotfuscatorAttribute", "ObfuscatedByGoliath", "PoweredByAttribute", "BabelObfuscatorAttribute",
        "BabelAttribute", "YanoAttribute", "NETGuard", "ZYXDNGuarder", "DotNetPatcherObfuscatorAttribute",
        "DotNetPatcherPackerAttribute", "CryptoObfuscatorAttribute", "ObfuscatedByAgileDotNetAttribute", "ObfuscatedByCliSecureAttribute",
        "Xenocode", "MaxtoCodeAttribute", "SmartAssemblyAttribute", "ObfuscationAttribute", "SuppressIldasmAttribute",
    };

    private static readonly HashSet<string> NetworkTypes = new(StringComparer.Ordinal)
    {
        "System.Net.WebClient", "System.Net.WebRequest", "System.Net.HttpWebRequest", "System.Net.FtpWebRequest", "System.Net.Dns",
        "System.Net.HttpListener", "System.Net.ServicePointManager", "System.Net.IPAddress", "System.Net.IPEndPoint", "System.Net.DnsEndPoint",
    };

    private static readonly string[] NetworkNamespaces =
    {
        "System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets", "System.Net.NetworkInformation", "System.Net.Quic",
        "System.Net.Security", "System.Net.Mail",
    };

    private static readonly HashSet<string> FileTypes = new(StringComparer.Ordinal)
    {
        "System.IO.File", "System.IO.FileInfo", "System.IO.FileStream", "System.IO.Directory", "System.IO.DirectoryInfo",
        "System.IO.StreamWriter", "System.IO.StreamReader", "System.IO.FileSystemWatcher", "System.IO.DriveInfo",
    };

    public static PackageScan Scan(string zipPath)
    {
        var executables = new List<string>();
        var scripts = new List<string>();
        var native = new List<string>();
        var archives = new List<string>();
        var host = new List<string>();
        var skipped = new List<string>();
        var assemblies = new List<AssemblyScan>();

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            var ext = Path.GetExtension(name);
            var file = Path.GetFileName(name);

            if (ExecutableExtensions.Contains(ext)) executables.Add(name);
            else if (ScriptExtensions.Contains(ext)) scripts.Add(name);
            else if (NativeExtensions.Contains(ext)) native.Add(name);
            else if (ArchiveExtensions.Contains(ext)) archives.Add(name);
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                && HostAssemblyPrefixes.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                host.Add(name);

            if (entry.Length > MaxEntryBytes) { skipped.Add(name); continue; }
            byte[] bytes;
            try
            {
                using var s = entry.Open();
                using var ms = new MemoryStream((int)Math.Min(entry.Length, MaxEntryBytes));
                CopyCapped(s, ms, MaxEntryBytes);
                bytes = ms.ToArray();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                skipped.Add(name);
                continue;
            }

            switch (Sniff(bytes))
            {
                case Magic.Script when !scripts.Contains(name):
                    scripts.Add(name);
                    break;
                case Magic.Elf or Magic.MachO when !native.Contains(name):
                    native.Add(name);
                    break;
                case Magic.Archive when !archives.Contains(name):
                    archives.Add(name);
                    break;
                case Magic.Pe:
                    var scan = ScanPe(name, bytes);
                    if (scan is null) { if (!native.Contains(name)) native.Add(name); }
                    else
                    {
                        assemblies.Add(scan);
                        if (scan.IsMixedMode && !native.Contains(name)) native.Add(name);
                    }
                    break;
            }
        }

        return new PackageScan(executables, scripts, native, archives, host, skipped, assemblies);
    }

    private enum Magic { None, Pe, Elf, MachO, Script, Archive }

    private static Magic Sniff(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 'M' && b[1] == 'Z') return Magic.Pe;
        if (b.Length >= 4 && b[0] == 0x7F && b[1] == 'E' && b[2] == 'L' && b[3] == 'F') return Magic.Elf;
        if (b.Length >= 4)
        {
            var be = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            if (be is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA) return Magic.MachO;
            if (b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4) return Magic.Archive;
        }
        if (b.Length >= 2 && b[0] == '#' && b[1] == '!') return Magic.Script;
        return Magic.None;
    }

    /// <summary>A managed assembly's scan, or null for a native PE (no .NET metadata).</summary>
    internal static AssemblyScan? ScanPe(string name, byte[] bytes)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes, writable: false));
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            var uses = new HashSet<ApiUse>();
            var markers = new SortedSet<string>(StringComparer.Ordinal);
            var nativeLibs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var h in md.TypeReferences)
            {
                var full = FullName(md, h);
                if (full is null) continue;
                Classify(full, uses);
                var simple = md.GetString(md.GetTypeReference(h).Name);
                if (ObfuscatorMarkers.Contains(simple)) markers.Add(simple);
            }

            foreach (var h in md.MemberReferences)
            {
                var m = md.GetMemberReference(h);
                if (m.Parent.Kind != HandleKind.TypeReference) continue;
                var parent = FullName(md, (TypeReferenceHandle)m.Parent);
                var member = md.GetString(m.Name);
                if (parent == "System.Reflection.Assembly" && member is "Load" or "LoadFrom" or "LoadFile" or "UnsafeLoadFrom" or "LoadWithPartialName")
                    uses.Add(ApiUse.DynamicCode);
                else if (parent == "System.Runtime.InteropServices.Marshal" && member is "GetDelegateForFunctionPointer" or "GetFunctionPointerForDelegate")
                    uses.Add(ApiUse.NativeInterop);
                else if (parent == "System.Environment" && member is "GetFolderPath")
                    uses.Add(ApiUse.Files);
            }

            var names = 0;
            var oddNames = 0;
            foreach (var h in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(h);
                var simple = md.GetString(t.Name);
                if (ObfuscatorMarkers.Contains(simple)) markers.Add(simple);
                names++;
                if (IsOddName(simple)) oddNames++;
            }
            foreach (var h in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(h);
                names++;
                if (IsOddName(md.GetString(m.Name))) oddNames++;
                if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    uses.Add(ApiUse.NativeInterop);
                    var import = m.GetImport();
                    if (!import.Module.IsNil) nativeLibs.Add(md.GetString(md.GetModuleReference(import.Module).Name));
                }
            }
            if (names >= 20 && oddNames * 5 > names)
                markers.Add($"{oddNames} of {names} type and method names are unreadable");

            var embedded = new List<string>();
            var corHeader = pe.PEHeaders.CorHeader;
            foreach (var h in md.ManifestResources)
            {
                var r = md.GetManifestResource(h);
                if (!r.Implementation.IsNil || corHeader is null) continue;
                var resName = md.GetString(r.Name);
                var looksBinary = ArchiveExtensions.Contains(Path.GetExtension(resName))
                    || ExecutableExtensions.Contains(Path.GetExtension(resName))
                    || resName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                if (!looksBinary)
                {
                    try
                    {
                        var block = pe.GetSectionData(corHeader.ResourcesDirectory.RelativeVirtualAddress);
                        var reader = block.GetReader();
                        reader.Offset = checked((int)r.Offset);
                        var length = reader.ReadInt32();
                        var head = reader.ReadBytes(Math.Min(4, Math.Max(0, length)));
                        looksBinary = Sniff(head) is Magic.Pe or Magic.Elf or Magic.MachO or Magic.Archive;
                    }
                    catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or OverflowException) { /* unreadable: not ours to judge */ }
                }
                if (looksBinary) embedded.Add(resName);
            }

            var ilOnly = corHeader is not null && (corHeader.Flags & CorFlags.ILOnly) != 0;
            return new AssemblyScan(name, uses, nativeLibs.ToList(), markers.ToList(), embedded, IsMixedMode: !ilOnly);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static void Classify(string full, HashSet<ApiUse> uses)
    {
        var ns = full.LastIndexOf('.') is var dot and > 0 ? full[..dot] : "";
        if (NetworkTypes.Contains(full) || NetworkNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal)))
            uses.Add(ApiUse.Network);
        else if (full is "System.Diagnostics.Process" or "System.Diagnostics.ProcessStartInfo")
            uses.Add(ApiUse.Process);
        else if (full == "System.Runtime.Loader.AssemblyLoadContext" || ns == "System.Reflection.Emit" || ns.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal))
            uses.Add(ApiUse.DynamicCode);
        else if (full == "System.Runtime.InteropServices.NativeLibrary")
            uses.Add(ApiUse.NativeInterop);
        else if (ns == "Microsoft.Win32" && full.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal))
            uses.Add(ApiUse.Registry);
        else if (FileTypes.Contains(full))
            uses.Add(ApiUse.Files);
    }

    /// <summary>"Namespace.Name" of a top-level type reference; null for nested types (their parent is classified).</summary>
    private static string? FullName(MetadataReader md, TypeReferenceHandle h)
    {
        var t = md.GetTypeReference(h);
        if (t.ResolutionScope.Kind == HandleKind.TypeReference) return null;
        var ns = md.GetString(t.Namespace);
        var name = md.GetString(t.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>A name no compiler writes: characters outside printable ASCII (obfuscators favour
    /// invisible and look-alike Unicode). Compiler-generated names ("&lt;Main&gt;b__0_0") are fine.</summary>
    internal static bool IsOddName(string name)
    {
        if (name.Length == 0) return true;
        foreach (var c in name)
            if (c < 0x21 || c > 0x7E) return true;
        return false;
    }

    private static void CopyCapped(Stream source, Stream target, long max)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > max) throw new IOException("Entry is larger than it says.");
            target.Write(buffer, 0, read);
        }
    }
}

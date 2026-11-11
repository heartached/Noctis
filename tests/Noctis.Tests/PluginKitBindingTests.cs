using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// How a plugin binds the kit (Noctis.Plugins.Abstractions). PluginLoadContext.Load returns null
/// for the kit, so the app's default load context supplies its own copy; .NET binds a reference
/// there only when the loaded assembly's version is at least the one the plugin was compiled
/// against. A plugin built against a kit with a higher AssemblyVersion than an older Noctis ships
/// therefore fails to load there, whatever its minAppVersion says. These tests build tiny plugin
/// assemblies that reference the kit at a chosen version and load them through the real context.
/// </summary>
public class PluginKitBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-kitbind-" + Guid.NewGuid().ToString("N"));

    public PluginKitBindingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The kit's AssemblyVersion is the version every published plugin references. It must stay
    /// what Noctis 1.6.6-1.6.9 shipped (1.6.0.0): raising it makes every plugin built afterwards
    /// unloadable on those versions (FileLoadException before Initialize), even when its plugin.json
    /// allows them. The API level lives in PluginApi.Major/Minor and the package Version instead.
    /// Never move this; a breaking (major) kit is the only reason to.
    /// </summary>
    [Fact]
    public void TheKitsAssemblyVersion_StaysWhatNoctis169Shipped()
    {
        Assert.Equal(new Version(1, 6, 0, 0), typeof(PluginApi).Assembly.GetName().Version);
        Assert.Equal(1, PluginApi.Major);
        Assert.True(PluginApi.Minor >= 8);
    }

    [Fact]
    public void APlugin_BuiltAgainstTheHostsKitVersionOrAnOlderOne_Binds()
    {
        var host = typeof(PluginApi).Assembly.GetName().Version!;
        Assert.Same(typeof(PluginInfo), FieldTypeThroughPluginContext(host));
        Assert.Same(typeof(PluginInfo), FieldTypeThroughPluginContext(new Version(1, 2, 0, 0)));
    }

    [Fact]
    public void APlugin_BuiltAgainstAHigherKitVersion_FailsToLoad()
    {
        var host = typeof(PluginApi).Assembly.GetName().Version!;
        var higher = new Version(host.Major, host.Minor + 2, 0, 0); // what a 1.6.0 -> 1.8.0 bump would have done
        // The default context has the kit at the host's version only: a higher reference is "not found".
        var ex = Assert.ThrowsAny<IOException>(() => FieldTypeThroughPluginContext(higher));
        Assert.Contains($"Version={higher}", ex.Message);
    }

    /// <summary>Loads a plugin assembly whose one field is a kit type referenced at
    /// <paramref name="kitVersion"/>, through <see cref="PluginLoadContext"/>, and resolves that type.</summary>
    private Type FieldTypeThroughPluginContext(Version kitVersion)
    {
        var path = Path.Combine(_dir, $"KitProbe{kitVersion.Major}_{kitVersion.Minor}.dll");
        File.WriteAllBytes(path, ProbeAssembly(Path.GetFileNameWithoutExtension(path), kitVersion));
        var context = new PluginLoadContext(path);
        try
        {
            var asm = context.LoadMain();
            return asm.GetType("Probe", throwOnError: true)!.GetField("Info")!.FieldType;
        }
        finally { context.Unload(); }
    }

    /// <summary>A minimal assembly: <c>public class Probe { public static Noctis.Plugins.PluginInfo Info; }</c>,
    /// referencing the kit at <paramref name="kitVersion"/>.</summary>
    private static byte[] ProbeAssembly(string name, Version kitVersion)
    {
        var md = new MetadataBuilder();
        var runtime = typeof(object).Assembly.GetName();
        md.AddAssembly(md.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.Sha1);
        md.AddModule(0, md.GetOrAddString(name + ".dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
        var corlib = md.AddAssemblyReference(md.GetOrAddString(runtime.Name!), runtime.Version!, default,
            md.GetOrAddBlob(runtime.GetPublicKeyToken()!), 0, default);
        var kit = md.AddAssemblyReference(md.GetOrAddString("Noctis.Plugins.Abstractions"), kitVersion, default, default, 0, default);
        var objectRef = md.AddTypeReference(corlib, md.GetOrAddString("System"), md.GetOrAddString("Object"));
        var infoRef = md.AddTypeReference(kit, md.GetOrAddString("Noctis.Plugins"), md.GetOrAddString("PluginInfo"));

        var sig = new BlobBuilder();
        new BlobEncoder(sig).Field().Type().Type(infoRef, isValueType: false);
        var field = md.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, md.GetOrAddString("Info"), md.GetOrAddBlob(sig));

        md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class, default, md.GetOrAddString("Probe"), objectRef,
            field, MetadataTokens.MethodDefinitionHandle(1));

        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
            new MetadataRootBuilder(md), new BlobBuilder());
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        return blob.ToArray();
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Noctis.Plugins;
using Noctis.Services.Plugins;

namespace Noctis.PluginCheck;

/// <summary>Pass and Note are fine; Red flag needs the reviewer's eyes; Problem blocks listing.</summary>
public enum CheckLevel { Pass, Note, RedFlag, Problem }

public sealed record CheckItem(CheckLevel Level, string Text);

/// <summary>Everything a check found, plus the plugins/index.json entry when one could be built.</summary>
public sealed class SubmissionResult
{
    public List<CheckItem> Items { get; } = new();
    public PluginManifest? Manifest { get; set; }
    public PackageScan? Scan { get; set; }
    public JsonObject? Entry { get; set; }
    public string? ComputedSha256 { get; set; }
    public long? ZipSize { get; set; }

    /// <summary>No problems: the entry may go into plugins/index.json once a reviewer approves it.</summary>
    public bool CanBeListed => Entry is not null && !Items.Any(i => i.Level == CheckLevel.Problem);
    public int Count(CheckLevel level) => Items.Count(i => i.Level == level);

    internal void Add(CheckLevel level, string text) => Items.Add(new CheckItem(level, text));
}

/// <summary>
/// The automatic half of a plugin review (docs/PLUGINS.md, "Reviewing a submission"). The zip
/// and its plugin.json go through <see cref="PluginInstaller.Inspect"/> and the built entry
/// through <see cref="PluginCatalog.Parse"/>: the same code the app runs on Install from file
/// and on Get plugins, so a submission that passes here installs there. Plugin code is never
/// loaded; <see cref="PackageScanner"/> reads assemblies as metadata.
/// </summary>
public static class SubmissionCheck
{
    /// <summary>Ids the Noctis team's own plugins use.</summary>
    internal const string ReservedIdPrefix = "dev.noctis.";

    /// <param name="zipPath">The downloaded zip, or null when it could not be fetched (<paramref name="downloadError"/> says why).</param>
    /// <param name="indexJson">plugins/index.json as it is now, to spot updates and id clashes.</param>
    /// <param name="remote">What the GitHub API said, or null when not asked (offline run).</param>
    public static SubmissionResult Run(SubmissionForm form, string? zipPath, string? downloadError, string? indexJson, RemoteFacts? remote)
    {
        var r = new SubmissionResult();
        CheckForm(form, r);
        CheckRemote(form, remote, r);

        if (zipPath is null)
        {
            r.Add(CheckLevel.Problem, downloadError ?? "The release zip was not downloaded.");
            return r;
        }

        r.ZipSize = new FileInfo(zipPath).Length;
        using (var fs = File.OpenRead(zipPath)) r.ComputedSha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        var declared = form[SubmissionForm.Sha256].Trim().ToLowerInvariant();
        if (declared == r.ComputedSha256) r.Add(CheckLevel.Pass, $"SHA-256 matches the downloaded zip ({r.ZipSize} bytes).");
        else r.Add(CheckLevel.Problem, $"SHA-256 doesn't match: the downloaded zip is {Code(r.ComputedSha256)}, the form says {Code(declared)}.");
        if (remote?.Asset is { AssetFound: true } asset)
        {
            if (asset.Size is { } size && size != r.ZipSize)
                r.Add(CheckLevel.Problem, $"GitHub lists the asset as {size} bytes, but {r.ZipSize} were downloaded.");
            if (asset.Sha256 is { } digest && digest != r.ComputedSha256)
                r.Add(CheckLevel.Problem, $"GitHub's own SHA-256 for the asset is {Code(digest)}, not the downloaded file's.");
        }

        PluginPackage package;
        try
        {
            package = PluginInstaller.Inspect(zipPath);
            r.Manifest = package.Manifest;
            r.Add(CheckLevel.Pass, $"Noctis accepts the zip and its plugin.json ({package.FileCount} files, {package.TotalBytes} bytes unpacked).");
        }
        catch (PluginInstallException ex)
        {
            r.Add(CheckLevel.Problem, "Noctis would refuse this zip: " + Code(ex.Message));
            return r;
        }

        try { r.Scan = PackageScanner.Scan(zipPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            r.Add(CheckLevel.Problem, "The zip could not be scanned: " + Code(ex.Message));
        }

        CheckManifest(form, package, r);
        if (r.Scan is not null) CheckCode(form, package.Manifest, r.Scan, r);
        BuildEntry(form, package.Manifest, r);
        if (r.Entry is not null) CheckAgainstIndex(r.Entry, indexJson, form, r);
        return r;
    }

    private static void CheckForm(SubmissionForm form, SubmissionResult r)
    {
        var missing = SubmissionForm.Required.Where(f => form[f].Length == 0).Select(f => f.Label).ToList();
        if (missing.Count > 0) r.Add(CheckLevel.Problem, "Not answered: " + string.Join(", ", missing.Select(Code)) + ".");

        var unticked = SubmissionForm.AgreementLabels.Where(l => !form.IsTicked(l)).ToList();
        if (unticked.Count == 0) r.Add(CheckLevel.Pass, "All three agreements are ticked.");
        else r.Add(CheckLevel.Problem, "Not ticked: " + string.Join(" ", unticked.Select(u => "\"" + u + "\"")));

        var id = form[SubmissionForm.Id];
        if (id.Length > 0 && !PluginManifest.IsValidId(id))
            r.Add(CheckLevel.Problem, $"Plugin id {Code(id)} isn't lowercase reverse-DNS style (e.g. `dev.yourname.myplugin`).");
        if (id.StartsWith(ReservedIdPrefix, StringComparison.OrdinalIgnoreCase))
            r.Add(CheckLevel.RedFlag, $"Ids starting with {Code(ReservedIdPrefix)} are for the Noctis team's own plugins.");

        var version = form[SubmissionForm.Version];
        if (version.Length > 0 && !PluginVersion.TryParse(version, out _))
            r.Add(CheckLevel.Problem, $"Version {Code(version)} isn't a semantic version like `1.0.0`.");
        var minApp = form[SubmissionForm.MinAppVersion];
        if (minApp.Length > 0 && !PluginVersion.TryParseLoose(minApp, out _))
            r.Add(CheckLevel.Problem, $"Minimum Noctis version {Code(minApp)} isn't a version like `1.6.4`.");

        if (form[SubmissionForm.Type].Length > 0 && form.TypeValue.Length == 0)
            r.Add(CheckLevel.Problem, $"Type {Code(form[SubmissionForm.Type])} isn't one of the form's choices.");
        if (form[SubmissionForm.Platforms].Length > 0 && form.PlatformValues.Count == 0)
            r.Add(CheckLevel.Problem, $"Platforms {Code(form[SubmissionForm.Platforms])} names none of Windows, macOS, Linux.");

        var description = form[SubmissionForm.Description];
        if (description.Length > 200)
            r.Add(CheckLevel.Problem, $"The short description is {description.Length} characters; keep it under 200 (it's one line in Settings).");
        else
            NoteDescriptionStyle(description, "The form's description", r);
        if (form[SubmissionForm.Name].Length > 60) r.Add(CheckLevel.Problem, "The plugin name is longer than 60 characters.");
        if (form[SubmissionForm.Author].Length > 60) r.Add(CheckLevel.Problem, "The author credit is longer than 60 characters.");

        var sha = form[SubmissionForm.Sha256].Trim();
        if (sha.Length > 0 && (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)))
            r.Add(CheckLevel.Problem, "The SHA-256 isn't 64 hex characters.");

        var source = form[SubmissionForm.Source];
        if (GitHubLinks.TryParseRepo(source, out var so, out var sr))
            r.Add(CheckLevel.Pass, $"Source code: [{so}/{sr}](https://github.com/{so}/{sr}).");
        else if (source.Length > 0)
            r.Add(CheckLevel.Problem, $"Source code {Code(source)} isn't a GitHub repository link (`https://github.com/owner/repo`).");

        var download = form[SubmissionForm.Download];
        if (GitHubLinks.TryParseAsset(download, out var ao, out var ar, out var tag, out var file))
        {
            if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                r.Add(CheckLevel.Problem, $"The release asset {Code(file)} isn't a .zip.");
            if (!string.Equals(ao, so, StringComparison.OrdinalIgnoreCase) || !string.Equals(ar, sr, StringComparison.OrdinalIgnoreCase))
                r.Add(CheckLevel.RedFlag, $"The zip is released from {Code(ao + "/" + ar)}, not from the source repository.");
            else
                r.Add(CheckLevel.Pass, $"The zip is a release asset of the source repository (tag {Code(tag)}).");
        }
        else if (download.Length > 0)
            r.Add(CheckLevel.Problem, $"Release zip {Code(download)} isn't a GitHub release asset link (`https://github.com/owner/repo/releases/download/tag/file.zip`).");

        var authorUrl = form[SubmissionForm.AuthorUrl];
        if (authorUrl.Length > 0 && !(Uri.TryCreate(authorUrl, UriKind.Absolute, out var au) && au.Scheme == Uri.UriSchemeHttps))
            r.Add(CheckLevel.Problem, $"Author link {Code(authorUrl)} isn't an https link.");

        if (form.IsYes(SubmissionForm.Network) && form[SubmissionForm.NetworkDetails].Length == 0)
            r.Add(CheckLevel.Problem, "It uses the internet, but \"What it connects to\" is empty.");
        if (form.IsYes(SubmissionForm.Files) && form[SubmissionForm.FilesDetails].Length == 0)
            r.Add(CheckLevel.Problem, "It uses files outside its folder, but \"Which files and why\" is empty.");

        var license = form[SubmissionForm.License];
        if (license.Length > 0) r.Add(CheckLevel.Note, $"Licence (as stated): {Code(license)}.");
    }

    private static void CheckRemote(SubmissionForm form, RemoteFacts? remote, SubmissionResult r)
    {
        if (remote is null)
        {
            r.Add(CheckLevel.Note, "GitHub API not asked (offline run): repository visibility and the release were not checked.");
            return;
        }
        if (remote.Error is not null)
        {
            r.Add(CheckLevel.RedFlag, "The GitHub API couldn't be read, so the repository and release weren't checked: " + Code(remote.Error));
            return;
        }
        if (remote.Source is { } s)
        {
            if (!s.Found || s.IsPrivate) r.Add(CheckLevel.Problem, "The source repository isn't public (or doesn't exist). Reviews need public source.");
            else
            {
                r.Add(CheckLevel.Pass, "The source repository is public.");
                if (s.IsArchived) r.Add(CheckLevel.RedFlag, "The source repository is archived.");
                if (s.IsFork) r.Add(CheckLevel.Note, "The source repository is a fork; check the plugin is the submitter's own work.");
                var stated = form[SubmissionForm.License];
                if (s.LicenseSpdx is null or "NOASSERTION")
                    r.Add(CheckLevel.RedFlag, "GitHub finds no licence file in the repository.");
                else if (stated.Length > 0 && !stated.Contains(s.LicenseSpdx, StringComparison.OrdinalIgnoreCase))
                    r.Add(CheckLevel.RedFlag, $"The repository's licence is {Code(s.LicenseSpdx)}, the form says {Code(stated)}.");
            }
        }
        if (remote.Asset is { } a)
        {
            if (!a.ReleaseFound) r.Add(CheckLevel.Problem, "No public release with that tag in the repository the zip link points at.");
            else if (!a.AssetFound) r.Add(CheckLevel.Problem, "The release has no asset with that file name.");
            else if (a.IsDraft) r.Add(CheckLevel.Problem, "The release is a draft.");
            else r.Add(CheckLevel.Pass, "The release and its asset exist on GitHub.");
        }
    }

    private static void CheckManifest(SubmissionForm form, PluginPackage package, SubmissionResult r)
    {
        var m = package.Manifest;
        var mismatches = new List<string>();
        void Same(string what, string formValue, string manifestValue, bool ignoreCase = false)
        {
            if (formValue.Length > 0 && !string.Equals(formValue, manifestValue, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                mismatches.Add($"{what}: form {Code(formValue)}, plugin.json {Code(manifestValue)}");
        }
        Same("id", form[SubmissionForm.Id], m.Id);
        Same("name", form[SubmissionForm.Name], m.Name);
        Same("version", form[SubmissionForm.Version], m.Version);
        Same("type", form.TypeValue, m.Type);
        if (mismatches.Count == 0) r.Add(CheckLevel.Pass, "plugin.json has the id, name, version and type the form gives.");
        else r.Add(CheckLevel.Problem, "plugin.json doesn't match the form (" + string.Join("; ", mismatches) + ").");

        var formMin = form[SubmissionForm.MinAppVersion];
        if (m.MinAppVersion is null)
            r.Add(CheckLevel.Problem, "plugin.json has no `minAppVersion`; add the oldest Noctis it works with.");
        else if (formMin.Length > 0 && PluginVersion.TryParseLoose(formMin, out var fv) && PluginVersion.TryParseLoose(m.MinAppVersion, out var mv) && fv != mv)
            r.Add(CheckLevel.Note, $"Minimum Noctis version: the form says {Code(formMin)}, plugin.json {Code(m.MinAppVersion)}; the list uses plugin.json's.");

        var formPlatforms = form.PlatformValues;
        if (m.Platforms.Count > 0 && formPlatforms.Count > 0 && !formPlatforms.Order().SequenceEqual(m.Platforms.Order()))
            r.Add(CheckLevel.Note, $"Platforms: the form says {Code(string.Join(", ", formPlatforms))}, plugin.json {Code(string.Join(", ", m.Platforms))}; the list uses plugin.json's.");

        if (m.IsDotnet)
        {
            if (PluginManifest.TryParseApiVersion(m.ApiVersion, out var major, out _) && major != PluginApi.Major)
                r.Add(CheckLevel.Problem, $"Built for plugin API {Code(m.ApiVersion)}; Noctis has API {PluginApi.Version}, so it would be refused.");
            else
                r.Add(CheckLevel.Pass, $"Plugin API {Code(m.ApiVersion)} (Noctis has {PluginApi.Version}).");
        }

        foreach (var w in m.Warnings) r.Add(CheckLevel.Note, "plugin.json: " + Code(w));
        if (!string.Equals(m.Description, form[SubmissionForm.Description], StringComparison.Ordinal))
            NoteDescriptionStyle(m.Description, "plugin.json's description", r);
        var credit = form[SubmissionForm.Author];
        if (m.Author.Length > 0 && credit.Length > 0 && !string.Equals(m.Author, credit, StringComparison.OrdinalIgnoreCase))
            r.Add(CheckLevel.Note, $"plugin.json's author is {Code(m.Author)}; the list will credit {Code(credit)}.");
        if (m.IsDotnet)
            r.Add(CheckLevel.Note, "Permissions in plugin.json: " + (m.Permissions.Count == 0 ? "none (playback.read only)" : string.Join(", ", m.Permissions.Select(Code))) + ".");
    }

    private static void CheckCode(SubmissionForm form, PluginManifest m, PackageScan scan, SubmissionResult r)
    {
        void Flag(IReadOnlyList<string> files, string what)
        {
            if (files.Count > 0) r.Add(CheckLevel.RedFlag, $"{what}: {List(files)}.");
        }
        Flag(scan.Executables, "Programs inside the zip");
        Flag(scan.Scripts, "Scripts inside the zip");
        Flag(scan.NativeBinaries, "Native (non-.NET) binaries inside the zip");
        Flag(scan.Archives, "Archives inside the zip (their contents can't be checked)");
        Flag(scan.Skipped, "Files too big or unreadable to inspect");
        if (scan.HostAssemblies.Count > 0)
            r.Add(CheckLevel.Note, $"Ships assemblies Noctis already provides (leave them out): {List(scan.HostAssemblies)}.");
        if (scan.Executables.Count + scan.Scripts.Count + scan.NativeBinaries.Count + scan.Archives.Count == 0)
            r.Add(CheckLevel.Pass, "No programs, scripts, native binaries or archives inside the zip.");

        foreach (var a in scan.Assemblies)
        {
            if (a.ObfuscationMarkers.Count > 0)
                r.Add(CheckLevel.RedFlag, $"{Code(a.Path)} looks obfuscated ({string.Join(", ", a.ObfuscationMarkers.Select(Code))}). Obfuscated plugins can't be reviewed.");
            if (a.EmbeddedBinaries.Count > 0)
                r.Add(CheckLevel.RedFlag, $"{Code(a.Path)} carries binaries as embedded resources: {List(a.EmbeddedBinaries)}.");
            if (a.NativeLibraries.Count > 0)
                r.Add(CheckLevel.Note, $"{Code(a.Path)} calls into native libraries: {List(a.NativeLibraries)}.");
        }

        var usesNetwork = scan.UsersOf(ApiUse.Network).ToList();
        var declaresNetwork = m.Permissions.Contains(PluginPermissions.Network);
        if (usesNetwork.Count > 0)
        {
            if (!declaresNetwork)
                r.Add(CheckLevel.Problem, $"The code uses network APIs ({List(usesNetwork)}) but plugin.json doesn't declare the `network` permission.");
            if (!form.IsYes(SubmissionForm.Network))
                r.Add(CheckLevel.Problem, $"The code uses network APIs ({List(usesNetwork)}) but the form says it never connects to the internet.");
        }
        else if (declaresNetwork || form.IsYes(SubmissionForm.Network))
            r.Add(CheckLevel.Note, "Internet access is declared, but no network API was found in the code (it may come through a library).");
        if (declaresNetwork && !form.IsYes(SubmissionForm.Network))
            r.Add(CheckLevel.Problem, "plugin.json declares `network`, but the form says it never connects to the internet.");

        foreach (var (use, why) in new[]
                 {
                     (ApiUse.Process, "Starts other programs"),
                     (ApiUse.DynamicCode, "Loads or generates code at run time"),
                     (ApiUse.NativeInterop, "Calls native code"),
                     (ApiUse.Registry, "Uses the Windows registry"),
                 })
        {
            var users = scan.UsersOf(use).ToList();
            if (users.Count > 0) r.Add(CheckLevel.RedFlag, $"{why}: {List(users)}. Check what for in the source.");
        }

        var files = scan.UsersOf(ApiUse.Files).ToList();
        if (files.Count > 0 && !form.IsYes(SubmissionForm.Files))
            r.Add(CheckLevel.Note, $"Uses file APIs ({List(files)}); the form says only its own folder. Check the paths in the source.");
    }

    private static void BuildEntry(SubmissionForm form, PluginManifest m, SubmissionResult r)
    {
        if (r.ComputedSha256 is null || r.ZipSize is null) return;
        if (!GitHubLinks.TryParseRepo(form[SubmissionForm.Source], out var so, out var sr)) return;

        // plugin.json is the source of truth for what the app compares (id, name, version, type,
        // minAppVersion, platforms); the form supplies the credit, the blurb and the links.
        var platforms = m.Platforms.Count > 0 ? m.Platforms : form.PlatformValues.Count > 0 ? form.PlatformValues : new[] { "windows", "macos", "linux" };
        var entry = new JsonObject
        {
            ["id"] = m.Id,
            ["name"] = m.Name,
            ["author"] = form[SubmissionForm.Author],
        };
        if (form[SubmissionForm.AuthorUrl] is { Length: > 0 } authorUrl && Uri.TryCreate(authorUrl, UriKind.Absolute, out var au) && au.Scheme == Uri.UriSchemeHttps)
            entry["authorUrl"] = au.AbsoluteUri;
        entry["description"] = form[SubmissionForm.Description].ReplaceLineEndings(" ");
        entry["version"] = m.Version;
        if (m.MinAppVersion is not null) entry["minAppVersion"] = m.MinAppVersion;
        entry["type"] = m.Type;
        entry["platforms"] = new JsonArray(platforms.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        entry["download"] = form[SubmissionForm.Download].Trim();
        entry["sha256"] = r.ComputedSha256;
        entry["size"] = r.ZipSize.Value;
        entry["homepage"] = $"https://github.com/{so}/{sr}";

        // The app's own parser must take it, with no warning, exactly as it reads the live list.
        var probe = new JsonObject { ["schema"] = PluginCatalog.Schema, ["plugins"] = new JsonArray(entry.DeepClone()) };
        var catalog = PluginCatalog.Parse(probe.ToJsonString());
        if (catalog.Plugins.Count == 1 && catalog.Warnings.Count == 0)
        {
            r.Entry = entry;
            r.Add(CheckLevel.Pass, "Noctis's plugin list reader accepts the entry below.");
        }
        else
            r.Add(CheckLevel.Problem, "Noctis's plugin list reader refuses the entry: " + Code(string.Join(" ", catalog.Warnings)));
    }

    private static void CheckAgainstIndex(JsonObject entry, string? indexJson, SubmissionForm form, SubmissionResult r)
    {
        if (indexJson is null) return;
        PluginCatalog current;
        try { current = PluginCatalog.Parse(indexJson); }
        catch (PluginCatalogException ex)
        {
            r.Add(CheckLevel.RedFlag, "plugins/index.json couldn't be read: " + Code(ex.Message));
            return;
        }
        var id = (string)entry["id"]!;
        var version = (string)entry["version"]!;
        var listed = current.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (listed is null)
        {
            r.Add(CheckLevel.Pass, "New plugin: the id isn't on the list yet.");
            var sameName = current.Plugins.FirstOrDefault(p => string.Equals(p.Name, (string?)entry["name"], StringComparison.OrdinalIgnoreCase));
            if (sameName is not null) r.Add(CheckLevel.RedFlag, $"Another listed plugin ({Code(sameName.Id)}) already has this name.");
            return;
        }
        if (PluginVersion.Compare(version, listed.Version) <= 0)
            r.Add(CheckLevel.Problem, $"{Code(id)} {Code(listed.Version)} is already listed; an update needs a higher version than that.");
        else
            r.Add(CheckLevel.Pass, $"Update of a listed plugin: {Code(listed.Version)} → {Code(version)}.");
        // Same repository = same owner/repo at the start of the homepage (the team's own entries
        // point into a folder of this repo: .../tree/main/plugins/...).
        var newRepo = ((string?)entry["homepage"] ?? "").TrimEnd('/') + "/";
        var listedPage = (listed.Homepage ?? "").TrimEnd('/') + "/";
        if (!listedPage.StartsWith(newRepo, StringComparison.OrdinalIgnoreCase))
            r.Add(CheckLevel.RedFlag, $"{Code(id)} is listed with the homepage {Code(listed.Homepage ?? "(none)")}, but this comes from another repository. Only its author can update it.");
        if (!string.Equals(listed.Author, form[SubmissionForm.Author], StringComparison.OrdinalIgnoreCase))
            r.Add(CheckLevel.Note, $"The credit changes from {Code(listed.Author)} to {Code(form[SubmissionForm.Author])}.");
    }

    // ── Markdown helpers. Every submitted value is shown as inline code: one line, capped,
    //    backticks swapped, so it can't add links, mentions, HTML or headings to the comment. ──

    /// <summary>The card shows two lines of a description (docs/PLUGINS.md, "Writing your description").</summary>
    internal const int DescriptionNoteLength = 56;

    /// <summary>Notes, not problems: a description over <see cref="DescriptionNoteLength"/>
    /// characters, or with an em dash. The reviewer can ask for a shorter, plainer one.</summary>
    internal static void NoteDescriptionStyle(string description, string what, SubmissionResult r)
    {
        if (description.Length > DescriptionNoteLength)
            r.Add(CheckLevel.Note, $"{what} is {description.Length} characters; the card shows two lines, so keep it to one short sentence of {DescriptionNoteLength} or fewer.");
        if (description.Contains('\u2014'))
            r.Add(CheckLevel.Note, $"{what} has an em dash; use a comma or a colon (plugin descriptions are plain sentences).");
    }

    internal static string Code(string? value)
    {
        var s = Text(value).Replace('`', '\'');
        return s.Length == 0 ? "` `" : "`" + s + "`";
    }

    /// <summary>One line, at most 200 characters.</summary>
    internal static string Text(string? value)
    {
        var s = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return s.Length > 200 ? s[..199] + "…" : s;
    }

    private static string List(IReadOnlyCollection<string> items)
        => string.Join(", ", items.Take(10).Select(Code)) + (items.Count > 10 ? $" and {items.Count - 10} more" : "");
}

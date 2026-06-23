using System.Text.RegularExpressions;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression guard for the v1.2.0 startup crash: an avares icon reference whose
/// case did not match the committed file name
/// (e.g. "Queue icon.png" vs the file "Queue ICON.png"). Avalonia's resource
/// manifest — and the macOS/Linux filesystems — are case-sensitive, so the
/// mismatch threw FileNotFoundException at runtime (MainWindow startup), which
/// the build and unit tests never caught because resources resolve at runtime.
///
/// This test scans every source reference to Assets/Icons/*.png and asserts it
/// resolves to a real committed file, case-sensitively.
/// </summary>
public class IconResourceReferenceTests
{
    [Fact]
    public void EveryIconReference_MatchesACommittedFile_CaseSensitively()
    {
        var repoRoot = FindRepoRoot();
        var iconsDir = Path.Combine(repoRoot, "src", "Noctis", "Assets", "Icons");
        Assert.True(Directory.Exists(iconsDir), $"Icons directory not found: {iconsDir}");

        // Authoritative file set, exact case as on disk (== committed in a clean checkout).
        var actual = new HashSet<string>(
            Directory.GetFiles(iconsDir, "*.png").Select(p => Path.GetFileName(p)!),
            StringComparer.Ordinal);

        var srcDir = Path.Combine(repoRoot, "src", "Noctis");
        var sources = Directory.EnumerateFiles(srcDir, "*.*", SearchOption.AllDirectories)
            .Where(p =>
            {
                var norm = p.Replace('\\', '/');
                return (norm.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                        || norm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                       && !norm.Contains("/bin/")
                       && !norm.Contains("/obj/");
            });

        var rx = new Regex(@"Assets/Icons/([^""'<>)]+?\.png)");
        var mismatches = new List<string>();

        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in rx.Matches(text))
            {
                var name = m.Groups[1].Value.Replace("%20", " "); // decode URL-encoded spaces
                if (!actual.Contains(name))
                {
                    var ci = actual.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                    var hint = ci is null ? "(no such file)" : $"(case mismatch — file is '{ci}')";
                    mismatches.Add($"{Path.GetFileName(file)}: 'Assets/Icons/{name}' {hint}");
                }
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "Icon references that won't resolve at runtime (avares is case-sensitive):\n  "
            + string.Join("\n  ", mismatches.Distinct()));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))
                || Directory.Exists(Path.Combine(dir.FullName, "src", "Noctis", "Assets", "Icons")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}

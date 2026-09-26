using Xunit;

namespace Noctis.Tests;

/// <summary>
/// macOS/Linux startup failures (libvlc not loading etc.) used to go to stderr only,
/// which a Finder/Dock/login launch never shows. Program.Main now raises a native
/// dialog; these pin the commands it runs.
/// </summary>
public class StartupErrorAlertTests
{
    private const string CrashLog = "/Users/me/Library/Application Support/Noctis/crash.log";

    [Fact]
    public void MacOS_ShowsCriticalAlert_WithMessageAsArgument()
    {
        const string error = "libvlc is required but was not found. Install VLC \"now\" \\ or via Homebrew";
        var cmds = global::Noctis.Program.StartupErrorAlertCommands(error, CrashLog, macOS: true);

        var (file, args) = Assert.Single(cmds);
        Assert.Equal("/usr/bin/osascript", file);
        Assert.Contains(args, a => a.Contains("display alert") && a.Contains("as critical"));

        // The text rides as argv, so quotes/backslashes can't break the script.
        var body = args[^1];
        Assert.Contains(error, body);
        Assert.Contains(CrashLog, body);
        Assert.DoesNotContain(args[..^1], a => a.Contains("libvlc"));
    }

    [Fact]
    public void Linux_TriesZenityThenKdialog_WithMarkupEscapedForZenity()
    {
        const string error = "open <libvlc.so.5> & plugins failed";
        var cmds = global::Noctis.Program.StartupErrorAlertCommands(error, CrashLog, macOS: false);

        Assert.Equal(new[] { "zenity", "kdialog" }, cmds.Select(c => c.FileName));

        var zenityText = Assert.Single(cmds[0].Args, a => a.StartsWith("--text="));
        Assert.Contains("open &lt;libvlc.so.5&gt; &amp; plugins failed", zenityText);
        Assert.Contains(CrashLog, zenityText);
        Assert.Contains("--error", cmds[0].Args);

        Assert.Contains("--error", cmds[1].Args);
        Assert.Contains(cmds[1].Args, a => a.Contains(error) && a.Contains(CrashLog));
    }

    [Fact]
    public void Program_NonWindowsCatch_ShowsTheAlert()
    {
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "Program.cs"));
        var elseBranch = program[program.IndexOf("Console.Error.WriteLine($\"Noctis failed to start", StringComparison.Ordinal)..];
        Assert.Contains("ShowStartupErrorAlert(ex.Message", elseBranch[..600]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}

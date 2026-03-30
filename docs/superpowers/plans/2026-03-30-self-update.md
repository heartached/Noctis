# Self-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add in-app self-update via GitHub Releases — check for new version, download installer, launch silent upgrade.

**Architecture:** New `UpdateService` calls GitHub Releases API, compares versions, downloads the Setup.exe asset to `%TEMP%`, validates file size, then launches it with `/SILENT`. SettingsViewModel exposes update state/commands. About section in SettingsView gets update UI.

**Tech Stack:** .NET 8, Avalonia, CommunityToolkit.Mvvm, System.Text.Json, HttpClient (existing)

---

### Task 1: Create UpdateService

**Files:**
- Create: `src/Noctis/Services/UpdateService.cs`

- [ ] **Step 1: Create the UpdateService class with version check**

```csharp
// src/Noctis/Services/UpdateService.cs
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Noctis.Services;

public sealed class UpdateService
{
    private const string ReleaseUrl = "https://api.github.com/repos/heartached/Noctis/releases/latest";
    private const string AssetPrefix = "Noctis-v";
    private const string AssetSuffix = "-Setup.exe";

    private readonly HttpClient _http;

    public UpdateService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Current assembly version as a comparable Version object.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Current version formatted for display, e.g. "Version 1.0.2".</summary>
    public static string CurrentVersionDisplay
    {
        get
        {
            var v = CurrentVersion;
            return $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Checks the latest GitHub release. Returns null if up-to-date or on error.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUrl);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
        if (release is null || string.IsNullOrEmpty(release.TagName))
            return null;

        // Parse tag like "v1.0.3" -> Version(1, 0, 3)
        var tagVersion = ParseTag(release.TagName);
        if (tagVersion is null || tagVersion <= CurrentVersion)
            return null;

        // Find the Setup.exe asset
        var installerAsset = release.Assets?.FirstOrDefault(a =>
            a.Name != null &&
            a.Name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo
        {
            TagName = release.TagName,
            Version = tagVersion,
            InstallerUrl = installerAsset?.BrowserDownloadUrl,
            InstallerSize = installerAsset?.Size ?? 0,
            ReleaseUrl = release.HtmlUrl ?? $"https://github.com/heartached/Noctis/releases/tag/{release.TagName}"
        };
    }

    /// <summary>
    /// Downloads the installer to %TEMP%. Reports progress 0-100.
    /// Returns the path to the downloaded file.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        string url, long expectedSize,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "Noctis-Update-Setup.exe");

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long bytesRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                bytesRead += read;

                if (totalBytes > 0)
                    progress?.Report((double)bytesRead / totalBytes * 100.0);
            }

            await fileStream.FlushAsync(ct);

            // Validate file size if GitHub reported one
            if (expectedSize > 0)
            {
                var actualSize = new FileInfo(tempPath).Length;
                if (actualSize != expectedSize)
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"Download corrupted: expected {expectedSize} bytes, got {actualSize}.");
                }
            }

            return tempPath;
        }
        catch
        {
            // Clean up partial download
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Launches the downloaded Inno Setup installer with /SILENT and returns true.
    /// The caller should exit the app after this returns.
    /// </summary>
    public bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
            return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT",
            UseShellExecute = true  // triggers UAC elevation prompt
        });

        return true;
    }

    private static Version? ParseTag(string tag)
    {
        // Strip leading 'v' or 'V'
        var raw = tag.TrimStart('v', 'V');
        return Version.TryParse(raw, out var v) ? v : null;
    }

    // ── GitHub API DTOs ──

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed class UpdateInfo
{
    public required string TagName { get; init; }
    public required Version Version { get; init; }
    public string? InstallerUrl { get; init; }
    public long InstallerSize { get; init; }
    public required string ReleaseUrl { get; init; }
}
```

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/UpdateService.cs
git commit -m "Add UpdateService for GitHub release checking and download"
```

---

### Task 2: Register UpdateService in DI

**Files:**
- Modify: `src/Noctis/Program.cs:77-116`

- [ ] **Step 1: Add UpdateService registration**

In `ConfigureServices`, add this line after the `ArtistImageService` registration (after line 114):

```csharp
services.AddSingleton<UpdateService>();
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Program.cs
git commit -m "Register UpdateService in DI container"
```

---

### Task 3: Add update state and commands to SettingsViewModel

**Files:**
- Modify: `src/Noctis/ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Add UpdateService field and observable properties**

Add a new field alongside the other private fields (near line 25):

```csharp
private UpdateService? _updateService;
private CancellationTokenSource? _updateCts;
private string? _downloadedInstallerPath;
```

Add observable properties in the "About" section (after line 172, replacing the hardcoded `AppVersion`):

```csharp
public string AppVersion => UpdateService.CurrentVersionDisplay;

[ObservableProperty] private string _updateStatusText = "";
[ObservableProperty] private bool _isCheckingForUpdate;
[ObservableProperty] private bool _isUpdateAvailable;
[ObservableProperty] private bool _isDownloadingUpdate;
[ObservableProperty] private double _downloadProgress;
[ObservableProperty] private bool _isReadyToInstall;
[ObservableProperty] private string _latestVersionTag = "";
```

- [ ] **Step 2: Add setter method matching existing pattern**

Add after `SetArtistImageService` (after line 218):

```csharp
public void SetUpdateService(UpdateService updateService) => _updateService = updateService;
```

- [ ] **Step 3: Add CheckForUpdate command**

Add before the `OpenGitHub` command (before line 1266):

```csharp
[RelayCommand]
private async Task CheckForUpdateAsync()
{
    if (_updateService is null || IsCheckingForUpdate) return;

    // Reset state
    IsUpdateAvailable = false;
    IsDownloadingUpdate = false;
    IsReadyToInstall = false;
    DownloadProgress = 0;
    UpdateStatusText = "Checking for updates...";
    IsCheckingForUpdate = true;
    _downloadedInstallerPath = null;

    try
    {
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var update = await _updateService.CheckForUpdateAsync(_updateCts.Token);

        if (update is null)
        {
            UpdateStatusText = "You're on the latest version.";
            _ = ClearUpdateStatusAfterDelay();
        }
        else if (update.InstallerUrl is null)
        {
            LatestVersionTag = update.TagName;
            UpdateStatusText = $"{update.TagName} available — installer not found. Visit GitHub.";
        }
        else
        {
            LatestVersionTag = update.TagName;
            UpdateStatusText = $"{update.TagName} is available.";
            IsUpdateAvailable = true;
        }
    }
    catch (OperationCanceledException)
    {
        UpdateStatusText = "Update check timed out. Try again later.";
        _ = ClearUpdateStatusAfterDelay();
    }
    catch
    {
        UpdateStatusText = "Couldn't check for updates. Try again later.";
        _ = ClearUpdateStatusAfterDelay();
    }
    finally
    {
        IsCheckingForUpdate = false;
    }
}

[RelayCommand]
private async Task DownloadUpdateAsync()
{
    if (_updateService is null || IsDownloadingUpdate) return;

    IsUpdateAvailable = false;
    IsDownloadingUpdate = true;
    DownloadProgress = 0;
    UpdateStatusText = "Downloading update...";

    try
    {
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Re-check to get fresh URL
        var update = await _updateService.CheckForUpdateAsync(_updateCts.Token);
        if (update?.InstallerUrl is null)
        {
            UpdateStatusText = "Update no longer available.";
            IsDownloadingUpdate = false;
            _ = ClearUpdateStatusAfterDelay();
            return;
        }

        var progress = new Progress<double>(p =>
            Dispatcher.UIThread.Post(() =>
            {
                DownloadProgress = p;
                UpdateStatusText = $"Downloading update... {p:F0}%";
            }));

        _downloadedInstallerPath = await _updateService.DownloadInstallerAsync(
            update.InstallerUrl, update.InstallerSize, progress, _updateCts.Token);

        UpdateStatusText = "Update ready to install.";
        IsReadyToInstall = true;
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("corrupted"))
    {
        UpdateStatusText = "Download corrupted. Try again.";
        _ = ClearUpdateStatusAfterDelay();
    }
    catch (OperationCanceledException)
    {
        UpdateStatusText = "Download timed out. Try again.";
        _ = ClearUpdateStatusAfterDelay();
    }
    catch
    {
        UpdateStatusText = "Download failed. Try again.";
        _ = ClearUpdateStatusAfterDelay();
    }
    finally
    {
        IsDownloadingUpdate = false;
    }
}

[RelayCommand]
private void InstallUpdate()
{
    if (_updateService is null || string.IsNullOrEmpty(_downloadedInstallerPath)) return;

    if (_updateService.LaunchInstaller(_downloadedInstallerPath))
    {
        // Shut down the app so Inno Setup can replace files
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown(0);
        }
    }
    else
    {
        UpdateStatusText = "Couldn't start installer. Download manually from GitHub.";
        IsReadyToInstall = false;
    }
}

private async Task ClearUpdateStatusAfterDelay()
{
    await Task.Delay(5000);
    if (!IsUpdateAvailable && !IsDownloadingUpdate && !IsReadyToInstall)
        UpdateStatusText = "";
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/ViewModels/SettingsViewModel.cs
git commit -m "Add update check, download, and install commands to SettingsViewModel"
```

---

### Task 4: Wire UpdateService into MainWindowViewModel

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs`

The existing pattern is that MainWindowViewModel resolves services from DI and calls setter methods on SettingsViewModel. Find where `SetArtistImageService` is called and add a similar line right after it:

- [ ] **Step 1: Add SetUpdateService call**

Find the line that calls `_settings.SetArtistImageService(...)` and add immediately after:

```csharp
_settings.SetUpdateService(App.Services!.GetRequiredService<UpdateService>());
```

Make sure `using Noctis.Services;` is in the file's usings (it likely already is).

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/MainWindowViewModel.cs
git commit -m "Wire UpdateService into SettingsViewModel via MainWindowViewModel"
```

---

### Task 5: Update the About section UI

**Files:**
- Modify: `src/Noctis/Views/SettingsView.axaml:1432-1447`

- [ ] **Step 1: Replace the single GitHub+updates button with split buttons and update area**

Replace the entire `<Button Command="{Binding OpenGitHubCommand}"...>` block (lines 1432-1446) with:

```xml
<!-- GitHub link -->
<Button Command="{Binding OpenGitHubCommand}"
        Background="Transparent" BorderThickness="0"
        Padding="0" Cursor="Hand"
        HorizontalAlignment="Left">
    <StackPanel Orientation="Horizontal" Spacing="8"
                VerticalAlignment="Center">
        <PathIcon Width="18" Height="18"
                  Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"
                  Data="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z" />
        <TextBlock Text="Star on GitHub"
                   FontSize="13" Opacity="0.6"
                   VerticalAlignment="Center" />
    </StackPanel>
</Button>

<!-- Update section -->
<StackPanel Spacing="8" Margin="0,4,0,0">
    <!-- Check for updates button -->
    <Button Command="{Binding CheckForUpdateCommand}"
            Background="Transparent" BorderThickness="0"
            Padding="0" Cursor="Hand"
            HorizontalAlignment="Left"
            IsVisible="{Binding !IsReadyToInstall}">
        <StackPanel Orientation="Horizontal" Spacing="8"
                    VerticalAlignment="Center">
            <PathIcon Width="16" Height="16"
                      Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"
                      Data="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 15v-4H7l5-7v4h4l-5 7z" />
            <TextBlock Text="Check for updates"
                       FontSize="13" Opacity="0.6"
                       VerticalAlignment="Center" />
        </StackPanel>
    </Button>

    <!-- Status text -->
    <TextBlock Text="{Binding UpdateStatusText}"
               FontSize="12" Opacity="0.7"
               IsVisible="{Binding UpdateStatusText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />

    <!-- Download progress bar -->
    <ProgressBar Minimum="0" Maximum="100"
                 Value="{Binding DownloadProgress}"
                 IsVisible="{Binding IsDownloadingUpdate}"
                 Height="4" CornerRadius="2"
                 Margin="0,2" />

    <!-- Update Now button -->
    <Button Command="{Binding DownloadUpdateCommand}"
            IsVisible="{Binding IsUpdateAvailable}"
            Classes="pill"
            HorizontalAlignment="Left"
            Padding="16,6">
        <TextBlock Text="{Binding LatestVersionTag, StringFormat='Update to {0}'}"
                   FontSize="12" />
    </Button>

    <!-- Install & Restart button -->
    <Button Command="{Binding InstallUpdateCommand}"
            IsVisible="{Binding IsReadyToInstall}"
            Classes="pill"
            HorizontalAlignment="Left"
            Padding="16,6">
        <TextBlock Text="Install &amp; Restart"
                   FontSize="12" />
    </Button>
</StackPanel>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/SettingsView.axaml
git commit -m "Add update UI to Settings About section"
```

---

### Task 6: Build and smoke test

- [ ] **Step 1: Full build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded with no errors.

- [ ] **Step 2: Run the app and verify**

Run: `dotnet run --project src/Noctis/Noctis.csproj`

Manual verification:
1. Navigate to Settings > About
2. Confirm "Star on GitHub" and "Check for updates" are separate buttons
3. Click "Check for updates" — should show "Checking..." then "You're on the latest version." (since we're running v1.0.2 which is latest)
4. Status text should auto-clear after ~5 seconds
5. "Star on GitHub" should still open the repo in browser

- [ ] **Step 3: Final commit if any fixups were needed**

```bash
git add -A
git commit -m "Fix any issues from smoke testing"
```

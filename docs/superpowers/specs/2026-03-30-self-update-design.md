# Self-Update via GitHub Releases

**Date:** 2026-03-30
**Status:** Approved

## Overview

Add an in-app update mechanism to Noctis that checks GitHub Releases for new versions, downloads the installer, and launches a silent upgrade. The update button lives in Settings > About.

## Architecture

### New: `Services/UpdateService.cs`

Responsible for all update logic. Injected via DI.

**Version check:**
- `GET https://api.github.com/repos/heartached/Noctis/releases/latest`
- Uses the existing `HttpClient` configured in `Program.cs`
- Parses JSON with `System.Text.Json` (already a dependency)
- Compares remote `tag_name` (e.g. `v1.0.3`) against current assembly version
- Returns: version string, asset download URL, asset file size, release notes URL

**Download:**
- Downloads `Noctis-v{x.y.z}-Setup.exe` asset to `%TEMP%\Noctis-Update-Setup.exe`
- Reports progress via `IProgress<double>` (0-100)
- Validates downloaded file size matches the GitHub API's reported `size` field
- Cleans up temp file on failure

**Install:**
- Launches downloaded installer with `/SILENT` flag (Inno Setup silent mode)
- Signals the app to shut down gracefully after launching installer
- Inno Setup handles: closing old process, replacing files, optional relaunch

### Modified: `ViewModels/SettingsViewModel.cs`

- Inject `UpdateService`
- Replace hardcoded `AppVersion` with assembly metadata: `Assembly.GetExecutingAssembly().GetName().Version`
- New observable properties:
  - `UpdateStatusText` (string) — status message shown to user
  - `IsCheckingForUpdate` (bool) — spinner state
  - `IsUpdateAvailable` (bool) — controls visibility of "Update Now" button
  - `IsDownloadingUpdate` (bool) — controls visibility of progress bar
  - `DownloadProgress` (double, 0-100) — progress bar value
  - `IsReadyToInstall` (bool) — controls visibility of "Install & Restart" button
  - `LatestVersionTag` (string) — e.g. "v1.0.3"
- New commands:
  - `CheckForUpdateCommand` — calls UpdateService.CheckForUpdateAsync()
  - `DownloadUpdateCommand` — calls UpdateService.DownloadUpdateAsync()
  - `InstallUpdateCommand` — calls UpdateService.LaunchInstallerAndExit()

### Modified: `Views/SettingsView.axaml`

In the About section:
- Split "Star on GitHub . Check for updates" into two separate buttons:
  - "Star on GitHub" (existing behavior, opens repo)
  - "Check for updates" (triggers CheckForUpdateCommand)
- Below the buttons, add an update status area:
  - Status text (bound to `UpdateStatusText`)
  - Progress bar (visible during download, bound to `DownloadProgress`)
  - "Update Now" button (visible when `IsUpdateAvailable`)
  - "Install & Restart" button (visible when `IsReadyToInstall`)

## UX Flow

```
[Check for updates] clicked
  -> Spinner + "Checking for updates..."
  -> If up to date:
      "You're on the latest version." (auto-clears after ~5s)
  -> If update available:
      "Version 1.0.3 is available" + [Update Now] button
        -> [Update Now] clicked
        -> "Downloading update... 45%" with progress bar
        -> "Update ready to install." + [Install & Restart] button
          -> Launches silent installer, app exits
          -> Inno Setup upgrades in-place, relaunches app
  -> If error (no internet, API failure, timeout):
      "Couldn't check for updates. Try again later."
```

## Error Handling

| Scenario | Behavior |
|----------|----------|
| No internet / API timeout | "Couldn't check for updates. Try again later." (15s timeout) |
| Download failure / network drop | Clean up temp file, show "Download failed. Try again." |
| File size mismatch after download | Delete temp file, show "Download corrupted. Try again." |
| No matching installer asset in release | "Update available but installer not found. Visit GitHub." + link |
| Installer fails to launch | "Couldn't start installer. Download manually from GitHub." + link |
| Download timeout | 5 minute timeout, same failure message as download failure |

## Security

- All communication over HTTPS (GitHub API and asset CDN)
- File size validation: compare downloaded bytes against GitHub API's `size` field
- Temp file written to `%TEMP%` (user-writable, no elevation needed for download)
- Installer requests UAC elevation as needed (existing Inno Setup behavior)
- No code signing currently (existing limitation — SmartScreen may warn)
- Rate limit: GitHub API allows 60 unauthenticated requests/hour — sufficient for manual checks

## Scope Exclusions

- No background/automatic update checking (manual button only)
- No delta/differential updates (full installer each time)
- No custom rollback beyond Inno Setup's built-in behavior
- No macOS update support
- No update channel selection (always latest non-prerelease)

## Files Changed

| File | Change |
|------|--------|
| New: `Services/UpdateService.cs` | GitHub API client, download, install logic |
| `ViewModels/SettingsViewModel.cs` | Update commands, properties, assembly version |
| `Views/SettingsView.axaml` | Update UI in About section |
| `Program.cs` | Register `UpdateService` in DI container |

## Dependencies

No new NuGet packages. Uses:
- `HttpClient` (existing, configured in Program.cs)
- `System.Text.Json` (existing dependency)
- `System.Diagnostics.Process` (framework built-in)
- `System.Reflection.Assembly` (framework built-in)

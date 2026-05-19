# Package-Manager Distribution for Noctis (Scoop / winget / Chocolatey)

Date: 2026-05-10
Status: Approved (pending written-spec review)

## Goal

Let Windows users install **and update** Noctis through the three common
Windows package managers:

- **Scoop** — via a Noctis-owned bucket repo (`heartached/scoop-bucket`).
- **winget** — via a manifest in `microsoft/winget-pkgs` (identifier `heartached.Noctis`).
- **Chocolatey** — via a `noctis` package on the community feed (`community.chocolatey.org`).

"Update support" here means: when a new GitHub Release is published, each
package manager's metadata is refreshed so `scoop update noctis`,
`winget upgrade heartached.Noctis`, and `choco upgrade noctis` pick up the new
version. It does **not** change the in-app updater.

## Non-goals

- No changes to `src/Noctis/Services/UpdateService.cs` or any in-app behavior.
- No re-hosting of binaries on Chocolatey/Scoop — all three reference the
  existing GitHub Release assets.
- No macOS/Linux package managers (Homebrew, apt, etc.) — out of scope.
- No changes to how releases themselves are produced (the release is still
  cut manually; see "Release flow context").

## Release flow context (current state)

- `.github/workflows/dotnet.yml` only **builds** artifacts and uploads them as
  workflow artifacts. It does not create GitHub Releases.
- Releases are created manually: bump `<Version>` in
  `src/Noctis/Noctis.csproj`, build the 6 artifacts (Windows `Setup.exe` +
  `Noctis-windows-x64.zip`, macOS arm64/x64 `.dmg`, Linux AppImage + arm64
  `.tar.gz`), and publish a GitHub Release with those assets attached.
- Windows assets relevant here:
  - `Noctis-v{version}-Setup.exe` — Inno Setup installer, supports `/SILENT`,
    `PrivilegesRequired=lowest` (per-user install, no UAC required for the
    install itself), `ArchitecturesAllowed=x64compatible`.
  - `Noctis-windows-x64.zip` — portable self-contained publish folder
    including the `libvlc/` native dependencies and `Noctis.exe`.
- User data lives under `%APPDATA%` (not the install dir), so portable
  installs (Scoop) do not need a `persist` directive.

## Artifact mapping

| Manager     | Consumes                              | Why |
|-------------|---------------------------------------|-----|
| winget      | `Noctis-v{version}-Setup.exe`         | winget supports `InstallerType: inno`; the installer already handles Start Menu entries, uninstall registration, per-user install. |
| Scoop       | `Noctis-windows-x64.zip`              | Scoop apps are portable: it extracts the zip and shims `Noctis.exe`. |
| Chocolatey  | `Noctis-v{version}-Setup.exe`         | Standard "installer-wrapper" package: `chocolateyInstall.ps1` calls `Install-ChocolateyPackage` against the GitHub URL + SHA256. |

## Repo layout (new)

```
packaging/
  scoop/
    noctis.json                 # Scoop manifest template (autoupdate-enabled)
  winget/
    heartached.Noctis.installer.yaml
    heartached.Noctis.locale.en-US.yaml
    heartached.Noctis.yaml      # version manifest
  chocolatey/
    noctis.nuspec
    tools/
      chocolateyInstall.ps1
      chocolateyUninstall.ps1
      VERIFICATION.txt
      LICENSE.txt               # copy of repo LICENSE (Chocolatey requires it)
docs/
  RELEASE-PACKAGING.md          # what CI does + manual Chocolatey steps
.github/workflows/
  package-managers.yml          # NEW workflow (see Automation)
```

The `packaging/winget/*` files are templates kept in sync with what's
submitted upstream; the actual submission happens via `komac`/`wingetcreate`
in CI (which generates its own manifest copies for the PR).

## Component details

### Scoop manifest (`packaging/scoop/noctis.json`)

```jsonc
{
  "version": "1.1.4",
  "description": "A music player that respects what's yours",
  "homepage": "https://github.com/heartached/Noctis",
  "license": "MIT",
  "architecture": {
    "64bit": {
      "url": "https://github.com/heartached/Noctis/releases/download/v1.1.4/Noctis-windows-x64.zip",
      "hash": "<sha256 of the zip>"
    }
  },
  "bin": "Noctis.exe",
  "shortcuts": [["Noctis.exe", "Noctis"]],
  "checkver": "github",
  "autoupdate": {
    "architecture": {
      "64bit": {
        "url": "https://github.com/heartached/Noctis/releases/download/v$version/Noctis-windows-x64.zip"
      }
    }
  }
}
```

Notes:
- `checkver`/`autoupdate` make the bucket self-healing: even if the CI push
  step fails, `scoop bucket` maintenance (or `scoop update` against the bucket)
  can refresh hashes from the GitHub release. CI still does the primary push.
- If the zip's top-level layout is a folder rather than files-at-root, add an
  `extract_dir` so `Noctis.exe` resolves. (Verify during implementation by
  inspecting the produced zip; the workflow `tar -czf ... -C publish/...`
  pattern suggests files-at-root, but the Windows zip is produced separately —
  confirm.)

### winget manifests (`packaging/winget/`)

Three-file manifest set targeting schema 1.6+:

- `heartached.Noctis.installer.yaml` — `InstallerType: inno`,
  `Scope: user`, `InstallerSwitches.Silent: /SILENT`,
  `Architecture: x64`, `InstallerUrl` + `InstallerSha256` pointing at the
  release `Setup.exe`, `AppsAndFeaturesEntries` with the Inno `AppId`
  (`{{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}_is1`) and `UpgradeBehavior: install`.
- `heartached.Noctis.locale.en-US.yaml` — publisher `heartached`, app name
  `Noctis`, short description, license, homepage, tags, release notes URL.
- `heartached.Noctis.yaml` — `PackageVersion`, `DefaultLocale: en-US`,
  `ManifestType: version`.

The repo copy is the source of truth for fields that don't change per release
(publisher, description, AppId). CI uses `komac update` which only needs the
version + new installer URL and re-derives the SHA256.

### Chocolatey package (`packaging/chocolatey/`)

- `noctis.nuspec` — `<id>noctis</id>` (fallback `noctis-music-player` if `noctis`
  is taken — decide at first push), title `Noctis`, authors `heartached`,
  `projectUrl`, `licenseUrl`, `iconUrl`, `tags`, `<packageSourceUrl>` pointing
  at this repo, `<docsUrl>`/`<bugTrackerUrl>` as available. Version templated
  from `<Version>` in the csproj.
- `tools/chocolateyInstall.ps1`:
  ```powershell
  $ErrorActionPreference = 'Stop'
  $packageName = 'noctis'
  $version     = $env:ChocolateyPackageVersion
  $url64       = "https://github.com/heartached/Noctis/releases/download/v$version/Noctis-v$version-Setup.exe"
  Install-ChocolateyPackage -PackageName $packageName `
    -FileType 'exe' `
    -SilentArgs '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-' `
    -Url64bit $url64 `
    -Checksum64 '<sha256 of Setup.exe>' -ChecksumType64 'sha256' `
    -ValidExitCodes @(0)
  ```
- `tools/chocolateyUninstall.ps1` — locate the Inno uninstaller via the
  `*_is1` registry key and run it with `/SILENT /SUPPRESSMSGBOXES /NORESTART`
  (use `Get-UninstallRegistryKey` / `Uninstall-ChocolateyPackage`).
- `tools/VERIFICATION.txt` — explains the binary is downloaded from the
  official GitHub Releases and how to verify the checksum.
- `tools/LICENSE.txt` — verbatim copy of the repo `LICENSE` (Chocolatey
  moderation requires `LICENSE.txt` + `VERIFICATION.txt` for packages that
  download binaries).

### Automation (`.github/workflows/package-managers.yml`)

Triggers: `release: { types: [published] }` and `workflow_dispatch`
(with an optional `version`/`tag` input for manual re-runs).

Job (runs on `ubuntu-latest` except where a Windows runner is needed):

1. **Resolve version + asset URLs + checksums.** From the release event (or
   the dispatch input), get the tag `v{version}`. `curl -L` the
   `Noctis-windows-x64.zip` and `Noctis-v{version}-Setup.exe` release assets,
   compute SHA256 for each.
2. **Scoop.** Render `packaging/scoop/noctis.json` with `version`, the zip URL,
   and the zip hash. Clone `heartached/scoop-bucket` using a PAT
   (`secrets.SCOOP_BUCKET_TOKEN`, `repo` scope), write `bucket/noctis.json`,
   commit `noctis: update to {version}` and push. (Bucket repo created
   out-of-band; if it has no `bucket/` dir yet, create it.)
3. **winget.** Run `komac update heartached.Noctis --version {version}
   --urls "<Setup.exe URL>" --token ${{ secrets.WINGET_PAT }} --submit`
   (PAT with `public_repo` scope; komac forks `microsoft/winget-pkgs`, fills
   the SHA256, and opens the PR). Use the official `michidk/run-komac` action
   or install komac directly — decide during implementation.
4. **Chocolatey: NOT in this workflow initially.** Documented manual steps in
   `docs/RELEASE-PACKAGING.md`:
   - update `noctis.nuspec` `<version>` and the `Checksum64` in
     `chocolateyInstall.ps1` to match the new `Setup.exe`,
   - `choco pack packaging/chocolatey/noctis.nuspec`,
   - `choco install noctis -s "packaging/chocolatey;https://community.chocolatey.org/api/v2/" -y` to smoke-test,
   - `choco push noctis.<version>.nupkg --source https://push.chocolatey.org/ --api-key %CHOCO_API_KEY%`,
   - wait for moderation.
   Once a couple of releases have gone through moderation cleanly, a follow-up
   change can add a Windows job to this workflow that does `choco pack`/`push`
   with `secrets.CHOCO_API_KEY`.

The workflow must **fail loudly** (non-zero exit, no `continue-on-error`) on
any step so a missed package update is visible in the Actions tab. Steps are
independent: a Scoop failure should not prevent the winget step from running
(use separate jobs or `if: always()` on later steps with an overall failure if
any failed).

## Out-of-repo prerequisites (the user must do these once)

1. Create the public GitHub repo `heartached/scoop-bucket` (can be empty; CI
   seeds `bucket/noctis.json` on the first release run, or seed it manually
   from `packaging/scoop/noctis.json`).
2. Create a GitHub PAT with `repo` scope on `heartached/scoop-bucket` →
   add as repo secret `SCOOP_BUCKET_TOKEN` on `heartached/Noctis`.
3. Create a GitHub PAT with `public_repo` scope (for forking/PR-ing
   `microsoft/winget-pkgs`) → add as repo secret `WINGET_PAT`. (Note: the
   default `GITHUB_TOKEN` cannot push to a fork in another org, hence a PAT.)
4. Register a Chocolatey community account, generate an API key → keep locally
   for the manual push (and later add as `CHOCO_API_KEY` secret when the
   Chocolatey step is automated).
5. Confirm/claim the Chocolatey package id `noctis`; if taken, the design
   falls back to `noctis-music-player` (update the nuspec + docs accordingly).

## Verification

- **Scoop manifest:** `scoop install .\packaging\scoop\noctis.json` on a clean
  Scoop install — confirm `Noctis.exe` launches and `noctis` appears in
  `scoop list`. `scoop update noctis` no-ops when current.
- **winget manifests:** `winget validate .\packaging\winget` passes; optionally
  `winget install --manifest .\packaging\winget` in a sandbox VM installs and
  launches. After CI, the `winget-pkgs` PR's automated validation passes.
- **Chocolatey package:** `choco pack` produces `noctis.<version>.nupkg` with
  no errors; `choco install noctis -s "packaging/chocolatey;..." -y` in a
  clean VM/Windows Sandbox installs, launches, and `choco uninstall noctis -y`
  removes it cleanly.
- **Workflow:** triggered via `workflow_dispatch` against the existing `v1.1.4`
  release as a dry run (or with a `--dry-run`/no-`--submit` komac flag) before
  relying on it for the next real release.
- All commands run and their output reported per the repo's verification
  policy; if a tool isn't available locally (e.g. `choco` on the dev box's
  shell), the exact commands the user must run are listed instead.

## Risks / unknowns

- The exact internal layout of `Noctis-windows-x64.zip` (files-at-root vs.
  nested folder) determines whether the Scoop manifest needs `extract_dir` —
  must inspect during implementation.
- Chocolatey moderation timing is outside our control; the package may sit in
  the moderation queue for days on first submission.
- `microsoft/winget-pkgs` occasionally changes manifest schema versions;
  `komac` tracks this, but the in-repo template copies may drift — the repo
  copies are reference-only, not the submitted artifact.
- winget `Scope: user` + per-user Inno install: `winget upgrade` relies on the
  `AppsAndFeaturesEntries` ProductCode/DisplayVersion matching what Inno writes
  to the registry — verify the `_is1` key name and that Inno writes a
  `DisplayVersion` equal to `{#MyAppVersion}`.

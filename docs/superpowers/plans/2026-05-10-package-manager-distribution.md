# Package-Manager Distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Noctis through Scoop, winget, and Chocolatey so Windows users can install and update it via those package managers, with the metadata refreshed automatically when a GitHub Release is published.

**Architecture:** Add a `packaging/` directory holding the source-of-truth manifests for all three managers; add a `package-managers.yml` GitHub Actions workflow (triggered on `release: published` + `workflow_dispatch`) that, on each release, computes the release-asset SHA256s, pushes an updated Scoop manifest to the `heartached/scoop-bucket` repo, and opens a `microsoft/winget-pkgs` PR via `komac`. Chocolatey is published manually for now via documented steps. Nothing in `src/` changes; all three managers reference the existing GitHub Release assets (`Noctis-v{version}-Setup.exe` for winget/Chocolatey, `Noctis-windows-x64.zip` for Scoop).

**Tech Stack:** GitHub Actions, Bash, PowerShell, Scoop JSON manifests, winget YAML manifests (schema 1.6), Chocolatey `.nuspec` + `chocolateyInstall.ps1`, `komac` (winget PR automation). Reference spec: `docs/superpowers/specs/2026-05-10-package-manager-distribution-design.md`.

**Known facts established during planning (don't re-investigate):**
- `Noctis-windows-x64.zip` has `Noctis.exe` at the archive root → Scoop manifest needs **no** `extract_dir`.
- The Inno installer (`installer.iss`) uses `AppId={{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}`, so the registry uninstall key is `{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}_is1` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\` (per-user, `PrivilegesRequired=lowest`).
- The installer supports `/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-`.
- Current version is `1.1.4` (`src/Noctis/Noctis.csproj` `<Version>`).
- Repo license is MIT (`LICENSE`).
- `.github/workflows/dotnet.yml` only builds artifacts; GitHub Releases are created manually.
- Asset URL patterns on a release tagged `v{ver}`:
  - `https://github.com/heartached/Noctis/releases/download/v{ver}/Noctis-v{ver}-Setup.exe`
  - `https://github.com/heartached/Noctis/releases/download/v{ver}/Noctis-windows-x64.zip`

**Out-of-repo prerequisites (the human must do these; the plan assumes they're done before the workflow runs for real, but all files can be created without them):**
1. Create public repo `heartached/scoop-bucket` (empty is fine).
2. Repo secret `SCOOP_BUCKET_TOKEN` on `heartached/Noctis` — a PAT with `repo` scope on `heartached/scoop-bucket`.
3. Repo secret `WINGET_PAT` on `heartached/Noctis` — a PAT with `public_repo` scope (for forking/PR-ing `microsoft/winget-pkgs`).
4. Chocolatey community account + API key (kept locally for the manual push).
5. Confirm the Chocolatey package id `noctis` is available; if not, use `noctis-music-player` and adjust the nuspec + docs.

---

## Task 1: Scaffold the `packaging/` directory and Scoop manifest

**Files:**
- Create: `packaging/scoop/noctis.json`
- Create: `packaging/README.md`

- [ ] **Step 1: Create the Scoop manifest**

Create `packaging/scoop/noctis.json` with this exact content (the `version`/`hash` here are placeholders that CI overwrites each release; they're set to the current 1.1.4 values so the file is valid and installable today — the implementer must fill the real SHA256, see Step 2):

```json
{
    "version": "1.1.4",
    "description": "A music player that respects what's yours",
    "homepage": "https://github.com/heartached/Noctis",
    "license": "MIT",
    "architecture": {
        "64bit": {
            "url": "https://github.com/heartached/Noctis/releases/download/v1.1.4/Noctis-windows-x64.zip",
            "hash": "<sha256-of-Noctis-windows-x64.zip-for-v1.1.4>"
        }
    },
    "bin": "Noctis.exe",
    "shortcuts": [
        ["Noctis.exe", "Noctis"]
    ],
    "checkver": {
        "github": "https://github.com/heartached/Noctis"
    },
    "autoupdate": {
        "architecture": {
            "64bit": {
                "url": "https://github.com/heartached/Noctis/releases/download/v$version/Noctis-windows-x64.zip"
            }
        }
    }
}
```

- [ ] **Step 2: Fill in the real SHA256 for the v1.1.4 zip**

Run (downloads the released asset and hashes it):

```bash
curl -sL -o /tmp/noctis-win.zip https://github.com/heartached/Noctis/releases/download/v1.1.4/Noctis-windows-x64.zip
sha256sum /tmp/noctis-win.zip
```

Expected: a 64-hex-char digest. Replace `<sha256-of-Noctis-windows-x64.zip-for-v1.1.4>` in `packaging/scoop/noctis.json` with that value (lowercase hex, no prefix).

If the v1.1.4 release asset is not reachable (e.g. not published yet), instead hash the local copy:

```bash
sha256sum release-staging-1.1.4/Noctis-windows-x64.zip
```

- [ ] **Step 3: Create `packaging/README.md`**

```markdown
# Packaging

Source-of-truth package-manager manifests for Noctis.

| Dir          | Manager     | Distributed via                                   | Consumes                       |
|--------------|-------------|---------------------------------------------------|--------------------------------|
| `scoop/`     | Scoop       | `heartached/scoop-bucket` repo                    | `Noctis-windows-x64.zip`       |
| `winget/`    | winget      | PR to `microsoft/winget-pkgs` (`heartached.Noctis`)| `Noctis-v{ver}-Setup.exe`      |
| `chocolatey/`| Chocolatey  | `community.chocolatey.org` (`noctis`)             | `Noctis-v{ver}-Setup.exe`      |

On every published GitHub Release, `.github/workflows/package-managers.yml`
refreshes the Scoop manifest in the bucket repo and opens the winget PR.
Chocolatey is currently published manually — see `docs/RELEASE-PACKAGING.md`.
```

- [ ] **Step 4: Validate the Scoop manifest is well-formed JSON**

Run:

```bash
python -c "import json,sys; json.load(open('packaging/scoop/noctis.json')); print('ok')"
```

Expected: `ok`

(Optional, only if Scoop is installed on the dev box — Windows): `scoop install .\packaging\scoop\noctis.json` then `scoop uninstall noctis`. If Scoop isn't available, note it and rely on the JSON validation + the bucket repo's own CI.

- [ ] **Step 5: Commit**

```bash
git add packaging/scoop/noctis.json packaging/README.md
git commit -m "packaging: add Scoop manifest for Noctis"
```

---

## Task 2: winget manifests

**Files:**
- Create: `packaging/winget/heartached.Noctis.yaml`
- Create: `packaging/winget/heartached.Noctis.installer.yaml`
- Create: `packaging/winget/heartached.Noctis.locale.en-US.yaml`

These are the three-file winget manifest set (schema 1.6.0). They mirror what gets submitted upstream; CI regenerates its own copies via `komac`, so these are reference/source-of-truth for the stable fields.

- [ ] **Step 1: Create the version manifest** `packaging/winget/heartached.Noctis.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: heartached.Noctis
PackageVersion: 1.1.4
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
```

- [ ] **Step 2: Create the installer manifest** `packaging/winget/heartached.Noctis.installer.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: heartached.Noctis
PackageVersion: 1.1.4
InstallerType: inno
Scope: user
InstallModes:
  - interactive
  - silent
  - silentWithProgress
InstallerSwitches:
  Silent: /SILENT /SUPPRESSMSGBOXES /NORESTART /SP-
  SilentWithProgress: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
UpgradeBehavior: install
AppsAndFeaturesEntries:
  - DisplayName: Noctis
    Publisher: heartached
    DisplayVersion: 1.1.4
    ProductCode: '{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}_is1'
    UpgradeCode: '{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}'
    InstallerType: inno
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/heartached/Noctis/releases/download/v1.1.4/Noctis-v1.1.4-Setup.exe
    InstallerSha256: <SHA256-OF-Noctis-v1.1.4-Setup.exe-UPPERCASE>
ManifestType: installer
ManifestVersion: 1.6.0
```

- [ ] **Step 3: Fill in the installer SHA256**

Run:

```bash
curl -sL -o /tmp/noctis-setup.exe https://github.com/heartached/Noctis/releases/download/v1.1.4/Noctis-v1.1.4-Setup.exe
sha256sum /tmp/noctis-setup.exe | tr 'a-f' 'A-F'
```

Expected: 64 uppercase hex chars (winget convention is uppercase). Replace `<SHA256-OF-Noctis-v1.1.4-Setup.exe-UPPERCASE>`. If the release asset isn't reachable, hash `release-staging-1.1.4/Noctis-v1.1.4-Setup.exe` instead.

- [ ] **Step 4: Create the locale manifest** `packaging/winget/heartached.Noctis.locale.en-US.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: heartached.Noctis
PackageVersion: 1.1.4
PackageLocale: en-US
Publisher: heartached
PublisherUrl: https://github.com/heartached
PublisherSupportUrl: https://github.com/heartached/Noctis/issues
PackageName: Noctis
PackageUrl: https://github.com/heartached/Noctis
License: MIT
LicenseUrl: https://github.com/heartached/Noctis/blob/main/LICENSE
Copyright: Copyright (c) 2026 heartached
ShortDescription: A music player that respects what's yours.
Description: |-
  Noctis is a desktop music player for your local library — fast browsing,
  synced lyrics, metadata editing, EQ, and more.
Tags:
  - music
  - music-player
  - audio
  - player
  - lyrics
  - avalonia
ReleaseNotesUrl: https://github.com/heartached/Noctis/releases/tag/v1.1.4
ManifestType: defaultLocale
ManifestVersion: 1.6.0
```

- [ ] **Step 5: Validate the manifests**

If `winget` is available (Windows 10/11):

```powershell
winget validate --manifest .\packaging\winget
```

Expected: `Manifest validation succeeded.`

If `winget` is not available on the dev box, validate YAML syntax instead:

```bash
python -c "import yaml,glob; [yaml.safe_load(open(f)) for f in glob.glob('packaging/winget/*.yaml')]; print('ok')"
```

Expected: `ok`. Note in the commit/PR that full `winget validate` was deferred to CI / the upstream PR's automated check.

- [ ] **Step 6: Commit**

```bash
git add packaging/winget/
git commit -m "packaging: add winget manifests for heartached.Noctis"
```

---

## Task 3: Chocolatey package

**Files:**
- Create: `packaging/chocolatey/noctis.nuspec`
- Create: `packaging/chocolatey/tools/chocolateyInstall.ps1`
- Create: `packaging/chocolatey/tools/chocolateyUninstall.ps1`
- Create: `packaging/chocolatey/tools/VERIFICATION.txt`
- Create: `packaging/chocolatey/tools/LICENSE.txt`

- [ ] **Step 1: Create the nuspec** `packaging/chocolatey/noctis.nuspec`

```xml
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd">
  <metadata>
    <id>noctis</id>
    <version>1.1.4</version>
    <packageSourceUrl>https://github.com/heartached/Noctis/tree/main/packaging/chocolatey</packageSourceUrl>
    <owners>heartached</owners>
    <title>Noctis</title>
    <authors>heartached</authors>
    <projectUrl>https://github.com/heartached/Noctis</projectUrl>
    <iconUrl>https://raw.githubusercontent.com/heartached/Noctis/main/src/Noctis/Assets/Icons/Noctis.ico</iconUrl>
    <licenseUrl>https://github.com/heartached/Noctis/blob/main/LICENSE</licenseUrl>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <projectSourceUrl>https://github.com/heartached/Noctis</projectSourceUrl>
    <docsUrl>https://github.com/heartached/Noctis/blob/main/USAGE.md</docsUrl>
    <bugTrackerUrl>https://github.com/heartached/Noctis/issues</bugTrackerUrl>
    <tags>noctis music music-player audio player lyrics</tags>
    <summary>A music player that respects what's yours.</summary>
    <description>
Noctis is a desktop music player for your local library — fast browsing, synced
lyrics, metadata editing, EQ, and more.

This package downloads the official signed-by-publisher installer from the
Noctis GitHub Releases page and installs it per-user.
    </description>
    <releaseNotes>https://github.com/heartached/Noctis/releases/tag/v1.1.4</releaseNotes>
  </metadata>
  <files>
    <file src="tools\**" target="tools" />
  </files>
</package>
```

> If the `noctis` id is unavailable on the community feed, change `<id>` to `noctis-music-player` here and update `$packageName` in `chocolateyInstall.ps1` (Step 2), `docs/RELEASE-PACKAGING.md` (Task 5), and `packaging/README.md` (Task 1).

- [ ] **Step 2: Create the install script** `packaging/chocolatey/tools/chocolateyInstall.ps1`

```powershell
$ErrorActionPreference = 'Stop'

$packageName  = 'noctis'
$version      = $env:ChocolateyPackageVersion
$url64        = "https://github.com/heartached/Noctis/releases/download/v$version/Noctis-v$version-Setup.exe"

$packageArgs = @{
  packageName    = $packageName
  fileType       = 'exe'
  url64bit       = $url64
  checksum64     = '<SHA256-OF-Noctis-v1.1.4-Setup.exe-LOWERCASE>'
  checksumType64 = 'sha256'
  silentArgs     = '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
```

- [ ] **Step 3: Fill in the checksum**

Run:

```bash
curl -sL -o /tmp/noctis-setup.exe https://github.com/heartached/Noctis/releases/download/v1.1.4/Noctis-v1.1.4-Setup.exe
sha256sum /tmp/noctis-setup.exe
```

Expected: 64 lowercase hex chars. Replace `<SHA256-OF-Noctis-v1.1.4-Setup.exe-LOWERCASE>`. (This is the same file as Task 2 Step 3 — same digest, just lowercase here.) If unreachable, hash `release-staging-1.1.4/Noctis-v1.1.4-Setup.exe`.

- [ ] **Step 4: Create the uninstall script** `packaging/chocolatey/tools/chocolateyUninstall.ps1`

```powershell
$ErrorActionPreference = 'Stop'

$packageName = 'noctis'
# Inno Setup writes its uninstall entry under the AppId + "_is1".
$key = Get-UninstallRegistryKey -SoftwareName 'Noctis*'

if ($key.Count -eq 1) {
  $uninstallString = $key.UninstallString
  # UninstallString looks like: "C:\...\unins000.exe" /...
  $exe = $uninstallString -replace '^"([^"]+)".*$', '$1'
  Uninstall-ChocolateyPackage -PackageName $packageName `
    -FileType 'exe' `
    -SilentArgs '/SILENT /SUPPRESSMSGBOXES /NORESTART' `
    -File $exe `
    -ValidExitCodes @(0)
} elseif ($key.Count -eq 0) {
  Write-Warning "$packageName is not installed (no matching uninstall registry key)."
} else {
  Write-Warning "Multiple matches for '$packageName' found; not uninstalling automatically. Remove via Settings > Apps."
  $key | ForEach-Object { Write-Warning "  $($_.DisplayName) - $($_.UninstallString)" }
}
```

- [ ] **Step 5: Create `packaging/chocolatey/tools/VERIFICATION.txt`**

```text
VERIFICATION

Verification is intended to assist the Chocolatey moderators and any user in
verifying that this package's contents are trustworthy.

This package downloads the official installer published by the Noctis author on
GitHub Releases:

  https://github.com/heartached/Noctis/releases

The installer URL embedded in tools\chocolateyInstall.ps1 is:

  https://github.com/heartached/Noctis/releases/download/v<version>/Noctis-v<version>-Setup.exe

To verify the SHA256 checksum of the downloaded installer:

  PowerShell:  Get-FileHash .\Noctis-v<version>-Setup.exe -Algorithm SHA256

Compare the result with the `checksum64` value in tools\chocolateyInstall.ps1.

The file tools\LICENSE.txt is a verbatim copy of the project's MIT license,
also available at https://github.com/heartached/Noctis/blob/main/LICENSE
```

- [ ] **Step 6: Copy the license into the package**

```bash
cp LICENSE packaging/chocolatey/tools/LICENSE.txt
```

- [ ] **Step 7: Validate the package builds (if `choco` is available — Windows)**

```powershell
choco pack packaging\chocolatey\noctis.nuspec --outputdirectory $env:TEMP
```

Expected: `Successfully created package '...\noctis.1.1.4.nupkg'`.

If `choco` is not available on the dev box: validate the nuspec is well-formed XML —

```bash
python -c "import xml.dom.minidom as m; m.parse('packaging/chocolatey/noctis.nuspec'); print('ok')"
```

Expected: `ok`. Note that `choco pack` + a clean-VM `choco install` smoke test must be done before the first `choco push` (covered in `docs/RELEASE-PACKAGING.md`).

- [ ] **Step 8: Commit**

```bash
git add packaging/chocolatey/
git commit -m "packaging: add Chocolatey package for Noctis"
```

---

## Task 4: The `package-managers.yml` workflow

**Files:**
- Create: `.github/workflows/package-managers.yml`

- [ ] **Step 1: Create the workflow**

```yaml
name: Update package managers

on:
  release:
    types: [published]
  workflow_dispatch:
    inputs:
      tag:
        description: 'Release tag to publish (e.g. v1.1.4). Defaults to the latest release.'
        required: false
        type: string

permissions:
  contents: read

jobs:
  resolve:
    name: Resolve version and checksums
    runs-on: ubuntu-latest
    outputs:
      version: ${{ steps.v.outputs.version }}
      tag: ${{ steps.v.outputs.tag }}
      zip_url: ${{ steps.v.outputs.zip_url }}
      zip_sha256: ${{ steps.v.outputs.zip_sha256 }}
      setup_url: ${{ steps.v.outputs.setup_url }}
      setup_sha256: ${{ steps.v.outputs.setup_sha256 }}
    steps:
      - name: Determine tag
        id: tag
        run: |
          set -euo pipefail
          if [ "${{ github.event_name }}" = "release" ]; then
            TAG="${{ github.event.release.tag_name }}"
          elif [ -n "${{ inputs.tag }}" ]; then
            TAG="${{ inputs.tag }}"
          else
            TAG=$(gh release view --repo ${{ github.repository }} --json tagName -q .tagName)
          fi
          echo "tag=$TAG" >> "$GITHUB_OUTPUT"
        env:
          GH_TOKEN: ${{ github.token }}

      - name: Resolve URLs and checksums
        id: v
        run: |
          set -euo pipefail
          TAG="${{ steps.tag.outputs.tag }}"
          VERSION="${TAG#v}"
          REPO="${{ github.repository }}"
          ZIP_URL="https://github.com/$REPO/releases/download/$TAG/Noctis-windows-x64.zip"
          SETUP_URL="https://github.com/$REPO/releases/download/$TAG/Noctis-v$VERSION-Setup.exe"
          curl -fsSL -o noctis-win.zip "$ZIP_URL"
          curl -fsSL -o noctis-setup.exe "$SETUP_URL"
          ZIP_SHA=$(sha256sum noctis-win.zip | cut -d' ' -f1)
          SETUP_SHA=$(sha256sum noctis-setup.exe | cut -d' ' -f1)
          {
            echo "version=$VERSION"
            echo "tag=$TAG"
            echo "zip_url=$ZIP_URL"
            echo "zip_sha256=$ZIP_SHA"
            echo "setup_url=$SETUP_URL"
            echo "setup_sha256=$SETUP_SHA"
          } >> "$GITHUB_OUTPUT"

  scoop:
    name: Update Scoop bucket
    needs: resolve
    runs-on: ubuntu-latest
    steps:
      - name: Checkout Noctis (for the manifest template)
        uses: actions/checkout@v4

      - name: Checkout scoop-bucket
        uses: actions/checkout@v4
        with:
          repository: heartached/scoop-bucket
          token: ${{ secrets.SCOOP_BUCKET_TOKEN }}
          path: scoop-bucket

      - name: Render and write manifest
        run: |
          set -euo pipefail
          VERSION="${{ needs.resolve.outputs.version }}"
          ZIP_URL="${{ needs.resolve.outputs.zip_url }}"
          ZIP_SHA="${{ needs.resolve.outputs.zip_sha256 }}"
          mkdir -p scoop-bucket/bucket
          jq \
            --arg version "$VERSION" \
            --arg url "$ZIP_URL" \
            --arg hash "$ZIP_SHA" \
            '.version=$version | .architecture."64bit".url=$url | .architecture."64bit".hash=$hash' \
            packaging/scoop/noctis.json > scoop-bucket/bucket/noctis.json

      - name: Commit and push
        run: |
          set -euo pipefail
          cd scoop-bucket
          git config user.name "github-actions[bot]"
          git config user.email "github-actions[bot]@users.noreply.github.com"
          if git diff --quiet -- bucket/noctis.json; then
            echo "No change to noctis.json; skipping."
            exit 0
          fi
          git add bucket/noctis.json
          git commit -m "noctis: update to ${{ needs.resolve.outputs.version }}"
          git push

  winget:
    name: Submit winget PR
    needs: resolve
    runs-on: ubuntu-latest
    steps:
      - name: Install komac
        run: |
          set -euo pipefail
          KOMAC_VERSION="2.11.0"
          curl -fsSL -o komac.tar.gz "https://github.com/russellbanks/Komac/releases/download/v${KOMAC_VERSION}/komac-${KOMAC_VERSION}-x86_64-unknown-linux-gnu.tar.gz"
          tar -xzf komac.tar.gz
          sudo install -m755 komac /usr/local/bin/komac
          komac --version

      - name: Update heartached.Noctis
        run: |
          set -euo pipefail
          komac update heartached.Noctis \
            --version "${{ needs.resolve.outputs.version }}" \
            --urls "${{ needs.resolve.outputs.setup_url }}" \
            --submit
        env:
          GITHUB_TOKEN: ${{ secrets.WINGET_PAT }}
```

Notes for the implementer:
- Pin `KOMAC_VERSION` to the latest stable komac release at implementation time and verify the linux-gnu tarball asset name matches that version's release assets (the naming above matches komac 2.x; adjust if it changed). If a maintained marketplace action is preferred, `michidk/run-komac@v1` is an alternative — either is acceptable; keep one approach.
- `secrets.WINGET_PAT` must be a classic PAT with `public_repo` (or fine-grained with PR write on a fork of `microsoft/winget-pkgs`). The built-in `GITHUB_TOKEN` cannot do this.
- The `scoop` and `winget` jobs are independent; if the bucket repo / secret isn't set up yet, that job fails but `winget` still runs (separate jobs, no `needs` between them). That's intended.
- Chocolatey is deliberately **not** a job here yet — see `docs/RELEASE-PACKAGING.md`.

- [ ] **Step 2: Lint the workflow YAML**

```bash
python -c "import yaml; yaml.safe_load(open('.github/workflows/package-managers.yml')); print('ok')"
```

Expected: `ok`

(If `actionlint` is available: `actionlint .github/workflows/package-managers.yml` — fix any reported issues.)

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/package-managers.yml
git commit -m "ci: workflow to update Scoop bucket and submit winget PR on release"
```

---

## Task 5: Release-packaging documentation

**Files:**
- Create: `docs/RELEASE-PACKAGING.md`
- Modify: `README.md` (add an "Install" section mentioning the package managers)

- [ ] **Step 1: Create `docs/RELEASE-PACKAGING.md`**

```markdown
# Release packaging (Scoop / winget / Chocolatey)

Noctis is distributed through three Windows package managers. Source-of-truth
manifests live in [`packaging/`](../packaging/).

## What's automated

When a GitHub Release is **published**, `.github/workflows/package-managers.yml`:

1. Downloads the release's `Noctis-windows-x64.zip` and `Noctis-v{ver}-Setup.exe`
   and computes their SHA256s.
2. **Scoop** — renders `packaging/scoop/noctis.json` with the new version + zip
   hash and pushes it to `heartached/scoop-bucket` as `bucket/noctis.json`.
3. **winget** — runs `komac update heartached.Noctis --version {ver} --urls
   {Setup.exe URL} --submit`, which opens a PR against `microsoft/winget-pkgs`.

You can re-run it manually from the Actions tab via **Run workflow** (optional
`tag` input).

### Required repo secrets

| Secret               | Scope                                                    |
|----------------------|----------------------------------------------------------|
| `SCOOP_BUCKET_TOKEN` | PAT with `repo` scope on `heartached/scoop-bucket`       |
| `WINGET_PAT`         | PAT with `public_repo` scope (forks/PRs `winget-pkgs`)  |

`heartached/scoop-bucket` must exist (a public repo; can start empty — the
workflow creates `bucket/noctis.json`).

## Chocolatey — manual, per release

The Chocolatey package (`packaging/chocolatey/`) is published by hand for now
(community-feed moderation makes unattended pushes risky early on).

1. Compute the installer checksum:

       Get-FileHash .\Noctis-v{ver}-Setup.exe -Algorithm SHA256

2. Update `packaging/chocolatey/noctis.nuspec` `<version>` and
   `packaging/chocolatey/tools/chocolateyInstall.ps1` `checksum64` to match.
3. Pack:

       choco pack packaging\chocolatey\noctis.nuspec --outputdirectory %TEMP%

4. Smoke-test in a clean VM / Windows Sandbox:

       choco install noctis -s "%TEMP%;https://community.chocolatey.org/api/v2/" -y
       # launch Noctis, confirm it runs
       choco uninstall noctis -y

5. Push:

       choco push %TEMP%\noctis.{ver}.nupkg --source https://push.chocolatey.org/ --api-key <CHOCO_API_KEY>

6. Wait for moderation at https://community.chocolatey.org/packages/noctis .

Once a couple of releases have cleared moderation cleanly, a Chocolatey job can
be added to `package-managers.yml` (Windows runner, `choco pack` + `choco push`
with a `CHOCO_API_KEY` secret).

> If the `noctis` package id was unavailable, the package id is
> `noctis-music-player` — substitute it everywhere above.
```

- [ ] **Step 2: Add an Install section to `README.md`**

Find the existing structure of `README.md` (read it first), then add a section near the top — adapt the heading level to match the file. Content:

```markdown
## Install

**Windows** — via a package manager:

```powershell
# winget
winget install heartached.Noctis

# Scoop (add the bucket once, then install)
scoop bucket add noctis https://github.com/heartached/scoop-bucket
scoop install noctis

# Chocolatey
choco install noctis
```

Or download the installer / portable zip from the
[latest release](https://github.com/heartached/Noctis/releases/latest).

**macOS / Linux** — download the `.dmg` / AppImage from the
[latest release](https://github.com/heartached/Noctis/releases/latest).
```

(If `README.md` already has install/download instructions, integrate rather than duplicate — keep one Install section.)

- [ ] **Step 3: Commit**

```bash
git add docs/RELEASE-PACKAGING.md README.md
git commit -m "docs: release-packaging guide + README install section"
```

---

## Task 6: Final verification pass

- [ ] **Step 1: Re-validate every generated file**

```bash
python -c "import json; json.load(open('packaging/scoop/noctis.json')); print('scoop json ok')"
python -c "import yaml,glob; [yaml.safe_load(open(f)) for f in glob.glob('packaging/winget/*.yaml')]; print('winget yaml ok')"
python -c "import yaml; yaml.safe_load(open('.github/workflows/package-managers.yml')); print('workflow yaml ok')"
python -c "import xml.dom.minidom as m; m.parse('packaging/chocolatey/noctis.nuspec'); print('nuspec xml ok')"
```

Expected: four `ok` lines.

- [ ] **Step 2: Confirm no placeholder checksums remain**

```bash
grep -rn "<sha256\|<SHA256\|<CHOCO_API_KEY>" packaging/ || echo "no placeholders"
```

Expected: `no placeholders` (the docs `<version>`/`<CHOCO_API_KEY>` literals in `docs/RELEASE-PACKAGING.md` and `VERIFICATION.txt` are intentional and not matched by this grep against `packaging/` except `VERIFICATION.txt`'s `<version>` which is fine — visually confirm only real checksums were filled).

- [ ] **Step 3: Confirm the app build still passes (sanity — nothing in `src/` changed)**

```bash
dotnet build src/Noctis/Noctis.csproj -v minimal
```

Expected: `Build succeeded.`

- [ ] **Step 4: Report**

Summarize: files created, which validations ran vs. were deferred to CI (e.g. `winget validate`, `choco pack` if those tools weren't on the dev box), and the outstanding human prerequisites (create `scoop-bucket` repo, add `SCOOP_BUCKET_TOKEN` + `WINGET_PAT` secrets, get a Chocolatey API key, confirm the `noctis` id). No commit (report only).

---

## Self-review notes

- **Spec coverage:** Scoop manifest (Task 1) ✓; winget manifests (Task 2) ✓; Chocolatey package incl. VERIFICATION.txt + LICENSE.txt (Task 3) ✓; `package-managers.yml` with resolve/scoop/winget jobs + `workflow_dispatch` + fail-loud independent jobs (Task 4) ✓; `docs/RELEASE-PACKAGING.md` + README (Task 5) ✓; prerequisites enumerated in the header and the doc ✓; verification commands per file (Tasks 1–4, 6) ✓. Chocolatey-in-CI is explicitly deferred per spec ✓.
- **Placeholders:** the only `<...>` tokens are checksum slots that each have an explicit "fill this in" step with the exact command, plus intentional `<version>`/`<CHOCO_API_KEY>` literals inside doc/verification text. No "TBD"/"TODO"/"add error handling"-style gaps.
- **Type/name consistency:** package id `noctis` used consistently (with the documented `noctis-music-player` fallback callout in every place it appears: nuspec, install script, README, RELEASE-PACKAGING.md); winget identifier `heartached.Noctis` consistent across all three YAMLs and the workflow; Inno `AppId`/ProductCode `{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}` consistent between the installer manifest and the spec; workflow job outputs (`zip_url`, `zip_sha256`, `setup_url`, `setup_sha256`, `version`) referenced consistently between the `resolve` job and the `scoop`/`winget` jobs.

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

2. Update `packaging/chocolatey/noctis.nuspec` `<version>` **and**
   `<releaseNotes>` (which is version-pinned to
   `https://github.com/heartached/Noctis/releases/tag/v{ver}`), and
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

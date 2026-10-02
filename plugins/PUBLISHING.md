# Publishing an official plugin

Official plugins show up in **Settings → Plugins → Get plugins** (Noctis 1.5.9 and newer) and on
the website. Both read [`index.json`](index.json) from `main`. Each plugin's zip lives in its own
GitHub release, tagged `plugin-<short name>-v<version>`. Format and app behaviour:
[docs/PLUGINS.md → Official plugins](../docs/PLUGINS.md#official-plugins-get-plugins).

## Before the first release

- Merge `feature/official-plugins` into `main` first. It carries `index.json` and a guard in
  `.github/workflows/package-managers.yml` that skips `plugin-*` releases (without it, publishing a
  plugin release starts the Scoop/winget job, which then fails looking for the app's zip). Release
  workflows run the file **at the tagged commit**, so tag plugin releases on `main` after that merge.
- **Mixxx needs Noctis 1.5.9** (`minAppVersion` 1.5.9, plugin API 1.2). 1.5.8 has no Get plugins
  page; a 1.5.8 user who installs the zip by hand sees "Needs Noctis 1.5.9".
- Kawarp is not listed: Noctis has Kawarp built in, so the plugin would only add duplicate
  "Kawarp background" options. It stays in `plugins/` as a code sample.
- The app has a copy of `index.json` built in (used offline, or when GitHub can't be reached).
  Make sure the sha256/size in `index.json` describe the zips you upload **before** building the
  1.5.9 app release, so that copy matches too.

## Steps

The zips below were built on Windows with .NET SDK 10.0.401. The build is reproducible: the same
source, OS and SDK give the same bytes. Another SDK or OS may give a different (equally valid)
zip; then use the new hash and size in step 3.

### 1. Build the zip

```powershell
dotnet build plugins/Noctis.Plugins.Mixxx/Noctis.Plugins.Mixxx.csproj -c Release
```

Output: `plugins/Noctis.Plugins.Mixxx/bin/Release/Noctis.Plugins.Mixxx-1.0.0.zip`

### 2. Check SHA-256 and size

```powershell
$z = "plugins/Noctis.Plugins.Mixxx/bin/Release/Noctis.Plugins.Mixxx-1.0.0.zip"
"{0}  {1}" -f (Get-FileHash $z -Algorithm SHA256).Hash.ToLower(), (Get-Item $z).Length
```

Expected (what `index.json` says now):

| Zip | sha256 | size |
|---|---|---|
| Noctis.Plugins.Mixxx-1.0.0.zip | `7b7c5ea058ca9f9940c14337975a2430937fc2725b711ef4c63c3eba3c223e02` | 11837 |

### 3. Update `index.json` if anything differs

Put the new `sha256` and `size` in the plugin's entry, commit, and push to `main` (step 5).
They must describe the exact file you upload, or Noctis refuses to install it.

### 4. Create the release

```powershell
gh release create plugin-mixxx-v1.0.0 `
  plugins/Noctis.Plugins.Mixxx/bin/Release/Noctis.Plugins.Mixxx-1.0.0.zip `
  --repo heartached/Noctis --target main `
  --title "Mixxx plugin 1.0.0" `
  --notes "Imports the BPM and musical key that Mixxx found for your tracks. Needs Noctis 1.5.9 or newer. Install it from Settings → Plugins → Get plugins." `
  --prerelease --latest=false
```

Why `--prerelease --latest=false`: a plugin release must never become the repo's "Latest" release.
The README links to `/releases/latest`, and the website's build picks the newest **stable**
release for its download buttons, so a stable plugin release published after an app release
would take over both. (The in-app updater ignores `plugin-*` tags, but it only reads the 10
newest releases, so keep plugin releases few between app releases.)

Then check what GitHub serves is the file you hashed:

```powershell
gh release download plugin-mixxx-v1.0.0 --repo heartached/Noctis --pattern "*.zip" --dir "$env:TEMP\plugin-check"
(Get-FileHash "$env:TEMP\plugin-check\Noctis.Plugins.Mixxx-1.0.0.zip" -Algorithm SHA256).Hash.ToLower()
```

### 5. Push the list

Commit `plugins/index.json` (if it changed) and push to `main`. Noctis fetches
`https://raw.githubusercontent.com/heartached/Noctis/main/plugins/index.json` when the Plugins
page opens; GitHub's cache can take about 5 minutes to serve the new file.

## Later versions

1. Bump `<Version>` in the plugin's csproj and `version` in its `plugin.json` (keep them equal;
   raise `minAppVersion` if it needs a newer Noctis).
2. Build, hash, and release under the new tag (`plugin-mixxx-v1.0.1`).
3. In `index.json` update that entry's `version`, `minAppVersion`, `download`, `sha256` and `size`.
   `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter PluginCatalogTests` checks the entry
   still matches the plugin.json.
4. Push. Users with the old version see **Update**; their data and settings are kept.

To stop offering a plugin, remove its entry from `index.json` and push. Installed copies stay.

## Testing before publishing

Point Noctis at a local list instead of the live one:

1. Make a folder, e.g. `D:\plugin-test`, copy `plugins/index.json` into it, and copy the zips from
   step 1 next to it.
2. In that copy, set each `download` to the zip's file name (`"Noctis.Plugins.Mixxx-1.0.0.zip"`).
   A local list may also use `http://` URLs (e.g. `python -m http.server` in that folder) or
   full file paths. Keep `sha256`/`size` as they are, or change one to see a mismatch refused.
3. Start Noctis with the list (and, while the app is still 1.5.8, as 1.5.9 so Mixxx is offered):

   ```powershell
   $env:NOCTIS_PLUGIN_INDEX = "D:\plugin-test\index.json"
   dotnet run --project src/Noctis/Noctis.csproj -p:Version=1.5.9
   ```

4. Settings → Plugins → Get plugins → Install. If the list can't be read, the page says
   "Couldn't reach GitHub. Showing the built-in list." instead.

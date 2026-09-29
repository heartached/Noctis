# Noctis plugins

Noctis (desktop: Windows, macOS, Linux) can load **plugins**: .NET class libraries that add
things to the app through a small, versioned API, the *plugin kit*
(`Noctis.Plugins.Abstractions`). This page is the reference for plugin authors and for anyone
deciding whether to install one.

> Just want Stream Deck buttons, an OBS "now playing" overlay or a script that controls
> playback? You may not need a plugin. The [Local API](LOCAL-API.md) is an HTTP + JSON
> interface on 127.0.0.1 that any language can use, and nothing runs inside Noctis.

- [What a plugin can do](#what-a-plugin-can-do)
- [Quick start](#quick-start)
- [plugin.json reference](#pluginjson-reference)
- [API reference](#api-reference)
- [Permissions](#permissions)
- [Safety model](#safety-model)
- [Lifecycle, threading and failures](#lifecycle-threading-and-failures)
- [Building and packaging](#building-and-packaging)
- [Installing, updating, removing](#installing-updating-removing)
- [Publishing plan](#publishing-plan)
- [Versioning and deprecation policy](#versioning-and-deprecation-policy)
- [Legacy plugins (API 1.0, no plugin.json)](#legacy-plugins-api-10-no-pluginjson)
- [Content packs](#content-packs)

## What a plugin can do

API **1.1** (Noctis 1.5.3 and later):

| Area | What | Permission |
|---|---|---|
| Now playing | Current track, play/pause state, position, change events | — (`playback.read`, implicit) |
| Audio taps | Live beat pulse and spectrum of what is playing | — |
| Visual layers | A control drawn behind the lyrics (offered as a style in Settings' Flowing background picker) | — |
| Scrobble hook | Called when a track counts as listened (half its length or 4 minutes) | — (`playback.read`) |
| Settings | Settings you declare in plugin.json, drawn by Noctis in Settings → Plugins | — |
| Storage | A private data folder that survives updates | — |
| Playback control | Play, pause, next, previous, seek | `playback.control` |
| Library | Read-only search over the library (copies, never live objects) | `library.read` |
| Lyrics | A lyrics provider that joins the online lyrics search | `lyrics.provider` |
| Track menu | Entries in the track context menu | `menu.commands` |
| Notices | A short message in the app's notice pill | `notifications` |

Not available (yet): writing to the library or tags, the queue, new pages or sidebar entries,
and the mobile apps (Android does not load plugins).

Themes, lyrics-page presets and translations do not need code at all: ship them as a
[content pack](#content-packs) (`"type": "content"`), plain JSON that installs without the
full-trust warning.

## Quick start

```xml
<!-- MyPlugin.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- The app supplies the kit (and Avalonia) at run time: never ship them. -->
    <PackageReference Include="Noctis.Plugins.Abstractions" Version="1.1.*" ExcludeAssets="runtime" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

> The kit is not on nuget.org yet (see [Publishing plan](#publishing-plan)). Until then, build
> it from this repository (`dotnet pack src/Noctis.Plugins.Abstractions -c Release`) and use a
> local package source, or reference the project the way the samples do.

```csharp
using Noctis.Plugins;

public sealed class HelloPlugin : INoctisPlugin
{
    private readonly List<IDisposable> _hooks = new();

    public PluginInfo Info { get; } = new("dev.example.hello", "Hello", "1.0.0", "You", "Says hello.");

    public void Initialize(IPluginHost host)
    {
        _hooks.Add(host.RegisterTrackCommand("Say hello", icon: null,
            track => host.Notify($"Hello, {track.Artist}!")));
        _hooks.Add(host.OnTrackScrobbled((track, startedAt) => host.Log($"listened: {track.Title}")));
    }

    public void Shutdown()
    {
        foreach (var h in _hooks) h.Dispose();
        _hooks.Clear();
    }
}
```

```json
{
  "id": "dev.example.hello",
  "name": "Hello",
  "version": "1.0.0",
  "author": "You",
  "description": "Says hello.",
  "type": "dotnet",
  "entry": "MyPlugin.dll",
  "apiVersion": "1.1",
  "minAppVersion": "1.5.3",
  "permissions": ["menu.commands", "notifications"]
}
```

Zip the DLL and plugin.json, then Settings → Plugins → **Install from file…**. Two complete
samples live in the repository:

- `samples/Noctis.SamplePlugin` — *Pulse Ring*, a visual layer that breathes with the beat.
- `samples/Noctis.SamplePlugin.TrackTools` — *Track Tools*: a track-menu command using
  library search and a notice, a lyrics provider stub, a scrobble hook, and declared settings.
- `plugins/Noctis.Plugins.Kawarp` — a Skia-shader visual layer (Noctis also ships Kawarp built in;
  this copy stays as a realistic visual-layer example).

## plugin.json reference

`plugin.json` sits next to the plugin DLL. Noctis reads it **without loading any code**: it is
what the plugin list shows, what the permission dialog lists, and what decides whether this
Noctis can run the plugin at all. Comments and trailing commas are allowed; property names
are case-insensitive.

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Unique, stable identity. Lowercase reverse-DNS: letters, digits, `-`, `_`, at least one dot (`dev.example.hello`). Never change it: enabled state, approvals, settings and the data folder are keyed by it. |
| `name` | yes | Display name, up to 60 characters. |
| `version` | yes | [Semantic version](https://semver.org) (`1.2.0`, `2.0.0-beta.1`). Updates must increase it. |
| `author` | no | Shown under the name. |
| `description` | no | One or two sentences. |
| `type` | no | `"dotnet"` (default), or `"content"` for a data-only [content pack](#content-packs). |
| `entry` | dotnet | File name of the plugin DLL in the same folder (no path). |
| `entryType` | no | Full type name of the `INoctisPlugin` to create, when the DLL holds several. Otherwise the only (or first by name) implementation is used. |
| `apiVersion` | dotnet | `"major.minor"` of the kit you built against (`"1.1"`). A different **major** is refused. |
| `minAppVersion` | no | Oldest Noctis that can run it (`"1.5.3"`). Older apps refuse with "Needs Noctis x or newer". Set it to the first release with the API minor you use. |
| `platforms` | no | Any of `"windows"`, `"macos"`, `"linux"`. Omit for all. |
| `permissions` | no | What the plugin uses from the API; see [Permissions](#permissions). Unknown names are ignored with a warning. |
| `settings` | no | Settings Noctis draws for you; see below. |
| `homepage` | no | `http(s)` link to the project. |
| `contents` | content | What a content pack provides; see [Content packs](#content-packs). Ignored (with a warning) on dotnet plugins. |

### Declared settings

```json
"settings": [
  { "key": "greeting",  "label": "Greeting",         "type": "string", "default": "Hi" },
  { "key": "loud",      "label": "Shout",            "type": "bool",   "default": false },
  { "key": "limit",     "label": "Results",          "type": "number", "default": 25, "min": 1, "max": 200 },
  { "key": "mode",      "label": "Mode",             "type": "choice", "choices": ["soft", "hard"], "default": "soft",
    "description": "Optional second line under the label." }
]
```

- `key`: letters, digits, `.`, `-`, `_` (max 64), unique.
- `type`: `bool` (switch), `string` (text box), `number` (number box, clamped to `min`/`max`), `choice` (dropdown of `choices`).
- Values are stored by Noctis (per plugin id) and survive updates. Read them with
  `host.Settings.GetString/GetBool/GetNumber(key)`; subscribe to `host.Settings.Changed` to react.

## API reference

Everything lives in the `Noctis.Plugins` namespace.

### `INoctisPlugin`

| Member | |
|---|---|
| `PluginInfo Info` | Id, name, version, author, description. With a plugin.json, the manifest wins for display. |
| `void Initialize(IPluginHost host)` | Called once, on the UI thread, when the plugin starts. Register everything here. |
| `void Shutdown()` | Called on disable, removal, reload and app exit. Stop timers, close files, dispose registrations. |

### `IPluginHost`

| Member | Since | Permission | |
|---|---|---|---|
| `string AppVersion` | 1.0 | | `"1.5.3"` |
| `string DataDirectory` | 1.0 | | Private folder; since 1.1 `<data>/plugin-data/<id>/`, outside the plugin folder. Create it before writing. |
| `INowPlaying NowPlaying` | 1.0 | | `Track`, `IsPlaying`, `Position`, events `TrackChanged`, `IsPlayingChanged` (UI thread). |
| `IBeatSource Beat` / `ISpectrumSource Spectrum` | 1.0 | | Poll per frame. |
| `void Log(string)` | 1.0 | | Writes to the Noctis debug log, prefixed with the plugin name. |
| `void RegisterVisualLayer(IVisualLayerProvider)` | 1.0 | | A lyrics-page background layer. |
| `string PluginDirectory` | 1.1 | | The folder the plugin was loaded from. Assemblies are loaded from memory, so `Assembly.Location` is empty: use this for bundled files. |
| `IPlaybackControl Playback` | 1.1 | `playback.control` | `PlayPause()`, `Play()`, `Pause()`, `Next()`, `Previous()`, `Seek(TimeSpan)`. Safe from any thread. |
| `ILibraryReader Library` | 1.1 | `library.read` | `TrackCount`, `Search(query, limit = 50)` (every word must appear in title, artist or album; accent- and case-insensitive; max 500). |
| `IPluginSettings Settings` | 1.1 | | Declared settings (see above). |
| `void Notify(string)` | 1.1 | `notifications` | A notice pill for ~4 s. At most one per second per plugin; long text is cut at 200 characters. |
| `IDisposable RegisterLyricsProvider(ILyricsProvider)` | 1.1 | `lyrics.provider` | See below. |
| `IDisposable RegisterTrackCommand(string label, string? icon, Action<TrackInfo>)` | 1.1 | `menu.commands` | Menu entry in the track context menu. `icon`: SVG path data (24×24 box) or null. The handler runs on the UI thread. |
| `IDisposable RegisterTrackCommand(string label, string? icon, Func<TrackInfo, Task>)` | 1.1 | `menu.commands` | Async variant. |
| `IDisposable OnTrackScrobbled(Action<TrackInfo, DateTimeOffset>)` | 1.1 | | Fires when a track counts as listened (Last.fm rule), with its start time, whether or not a scrobbling service is connected. |

Using a hook without declaring its permission throws `PluginPermissionException`.

`TrackInfo` is an immutable copy of a library track: `Id`, `Title`, `Artist`, `Album`,
`AlbumArtist`, `Duration`, `Year`, `Genre`, `TrackNumber`, `FilePath`, `IsFavorite`,
`PlayCount`, `Rating`.

### Lyrics providers

```csharp
public interface ILyricsProvider
{
    string Name { get; }                                            // "Try <Name>" label
    Task<PluginLyrics?> FindAsync(LyricsQuery query, CancellationToken ct);
}
public sealed record LyricsQuery(string Artist, string Title, string Album, TimeSpan Duration);
public sealed record PluginLyrics(string? Synced, string? Plain, bool Instrumental = false);
```

- `FindAsync` runs on a worker thread, in parallel with LRCLIB and NetEase.
- Ranking: a synced answer beats an unsynced one; on a tie the built-in providers win, then
  plugins in load order. The runner-up is offered as "Try &lt;Name&gt;".
- Return `null` for "not found". Throw for "could not ask" (network down): the search then
  knows the difference between "no lyrics" and "offline".
- Noctis waits **8 seconds**, then cancels `ct` and moves on. A provider that throws or times
  out is logged; it does not stop the plugin.
- `Synced` is LRC (ELRC word timing allowed); `Plain` is plain text.

## Permissions

| Name | Grants | Shown to the user as |
|---|---|---|
| `playback.read` | Now playing, events, scrobble hook. Implicit: you need not list it. | — |
| `playback.control` | `host.Playback` | Control playback: play, pause, skip and seek |
| `library.read` | `host.Library` | Search your library (titles, artists, albums, file paths, play counts) |
| `network` | Nothing in the API: a **disclosure** that the plugin talks to the internet. Declare it if you do. | Connect to the internet |
| `lyrics.provider` | `RegisterLyricsProvider` | Supply lyrics to the lyrics search |
| `menu.commands` | `RegisterTrackCommand` | Add entries to the track menu |
| `notifications` | `Notify` | Show short notices |

The first time a user switches a plugin on, Noctis shows exactly this list and asks. If an
update asks for more, the plugin waits in *Needs approval* until the user switches it on again.

## Safety model

Be clear about what protects users and what does not:

1. **.NET plugins run with full trust.** A plugin is code inside the Noctis process. It can
   read and write any file the user can, open network connections, and start programs,
   whatever its permissions say. .NET has no sandbox for in-process code (Code Access
   Security is gone). **Permissions only gate the Noctis API**; they are an honest summary
   of intent, not a jail. The approval dialog says so.
2. **Restricted mode (Community plugins off).** No third-party plugin code is loaded; installed
   plugins are still listed. Fresh installs start in restricted mode. Installs that already
   had plugins before this switch existed start with it **on**, and the plugins that were
   running are approved as-is, so an update does not silently break someone's setup.
3. **Approval on first enable**, listing the declared permissions, and again when an update
   adds permissions.
4. **Isolation for stability, not security.** Each plugin gets its own collectible
   `AssemblyLoadContext` (its dependencies do not clash with the app's or other plugins') and
   is loaded from memory (its folder is never locked, so update/remove work while Noctis runs).
5. **Containment.** Every call into a plugin is wrapped: an exception from `Initialize`, an
   event handler, a menu command, a setting-change handler or the scrobble hook marks the
   plugin **Failed** (the message is shown in Settings → Plugins) and stops it; a synchronous
   callback that holds the UI thread for more than **1 second** is treated the same way.
   Lyrics providers are time-boxed (8 s). Noctis cannot pre-empt code that loops forever on
   the UI thread; the budget is enforced when the call returns.
6. **Clean unload.** Disabling or removing a plugin calls `Shutdown()`, then drops every
   subscription and registration the plugin made (player events, lyrics providers, menu
   commands, scrobble handlers, setting handlers, visual layers) even if the plugin forgot to,
   and makes the host object it holds inert.
7. **Install checks.** A package is validated before anything is written: plugin.json must
   parse, the entry DLL must be present, the archive must stay under 2000 files / 256 MB, and
   any entry with an absolute path, a drive letter, `:` or `..` rejects the whole file
   (zip-slip). Files are extracted to a staging folder and moved into place.

Install plugins only from people you trust, the same advice as for any desktop app.

## Lifecycle, threading and failures

- **Start**: when Noctis starts (after settings load, at background priority), when the user
  switches the plugin on, after an install/update, and on *Reload plugins*.
- **Stop**: switch off, *Remove*, *Reload plugins*, turning Community plugins off, app exit.
  `Shutdown()` is always called before the plugin's registrations are dropped.
- `Initialize`, events, menu commands, setting changes and the scrobble hook run on the **UI
  thread**: keep them short, `await` I/O. `ILyricsProvider.FindAsync` runs on a worker thread.
- Registrations return `IDisposable`; dispose them in `Shutdown` (Noctis also drops them).
- A **Failed** plugin stays failed until the user switches it off and on, or reloads.

### Where things live

| | Path |
|---|---|
| Plugin folders | `<data>/plugins/<id>/` (Settings → Plugins → Open plugins folder) |
| Plugin data | `<data>/plugin-data/<id>/` |
| Enabled state, approvals, setting values | Noctis's `settings.json` |

`<data>` is the Noctis data folder: `%APPDATA%\Noctis` on Windows, `Noctis` under .NET's
per-user application-data folder elsewhere, or `NOCTIS_DATA_DIR` when set. Settings → Plugins
shows the plugins folder's full path.

Plugins built for API 1.0 kept data in `<plugin folder>/data/`. On first load Noctis moves that
folder to `plugin-data/<id>/` for legacy plugins and plugins declaring `"apiVersion": "1.0"`
(never merging into existing data). Plugins built for 1.1 may ship their own `data/` folder.

## Building and packaging

A package is a `.zip` with **plugin.json at its top** (or inside one top-level folder) next to
the entry DLL and any private dependencies:

```
hello-1.0.0.zip
├── plugin.json
├── MyPlugin.dll
├── MyPlugin.deps.json        (optional)
└── SomeDependency.dll        (optional; must not be Noctis.Plugins.Abstractions or Avalonia)
```

- Do **not** ship `Noctis.Plugins.Abstractions.dll`, `Avalonia*.dll` or `SkiaSharp*.dll`: the
  app's copies are used so types match. (`ExcludeAssets="runtime"` keeps them out of `bin`.)
- Other dependencies load in the plugin's own context; native libraries resolve through
  `deps.json`.
- The samples import `samples/PluginPackage.targets`, which zips `bin/Release/<name>-<version>.zip`
  after every Release build. Copy it into your project or zip by hand.
- Keep `version` in plugin.json and `<Version>` in the csproj in step.

## Installing, updating, removing

- **Install**: Settings → Plugins → *Install from file…* and pick the zip. It lands in
  `plugins/<id>/`, switched off, until you enable it (with the permission prompt).
- **Update**: installing a zip with the same id and a **higher** version offers to update; the
  folder is replaced, data and settings are kept, and approval carries over unless new
  permissions are requested. Same or lower versions are refused.
- **Remove**: the *Remove* button asks, deletes the plugin folder, and keeps its data and
  settings unless you tick *Also delete its data and settings*. A reinstall asks for approval again.
- **By hand**: drop the unzipped folder into the plugins folder and press *Reload plugins*.

## Publishing plan

Phase 1 (this release) ships the loader, manifest, permissions, restricted mode and
install-from-file. There is **no in-app directory yet**; share zips through GitHub releases.

Planned for phase 2, modelled on Flow Launcher and Obsidian:

1. A public index repository with one JSON entry per plugin (id, name, author, repo URL),
   added by pull request. Releases are GitHub releases whose tag equals the manifest version
   and whose asset is the package zip; the index is refreshed by CI, not by hand.
2. CI validation of every release: manifest schema, id ownership, version monotonicity,
   SHA-256 of the zip recorded in the index, and an automated malware scan.
3. An in-app *Browse* tab reading the index, verifying the SHA-256 on download, and offering
   updates. Review is best-effort, which is why restricted mode and the permission prompt stay.
4. `Noctis.Plugins.Abstractions` on nuget.org (the csproj already carries package metadata;
   `dotnet pack` produces it).
5. ~~Content packs~~ shipped (themes, lyrics presets, languages; see [Content packs](#content-packs)).
   Next: listing them in the same index.

## Versioning and deprecation policy

- The kit's **major** version is the compatibility contract. Noctis refuses a plugin whose
  `apiVersion` major differs from its own. A major bump happens only for breaking changes,
  and is announced at least one Noctis minor release ahead in the changelog.
- **Minor** versions only **add**: new members get default implementations, existing members
  keep their signatures and behaviour, so a plugin built for 1.0 runs unchanged on 1.1 (the
  host binds it to its own kit). Use `minAppVersion` to require the first Noctis that has the
  minor you need.
- Deprecated members are marked `[Obsolete]` with the replacement for at least one minor
  release (and at least three months) before a major removes them.
- Permission names, manifest fields and the package layout follow the same rule: new ones may
  appear in a minor; removals wait for a major. Unknown permissions and fields are ignored
  (with a warning), so newer manifests degrade gracefully on older apps.

| Kit | Noctis | Changes |
|---|---|---|
| 1.0 | before 1.5.3 | Plugin entry point, now playing, beat/spectrum taps, data folder, visual layers. |
| 1.1 | 1.5.3+ | plugin.json, permissions, playback control, library search, lyrics providers, track menu commands, scrobble hook, declared settings, notices, `PluginDirectory`; data folder moved out of the plugin folder. |

## Legacy plugins (API 1.0, no plugin.json)

A plugin folder without plugin.json still loads, marked **Legacy** in the list: its folder name
is its id, the DLL named like the folder (or the first DLL) is loaded, and it has **no
permissions** (1.0 had no gated hooks, so nothing it used is affected). Add a plugin.json to
get a stable id, updates from zip, declared settings and the 1.1 hooks.

## Content packs

A **content pack** is a plugin made only of data: colour themes, lyrics-page style presets and
translations, as JSON files. Nothing in it runs: Noctis never loads a DLL, script or XAML from a
content pack, and refuses a pack that contains one. So content packs:

- need **no approval** and no permissions, and work while *Community plugins* is off;
- are **on as soon as they are installed** (the switch in Settings → Plugins turns them off);
- are validated strictly: an unknown key, a bad value or a stray file refuses the whole pack,
  with the reason on its card (this is stricter than dotnet manifests, where unknown fields
  are ignored);
- are meant to work unchanged on the mobile apps later, which will never load plugin code.

The reference pack is `samples/Noctis.ContentPack.Sample` (two themes, one lyrics preset, an
English pseudo-locale). Its `pack/` folder is the pack itself.

### Layout

```
sample-pack-1.0.0.zip
├── plugin.json
├── themes/dusk.json
├── themes/paper.json
├── lyrics/karaoke.json
└── languages/en-XA.json
```

```json
{
  "id": "dev.example.sunset",
  "name": "Sunset",
  "version": "1.0.0",
  "author": "You",
  "description": "Two warm themes and a karaoke preset.",
  "type": "content",
  "minAppVersion": "1.5.3",
  "contents": {
    "themes": ["themes/dusk.json", "themes/paper.json"],
    "lyricsPresets": ["lyrics/karaoke.json"],
    "languages": ["languages/en-XA.json"]
  }
}
```

- `plugin.json` may use `$schema`, `id`, `name`, `version`, `author`, `description`, `type`,
  `apiVersion`, `minAppVersion`, `platforms`, `homepage` and `contents`. `entry`, `entryType`,
  `permissions` and `settings` are refused (content packs carry no code); any other field is refused.
- `contents` lists files per kind: `themes`, `lyricsPresets`, `languages` (at least one file in
  total). Paths are relative to the pack folder, use `/`, end in `.json`, and may not start with
  `/`, contain a drive, `:` or `..`.
- Every file in the pack must be `.json`, `.md`, `.txt`, `.png`, `.jpg`, `.jpeg` or `.webp` (or a
  `LICENSE`/`README`/`NOTICE`/`COPYING`/`AUTHORS` without extension). Links are refused.
- Ids inside a pack (`id` of a theme or preset) are lowercase letters, digits, `-`, `_`, up to 40.
- Comments and trailing commas are allowed in every JSON file. Field names (and theme colour
  keys) are case-insensitive; string keys in a language file must match exactly.

### Themes

```json
{
  "id": "dusk",
  "name": "Dusk",
  "base": "dark",
  "accent": "#FF8A5B",
  "colors": {
    "AppMainBackground": "#17131F",
    "AppSidebarBackground": "#1F1929",
    "PrimaryTextBrush": "#F1ECF7",
    "SecondaryTextBrush": "#A99DBB",
    "HomeCardBackground": "#241D30",
    "IslandBackground": "#F21F1929"
  }
}
```

- `base` (required): `"dark"` or `"light"`, the base palette the theme starts from.
- `accent` (optional, `#RRGGBB`): picked as the accent when the user selects the theme; they can
  change it afterwards as usual.
- `colors` (optional): `#RRGGBB` or `#AARRGGBB` for these keys only. Anything a theme leaves out
  is derived from `base`, `AppMainBackground`, `AppSidebarBackground` and the accent, exactly as
  for themes made with Settings → Appearance → *Custom*, so two colours already make a theme.

| Group | Keys |
|---|---|
| Surfaces | `AppWindowBackgroundBrush`, `AppMainBackground`, `AppSidebarBackground`, `TrackListStripeBrush`, `TrackListHoverBrush`, `TrackListMultiSelectBrush` |
| Text | `PrimaryTextBrush`, `SecondaryTextBrush`, `TertiaryTextBrush` |
| Cards and pills | `HomeCardBackground`, `HomeCardHoverBackground`, `ChipBackground`, `ChipHoverBackground`, `GlassPillBackground`, `GlassPillHoverBackground`, `GlassPillPressedBackground`, `GlassPillBorder`, `InputOutlineBrush`, `ArtworkPlaceholderBackground`, `RankGoldBrush`, `RankSilverBrush`, `RankBronzeBrush`, `ToggleTrackOffBrush`, `ToggleKnobOffBrush` |
| Playback island | `IslandBackground`, `IslandBorder`, `IslandForeground`, `IslandForegroundSecondary`, `IslandForegroundTertiary`, `IslandIconFill`, `IslandSliderUnfilled`, `IslandAlbumArtPlaceholder`, `IslandExplicitBadge`, `IslandTrackBoxSliderUnfilled` |
| Sidebar and queue | `SidebarSelectedBrush`, `SidebarSelectedHoverBrush`, `SidebarHoverBrush`, `QueueDrawerBackground`, `QueueDrawerForeground` |

Accent-coloured surfaces (the now-playing row, toggles, sliders, accent buttons) always follow the
user's accent and cannot be set by a theme. Pack themes appear in Settings → Appearance → Themes
after the built-in and custom ones, labelled *by &lt;pack name&gt;*, and apply live. When the pack
is switched off or removed, a pack theme that was in use falls back to the default theme (Gray,
Crimson accent).

### Lyrics presets

```json
{
  "id": "karaoke",
  "name": "Karaoke night",
  "settings": {
    "minLineOpacity": 35,
    "fullScreenFocus": true,
    "flowingBackground": "Kawarp",
    "kawarpWarp": 1.6,
    "kawarpBlur": 9,
    "visualizer": true,
    "visualizerStyle": "Mirror"
  }
}
```

A preset sets the lyrics-page settings it names and leaves the rest alone. It shows up in
Settings → Lyrics → *Style presets* with an **Apply** button; afterwards every setting can be
adjusted as usual (applying is a one-time copy, not a mode).

| Key | Type | Setting |
|---|---|---|
| `minLineOpacity` | whole number 0–60 | Minimum line opacity (%) |
| `fullScreenFocus` | bool | Fullscreen lyrics focus |
| `joinSplitWords` | bool | Join words split across timed syllables |
| `showTranslations` / `showRomanization` / `showBackgroundVocals` | bool | TTML extras |
| `titleMarquee` / `artistMarquee` | bool | Scroll long titles / artists |
| `flowingBackground` | `"Off"`, `"Drift"`, `"DriftCalm"`, `"Kawarp"`, `"KawarpCalm"` | Flowing lyrics background |
| `kawarpWarp` | number 0–3 | Kawarp warp strength |
| `kawarpBlur` | whole number 1–16 | Kawarp blur |
| `visualizer` | bool | Audio visualizer on the lyrics page |
| `visualizerStyle` | `"Bars"`, `"Mirror"`, `"Wave"` | Visualizer style |
| `visualizerArtworkColor` | bool | Tint the visualizer with the cover colour |

The visualizer only offers these three styles and the colour switch, so there is no separate
"visualizer preset" kind: its settings live in lyrics presets. Font size, alignment and the
lyrics background colour are not preset-able (the first two are not settings in Noctis; the
background colour belongs to the lyrics page's own picker).

### Languages

```json
{
  "culture": "en-XA",
  "name": "English (Pseudo)",
  "strings": {
    "Nav.Home": "[Ĥöɱé]",
    "Plugins.ByPack": "[ƀý {0}]"
  }
}
```

- `culture`: a culture code (`"pt-BR"`, `"eo"`, `"en-XA"`). A culture Noctis does not ship is
  added to Settings → General → Language as *&lt;name&gt; · by &lt;pack&gt;*; for a shipped one,
  the pack's strings replace the built-in ones.
- `strings`: key → text, using the keys of `src/Noctis.UI/Localization/Strings.resx`. Keys this
  Noctis does not know are ignored (counted in a note on the pack's card), and so is a text whose
  `{0}` placeholders do not fit the English one, so a pack can never break a formatted message.
  Any key the pack leaves out shows in English.
- Switching the pack off removes its strings at once; if its language was selected, Noctis goes
  back to the system language.

Translations of Noctis itself are better contributed on
[Crowdin](https://crowdin.com/project/noctis), where they ship with the app; language packs are
for languages or variants that are not there yet, and for trying a translation out.

### Limits

| | |
|---|---|
| Files in a pack | 200 |
| One file | 2 MB |
| Whole pack | 20 MB |
| Themes / lyrics presets / languages per pack | 50 / 50 / 20 |
| Strings per language file | 5000, each up to 4000 characters |

### Testing a pack locally

1. Put the folder (plugin.json at its top) into the plugins folder (Settings → Plugins → *Open
   plugins folder*) and press *Reload plugins*, or zip it and use *Install from file…*.
2. The pack's card says what it found ("2 themes · 1 lyrics preset · 1 language"), or why it
   was refused.
3. Edit the JSON and press *Reload plugins* again; an active pack theme re-applies with the new colours.

`samples/Noctis.ContentPack.Sample` builds its zip with `dotnet build -c Release`
(`samples/PluginPackage.targets` zips `pack/` when the project sets
`<NoctisContentPack>true</NoctisContentPack>`).

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
- [Official plugins (Get plugins)](#official-plugins-get-plugins)
- [Submitting a plugin](#submitting-a-plugin)
- [Reviewing a submission](#reviewing-a-submission)
- [Publishing plan](#publishing-plan)
- [Versioning and deprecation policy](#versioning-and-deprecation-policy)
- [Legacy plugins (API 1.0, no plugin.json)](#legacy-plugins-api-10-no-pluginjson)
- [Content packs](#content-packs)

## What a plugin can do

API **1.6** (Noctis 1.6.6 and later; 1.2 since 1.5.9; 1.1 since 1.5.3):

| Area | What | Permission |
|---|---|---|
| Now playing | Current track, play/pause state, position, change events | — (`playback.read`, implicit) |
| Audio taps | Live beat pulse and spectrum of what is playing | — |
| Visual layers | A control drawn behind the lyrics (offered as a style in Settings' Flowing background picker) | — |
| Lyrics motion | How the letters of word-synced lyrics move, and which lines read right to left (1.5) | — |
| Views | A page of your own, opened from Settings → Plugins ("Open &lt;name&gt;") in its own window (1.3), at the size it asks for (1.5) | — |
| Album page | Show an album's page in the main window (1.3) | — |
| Scrobble hook | Called when a track counts as listened (half its length or 4 minutes) | — (`playback.read`) |
| Settings | Settings you declare in plugin.json, drawn by Noctis in Settings → Plugins | — |
| Storage | A private data folder that survives updates | — |
| Playback control | Play, pause, next, previous, seek; since 1.3 play a list of library tracks; since 1.5 read the queue, set what is up next and hear when the user queues songs | `playback.control` |
| Library | Read-only search over the library, or all of it, and since 1.4 the playlists (copies, never live objects) | `library.read` |
| BPM and key | Set the BPM and musical key of library tracks (in Noctis only, never in files) | `library.write.analysis` |
| Add to library | Add audio files the plugin saved (downloads, conversions) to the library, and read the music folders (1.5) | `library.import` |
| Decode audio | Decode a song file to mono PCM for offline analysis, with the ffmpeg Noctis ships (1.6) | `audio.decode` |
| Lyrics | A lyrics provider that joins the online lyrics search | `lyrics.provider` |
| Track menu | Entries in the track context menu | `menu.commands` |
| Notices | A short message in the app's notice pill | `notifications` |

Not available (yet): writing other library fields or file tags, editing single queue entries
(1.5 sets the whole of Up Next at once), replacing the order of Noctis's own Shuffle button, sidebar
entries, and the mobile apps (Android does not load plugins).

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
- `plugins/Noctis.Plugins.AlbumTown` — *Album Town* (API 1.4): a view of its own where your albums
  live as little creatures in a plaza and in apartments you build from albums and playlists. Uses
  `RegisterView`, `Library.GetAll()` with the 1.3 track fields, `Library.GetPlaylists()`,
  `PlayTracks` and `ShowAlbum`.
- `plugins/Noctis.Plugins.TrueShuffle` — *True Shuffle* by Alfamin (API 1.5, MIT): shuffle modes over
  the queue, a playlist or the whole library, planned off the UI thread. Uses `GetAll()`,
  `GetPlaylists()`, `GetQueue()`, `SetUpNext()`, `TracksQueued`, a view and a track-menu command.
- `plugins/Noctis.Plugins.LyricMotion` — *Lyric Motion* by Alfamin (API 1.5, MIT): letter-by-letter
  lyrics motion and right-to-left layout through `RegisterLyricsEffect`, with no allocation per frame.
- `plugins/Noctis.Plugins.YouTubeImport` — *Add from YouTube* (API 1.5): a view of its own that
  downloads audio with yt-dlp (installed and kept current by the plugin, checked against the
  release's SHA-256 list), tags it with the app's TagLib#, and adds it through
  `host.LibraryImport`.
- `plugins/Noctis.Plugins.Chords` — *Chords* (API 1.6): estimates the playing song's chords from
  its audio, decoded through `host.AudioDecoder`, and shows them in time with the song in a view
  of its own.
- `plugins/Noctis.Plugins.Mixxx` — *Mixxx* (API 1.2): imports the BPM and key the Mixxx DJ app
  analysed, reading its `mixxxdb.sqlite` read-only with the app's SQLite, matching tracks by file
  path, and writing through `host.TrackAnalysis`.

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
| `description` | no | One short, plain sentence; see [Writing your description](#writing-your-description). |
| `type` | no | `"dotnet"` (default), or `"content"` for a data-only [content pack](#content-packs). |
| `entry` | dotnet | File name of the plugin DLL in the same folder (no path). |
| `entryType` | no | Full type name of the `INoctisPlugin` to create, when the DLL holds several. Otherwise the only (or first by name) implementation is used. |
| `apiVersion` | dotnet | `"major.minor"` of the kit you built against (`"1.1"`). A different **major** is refused. |
| `minAppVersion` | no | Oldest Noctis that can run it (`"1.5.3"`). Older apps refuse with "Needs Noctis x or newer". Set it to the first release with the API minor you use. |
| `platforms` | no | Any of `"windows"`, `"macos"`, `"linux"`. Omit for all. |
| `permissions` | no | What the plugin uses from the API; see [Permissions](#permissions). Unknown names are ignored with a warning. |
| `settings` | no | Settings Noctis draws for you; see below. |
| `homepage` | no | `http(s)` link to the project. Shown as *Visit homepage* on the plugin's page. |
| `icon` | no | The plugin's icon: a PNG in the package, as a path relative to plugin.json (`"icon.png"`). See [Icons](#icons). |
| `contents` | content | What a content pack provides; see [Content packs](#content-packs). Ignored (with a warning) on dotnet plugins. |

### Writing your description

The card in Settings → Plugins shows two lines of it, so write it to fit there:

- **One short, plain sentence** of 56 characters or fewer, so it fits the card's two lines. Say what the
  plugin does for the listener, in everyday words: "Spin a reel of your albums and let it pick
  what to play." Leave the details (modes, options, how it works) for its settings and homepage.
- **No by-line.** The card already says "by X", so don't end with "By X." or credit anyone in it.
- **No em dashes** (the long dash, U+2014). Use a comma or a colon instead.
- Each setting's `description` follows the same rules, in one short line of about 50 characters.

`PluginDescriptionRulesTests` checks every plugin.json in this repo (56 characters at most, no em
dash, no by-line), and the submission checker notes a description over 56 characters or with an
em dash.

### Icons

Settings → Plugins shows every plugin as a card with its icon, and the plugin's own page shows
it large. Give your plugin one:

- A **PNG**, square, **256 × 256** px, at most 512 KB, in the package next to plugin.json
  (a subfolder is fine: `"icon": "assets/icon.png"`). No path outside the package, no `..`.
- Draw it as a rounded-square app tile: one colour or a two-colour gradient filling the square,
  one simple white glyph, transparent corners. It is shown at 60 px on the card and 88 px on the
  page, so keep detail out of it.
- `samples/PluginPackage.targets` puts an `icon.png` that sits next to plugin.json into the zip.
- No `icon`, or one that can't be read: the card draws a letter tile instead (the name's
  initials on a colour picked from the name). A bad `icon` value is a warning, never an error.

The official list (`plugins/index.json`) has the same optional `icon` per entry, so a card shows
its icon before the plugin is installed: a path relative to the list (`"Noctis.Plugins.Mixxx/icon.png"`)
or an https link on GitHub. Icons of plugins in this repo are built into the app; others are
fetched once from that link. The repo's icons are made by
[`scripts/plugin-icons/generate_plugin_icons.py`](../scripts/plugin-icons/generate_plugin_icons.py).

A content pack may set `icon` too, but only Noctis 1.6.6 and newer read it (content packs refuse
fields they don't know, so give such a pack `"minAppVersion": "1.6.6"`). A .NET plugin can add
`icon` without raising its `minAppVersion`: older Noctis ignores the field.

### Declared settings

```json
"settings": [
  { "key": "greeting",  "label": "Greeting",         "type": "string", "default": "Hi", "placeholder": "Say hi" },
  { "key": "loud",      "label": "Shout",            "type": "bool",   "default": false },
  { "key": "limit",     "label": "Results",          "type": "number", "default": 25, "min": 1, "max": 200 },
  { "key": "mode",      "label": "Mode",             "type": "choice", "choices": ["soft", "hard"], "default": "soft",
    "description": "Optional second line under the label." }
]
```

- `key`: letters, digits, `.`, `-`, `_` (max 64), unique.
- `type`: `bool` (switch), `string` (text box, the full width of the row), `number` (clamped to `min`/`max`:
  a slider with its value when both are set, else a minus/plus stepper), `choice` (dropdown of `choices`).
- `description` (optional): one short line, about 50 characters ("When the daily pick shows up (0 to 23)."); the page shows one line and the rest in its tooltip.
- `placeholder` (string settings only, optional): grey hint shown inside the empty text box.
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
| `IPlaybackControl Playback` | 1.1 | `playback.control` | `PlayPause()`, `Play()`, `Pause()`, `Next()`, `Previous()`, `Seek(TimeSpan)`; since 1.3 `PlayTracks(trackIds, startIndex = 0)` replaces the queue with those library tracks (unknown ids are skipped); since 1.5 `GetQueue()`, `SetUpNext(trackIds)` and the `TracksQueued` event; since 1.7 `QueueTracks(trackIds, playNext)`, see [The queue](#the-queue). Safe from any thread. |
| `ILibraryReader Library` | 1.1 | `library.read` | `TrackCount`, `Search(query, limit = 50)` (every word must appear in title, artist or album; accent- and case-insensitive; max 500); since 1.2 `GetAll()`, every track; since 1.4 `GetPlaylists()`, see below. |
| `IPluginSettings Settings` | 1.1 | | Declared settings (see above). |
| `void Notify(string)` | 1.1 | `notifications` | A notice pill for ~4 s. At most one per second per plugin; long text is cut at 200 characters. |
| `IDisposable RegisterLyricsProvider(ILyricsProvider)` | 1.1 | `lyrics.provider` | See below. |
| `IDisposable RegisterTrackCommand(string label, string? icon, Action<TrackInfo>)` | 1.1 | `menu.commands` | Menu entry in the track context menu. `icon`: SVG path data (24×24 box) or null. The handler runs on the UI thread. |
| `IDisposable RegisterTrackCommand(string label, string? icon, Func<TrackInfo, Task>)` | 1.1 | `menu.commands` | Async variant. |
| `IDisposable RegisterLibraryCommand(string label, string? icon, LibraryCommandPlaces places, Action<LibraryCommandContext>)` | 1.7 | `menu.commands` | An entry on library pages: `ArtistPage` (the artist page's "…" menu; the context carries the `Artist`) and/or `SongsPage` (an icon button in the Songs page's top bar, next to Shuffle; needs `icon`). The handler runs on the UI thread. |
| `IDisposable OnTrackScrobbled(Action<TrackInfo, DateTimeOffset>)` | 1.1 | | Fires when a track counts as listened (Last.fm rule), with its start time, whether or not a scrobbling service is connected. |
| `ITrackAnalysisWriter TrackAnalysis` | 1.2 | `library.write.analysis` | Sets BPM and key of library tracks; see below. |
| `IDisposable RegisterView(IPluginViewProvider)` | 1.3 | | A view of your own; see [Views](#views). |
| `bool OpenView(IPluginViewProvider)` | 1.3 | | Opens a view you registered (from a menu command, say). False when it is not registered or the plugin stopped. |
| `bool ShowAlbum(string albumId)` | 1.3 | | Shows the album's page (`TrackInfo.AlbumId`) in the main window and brings it forward. False for an unknown album. |
| `IDisposable RegisterLyricsEffect(ILyricsEffect)` | 1.5 | | How word-synced lyrics move; see [Lyrics effects](#lyrics-effects). |
| `ILibraryImport LibraryImport` | 1.5 | `library.import` | Adds audio files the plugin saved to the library; see [Adding files to the library](#adding-files-to-the-library). |
| `IAudioDecoder AudioDecoder` | 1.6 | `audio.decode` | Decodes a song file to mono PCM; see [Decoding songs for analysis](#decoding-songs-for-analysis). |

Using a hook without declaring its permission throws `PluginPermissionException`.

`TrackInfo` is an immutable copy of a library track: `Id`, `Title`, `Artist`, `Album`,
`AlbumArtist`, `Duration`, `Year`, `Genre`, `TrackNumber`, `FilePath`, `IsFavorite`,
`PlayCount`, `Rating`, since 1.2 `Bpm` (0 = unknown) and `MusicalKey` (empty = unknown), and since
1.3 `AlbumId` (the album as Noctis groups it; empty = unknown), `ArtworkPath` (the cover image Noctis
shows for the track, or null; read it, never write it), `ReleaseType` (`"Album"`, `"Single"`, `"EP"`,
`"Compilation"`, `"Live"`, `"Remix"`, `"Soundtrack"`, `"Other"`), `DateAdded` and `LastPlayed` (null = never),
and since 1.5 `IsDisliked`, `IsExplicit`, `SkipWhenShuffling` and `SnoozedUntil` (null = not snoozed):
what Noctis's own shuffle looks at.

`Library.GetPlaylists()` (1.4, `library.read`) returns `PlaylistInfo` copies of the user's
playlists in sidebar order: `Id`, `Name`, `CoverPath` (the playlist's own cover image, or null when
it has none; Noctis then shows a mosaic of its songs' covers), `TrackIds` (library track ids in
playlist order; ids the library no longer has are left out; a smart playlist lists the tracks its
rules match right now), `Color` (`"#RRGGBB"`, the tile colour without a cover) and `IsSmart`.
Play one with `Playback.PlayTracks(playlist.TrackIds)` (`playback.control`). Call it off the UI
thread for big libraries, like `GetAll()`.

### The queue

API 1.5, `playback.control`:

- `PlaybackQueue GetQueue()`: `CurrentTrackId` (or null) and `UpNext`, the track ids up next in
  order. Exact on the UI thread; from another thread a best-effort copy.
- `void SetUpNext(IReadOnlyList<string> trackIds)`: Up Next becomes these library tracks, in this
  order, while the current song plays on (unknown ids are skipped; explicit songs are left out while
  Settings hides them). Noctis's own Shuffle is switched off, because the order is now yours; Track
  Radio ends; Repeat All repeats the playing song and this list. It does not raise `TracksQueued`.
- `void QueueTracks(IReadOnlyList<string> trackIds, bool playNext)` (1.7): Play Next (`true`, to
  the front, in this order) or Add to Queue (`false`, to the end), exactly like the app's own menu
  entries: Shuffle stays as it is, the queue notice shows and `TracksQueued` is raised.
- `event EventHandler<TracksQueuedEventArgs> TracksQueued`: the user queued songs themselves (Play
  Next or Add to Queue, one song or a batch), raised on the UI thread after the queue changed, with
  `TrackIds` (in the order they now stand) and `PlayNext` (true: they went to the front; false: the
  end). Restoring, shuffling, reordering and autoplay do not raise it.

Keep Up Next short (True Shuffle puts 200 songs there and adds more when 20 are left): Noctis shows
and edits the whole queue, and a library-sized queue is slow to show.

### Lyrics effects

```csharp
public interface ILyricsEffect
{
    string Name { get; }
    void GetLineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft);
    LyricsWordMode GetMode(in LyricsWord word);                // Native, Whole or Letters
    LyricsLetterPose GetPose(in LyricsWord word, int letter);  // scale, offset (em), glow
}
```

API 1.5, no permission. An effect changes how the words of word-synced lyrics move on the lyrics
page, the side panel and the mini player. Noctis keeps drawing them (the sweep, the colours, the glow
layer); the effect only says where each letter sits and how big it is, as a function of what Noctis
reports for the word this frame (`LyricsWord`: where the sweep is through it, how long it is sung,
Noctis's own float, whether it is a held note or the line's last word, its letters, font size and
where it is shown). One effect is used at a time: the latest registered. Dispose the registration (or
switch the plugin off) and Noctis's own motion comes back.

- `GetLineDirections` runs once per lyrics load. Mark the lines that read right to left: Noctis then
  lays their words out from the right, puts the line against the right edge and fills each word
  from the right. Words of such a line are always drawn in one piece (their letters join).
- `GetMode` is asked when a word is laid out (not per frame): `Native` leaves the word to Noctis,
  `Whole` poses it in one piece (letter 0), `Letters` poses each letter (grapheme) on its own.
- `GetPose` runs on the UI thread while a word is drawn, every frame the word changes. It must be
  quick and must not allocate: no strings, LINQ, closures or boxing. Noctis caches every word's
  text layouts; only the pose is asked per frame.
- An exception from any of them withdraws the effect and stops the plugin (Failed), after the frame.

Noctis draws words of joining scripts (Arabic, Persian, Syriac, N'Ko and others) in one piece in any
case, with or without an effect: drawn letter by letter they lost their joined forms.

### Views

```csharp
public interface IPluginViewProvider
{
    string Name { get; }      // window title, and the button: "Open <Name>"
    Control CreateView();     // called each time the user opens it

    // API 1.5, optional (default bodies): the window's size
    Size? PreferredWindowSize => null;                       // DIPs; null = 1000 × 680
    Size? MinWindowSize => null;                             // DIPs; null = 480 × 360 (or the preferred size when smaller)
    SizeToContent WindowSizeToContent => SizeToContent.Manual;
}
```

API 1.3. For plugins that are a place of their own rather than an add-on to an existing page
(a game, a dashboard, a visualizer). Register it in `Initialize`; Settings → Plugins then shows an
**Open &lt;Name&gt;** button on your plugin's card, and Noctis shows your control in a window of its
own (one per view: opening it again brings that window forward).

- `CreateView` runs on the UI thread, guarded like every callback: a throw or a `null` control
  marks the plugin Failed.
- The window closes when the plugin stops (switched off, removed, reloaded) or you dispose the
  registration. The control is then detached, so stop timers in `DetachedFromVisualTree`.
- Keep it cheap: draw only while the control is attached, visible and its window is not minimized.
- **Window size (1.5).** The window opens at 1000 × 680, a size made for pages. A smaller view says
  what it wants with `PreferredWindowSize` (and `MinWindowSize`), declared as public properties on
  the class that implements `IPluginViewProvider`:

  ```csharp
  public Size? PreferredWindowSize => new Size(440, 700);
  public Size? MinWindowSize => new Size(360, 560);
  ```

  Sizes are device-independent pixels of the client area (a 440 × 700 window is 550 × 875 pixels
  at 125 %). Noctis sizes the window before it shows it, centres it on the screen Noctis is on,
  keeps it within that screen, and the user can still resize it down to the minimum. An axis that
  is not a positive number keeps the default. `WindowSizeToContent` opens the window at the size
  of your content instead (set a `MinWindowSize` too, or the default minimum applies). Older
  Noctis versions ignore all three, so declaring them never stops a plugin from loading.
  Don't resize the window from your control (`TopLevel.GetTopLevel(this)` in
  `OnAttachedToVisualTree`): that runs inside the window's first layout pass, and the window then
  goes back to the size it started with.
- Your window wears Noctis's look: every `CheckBox` is the Settings switch (label left, switch right),
  buttons, single-line text boxes and `ComboBox`es are pills, and menus, flyouts and drop-down lists
  are frosted glass over your window. The pop-up classes are there too: `pill-primary`,
  `pill-secondary`, `pill-field`, `pill-well`, and for a pop-up inside your view a
  `ContentControl` with class `plugin-backdrop` (your view behind it, blurred and dimmed; give it a
  dim `Background`) under a `Border` with class `plugin-dialog` (the card). Your own styles win.

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

### BPM and key

```csharp
public interface ITrackAnalysisWriter
{
    Task<int> SetTrackAnalysisAsync(IReadOnlyList<TrackAnalysisUpdate> updates, bool overwrite = false);
}
public sealed record TrackAnalysisUpdate(string TrackId, int? Bpm = null, string? MusicalKey = null);
```

For plugins that bring tempo and key from elsewhere (a DJ app's analysis, an online service).
These are the two values Noctis's own background tempo/key analysis fills in, used by the Songs
BPM column, AutoMix transitions and Track Radio.

- Only Noctis's library changes. File tags are never written and no other field can be set.
- `TrackId` is `TrackInfo.Id` (find tracks with `Library.GetAll()` or `Search`). `null` leaves a
  value alone. Without `overwrite` only empty values are filled (BPM 0, no key).
- Skipped silently: unknown ids, BPM outside 1–999, a blank key or one longer than 32 characters.
  Write keys the way Noctis's analysis does (`"A minor"`, `"F# major"`); Camelot (`"8A"`) and
  short forms (`"Am"`) are understood too.
- The values are set on the UI thread, then the library is saved once per call: send one batch,
  not one call per track. Callable from any thread; returns how many tracks changed. A stopped
  plugin's calls change nothing.
- Values set this way survive a rescan unless the file's own tags carry a BPM or key, and tracks
  that have both are skipped by the background analysis.

### Adding files to the library

```csharp
public interface ILibraryImport
{
    IReadOnlyList<string> MusicFolders { get; }
    Task<IReadOnlyList<TrackInfo>> ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
}
```

For plugins that produce audio files (a downloader, a converter, a recorder). `ImportFilesAsync`
does what dropping the files on the window does: it adds them to the library (or refreshes them
when already there) without a rescan, and marks them as added one by one, so they stay in the
library across rescans even outside the music folders.

- Nothing is moved, renamed or retagged: put the file where it belongs and write its tags first.
  `MusicFolders` (the folders from Settings → Library, copies) helps pick that place.
- Paths that are not absolute, do not exist, or are not a format Noctis plays are skipped; at most
  500 paths per call. The result holds the library's copies (`TrackInfo`) of the files that are in
  the library afterwards, so a file Noctis could not read is simply missing from it.
- Waits for a running library scan, then imports on the UI thread. Callable from any thread. A
  stopped plugin's calls change nothing.

### Decoding songs for analysis

```csharp
public interface IAudioDecoder
{
    bool IsAvailable { get; }
    Task<DecodedAudio> DecodeMonoAsync(string filePath, int sampleRate, TimeSpan maxDuration, CancellationToken ct = default);
}
public sealed record DecodedAudio(float[] Samples, int SampleRate) { public TimeSpan Duration { get; } }
```

For plugins that look at the sound itself (chords, tempo, loudness) without shipping a decoder
per plugin. Noctis decodes with the same ffmpeg its converter and its BPM/key analysis use, in a
separate process, so any format Noctis plays works.

- From the start of the file, the first audio stream, downmixed to one channel, resampled to
  `sampleRate` (clamped to 8000–48000 Hz), floats in -1..1. `maxDuration` caps the length (20
  minutes at most). Pick the lowest rate your analysis needs: 11025 Hz for 4 minutes is about 10 MB.
- `IsAvailable` is false when Noctis found no ffmpeg (Settings → Advanced → Helper programs); then
  `DecodeMonoAsync` throws `InvalidOperationException`, as it does for a file that is not audio and
  for a decode that runs past 3 minutes. A missing file throws `FileNotFoundException`.
- Runs off the UI thread; at most two decodes run at once across all plugins (the rest wait).
  Cancelling the token stops ffmpeg at once. A stopped plugin's calls throw.

## Permissions

| Name | Grants | Shown to the user as |
|---|---|---|
| `playback.read` | Now playing, events, scrobble hook. Implicit: you need not list it. | — |
| `playback.control` | `host.Playback` | Control playback: play, pause, skip and seek |
| `library.read` | `host.Library` | Read your library and playlists (titles, artists, albums, file paths, play counts) |
| `network` | Nothing in the API: a **disclosure** that the plugin talks to the internet. Declare it if you do. | Connect to the internet |
| `lyrics.provider` | `RegisterLyricsProvider` | Supply lyrics to the lyrics search |
| `menu.commands` | `RegisterTrackCommand`, `RegisterLibraryCommand` (1.7) | Add entries to the track menu |
| `notifications` | `Notify` | Show short notices |
| `library.write.analysis` | `TrackAnalysis` (1.2) | Set the BPM and musical key of library tracks (in Noctis only, never in your files) |
| `library.import` | `LibraryImport` (1.5) | Add audio files it saved (such as downloads) to your library |
| `audio.decode` | `AudioDecoder` (1.6) | Decode songs to analyze their sound (nothing is changed or sent anywhere) |

The first time a user switches a plugin on, Noctis shows exactly this list and asks. If an
update asks for more, the plugin waits in *Needs approval* until the user switches it on again.

## Safety model

Be clear about what protects users and what does not:

1. **.NET plugins run with full trust.** A plugin is code inside the Noctis process. It can
   read and write any file the user can, open network connections, and start programs,
   whatever its permissions say. .NET has no sandbox for in-process code (Code Access
   Security is gone). **Permissions only gate the Noctis API**; they are an honest summary
   of intent, not a jail. The approval dialog says so.
2. **Restricted mode (Community plugins off).** No community plugin code is loaded; installed
   plugins are still listed. Verified official plugins don't depend on the switch: they run either
   way, and flipping it never restarts them. Fresh installs start in restricted mode. Installs that already
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
- **Stop**: switch off, *Remove*, *Reload plugins*, turning Community plugins off (community
  plugins only), app exit.
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
  `deps.json`. An assembly the app has already loaded is always the app's copy, and one the
  plugin does not ship falls back to the app's: that is how the Mixxx plugin uses the app's
  `Microsoft.Data.Sqlite` (referenced with `ExcludeAssets="runtime"`, so the zip carries no SQLite).
- The samples import `samples/PluginPackage.targets`, which zips `bin/Release/<name>-<version>.zip`
  after every Release build. Copy it into your project or zip by hand. An `icon.png` next to the
  project's plugin.json goes into the zip too (the official plugins ship a 256 px one).
- That zip is **reproducible**: Release builds leave the commit hash and PDB path out of the DLL,
  and the zip lists its files in name order with one fixed date. The same source, OS and .NET SDK
  give a byte-identical zip, so a published SHA-256 can be checked by rebuilding.
- Keep `version` in plugin.json and `<Version>` in the csproj in step.

## Installing, updating, removing

- **Get plugins**: Settings → Plugins lists the plugins on the official list as cards; a card's
  page has its *Install* button. See [Official plugins](#official-plugins-get-plugins).
- **Install**: Settings → Plugins → *Install from file* (top of the page) and pick the zip. It lands in
  `plugins/<id>/`, switched off, until you enable it (with the permission prompt).
- **Update**: installing a zip with the same id and a **higher** version offers to update; the
  folder is replaced, data and settings are kept, and approval carries over unless new
  permissions are requested. Same or lower versions are refused.
- **Remove**: the *Remove* button asks, deletes the plugin folder, and keeps its data and
  settings unless you tick *Also delete its data and settings*. A reinstall asks for approval again.
  On release builds only a plugin you added yourself (Install from file, or the folder) has
  *Remove*; plugins listed in Noctis, official and community, can only be switched on and off.
- **By hand**: drop the unzipped folder into the plugins folder (the folder button next to
  *Install from file*; its tooltip shows the path) and press the refresh button next to it.

## Official and community plugins

Settings → Plugins shows two sections of cards (icon, name, "by …", two lines of description;
a search box above filters both). A card opens the plugin's own page: its icon, version and
description, Install / Update / Open, the on/off switch, permissions, settings, homepage, the
plugin folder and Remove.

**Official Noctis plugins** are built by the Noctis team.

- Their `author` is **Noctis**, in plugins/index.json and in their own plugin.json.
- They are listed in the official list, [`plugins/index.json`](../plugins/index.json), and their
  source lives in this repo's `plugins/` folder.
- They **always** carry the blue verified check, on the card and on the page.

**Community plugins** are made and submitted by users ([Submitting a plugin](#submitting-a-plugin))
and are credited by name ("by Jafezy JD"). They never carry the check, whether they are on the
list (a reviewed submission) or installed from a file. Album Town (by Jafezy JD), True Shuffle
and Lyric Motion (by Alfamin) are community plugins; Mixxx is official.

**How the app decides.** The check is never something a plugin can claim. Writing
`"author": "Noctis"` in a plugin.json, or reusing an official plugin's `id`, does nothing. An
installed copy counts as official only when its zip was checked against the official list when
it was installed:

1. the list has an entry with that `id` whose `author` is Noctis,
2. the zip's own plugin.json says `author` Noctis, with the same `id` and `version`, and
3. the zip's SHA-256 equals the entry's `sha256`.

*Get plugins* downloads always go through that check. *Install from file* does too, when the zip
is the listed id and version, so the official zip installed by hand is still official. Anything
else with an official plugin's id shows as a separate community card, and the official card
offers **Replace with official** (downloads the listed zip and puts it in place; data and
settings are kept). A copy installed before this check existed shows the same way until replaced
or updated once. The result is kept per plugin id in settings.json (`VerifiedOfficialPlugins`)
and dropped when the plugin is removed or replaced by another zip.

## Official plugins (Get plugins)

The official list, plugins/index.json, holds the plugins the Noctis project publishes itself and
community plugins it has reviewed ([Submitting a plugin](#submitting-a-plugin)). Entries by
Noctis show under Official Noctis plugins, the others under Community plugins. Each card's page
has the author's credit ("by …") and a single button:

| Button | When |
|---|---|
| Install | not installed |
| Update | an older version is installed (same flow; data and settings are kept, approval carries over unless new permissions are asked for) |
| Installed | this version or a newer one is installed (the page shows the plugin's switch instead) |
| Replace with official | an official plugin's id is installed, but not from its checked zip (see above) |
| Requires Noctis x.y.z | this Noctis is older than the plugin's `minAppVersion` (disabled) |
| Not available on this OS | the plugin's `platforms` leave this OS out (disabled) |

**The list** is one file in this repository, [`plugins/index.json`](../plugins/index.json):

```json
{
  "schema": 1,
  "plugins": [
    {
      "id": "dev.noctis.plugins.mixxx",
      "name": "Mixxx",
      "author": "Noctis",
      "description": "One short, plain sentence.",
      "version": "1.0.0",
      "minAppVersion": "1.5.9",
      "type": "dotnet",
      "platforms": ["windows", "macos", "linux"],
      "download": "https://github.com/heartached/Noctis/releases/download/plugin-mixxx-v1.0.0/Noctis.Plugins.Mixxx-1.0.0.zip",
      "sha256": "<lowercase hex SHA-256 of that exact zip>",
      "size": 11758,
      "homepage": "https://github.com/heartached/Noctis/tree/main/plugins/Noctis.Plugins.Mixxx",
      "icon": "Noctis.Plugins.Mixxx/icon.png"
    }
  ]
}
```

- `icon` (optional) is the card's picture: a path relative to index.json, or an https link on
  GitHub, to a PNG ([Icons](#icons)). Older Noctis versions ignore it.

- `id`, `name`, `version`, `minAppVersion`, `type` and `platforms` must match the plugin's own
  plugin.json (a unit test checks every listed plugin). `download` must be https on GitHub; the
  release tag is `plugin-<short name>-v<version>`.
- An entry with a missing or bad field is skipped (and logged), never the whole list; a file
  with another `schema` is ignored.
- A community entry has `homepage` = its source repository and may add `authorUrl`, an https
  link for the credit (the app ignores fields it doesn't know).

**Where the app reads it**: `https://raw.githubusercontent.com/heartached/Noctis/main/plugins/index.json`,
when the Plugins page is opened (once per session) and when you press the refresh button, never
at startup. A copy of the file is built into the app: it shows at once while the live list
loads, and stays (with "Couldn't reach GitHub…") when the live one can't be fetched, e.g.
offline or before main has the file. The website reads the same file when it is built.

**Installing from the list**: the zip is downloaded (resumed on a flaky connection, cancellable),
then its size and SHA-256 are checked against the list **before** anything touches the plugins
folder; a mismatch is refused. The zip must also be the listed id and version. It then installs
exactly like *Install from file*: it lands switched off and asks for approval when you switch it
on. An official plugin runs whether *Community plugins* is on or off. A community .NET plugin
installed while it is off offers that switch's own confirmation; saying no leaves the plugin
installed and off.

**Testing a list locally**: set `NOCTIS_PLUGIN_INDEX` to a file path or a URL before starting
Noctis. That list is used instead of the live one, and only for it, `download` may also be
`http://`, a local file, or a path relative to the list file. For example, next to a copy of
`index.json` whose Mixxx `download` is `"Noctis.Plugins.Mixxx-1.0.0.zip"`:

```powershell
$env:NOCTIS_PLUGIN_INDEX = "D:\plugin-test\index.json"
dotnet run --project src/Noctis/Noctis.csproj -p:Version=1.5.9
```

(`-p:Version=1.5.9` only matters while the app is still 1.5.8 and the plugin needs 1.5.9.)

**Publishing an official plugin** (build zip → SHA-256 → release tag → update `index.json` →
push): the exact commands are in [`plugins/PUBLISHING.md`](../plugins/PUBLISHING.md).

### The official plugins

| Plugin | Folder | Uses |
|---|---|---|
| Mixxx | `plugins/Noctis.Plugins.Mixxx` | API 1.2: BPM and key from Mixxx's library |
| Album Roulette | `plugins/Noctis.Plugins.AlbumRoulette` | API 1.4: a view, `PlayTracks`, `ShowAlbum`, a track command, notices |
| A-B Loop | `plugins/Noctis.Plugins.ABLoop` | API 1.4: a view, `Seek`/`Play`/`Pause`, a track command |
| Chords | `plugins/Noctis.Plugins.Chords` | API 1.6: `AudioDecoder`, a view, `Seek`, `PlayTracks`, a track command |

Album Roulette's, A-B Loop's and Chords' icons come from `scripts/plugin-icons/generate_official_plugin_icons.py`
(Mixxx's and the community plugins' from `generate_plugin_icons.py`; Pillow and numpy; run it
after changing a design and commit the `icon.png` files it writes).

**Album Roulette's reel** draws on `TopLevel.RequestAnimationFrame` only while it moves and waits
while its window is hidden or minimized. A frame allocates nothing: covers are decoded once,
straight to the drawn size, with their corners rounded into the bitmap; the glow and the edge
fades are drawn once into bitmaps; brushes are immutable; no clip or opacity is pushed (each push
costs the drawing context an object). `AlbumRoulettePluginTests.Reel_FrameBudget_NoAllocationsPerFrame`
measures it (0 bytes per frame, stepping and drawing).

**How exact A-B Loop is.** A plugin reads `NowPlaying.Position`, which Noctis updates about every
100 ms (the player's position timer), and stays still for 300 ms after a seek while the player
settles. Looping on that alone lands up to ~100 ms past B. A-B Loop instead runs a ~15 ms timer
(while a loop is on) that predicts the position between updates (the last value plus the time
since it was first seen, re-anchored on every update and at each jump) and jumps half a tick
early, so the error is centred on zero. Measured against a simulated host with 100 ms updates
(+-8 ms jitter, up to 6 ms to reach the UI thread) and a 15 ms (+-2 ms) timer, over 400 loops
(`ABLoopPluginTests.LoopBack_Timing_PredictedVersusRawPosition`): mean |error| 5.8 ms, p95 13.8 ms,
max 20.7 ms, against 54.5 / 98.8 / 112.9 ms when looping on the raw position. The jump itself is
an ordinary seek, so the audio engine's seek time (it flushes and refills its buffer) comes on top
and is not part of that number, and the seam is not sample-exact. A host `SetLoop(a, b)` was
considered and left out: in the app it would sit on the same 100 ms position updates and jump
with the same seek, so it would not be more exact; a sample-exact loop needs the decoder itself to
wrap around, which is engine work, not a small additive API. Expect ~0.1 s or better, not a
seamless loop.

**How Chords works, and how exact it is.** "Find chords" (or "Show chords" in a track's menu)
has Noctis decode the playing song to 11025 Hz mono (`host.AudioDecoder`, the first 15 minutes)
and analyses it on a below-normal-priority thread, cancellable, entirely on the computer: no chord
website is asked (they forbid scraping). The result is cached per file (path, size and date) in the
plugin's data folder, so a song opens with its chords at once the next time. The analysis:
4096-sample Hann frames every 1024 samples (93 ms); spectral peaks between 50 Hz and 2.5 kHz,
placed to a fraction of a bin; the recording's tuning estimated from where all peaks sit against
A = 440 Hz; each peak credited to its pitch class and, with falling weight, to the fundamentals it
could be a harmonic of (a harmonic pitch-class profile), plus a separate bass profile; each frame
scored against major and minor templates (with "Show seventh chords", also 7, maj7 and m7) by
cosine similarity and a bass bonus for the root; Viterbi decoding where each chord change costs a
fixed penalty (so one odd frame does not flip the chord); the key from Krumhansl-Kessler profiles
plus the tonic chord the song holds longest, then a second pass that favours the key's chords
a little. Chords are spelled with flats in flat keys.

Accuracy is measured, not claimed: `ChordsAccuracyTests` synthesizes songs with a known chord at
every instant (each note a stack of harmonics, so the overtones mislead as they do on a real
instrument) and counts the share of 10 ms instants named right:

| Synthetic test | Frame accuracy |
|---|---|
| C G Am F, held notes | 98.8% |
| C G Am F, piano, drums, noise at 15 dB SNR, detuned +30 cents (tuning found: +29) | 97.6% |
| C G Am F, piano, a melody of scale notes on top, inversions in the bass, drums, noise at 20 dB | 94.6% |
| Am Dm E Am F G E Am, piano | 96.9% |
| All 24 major and minor chords, 1 s each, random order | 97.5% |
| Dm7 G7 Cmaj7 Am7, sevenths on (exact chord) / off (as Dm G C Am) | 98.8% / 98.9% |
| Silence, G D Em C, silence (silence must read "no chord") | 97.6% |
| C G Am F as a WAV and an MP3, decoded through the real host and ffmpeg | 97.5% / 97.5% |

Most of what is missed sits at the chord changes (the frames straddle them). The key is right in
all seven key tests (C, A minor, F, E minor, Eb, G, and through the host). A 4-minute song takes
about 1.3 s to analyse on a desktop CPU, plus the decode. Synthetic audio is much easier than a
mixed record (no voice, no passing notes, every chord exactly a triad or a seventh), so these
numbers are a ceiling: on real songs expect clearly less, especially with distorted guitars,
heavy vocals or chords beyond major, minor and sevenths. That is why the window says "Estimate"
and explains it.

## Submitting a plugin

Made a plugin? Submit it for the list in **Get plugins**. Someone from the Noctis team reviews it
and, once it's approved, it shows up under **Community plugins** in every copy of Noctis, credited
to you.
New to plugins? The beginner guide, [Make a plugin](https://noctisapp.cc/plugins/make) (also
Settings → Plugins → Make your own plugin → *Read the Guide*), goes from the sample to a submission.

1. **Put the source on GitHub**, in a public repository with a licence file. Reviews read the
   source, so closed-source or obfuscated plugins can't be listed.
2. **Publish a GitHub release** of that repository with the plugin `.zip` attached (the same zip
   you'd use for Install from file, see [Building and packaging](#building-and-packaging)). Its
   `plugin.json` needs `id`, `name`, `version`, `minAppVersion` and, for a .NET plugin, `apiVersion`.
   Declare `network` if the plugin ever goes online.
3. **Get the zip's SHA-256**: `(Get-FileHash file.zip -Algorithm SHA256).Hash.ToLower()` on
   Windows, `shasum -a 256 file.zip` on macOS and Linux.
4. **Fill in the form**: in Noctis, Settings → Plugins → Make your own plugin → **Submit a Plugin**, or
   [open it on GitHub](https://github.com/heartached/Noctis/issues/new?template=3-plugin-submission.yml).
   It asks for the name, id, version, a one-line description, what the plugin does and how to try
   it (screenshots help), your credit, the source and release links, the SHA-256, the licence, and
   what the plugin does with the internet and files.

Within a few minutes a bot comments with a report: it downloads your zip and checks it with the
same code Noctis uses to install plugins (it never runs your plugin). **Problems** must be fixed
before the plugin can be listed; **red flags** (a bundled program or script, native code,
starting other programs, loading code at run time, the registry) aren't refusals, but explain
them in a comment. Edit the issue to re-run the check, for example after a new release.

**Updating a listed plugin**: publish a new release with a higher version and submit the form
again with the new link and SHA-256, from the same repository. Approved updates show as
**Update** in Get plugins; your users' data and settings are kept.

What gets refused: telemetry or ads, anything that downloads copyrighted music or gets around
DRM, collecting tokens or passwords, hidden behaviour, and code nobody can read.

## Reviewing a submission

Every "Plugin submission" issue gets one report comment from
[`.github/workflows/plugin-submission.yml`](../.github/workflows/plugin-submission.yml), updated
each time the issue is edited. The automatic part
([`tools/Noctis.PluginCheck`](../tools/Noctis.PluginCheck)) downloads the zip (GitHub release
assets only, 100 MB at most), checks its SHA-256 and size against the form and GitHub's own
digest, runs `PluginInstaller.Inspect` and `PluginCatalog.Parse` on it (what Install from file
and Get plugins run), compares plugin.json with the form, and lists what the code references,
read as metadata only. The bottom of the comment is the `plugins/index.json` entry.

The manual part is yours. Before applying the label, check that:

- [ ] the source repository builds the released zip (build the release tag yourself; a zip
      made with `samples/PluginPackage.targets` on the same OS and .NET SDK has the same
      SHA-256, otherwise compare the file list and DLL sizes);
- [ ] what it does with the internet and files matches the form and plugin.json's permissions:
      read the code behind every "network" and "files" use;
- [ ] there's no telemetry, ads, miners, token or password collection, copyrighted downloads or
      DRM workarounds;
- [ ] every red flag in the report has a good reason (a native library it really needs, …);
- [ ] the licence lets people use it, and the name and description are plain and accurate;
- [ ] it works: Install from file in a dev build, switch it on, use it, switch it off, remove it.

Then **apply the `approved` label**. The workflow checks everything again from scratch (the
earlier comment isn't trusted), and opens a pull request `plugin-submission/<issue>` that adds or
updates the entry in `plugins/index.json`, crediting the submitter in `author`. **Merging the PR
lists the plugin**; the PR closes the issue. If the second check finds a problem, there's no PR:
the label comes off and the issue says why. Only people with write access can approve.

To reject, say why in a comment and close the issue. To de-list a plugin later, remove its entry
from `plugins/index.json`; installed copies stay. The app's built-in copy of the list is updated
by the next app release.

Run the check by hand (offline, against a local zip):

```powershell
dotnet run --project tools/Noctis.PluginCheck -c Release -- check --form body.md --zip plugin.zip --index plugins/index.json --offline
```

`body.md` is the issue's text (Edit → copy). Drop `--offline` (and `--zip`) to also download the
zip and ask the GitHub API, as the workflow does.

**One-time setup on GitHub**: create the labels `plugin-submission`, `needs-review` and
`approved` (the form only applies labels that exist), and under Settings → Actions → General →
Workflow permissions, tick **Allow GitHub Actions to create and approve pull requests**. A PR
opened by the workflow doesn't start the Build workflow by itself (GitHub's rule for its own
token); the push to `main` after merging does.

### Discord announcement

Text to pin in the Discord's plugins channel:

> **Made a Noctis plugin? Get it into the app.**
> Submit it for the official list in Settings → Plugins → Get plugins, credited to you:
> https://github.com/heartached/Noctis/issues/new?template=3-plugin-submission.yml
> (or in Noctis: Settings → Plugins → Submit a Plugin).
> You need the source on GitHub and the plugin zip on a GitHub release. A bot checks the zip
> within minutes, then the team reviews it. Guide: https://github.com/heartached/Noctis/blob/main/docs/PLUGINS.md#submitting-a-plugin

## Publishing plan

Phase 1 shipped the loader, manifest, permissions, restricted mode and install-from-file.
Noctis 1.5.9 adds [Get plugins](#official-plugins-get-plugins) for the official plugins
(`plugins/index.json`, SHA-256 checked on download). Community plugins join the same list
through a reviewed [submission](#submitting-a-plugin): an issue form, an automatic check of the
zip, and a pull request once a maintainer approves it.

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
| 1.2 | 1.5.9+ | `TrackInfo.Bpm` / `MusicalKey`, `ILibraryReader.GetAll()`, `ITrackAnalysisWriter` (`host.TrackAnalysis`) with the `library.write.analysis` permission. |
| 1.3 | 1.6.6+ | Views (`RegisterView` / `OpenView`), `ShowAlbum`, `IPlaybackControl.PlayTracks`, `TrackInfo.AlbumId` / `ArtworkPath` / `ReleaseType` / `DateAdded` / `LastPlayed`. |
| 1.4 | 1.6.6+ | `ILibraryReader.GetPlaylists()` and `PlaylistInfo` (under `library.read`). |
| 1.5 | 1.6.6+ | `IPlaybackControl.GetQueue()` / `SetUpNext()` / `TracksQueued` (under `playback.control`), `TrackInfo.IsDisliked` / `IsExplicit` / `SkipWhenShuffling` / `SnoozedUntil`, lyrics effects (`RegisterLyricsEffect`, `ILyricsEffect`); `ILibraryImport` (`host.LibraryImport`) with the `library.import` permission: add audio files to the library, read the music folders; view window size (`IPluginViewProvider.PreferredWindowSize` / `MinWindowSize` / `WindowSizeToContent`). |
| 1.6 | 1.6.6+ | `IAudioDecoder` (`host.AudioDecoder`) with the `audio.decode` permission: decode a song file to mono PCM for offline analysis. |

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
user's accent and cannot be set by a theme. `QueueDrawerBackground` is always drawn opaque (its
alpha is ignored), so the page never shows through the queue drawer. Pack themes appear in Settings → Appearance → Themes
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

1. Put the folder (plugin.json at its top) into the plugins folder (Settings → Plugins →
   Installed → folder button) and press the reload button, or zip it and use *Install from file*.
2. The pack's card says what it found ("2 themes · 1 lyrics preset · 1 language"), or why it
   was refused.
3. Edit the JSON and press reload again; an active pack theme re-applies with the new colours.

`samples/Noctis.ContentPack.Sample` builds its zip with `dotnet build -c Release`
(`samples/PluginPackage.targets` zips `pack/` when the project sets
`<NoctisContentPack>true</NoctisContentPack>`).

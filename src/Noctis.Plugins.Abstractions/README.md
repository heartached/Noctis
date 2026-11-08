# Noctis.Plugins.Abstractions

The plugin kit for [Noctis](https://github.com/heartached/Noctis), a desktop music player.
Reference it with `ExcludeAssets="runtime"`: the app supplies this assembly (and Avalonia) at run time.

```xml
<PackageReference Include="Noctis.Plugins.Abstractions" Version="1.6.*" ExcludeAssets="runtime" PrivateAssets="all" />
```

Implement `INoctisPlugin`, add a `plugin.json` manifest next to your DLL, zip the folder and
install it from Settings → Plugins → Install from file…

Full guide (manifest reference, API, permissions, safety model, packaging):
https://github.com/heartached/Noctis/blob/main/docs/PLUGINS.md

## Release notes

- **1.7** (unreleased): `ILibraryReader.GetLyricLines` (the lyric lines Noctis already has for a
  track; older Noctis versions throw `NotSupportedException`), `IPlaybackControl.QueueTracks` (Play
  Next / Add to Queue as the app does them), and `RegisterLibraryCommand` with
  `LibraryCommandPlaces` (entries on the artist page's menu and the Songs page's top bar, under
  `menu.commands`).
- **1.5** (Noctis 1.6.6+): the queue (`GetQueue`, `SetUpNext`, `TracksQueued`), more `TrackInfo`
  fields, lyrics effects, `ILibraryImport`, and the size a view's window opens at
  (`IPluginViewProvider.PreferredWindowSize`, `MinWindowSize`, `WindowSizeToContent`; older Noctis
  versions ignore them).
- Earlier versions: see "Versioning and deprecation policy" in the full guide.

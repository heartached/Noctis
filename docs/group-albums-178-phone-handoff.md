# GitHub #178 "Group songs into one album": phone handoff

Branch `feat/178-group-albums-1105`. Desktop work only; no phone UI was built.

## What the phone gets for free

The automatic fix is in Noctis.Core, on the same path the phone uses:

- `MetadataService.BuildTrack` (shared by desktop paths and the phone's stream-backed
  `ScanEntry` reads) now sets `Track.AlbumArtistFromTag`.
- `LibraryService.RebuildIndexesCoreAsync` runs `AlbumGrouping.Apply` before grouping by
  `AlbumId`. The phone's `ILibraryService` is the same `LibraryService`.
- Rule: songs without an Album Artist tag (not compilation-flagged) with the same album name
  (case and spacing ignored) in the same folder become one album. Album artist = the tagged
  songs' album artist if they agree, else the first artist credited on every song, else
  "Various Artists". Real tags are never overridden.
- Covers, animated covers and `AppSettings.PinnedAlbumIds` (the phone's Pinned rail) follow a
  re-keyed album (`LibraryService.CarryAlbumKeyedFiles`, `RemapPinnedAlbumIds`).

## Where the phone differs

- **Folder songs (SAF document URIs)** group: `AlbumGrouping.FolderKey` decodes
  `content://…/document/primary%3AMusic%2FAlbum%2F01.mp3` to the folder `primary:Music/Album`
  (a `CD1`/`Disc 2` subfolder counts as its parent).
- **"All Music on This Phone" songs (MediaStore, `content://media/external/audio/media/{id}`)
  are NOT grouped automatically**: the URI has no folder. To cover them, the MediaStore row's
  `RELATIVE_PATH` (already read in `DeviceMusicScan`) would have to be kept on the track and
  used by `FolderKey`. Not done.
- **Existing phone libraries**: `LibraryService.ResolveAlbumArtistSourcesAsync` (runs in
  `LoadAsync`'s background init) cannot open `content://` files outside a scan. It clears the
  stored modification stamp of the phone songs in split albums, so the next scan re-reads
  them. With "Scan on startup" off, the user has to rescan once before those albums group.
- **Songs streamed from the desktop** (`SourceType.NoctisServer`) arrive with the desktop's
  `AlbumId`/`AlbumArtist`, so they are already grouped by the desktop. Their
  `AlbumArtistFromTag` is null, so the phone never re-keys them.
- The phone's `LibraryViewModel._pinnedAlbumIds` is read from settings at load; a pin re-keyed
  while the app runs shows after the next settings reload.

## Manual "Group into One Album"

Desktop only for now: `ILibraryService.GroupIntoAlbumAsync(tracks, album, albumArtist)`
(Core, so the phone can call it). It writes Album and Album Artist through
`IMetadataService.WriteTrackMetadata`, which takes a filesystem path. On the phone, SAF songs
come back in `AlbumGroupResult.UnwrittenFiles` (grouped in the library only, lost if the file
is re-read). A phone version needs a SAF tag write first.

Defaults for a picker: `AlbumGrouping.SuggestGroupTarget(tracks)`; a single album's namesakes:
`AlbumGrouping.SameNameAlbums(library.Albums, album)`.

## Tests

`tests/Noctis.Tests/GroupAlbums178Tests.cs` (includes the document-URI folder key test).

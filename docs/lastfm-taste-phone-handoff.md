# Last.fm mixes and Discover (GitHub #177): phone handoff

The desktop shows two Home sections built from the connected Last.fm account:

- **From your Last.fm**: three mixes made only of songs already in the library
  (Your Last.fm Top Songs, Loved on Last.fm, From the artists you play most), with a
  Refresh pill.
- **Discover on Last.fm**: artists and albums the library lacks. Each entry only opens its
  Last.fm page in the browser. No download, no store links, nothing else.

All logic is in `src/Noctis.Core` (namespace `Noctis.Services`), so the phone needs only UI.

## Shared API

`src/Noctis.Core/Services/LastFmTaste.cs`

- `LastFmTasteService(HttpClient http, string cachePath, Func<DateTimeOffset>? now = null, TimeSpan? ttl = null, Func<TimeSpan, CancellationToken, Task>? delay = null)`
  - `Task<LastFmTasteResult> GetAsync(string user, bool forceRefresh = false, string period = LastFmPeriod.Default, CancellationToken ct = default)`
    returns `LastFmTasteResult(LastFmTasteSnapshot? Snapshot, LastFmTasteError? Error, bool FromCache)`.
    It never throws for network or Last.fm trouble (only cancellation throws). A failed refresh
    still returns the old snapshot, with `Error` set.
  - `Task<LastFmTasteSnapshot?> PeekAsync(string user)` returns what is cached and makes no request.
  - `Task ClearAsync()` forgets the snapshot and deletes the file. Call it on sign-out and on reset.
- `LastFmTasteSnapshot`: `User`, `Period`, `FetchedUtc`, `TopTracks`, `LovedTracks`,
  `TopArtists`, `TopAlbums`, `SimilarArtists` (records `LastFmTasteTrack`, `LastFmTasteArtist`,
  `LastFmTasteAlbum`, `LastFmSimilarArtist`).
- `LastFmTasteError`: `UserNotFound` (Last.fm error 6), `Private` (17), `Unreachable`
  (offline, timeout, 429/5xx or codes 8/11/16/29 after the retries), `BadReply`.
- `LastFmPeriod`: the API's period strings. The default is `6month`.
- `LastFmTasteClient` is the HTTP reader. You normally go through the service. One refresh
  makes 4 account reads plus up to 5 `artist.getSimilar` calls. Requests run one at a time,
  250 ms apart, with up to 3 attempts and back-off. A similar-artist read that fails only
  leaves that one seed out.

`src/Noctis.Core/Services/LastFmTasteMixes.cs`

- `LastFmTasteMixes.Build(IReadOnlyList<Track> library, LastFmTasteSnapshot snapshot, int mixSize = 50, int discoverSize = 12)`
  returns `LastFmTasteMixSet { TopSongs, Loved, TopArtists, Discover }`.
  - This is pure code (no I/O), so run it off the UI thread.
  - Pass only songs that may be suggested. The desktop drops `IsSnoozed` and `IsInInbox` songs.
  - The artist mix leaves out songs that are already in Top Songs.
  - Matching uses `LibrarySongIndex`, the play count import's matcher, which now lives in
    `src/Noctis.Core/Services/PlayCountImportMatcher.cs`. It ignores case, accents, feat.
    credits and remaster/edition suffixes. Live, remix and other versions never match the
    studio song.
- `LastFmDiscoverItem(Name, Artist?, Reason, Because, Url)` with `IsAlbum`. `Reason` is one of:
  - `SimilarTo`: `Because` holds the user's artists it is similar to.
  - `TopArtistMissing`
  - `TopAlbumMissing`

  Artists the library has (as artist, album artist or any credited name) never appear.
- `LastFmLinks.Artist/Album/IsLastFmPage`: `Url` is always an https last.fm page. Check
  `IsLastFmPage` again before you open it.

## Auth: no session needed

Every read uses only the account **name** plus the app's API key (`LastFmApi.ApiKey`, internal
to Core). The session key is never sent and never logged. Request URLs carry the API key, so
exception messages hold only the exception type. On the phone, pass the name the phone
scrobbler already knows, once the account is connected.

## Caching

- The desktop file is `<data root>/cache/lastfm_taste.json`. Use the phone's own
  `<dataDirectory>/cache/lastfm_taste.json`.
- The file holds one account. Another name means a fresh read.
- The file format is versioned (`LastFmTasteSnapshot.CurrentVersion`). A corrupt file or one
  of another version is ignored.
- The file is written to `.tmp` first and then swapped in.
- TTL is 12 h (`LastFmTasteService.DefaultTtl`). Inside it, `GetAsync` makes no request.
- After a failed refresh, `GetAsync` doesn't retry for 10 min (`FailureCooldown`) unless
  `forceRefresh` is set, so an offline Home visit costs nothing.

## Settings keys (AppSettings, `src/Noctis.Core/Models/AppSettings.cs`)

- `LastFmTasteEnabled` (bool, default **true**) is the Settings toggle "Mixes and
  Recommendations" under Last.fm. It is on by default because the sections only show while
  an account is connected, and connecting is the opt-in.
- `HomeLastFmExpanded` and `HomeLastFmDiscoverExpanded` (bool, default true) are the
  sections' fold state.

Show the sections only when an account is connected, its name is known and
`LastFmTasteEnabled` is on. Otherwise hide them and clear them.

## Strings

The desktop added `Home.LastFm.*` and `Settings.LastFmTaste.*` to
`src/Noctis.UI/Localization/Strings*.resx` (en, de, ru). The phone can reuse them.

## Desktop reference

- `src/Noctis/ViewModels/HomeViewModel.LastFm.cs`: wiring, refresh, and status lines per error.
- `src/Noctis/Views/HomeView.axaml`: `LastFmSection` and `LastFmDiscoverSection`.
- Tests: `tests/Noctis.Tests/LastFmTasteTests.cs`.

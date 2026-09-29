# Noctis Local API

A small HTTP + JSON API on `127.0.0.1` that lets programs on the same PC see what
Noctis is playing and control it: Stream Deck buttons, OBS "now playing" overlays,
Rainmeter skins, Discord bots, home-automation bridges, shell scripts. Nothing runs
inside Noctis; any language that can make an HTTP request works.

- Off by default. Turn it on in **Settings > Account & Devices > Local API**.
- Only reachable from this PC. It never listens on the network. (For phone control
  over Wi-Fi, use the separate **Web Remote** on the same Settings page.)
- Versioned: everything lives under `/api/v1`.

Samples: [`samples/LocalApi`](../samples/LocalApi): an OBS overlay
(`obs-now-playing.html`), a PowerShell script (`local-api.ps1`) and curl examples
(`curl-examples.sh`).

## Contents

- [Connecting](#connecting)
- [Errors](#errors)
- [Endpoints](#endpoints)
- [Events (SSE)](#events-sse)
- [Security model](#security-model)
- [Versioning policy](#versioning-policy)

## Connecting

### Discovery: `local-api.json`

While the API is on, Noctis keeps `local-api.json` in its data folder:

| OS | Folder |
|---|---|
| Windows | `%APPDATA%\Noctis` |
| Linux, macOS | `~/.config/Noctis` (or `$XDG_CONFIG_HOME/Noctis`) |

`NOCTIS_DATA_DIR` overrides the folder, as it does for the app. Debug builds use
`Noctis-dev` instead of `Noctis`.

```json
{
  "apiVersion": 1,
  "running": true,
  "port": 9421,
  "baseUrl": "http://127.0.0.1:9421/api/v1",
  "token": "3f1c…64 hex characters…9a0e",
  "pid": 12345,
  "updatedUtc": "2026-09-24T10:15:30.0000000Z"
}
```

- `port`: 9421 by default. If that port is taken, Noctis picks a free one and writes it
  here. Read the file instead of hard-coding the port. (`localApiPort` in settings.json
  changes the preferred port.)
- `running` turns `false` and `port`/`baseUrl` become `null` when the API is switched
  off or Noctis exits normally. After a crash the file can still say `running: true`,
  so treat a refused connection as "Noctis isn't running".
- The token stays the same across restarts. It only changes when you press
  **Regenerate token** in Settings.

### Authentication

Every `/api/v1` request needs the token, sent one of two ways:

```
Authorization: Bearer <token>
```

or as a query parameter, for clients that can't set headers (an OBS browser source,
an `<img>` tag, `EventSource`):

```
http://127.0.0.1:9421/api/v1/now-playing?token=<token>
```

Use `127.0.0.1` in URLs. `localhost` also works, but some clients try IPv6 `::1`
first, and the API only listens on IPv4 loopback.

### CORS

Responses carry `Access-Control-Allow-Origin: *`, and `OPTIONS` preflights are
answered without a token, so a web page (including a `file://` page in OBS) can call
the API with `fetch` or `EventSource`. The page still needs the token.

### Transport notes

- HTTP/1.1, one request per connection (`Connection: close`). No TLS: traffic never
  leaves the machine.
- Request bodies are JSON, at most 16 KB, sent with `Content-Length` (chunked bodies are
  rejected).
- Responses are `application/json; charset=utf-8` unless noted, with
  `Cache-Control: no-store`.
- Times are in milliseconds. Track ids are GUID strings.

## Errors

Every error has the same shape:

```json
{ "error": { "code": "bad_request", "message": "Body must be {\"volume\": <0-100>}." } }
```

| HTTP | `code` | When |
|---|---|---|
| 400 | `bad_request` | Invalid JSON, a missing or out-of-range field, a bad `limit`. |
| 401 | `unauthorized` | Missing or wrong token. Checked before the route, so unknown routes also answer 401 without a token. |
| 403 | `forbidden_host` | The `Host` header isn't `127.0.0.1`, `localhost` or `[::1]`. This blocks DNS-rebinding pages. |
| 404 | `not_found` | Unknown route. |
| 404 | `no_artwork` | The track has no cover. |
| 405 | `method_not_allowed` | Wrong method, for example `GET /playback/next`. The `Allow` header names the right one. |
| 409 | `nothing_playing` | Seeking with no current track. |
| 411 | `length_required` | A chunked request body. |
| 413 | `payload_too_large` | A body over 16 KB. |
| 429 | `rate_limited` | Too many failed logins (10 per minute). Wait for `Retry-After` seconds. |
| 503 | `too_many_streams` | Already 8 open event streams. |
| 500 | `internal_error` | Something failed inside Noctis. |

## Endpoints

All paths below are relative to `http://127.0.0.1:<port>/api/v1`. Reads are `GET`.
Everything that changes something is `POST`, and nothing changes on `GET`.

### Shared shapes

**Track**

```json
{
  "id": "6f0c3c1e-8a55-4f0b-9d7e-2b1f5d0c9e11",
  "title": "Midnight City",
  "artist": "M83",
  "artists": ["M83"],
  "album": "Hurry Up, We're Dreaming",
  "albumArtist": "M83",
  "durationMs": 243000,
  "trackNumber": 2,
  "discNumber": 1,
  "year": 2011,
  "genre": "Electronic",
  "isFavorite": true,
  "artworkUrl": "/api/v1/artwork/6f0c3c1e-8a55-4f0b-9d7e-2b1f5d0c9e11"
}
```

`artists` splits a combined credit ("Kanye West, GLC") into names using the app's own
artist-separator settings. `artworkUrl` is relative, with no token: add
`?token=<token>` (or send the header) when you fetch it. File paths are never
included.

**Playback** (returned by every `POST /playback/*`)

```json
{ "state": "playing", "playing": true, "positionMs": 61234, "durationMs": 243000,
  "volume": 70, "muted": false, "shuffle": false, "repeat": "off" }
```

`state` is `playing`, `paused` or `stopped`. `repeat` is `off`, `all` or `one`.

### GET /status

```sh
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:9421/api/v1/status
```

```json
{ "app": "Noctis", "appVersion": "1.5.3", "apiVersion": 1,
  "state": "playing", "playing": true, "hasTrack": true }
```

### GET /now-playing

```json
{
  "state": "playing",
  "playing": true,
  "track": { "…": "Track shape, or null when nothing is loaded" },
  "title": "Midnight City",
  "artists": ["M83"],
  "album": "Hurry Up, We're Dreaming",
  "albumArtist": "M83",
  "durationMs": 243000,
  "positionMs": 61234,
  "shuffle": false,
  "repeat": "off",
  "volume": 70,
  "muted": false,
  "trackId": "6f0c3c1e-8a55-4f0b-9d7e-2b1f5d0c9e11",
  "artworkUrl": "/api/v1/artwork/6f0c3c1e-8a55-4f0b-9d7e-2b1f5d0c9e11"
}
```

The flat fields (`title`, `artists`, …) repeat what's in `track` so simple clients
don't have to dig into it. With nothing loaded, `track`, `trackId` and `artworkUrl`
are `null` and the strings are empty.

### GET /artwork/current, GET /artwork/{trackId}

Return the cover image bytes (`image/jpeg`, `image/png`, `image/webp`, `image/gif` or
`image/bmp`), or `404 no_artwork`. `{trackId}` works for anything in the library, the
queue or the history. The server picks the file itself: the track's embedded cover,
else the album's. You can't ask it for a path.

```html
<img src="http://127.0.0.1:9421/api/v1/artwork/current?token=TOKEN">
```

Prefer the per-track URL from `artworkUrl` in overlays. It only changes when the
track does, so the image doesn't flicker on every refresh.

### POST /playback/play | pause | toggle | next | previous

No body. `play` and `pause` do nothing if already in that state. `toggle` is the
play/pause button. `previous` restarts the track if more than 3 s in, like the app.
Returns `{ "ok": true, "playback": Playback }`.

```sh
curl -X POST -H "Authorization: Bearer $TOKEN" http://127.0.0.1:9421/api/v1/playback/toggle
```

### POST /playback/seek

```json
{ "positionMs": 90000 }
```

Clamped to the track length. `409 nothing_playing` when there's no current track.

### POST /playback/volume

```json
{ "volume": 40 }
```

`0`–`100`. Anything outside that range is a `400`.

### POST /playback/shuffle

```json
{ "enabled": true }
```

Omit the body (or `enabled`) to toggle.

### POST /playback/repeat

```json
{ "mode": "all" }
```

`off`, `all` or `one`. Omit the body to cycle Off → All → One → Off like the button.

### GET /queue?limit=100

```json
{
  "current": { "…": "Track or null" },
  "upNext": [ { "…": "Track" } ],
  "upNextTotal": 812,
  "truncated": true
}
```

`limit` is 1–500, default 100. `upNextTotal` is the full length of the queue.

### POST /queue/add

```json
{ "trackIds": ["6f0c3c1e-8a55-4f0b-9d7e-2b1f5d0c9e11"], "mode": "next" }
```

`mode` is `next` (play after the current track, keeping the order you gave) or `end`
(the default). 1–500 ids. Ids not in the library are skipped and listed back:

```json
{ "ok": true, "added": 1, "notFound": [], "upNextCount": 13 }
```

Get ids from `/library/search`, `/queue` or `/now-playing`.

### GET /library/search?q=&limit=

```sh
curl -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:9421/api/v1/library/search?q=daft%20punk&limit=5"
```

```json
{
  "query": "daft punk",
  "limit": 5,
  "tracks":  [ { "…": "Track" } ],
  "albums":  [ { "id": "…", "name": "Discovery", "artist": "Daft Punk", "year": 2001,
                 "trackCount": 14, "artworkUrl": "/api/v1/artwork/…" } ],
  "artists": [ { "id": "…", "name": "Daft Punk", "albumCount": 4, "trackCount": 61 } ]
}
```

- `q`: 1–200 characters. Matching ignores case, accents and punctuation. Every word
  must appear in the title, artist or album (for albums: name or artist).
- `limit`: 1–50 per list, default 20. Exact and prefix title matches sort first.

### GET /lyrics/current

The lyrics Noctis is currently showing for the playing track (sidecar, embedded,
online or plugin, whichever it loaded).

```json
{
  "trackId": "6f0c…",
  "available": true,
  "synced": true,
  "wordLevel": true,
  "lines": [
    { "startMs": 12340, "endMs": 15100, "text": "Waiting in a car",
      "words": [ { "startMs": 12340, "endMs": 12800, "text": "Waiting " },
                 { "startMs": 12800, "endMs": 13050, "text": "in " } ] }
  ],
  "text": "Waiting in a car\n…"
}
```

- `synced: false`: plain lyrics. `lines` has `startMs: null` and `text` holds the
  whole text.
- `wordLevel: true`: at least one line has per-word timing (ELRC / TTML karaoke).
  `words` is `null` on lines without it.
- `{ "trackId": "…", "available": false }` while nothing is loaded for this track yet
  (lyrics can arrive a moment after a track starts), or there are none.

## Events (SSE)

`GET /events` keeps the connection open and pushes
[Server-Sent Events](https://html.spec.whatwg.org/multipage/server-sent-events.html).

```js
const es = new EventSource(`http://127.0.0.1:${port}/api/v1/events?token=${token}&lyrics=1`);
es.addEventListener('track-changed', e => render(JSON.parse(e.data)));
```

```sh
curl -N -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:9421/api/v1/events"
```

| Event | `data` | When |
|---|---|---|
| `track-changed` | the `/now-playing` object | The current track changes. Also sent once when you connect. |
| `state-changed` | the Playback object | Play/pause/stop, shuffle, repeat, volume or mute changes. Also sent once when you connect. |
| `position` | `{ "positionMs", "durationMs", "state" }` | At most once a second while the position moves, and right after a seek. Nothing is sent while paused, so interpolate locally between events. |
| `queue-changed` | `{ "upNextCount": 12 }` | The Up Next queue changes. Fetch `/queue` for the contents. |
| `lyrics-line` | `{ "trackId", "index", "text", "startMs", "nextStartMs", "positionMs" }` | Only with `?lyrics=1`, and only for synced lyrics: when the line under the playhead changes (also once on connect). `index` matches `lines` in `/lyrics/current`. `-1` means before the first line. |

- The stream starts with `retry: 3000`, so `EventSource` reconnects after 3 s if
  Noctis restarts.
- A comment line (`: ping`) is sent after 15 s without events. Ignore it.
- At most 8 streams can be open at once. The 9th gets `503 too_many_streams`.
- Regenerating the token closes every open stream. Clients reconnect with the new
  token. (`EventSource` gives up on a `401`, so reconnect by hand.)
- Clients that stop reading are dropped. Each stream buffers 256 events and discards
  the oldest after that.

## Security model

- **Loopback only.** The API binds to `127.0.0.1` and drops any connection from
  another address. It never listens on your LAN. The Web Remote is a separate
  listener with its own per-session key.
- **Token on every request**, compared in constant time. It's 256 random bits,
  stored only in `local-api.json` in your user profile, never in settings.json. After
  10 failed attempts in a minute, authentication locks for 30 s.
- **DNS rebinding and CSRF.** A website can't read the token, and requests whose
  `Host` isn't a loopback name are refused. Browsers that enforce Private Network
  Access also block public sites from reaching `127.0.0.1`, and the API doesn't opt
  out of that.
- **Limited surface.** Fixed routes, POST-only mutations, 16 KB bodies, bounded headers
  and read timeouts, capped lists and streams. No endpoint reads a file you name,
  returns a file path or runs anything. Artwork comes from paths Noctis resolves
  itself.
- **What the token allows.** Any program on this PC that has the token can control
  playback, edit the queue and read your library's titles and lyrics. That's the
  point, but it means you should only give it to tools you trust. Anything running as
  your user can already read the token file, so the API doesn't defend against
  malware on the same account. If the token leaks (for example in a screenshot of an
  OBS URL), press **Regenerate token**.

## Versioning policy

- The `v1` in the path is the major version. Within v1, changes are **additive**:
  new endpoints, new fields in responses, new event names, new optional request
  fields. Clients should ignore fields and events they don't know.
- Removing or renaming a field, changing its type or meaning, or making an optional
  input required needs a new major version (`/api/v2`). The old version would then be
  kept alongside it for at least two minor releases of Noctis, and the removal
  announced in the release notes.
- `apiVersion` in `/status` and `local-api.json` is the major version the running app
  serves.

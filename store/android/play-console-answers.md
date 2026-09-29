# Noctis for Android — Play Console answers

Copy these into Play Console → your app. Section names are the Console's as of
September 2026; if a label moved, the question text is what to match. Facts behind each
answer were verified against the Android build (see docs/superpowers/plans/phase4-parts/
part-a-release.md, Task A5); the Noctis-account answers against the account design
(docs/superpowers/specs/2026-09-28-android-account-sync-design.md). Re-check "Data safety"
if a later version adds any online lookup.

## Create app

| Field | Answer |
|---|---|
| App name | Noctis |
| Default language | English (United States) – en-US |
| App or game | App |
| Free or paid | Free (this cannot be changed to paid later) |
| Declarations | Tick Developer Program Policies and US export laws |

## Store settings

| Field | Answer |
|---|---|
| App category | Music & Audio |
| Tags | Music player, Lyrics (pick the closest the Console offers) |
| Contact email | the address you want shown publicly on the listing |
| Website | https://noctisapp.cc |

## Main store listing

| Field | Source |
|---|---|
| App name | `listing/en-US/title.txt` |
| Short description | `listing/en-US/short-description.txt` |
| Full description | `listing/en-US/full-description.txt` |
| App icon | `icon-512.png` |
| Feature graphic | `feature-graphic.png` |
| Phone screenshots | `screenshots/*.png` (2–8; upload in file-name order) |

## App content

**Privacy policy:** `https://noctisapp.cc/privacy/android`

**Ads:** No, my app does not contain ads.

**App access:** All functionality is available without any special access. The one sign-in
(Settings → Account) is optional and links the phone to the user's own Noctis desktop app on
their computer; there is no account on any developer server to give the reviewer. If the
form offers an instructions box, paste: "Noctis plays audio files already on the device. Tap
Add folder and pick any folder that contains music files. Settings → Account optionally
connects to the Noctis desktop app the user runs on their own computer (local network); the
rest of the app works without it."

**Content rating** (IARC questionnaire). Email: your contact email. Category: the one for
utility / productivity / communication / other apps (Noctis is not a game, not social,
not a store or streaming service). Answer **No** to every content question:

- Violence, blood, gore: No
- Sexuality, nudity: No
- Profanity or crude humour: No (the app shows no content of its own; lyrics come from
  the user's own files)
- Controlled substances (drugs, alcohol, tobacco): No
- Gambling or simulated gambling: No
- User interaction: users cannot communicate with each other, share content, or share
  their location: No to all
- Digital purchases: No
- Unrestricted internet / web browser: No

Expected result: rated for everyone (e.g. ESRB Everyone, PEGI 3).

**Target audience and content:** Target age group **18 and over** only. Appeals to
children: No. (Selecting any under-13 group puts the app under the Families policy.)

**News app:** No.

**Data safety:**

- Does your app collect or share any of the required user data types? **No.**
  - Why this is true: nothing reaches the developer or any third party. The app has no
    analytics, crash reporting or ads SDK and no developer server, and keeps the library
    index, settings, favourites, playlists, play history and queue in app-private storage.
    Its only network connection is optional and user-initiated: after the user signs in
    under Settings → Account, it talks to the Noctis desktop app on the user's own computer
    at the address the user typed, over TLS pinned to a certificate fingerprint the user
    confirmed. It sends the account name and password once to get a per-device key (the
    password is never stored), then the device key, a random install id, the phone's model
    name, library requests and the user's own favourites, ratings, playlist edits and plays;
    it streams and downloads the user's own music back. The developer never receives any of
    it. No log file is written automatically; Settings → Export logs saves one (with keys and
    passwords masked) only where the user chooses. Android's own device backup (allowBackup)
    is the user's backup to their Google account, not data the developer receives; the
    account link and downloads are kept in no-backup storage.
  - Owner's call: Google defines collection as data leaving the device and has no explicit
    exemption for a server the user runs themselves; "No" rests on the developer receiving
    nothing, as self-hosted clients commonly answer. If a reviewer disagrees, declare instead:
    collected, not shared, processed not ephemerally, required only for the optional account
    feature — Personal info → User IDs (the account name, device id), App activity → App
    interactions and Other user-generated content (plays, favourites, ratings, playlists);
    encrypted in transit: Yes; users can request deletion: Yes (Sign out, or remove the
    device in the desktop's Settings → Account & Devices).
- With "No", the Console skips the data-type questions. If it asks about encryption in
  transit or deletion requests, those apply only to collected data: none.

**Government apps:** No. **Financial features:** My app doesn't provide any financial
features. **Health:** My app does not have any health features.

**Advertising ID:** No, my app does not use advertising ID. (The manifest has no
`com.google.android.gms.permission.AD_ID`.)

**Foreground service permissions:**

- Type: **Media playback** (`FOREGROUND_SERVICE_MEDIA_PLAYBACK`).
- Use case: Media playback.
- Description: "Noctis is a music player. Its androidx.media3 MediaSessionService runs as
  a mediaPlayback foreground service while the user is playing music they started, so
  playback continues when they leave the app or turn the screen off, with the standard
  media notification and lock-screen controls. After the user pauses, Media3 keeps it in
  the foreground for up to 10 minutes so playback can be resumed from the notification,
  then the service stops."
- Impact if deferred or interrupted: "The music the user is listening to would stop as
  soon as they switched apps or turned the screen off, which breaks the app's core
  function."
- Video link: upload `artifacts/android/fgs-demo.mp4` to YouTube as **Unlisted** (or Google
  Drive, "anyone with the link") and paste the link.

**Photo and video permissions / Full-screen intent / Exact alarms:** not used; the Console
only shows these forms when the manifest declares them.

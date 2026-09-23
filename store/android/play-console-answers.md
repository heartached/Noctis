# Noctis for Android — Play Console answers

Copy these into Play Console → your app. Section names are the Console's as of
September 2026; if a label moved, the question text is what to match. Facts behind each
answer were verified against the Android build (see docs/superpowers/plans/phase4-parts/
part-a-release.md, Task A5). Re-check "Data safety" if a later version adds any online
lookup.

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

**App access:** All functionality is available without any special access. (There is no
login. If the form offers an instructions box, paste: "Noctis plays audio files already on
the device. Tap Add folder and pick any folder that contains music files.")

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
  - Why this is true: the Android app makes no network requests (no HTTP client, socket
    or web view in any assembly it ships), has no analytics, crash reporting or ads SDK,
    and keeps the library index, settings, favourites, playlists, play history and queue in
    app-private storage. No log file is written automatically; Settings → Export logs saves
    one only where the user chooses.
    Android's own device backup (allowBackup) is the user's backup to their Google
    account, not data the developer receives.
- The Console then skips the data-type questions. If it asks about encryption in transit
  or deletion requests, those apply only to collected data: none.

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

# Noctis for Android — Privacy policy

Last updated 23 September 2026. Published at https://noctisapp.cc/privacy/android
(source: `website/src/pages/privacy/android.astro` on `main`; keep the two identical).

Noctis for Android does not collect, transmit, sell or share any personal data or usage
data. There is no account, no advertising, no analytics and no crash reporting.

## What stays on your device

- **The folders you choose.** Noctis reads only the folders you pick with Android's folder
  picker. It has no general storage, media or audio permission. Uninstalling the app, or
  clearing its storage in Android's settings, removes that access.
- **Your library index.** Titles, artists, albums, artwork and lyrics read from the tags
  and lyric files in those folders are stored in the app's private storage so the library
  opens quickly.
- **Your activity in the app.** Favourites, playlists, play history, the play queue, a
  record of library changes and your settings are stored in the app's private storage.
- **Diagnostics.** Noctis writes diagnostic messages to Android's system log on your
  device. They are never sent anywhere. If you use Settings → Export logs, the log is
  saved only to the file you choose; Noctis never sends it anywhere.

## Network

This version of Noctis for Android makes no network requests. It does not look anything
up online, and it contains no third-party SDKs that do.

## Permissions

- **Notifications**, to show playback controls in the notification shade and on the lock
  screen.
- **Foreground service (media playback) and wake lock**, so music keeps playing with the
  screen off.

Noctis does not request location, contacts, camera, microphone, phone, storage or media
permissions. The package also declares the network-state permission (added by the Android
playback library it uses); this version of Noctis does not use the network.

## Backups

If you have Android backup turned on, Android may include the app's local data (your
library index and artwork, favourites, playlists, play history, play queue and settings) in
your device backup to your Google account. Google handles that
backup under your account; the developer cannot see it. Turn off backup in Android's
settings to exclude it.

## Deleting your data

Everything Noctis stores is on your device. Uninstalling the app, or clearing its storage
in Android's settings, deletes all of it. Your music files are never changed or deleted
by uninstalling.

## Children

Noctis is not directed at children under 13.

## Changes and contact

If this policy changes, this page is updated and its date changes. Questions: open an
issue at https://github.com/heartached/Noctis/issues.

# Noctis for Android — Privacy policy

Last updated 28 September 2026. Published at https://noctisapp.cc/privacy/android
(source: `website/src/pages/privacy/android.astro` on `main`; keep the two identical).

Noctis for Android does not collect, sell or share any personal data or usage data. The
developer receives nothing from the app: there is no advertising, no analytics, no crash
reporting and no developer server. The only network connection the app ever makes is the
optional one described under Network: to your own computer, running the Noctis desktop app,
after you sign in to it.

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
  device, with account keys and passwords masked. They are never sent anywhere. If you use
  Settings → Export logs, the log is saved only to the file you choose; Noctis never sends
  it anywhere.
- **Your Noctis account link (only if you sign in).** The address of your computer, your
  account name, the device key your computer issued, its certificate fingerprint and a random
  id for this install are stored in app-private storage that Android never backs up. Your
  password is never stored. Songs you download are kept in that same never-backed-up storage.

## Network

Noctis does not look anything up online and contains no third-party SDKs that do. It
connects to one place only, and only if you choose to: **your own computer**, running the
Noctis desktop app, when you sign in under Settings → Account.

- **Signing in.** You enter your computer's address. Noctis first shows the fingerprint of
  your computer's security certificate so you can check it matches the one your computer
  shows, and only then sends your account name and password — once, over an encrypted (TLS)
  connection — to receive a key for this device. The key, not your password, is used from
  then on, and every later connection must present that same certificate.
- **What goes to your computer.** The device key, a random id and your phone's model name
  (so your computer can list and remove this device), requests for your songs, covers and
  library, and the changes you make on the phone: favourites, ratings, playlists and plays.
- **What comes back.** Your library (song and album details), covers, playlists, favourites,
  ratings and play counts from your computer, and the music itself, streamed or downloaded.

All of this travels only between your phone and your computer, encrypted. None of it goes to
the developer or any third party. Nothing is sent without an address you entered and a
certificate you confirmed, and signing out stops it.

## Permissions

- **Notifications**, to show playback controls in the notification shade and on the lock
  screen.
- **Foreground service (media playback) and wake lock**, so music keeps playing with the
  screen off.
- **Internet and network state**, used only for the optional connection to your own computer
  described under Network.

Noctis does not request location, contacts, camera, microphone, phone, storage or media
permissions.

## Backups

If you have Android backup turned on, Android may include the app's local data (your
library index and artwork, including the song list and covers from your computer if you
signed in, favourites, playlists, play history, play queue and settings) in your device
backup to your Google account. Google handles that backup under your account; the developer
cannot see it. Your account link and downloaded songs are never included. Turn off backup in
Android's settings to exclude the rest.

## Deleting your data

Everything Noctis stores is on your device. Uninstalling the app, or clearing its storage
in Android's settings, deletes all of it. Your music files are never changed or deleted
by uninstalling.

Signing out (Settings → Account) removes your computer's songs from the phone's library and
asks your computer to revoke this device's key, and can also delete the downloaded songs. On your computer,
Settings → Account & Devices lists this phone and can remove it. What your phone sent to
your computer (plays, favourites, playlist changes) lives in your computer's own Noctis
library, under your control.

## Children

Noctis is not directed at children under 13.

## Changes and contact

If this policy changes, this page is updated and its date changes. Questions: open an
issue at https://github.com/heartached/Noctis/issues.

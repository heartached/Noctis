# Noctis for Android — Privacy policy

Last updated 4 November 2026. Published at https://noctisapp.cc/privacy/android
(source: `website/src/pages/privacy/android.astro` on `main`; keep the two identical).

Noctis for Android does not collect, sell or share any personal data or usage data. The
developer receives nothing from the app: there is no advertising, no analytics, no crash
reporting and no developer server. The app's network connections are all described under
Network: it asks Last.fm for album descriptions, sending only the album's artist and title; it
asks Deezer for artist photos, sending only the artist's name; it asks LRCLIB for lyrics to
songs that have none of their own, sending only the song's title, artist and length; it
may ask MusicBrainz, fanart.tv and TheAudioDB for artists' logos, sending only the artist's name
and sometimes a few song titles; only if you connect Last.fm or ListenBrainz, it sends them the
songs you play; and, only if you sign in, it connects to your own computer running the Noctis
desktop app.

## What stays on your device

- **The folders you choose, and your phone's music if you allow it.** Noctis reads the
  folders you pick with Android's folder picker. Only if you turn on Find All Music
  Automatically, it asks for Android's music and audio permission (on Android 12 and older,
  permission to read storage) and reads the songs Android lists on your device, to add them to
  your library on the device; nothing from them leaves the device. It has no other storage or
  media permission. Turning the option off stops it; you can also take the permission back in
  Android's settings. Uninstalling the app, or clearing its storage in Android's settings,
  removes that access.
- **Your library index.** Titles, artists, albums, artwork and lyrics read from the tags
  and lyric files in those folders are stored in the app's private storage so the library
  opens quickly.
- **Your activity in the app.** Favourites, playlists, play history, the play queue, a
  record of library changes and your settings are stored in the app's private storage.
- **Album descriptions.** Descriptions fetched from Last.fm (see Network) are kept in the
  app's private storage so each album is looked up only once.
- **Artist photos.** Photos fetched from Deezer (see Network) are kept in the app's private
  storage so each artist is looked up only once, with a note of the artists Deezer had no
  photo for, so those are not asked about again for a few days.
- **Lyrics found online.** Lyrics fetched from LRCLIB (see Network) are kept in the app's
  private storage so each song is looked up only once.
- **Artist logos.** Logos fetched for artist pages (see Network) are kept in the app's private
  storage so each artist is looked up only once, with a note of the artists that had no logo,
  so those are not asked about again for a while.
- **Your scrobbling connections (only if you connect them).** Your Last.fm user name and the
  session key Last.fm issued, or your ListenBrainz user name and the token you pasted, are
  stored in the app's private storage, with the plays still waiting to be sent. Your Last.fm
  password is never entered in Noctis or stored.
- **Diagnostics.** Noctis writes diagnostic messages to Android's system log on your
  device, with account keys and passwords masked. They are never sent anywhere. If you use
  Settings → Export logs, the log is saved only to the file you choose; Noctis never sends
  it anywhere.
- **Your Noctis account link (only if you sign in).** The address of your computer, your
  account name, the device key your computer issued, its certificate fingerprint and a random
  id for this install are stored in app-private storage that Android never backs up. Your
  password is never stored. Songs you download are kept in that same never-backed-up storage.

## Network

Noctis contains no third-party SDKs, and the developer runs no server. It connects only to
the places below.

**Last.fm, for album descriptions.** When you open an album page, Noctis asks Last.fm's public
web service (ws.audioscrobbler.com, run by Last.fm Limited) for a description of that album,
over an encrypted (HTTPS) connection. The request contains only the album's artist and title
(if Last.fm has nothing for the full artist credit, Noctis asks once more with the first
artist named) and Noctis's own application key. It contains no account, no device id and
nothing else about you, although, as with any website, Last.fm can see your device's IP
address. The answers are stored on your phone so each album is looked up only once. Last.fm's
privacy policy covers its service: https://www.last.fm/legal/privacy.

**Deezer, for artist photos.** When you open an artist page, or artists come into view in the
Artists list, in search results or among your pinned items, Noctis asks Deezer's public web
service (api.deezer.com, run by Deezer SA) for a photo of each artist it does not have yet,
over an encrypted (HTTPS) connection, and downloads the photo from Deezer's image server
(cdn-images.dzcdn.net). The request contains only the artist's name (if Deezer has nothing for
the full artist credit, Noctis asks once more with the first artist named): no account, no
device id and nothing else about you, although, as with any website, Deezer can see your
device's IP address. When several Deezer artists share that name, Noctis also asks Deezer for
those artists' top songs and compares them with your library on your phone to pick the right
one; your library is never sent. The photos are stored on your phone so each artist is looked
up only once. Deezer's privacy policy covers its service: https://www.deezer.com/legal/personal-datas.

**LRCLIB, for lyrics.** When you open the lyrics of a song that has no lyrics file and no lyrics
in its tags, Noctis asks LRCLIB's public web service (lrclib.net) for that song's lyrics, over an
encrypted (HTTPS) connection. The request contains only the song's title, artist and length:
no account, no device id and nothing else about you, although, as with any website,
LRCLIB can see your device's IP address. The lyrics found are stored on your phone so each song
is looked up only once. Turn off Settings → Lyrics → Find Lyrics Online to stop these lookups.
LRCLIB is a free, community-run service: https://lrclib.net.

**MusicBrainz, fanart.tv and TheAudioDB, for artist logos.** When you open an artist page,
Noctis may look for that artist's logo, over encrypted (HTTPS) connections. It asks MusicBrainz
(musicbrainz.org, run by the MetaBrainz Foundation) which artist the name belongs to, sending
only the artist's name; when several artists share that name, it also sends up to four titles
of that artist's songs in your library so MusicBrainz can tell them apart. It then asks
fanart.tv (webservice.fanart.tv) and TheAudioDB (www.theaudiodb.com) for that artist's logo,
sending only MusicBrainz's id for the artist, or, if MusicBrainz cannot be reached, the
artist's name to TheAudioDB. No account, no device id and nothing else about you is sent,
although, as with any website, these services can see your device's IP address. MusicBrainz's
privacy policy covers its service: https://metabrainz.org/privacy; fanart.tv and TheAudioDB
describe theirs on their own sites: https://fanart.tv and https://www.theaudiodb.com.

**Last.fm and ListenBrainz, only if you connect them.** Under Settings → Scrobbling you can
connect Last.fm (you approve Noctis on Last.fm's own website, which gives Noctis a session key)
or ListenBrainz (you paste your user token). From then on, for each song you listen to past half
its length or four minutes, Noctis sends its title, artist, album, length and the time you played
it to the service you connected (ws.audioscrobbler.com for Last.fm, api.listenbrainz.org for
ListenBrainz), over an encrypted (HTTPS) connection, and, unless you turn it off, the song you
are playing now. These plays are stored in your account on that service, as you asked. Disconnect under Settings → Scrobbling to stop. Their privacy
policies cover their services: https://www.last.fm/legal/privacy and https://metabrainz.org/privacy.

**Your own computer, only if you choose to.** When you sign in under Settings → Account,
Noctis connects to your own computer, running the Noctis desktop app.

- **Signing in.** You enter your computer's address. Noctis first shows the fingerprint of
  your computer's security certificate so you can check it matches the one your computer
  shows, and only then sends your account name and password — once, over an encrypted (TLS)
  connection — to receive a key for this device. The key, not your password, is used from
  then on, and every later connection must present that same certificate.
- **Signing in with a QR code.** Instead of typing, you can tap Scan QR Code and scan the
  code your computer shows. The code holds your computer's address, its certificate
  fingerprint and a one-time sign-in code that expires after two minutes and works only once;
  it never contains your password. Noctis checks the certificate against the fingerprint
  before sending the one-time code. The scanner is Google's code scanner, part of Google Play
  services: it runs on your phone and gives Noctis only the text of the code, so Noctis never
  gets camera access. Google may collect usage and diagnostic information about the scanner
  under the Google Privacy Policy.
- **What goes to your computer.** The device key, a random id and your phone's model name
  (so your computer can list and remove this device), requests for your songs, covers and
  library, and the changes you make on the phone: favourites, ratings, playlists and plays.
- **What comes back.** Your library (song and album details), covers, playlists, favourites,
  ratings and play counts from your computer, and the music itself, streamed or downloaded.

All of the account traffic travels only between your phone and your computer, encrypted.
None of it goes to the developer or to any third party (apart from the QR scanner's own usage
information to Google described above, which contains none of your account traffic). Nothing
is sent without an address you entered and a certificate you confirmed (or a code you scanned
from your own computer), and signing out stops it.

## Permissions

- **Notifications**, to show playback controls in the notification shade and on the lock
  screen.
- **Foreground service (media playback) and wake lock**, so music keeps playing with the
  screen off.
- **Internet and network state**, used only for the connections described under Network.
- **Music and audio** (on Android 12 and older, read storage), only if you turn on Find All
  Music Automatically, to add the songs on your device to your library.

Noctis does not request location, contacts, camera, microphone, phone, photo or video
permissions.

## Backups

If you have Android backup turned on, Android may include the app's local data (your
library index and artwork, including the song list and covers from your computer if you
signed in, album descriptions, artist photos, artist logos, lyrics found online, favourites,
playlists, play history, play queue, your scrobbling connections and settings) in your device backup to your Google account. Google handles that backup under
your account; the developer cannot see it. Your account link and downloaded songs are never
included. Turn off backup in Android's settings to exclude the rest.

## Deleting your data

Everything Noctis stores is on your device. Uninstalling the app, or clearing its storage
in Android's settings, deletes all of it. Noctis keeps nothing on the services it asks for
descriptions, photos, lyrics or logos; how they handle their own server logs is covered by their
privacy policies. Plays sent to Last.fm or ListenBrainz stay in your account there until you
delete them on that service. Your music files are never
changed or deleted by uninstalling.

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

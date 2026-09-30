# Noctis for Android — first upload, owner checklist

Do these in order. Commands run in PowerShell from
`C:\Users\okfer\Downloads\Noctis\Noctis-phase3` (branch `android-phase4`, or wherever
Phase 4 was merged). Anything in `store/android/` is ready to copy.

Prerequisite: your Play Console developer account is created, paid and identity-verified
(design spec §8 steps 1–4). If verification is still pending, do steps 1–3 below anyway.

## 1. Create the upload key (once, ever)

```powershell
pwsh -File scripts/android/create-upload-key.ps1
```

- keytool asks for a new password twice (nothing is shown as you type), then once more
  to print the certificate. Use a long random password; put it in your password manager
  first.
- It writes `%USERPROFILE%\.noctis\noctis-upload.jks`. **Back that file up now** to two
  places that are not this PC and not the repo (password manager attachment, encrypted
  USB). Write down the SHA-256 fingerprint it prints.
- Never commit it, never paste the password anywhere. `.gitignore` blocks `*.jks`.

## 2. Build the signed bundle

**Smoke-test first.** The upload is a trimmed, fully AOT-compiled Release build, which can
fail in ways a Debug build never shows. Build the same thing with the debug key and try it
on the emulator (`emulator-5554` running) or a phone plugged in with USB debugging
(add `-DeviceSerial <serial from adb devices>`):

```powershell
pwsh -File scripts/android/build-release.ps1 -UseDebugKey
```

It ends by printing an `Install:` line; run that line to install the `.apks` it built. A
phone that has the Play version installed refuses it (different signature): uninstall that
first, or use the emulator. Then walk the main screens: Home, Library (a list page, an
album, an artist), Search, Now Playing (seek, volume, lyrics, queue) and Settings. Only if
nothing crashes or looks broken, build with the real key:

```powershell
pwsh -File scripts/android/build-release.ps1
```

Enter the keystore password when asked. It takes about 2 minutes and ends with:

```
AAB:          ...\artifacts\android\noctis-1.0.0-1.aab (57 MB)
versionCode:  1    versionName: 1.0.0    targetSdk: 36
Signer:       Owner: CN=Noctis Upload Key, O=heartached
SHA-256:      <your upload key fingerprint, same as step 1>
```

If it stops with an error, nothing was produced; read the message (wrong password,
missing keystore, signature mismatch) and rerun.

**Upload only** `artifacts\android\noctis-1.0.0-1.aab` (the exact
`noctis-<versionName>-<versionCode>.aab` file the script prints above). Never upload:

- `src\Noctis.Android\bin\Release\net10.0-android\com.heartached.noctis-Signed.aab` — that
  is the same build's raw output before it's copied to `artifacts\android\`, and it's what
  gets left there debug-signed if anyone ran the script with `-UseDebugKey` overnight.
- any file whose name contains `DEBUGKEY-NOT-FOR-UPLOAD` — those are overnight test builds
  and Play rejects them.

Before you trust either file, check the `Signer:` / `SHA-256:` lines the script just
printed: `Signer` must read `CN=Noctis Upload Key, O=heartached` and `SHA-256` must match
the fingerprint from step 1. If `Signer` says `CN=Android Debug`, that bundle is
debug-signed — stop, do not upload it, rerun the script without `-UseDebugKey`.

## 3. Publish the privacy policy page

The page is committed on branch `website-android-privacy` in
`C:\Users\okfer\Downloads\Noctis\Noctis-website`. Publishing = pushing it to `main`, which
runs the "Deploy website" workflow. That worktree's `website-android-privacy` branch
tracks `origin/main` (not a same-named remote branch), so a bare `git push` refuses to run
— push with the explicit `<local>:main` refspec:

```powershell
git -C C:\Users\okfer\Downloads\Noctis\Noctis-website pull --rebase
git -C C:\Users\okfer\Downloads\Noctis\Noctis-website push origin website-android-privacy:main
```

(Prefer a PR? `gh pr create --repo heartached/Noctis --head website-android-privacy --base main`,
then merge it.) Wait for the workflow (`gh run list --workflow deploy-website.yml -L 1`),
then check:

```powershell
curl.exe -sI https://noctisapp.cc/privacy/android | Select-Object -First 1
```

Expected `HTTP/1.1 200 OK`. Open it in a browser once.

## 4. Upload the foreground-service demo video

Upload `artifacts\android\fgs-demo.mp4` (about 29 seconds) to YouTube as **Unlisted**
(title "Noctis for Android – background playback"). Copy the link for step 6.

## 5. Create the app

Play Console → **Create app**, using `store/android/play-console-answers.md` → "Create
app": name Noctis, English (United States), App, Free, tick both declarations.

## 6. Fill in App content and the store listing

Dashboard → "Set up your app". Work through every item with
`store/android/play-console-answers.md`:

1. Privacy policy → `https://noctisapp.cc/privacy/android`
2. App access → all functionality available without special access
3. Ads → No
4. Content rating → questionnaire, all No
5. Target audience → 18 and over
6. News app → No
7. Data safety → No data collected or shared
8. Government apps, Financial features, Health → No / none
9. Advertising ID → No
10. Foreground service permissions → Media playback, the two texts, the video link from step 4
11. Store settings → Music & Audio, contact email, website
12. Main store listing → the three text files, `icon-512.png`, `feature-graphic.png`,
    the PNGs in `store/android/screenshots/` (in file-name order). That folder doesn't
    exist yet as of this writing — a later task tonight builds the screenshots and creates
    it; check it's there before you get to this item.

## 7. Internal testing release

1. **Test and release → Testing → Internal testing → Testers**: create an email list
   ("Noctis internal"), add your own Google account, save.
2. **Releases → Create new release**. When asked about app signing, keep **"Use
   Google-generated key"** (Play App Signing). Google keeps the app signing key; your
   upload key from step 1 only proves uploads are yours.
3. Upload `artifacts\android\noctis-1.0.0-1.aab`. Release name defaults to `1 (1.0.0)`.
   Release notes: "First internal build."
4. **Next → Save and publish** (or "Start rollout to Internal testing").
5. **Setup → App signing**: the **Upload key certificate** SHA-256 must equal the
   fingerprint from steps 1–2. If it does not, stop: the wrong file was uploaded.
6. Internal testing → Testers → **Copy link**, open it on your phone signed in with the
   tester account, accept, install from Play. A local build installed earlier on that
   phone has a different signature: uninstall it first.

## 8. Closed testing (required before production)

New personal accounts must run a closed test with **at least 12 testers opted in for 14
continuous days** before applying for production.

1. **Testing → Closed testing → Create track** (or use "Closed testing – Alpha").
2. Countries/regions: add the ones your testers are in (or all).
3. Testers: a **Google Group** is easiest (create one at groups.google.com, add the
   Discord volunteers' Gmail addresses, paste the group email). Aim for 15+ so a few
   drop-outs do not reset you below 12.
4. Create a release on the track: **Promote release** from Internal testing (same
   versionCode 1), or upload a new build. Send for review; the first review can take a
   few days.
5. Post the opt-in link in Discord with the rule: stay opted in for 14 days; opting out
   and back in restarts your own count.
6. After 14 days with 12+ opted in: Dashboard → **Apply for production** (three short
   forms). Review takes up to 7 days.

## Every later upload

Smoke-test first, as in step 2 (`build-release.ps1 -UseDebugKey`, run its `Install:`
line, walk the main screens). Then bump the version code (it must always go up) and
usually the name:

```powershell
pwsh -File scripts/android/build-release.ps1 -VersionCode 2 -VersionName 1.0.1
```

Upload `artifacts\android\noctis-1.0.1-2.aab` to the track you want.

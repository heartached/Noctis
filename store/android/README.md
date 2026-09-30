# Google Play store assets and answers (Noctis for Android)

Everything the Play Console asks for, reviewed and versioned with the app.

| Path | What | Made by |
|---|---|---|
| `listing/en-US/title.txt` | App name (≤30) | hand |
| `listing/en-US/short-description.txt` | Short description (≤80) | hand |
| `listing/en-US/full-description.txt` | Full description (≤4000) | hand |
| `icon-512.png` | Hi-res icon, 512x512 32-bit PNG | `python scripts/android/make-icons.py` |
| `feature-graphic.png` | 1024x500 24-bit PNG | `python scripts/android/make-icons.py` |
| `screenshots/*.png` | Phone screenshots, 1080x1920 24-bit PNG | `python scripts/android/store-screenshot.py` (plan Task A7) |
| `play-console-answers.md` | Every App content form, answered | hand |
| `privacy-policy.md` | Source text of https://noctisapp.cc/privacy/android | hand, mirrored on `main` in `website/src/pages/privacy/android.astro` |
| `OWNER-CHECKLIST.md` | The owner's steps, in order, for the first upload | hand |

Not here on purpose: the upload keystore (lives in `%USERPROFILE%\.noctis\`, never in the
repo), built bundles (`artifacts/android/`, gitignored) and the foreground-service demo
video (`artifacts/android/fgs-demo.mp4`, uploaded to YouTube as unlisted).

When the app gains a feature, update `full-description.txt`; when it gains a network
request, update `privacy-policy.md`, the website page and the Data safety answers together.

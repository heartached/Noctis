/**
 * Single source of truth for every value that changes outside a code edit.
 * Nothing else in the site should hardcode a URL, handle, or repo path.
 */

export const REPO_OWNER = 'heartached';
export const REPO_NAME = 'Noctis';
export const REPO = `${REPO_OWNER}/${REPO_NAME}` as const;

export const site = {
  name: 'Noctis',
  /**
   * Verbatim from the app README, kept for reference only — it is not rendered.
   * Prefer the verified privacy wording on the page itself: "No telemetry. No
   * analytics. No crash reporting. No ads." The app does contact GitHub for the
   * update check and Deezer for artist images, so a broader claim than that
   * would not survive scrutiny. See src/data/verified-claims.md.
   */
  tagline: "A music player that respects what's yours. Zero tracking, total control.",
  url: 'https://noctisapp.cc',
  locale: 'en',
  license: 'MIT',
} as const;

export const links = {
  repo: `https://github.com/${REPO}`,
  releases: `https://github.com/${REPO}/releases`,
  releasesLatest: `https://github.com/${REPO}/releases/latest`,
  issues: `https://github.com/${REPO}/issues`,
  license: `https://github.com/${REPO}/blob/main/LICENSE`,
  readme: `https://github.com/${REPO}#readme`,
  /** Live invite. The canonical discord.com/invite form, not the discord.gg
      shortener — same destination, but it survives link scanners that block
      the short domain. */
  discord: 'https://discord.com/invite/BNCDZQUVx7',
  support: 'https://buymeacoffee.com/heartached',
  scoopBucket: `https://github.com/${REPO_OWNER}/scoop-bucket`,
} as const;

export const platforms = {
  windows: { id: 'windows', label: 'Windows', spec: 'Windows 10/11 · x64' },
  macos: { id: 'macos', label: 'macOS', spec: 'macOS 12+ · Intel & Apple Silicon' },
  /* Only x86_64 gets an AppImage. arm64 ships a tar.gz — the release workflow
     produces no arm64 AppImage, so "x64 & ARM64 · AppImage" would be false. */
  linux: { id: 'linux', label: 'Linux', spec: 'x86_64 AppImage · ARM64 tar.gz' },
} as const;

export type PlatformId = keyof typeof platforms;

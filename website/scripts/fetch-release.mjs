/**
 * Resolves release data from the GitHub API at build time and writes
 * public/api/downloads.json.
 *
 * This file is the single source for the download total, the version number
 * and every asset URL on the site — nothing is hardcoded and nothing is
 * fetched from api.github.com in the browser (60 unauthenticated requests per
 * hour per IP would 403 under any traffic spike, and it would leak visitor IPs
 * to GitHub).
 *
 * A scheduled GitHub Action re-runs this hourly and commits the result, so the
 * number stays fresh on a fully static host. See .github/workflows/downloads.yml.
 */
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const OUT = resolve(here, '../public/api/downloads.json');

const OWNER = 'heartached';
const REPO = 'Noctis';
const API = `https://api.github.com/repos/${OWNER}/${REPO}`;
const RELEASES_LATEST = `https://github.com/${OWNER}/${REPO}/releases/latest`;

const headers = {
  'User-Agent': 'noctis-site-build',
  Accept: 'application/vnd.github+json',
  ...(process.env.GITHUB_TOKEN ? { Authorization: `Bearer ${process.env.GITHUB_TOKEN}` } : {}),
};

async function api(path) {
  const res = await fetch(`${API}${path}`, { headers });
  if (!res.ok) throw new Error(`GET ${path} -> ${res.status} ${res.statusText}`);
  return res.json();
}

/**
 * Asset selectors. First matching asset wins, so order is priority.
 * Kept as regexes because release asset names carry the version string.
 */
const SELECTORS = {
  windowsInstaller: [/Setup\.exe$/i],
  windowsPortable: [/windows-x64\.zip$/i],
  macosArm: [/osx-arm64\.dmg$/i, /osx-arm64\.zip$/i],
  macosIntel: [/osx-x64\.dmg$/i, /osx-x64\.zip$/i],
  linuxAppImage: [/\.AppImage$/i],
  linuxArm: [/linux-arm64\.tar\.gz$/i],
  linuxTar: [/linux-x64\.tar\.gz$/i],
  checksums: [/^SHA256SUMS$/i],
};

/**
 * Release URLs become live hrefs on the site verbatim — the download buttons.
 * Accept only this repo's own https URLs; anything else fails the run, which
 * falls back to the last good file instead of shipping a link elsewhere.
 */
function assertRepoUrl(raw) {
  const u = new URL(raw);
  const ok =
    u.protocol === 'https:' &&
    (u.hostname === 'objects.githubusercontent.com' ||
      (u.hostname === 'github.com' && u.pathname.startsWith(`/${OWNER}/${REPO}/`)));
  if (!ok) throw new Error(`unexpected release URL: ${raw}`);
  return u.href;
}

function pick(assets, patterns) {
  for (const re of patterns) {
    const hit = assets.find((a) => re.test(a.name));
    if (hit) {
      return {
        name: hit.name,
        url: assertRepoUrl(hit.browser_download_url),
        size: hit.size,
        downloads: hit.download_count,
      };
    }
  }
  return null;
}

const bytes = (n) => (n == null ? null : `${(n / 1024 / 1024).toFixed(0)} MB`);

let payload;

try {
  // Every release, paginated — the total must span all of them, not just latest.
  const all = [];
  for (let page = 1; page <= 10; page++) {
    const batch = await api(`/releases?per_page=100&page=${page}`);
    all.push(...batch);
    if (batch.length < 100) break;
  }

  const published = all.filter((r) => !r.draft);

  /**
   * LATEST MEANS NEWEST STABLE. Never a pre-release, and never a draft.
   *
   * Sorted by published_at rather than trusting list order. /releases comes
   * back newest-first by created_at, which is the date the release was CREATED
   * — a release drafted early and published late sorts wrong under it, and a
   * back-dated tag sorts wrong the other way. published_at is the date that
   * decides which version people should actually be offered.
   *
   * The old `?? published[0]` fallback is gone: it silently handed the site a
   * pre-release if every release happened to be one. Better to fail the run and
   * keep the previous good file than to advertise a beta as the current build.
   */
  const stable = published
    .filter((r) => !r.prerelease && r.published_at)
    .sort((a, b) => new Date(b.published_at) - new Date(a.published_at));

  const latest = stable[0];
  if (!latest) throw new Error('no published stable release found (all are drafts or pre-releases)');

  const sumAssets = (r) => r.assets.reduce((n, a) => n + a.download_count, 0);
  const total = published.reduce((n, r) => n + sumAssets(r), 0);

  const byRelease = Object.fromEntries(published.map((r) => [r.tag_name, sumAssets(r)]));

  const assets = Object.fromEntries(
    Object.entries(SELECTORS).map(([key, pats]) => [key, pick(latest.assets, pats)])
  );

  const repo = await api('');

  payload = {
    total,
    byRelease,
    // Tag names flow into HTML and JSON-LD — keep them to plain version tokens.
    latestVersion: latest.tag_name.replace(/^v/, '').replace(/[^\w.\-+]/g, ''),
    latestTag: latest.tag_name,
    latestUrl: assertRepoUrl(latest.html_url),
    publishedAt: latest.published_at,
    releaseCount: published.length,
    stars: repo.stargazers_count,
    openIssues: repo.open_issues_count,
    assets,
    sizes: Object.fromEntries(Object.entries(assets).map(([k, v]) => [k, bytes(v?.size)])),
    updatedAt: new Date().toISOString(),
    degraded: false,
  };
} catch (err) {
  console.warn(`! release fetch failed: ${err.message}`);
  // Keep the last good file rather than shipping zeros; fall back to the
  // /releases/latest redirect for every link if there is nothing on disk.
  try {
    const prev = JSON.parse(await readFile(OUT, 'utf8'));
    console.warn('  reusing the previous downloads.json');
    payload = { ...prev, degraded: true };
  } catch {
    console.warn('  no previous data — emitting a link-only fallback');
    payload = {
      total: null,
      byRelease: {},
      latestVersion: null,
      latestTag: null,
      latestUrl: RELEASES_LATEST,
      publishedAt: null,
      releaseCount: null,
      stars: null,
      openIssues: null,
      assets: Object.fromEntries(
        Object.keys(SELECTORS).map((k) => [k, { name: null, url: RELEASES_LATEST, size: null }])
      ),
      sizes: {},
      updatedAt: new Date().toISOString(),
      degraded: true,
    };
  }
}

/**
 * Keep the previous `updatedAt` when nothing else changed, so the hourly
 * workflow produces a byte-identical file and git has nothing to commit.
 * Without this the repo collects an empty commit every hour.
 */
try {
  const prev = JSON.parse(await readFile(OUT, 'utf8'));
  const strip = ({ updatedAt, ...rest }) => JSON.stringify(rest);
  if (strip(prev) === strip(payload)) {
    payload.updatedAt = prev.updatedAt;
    console.log('no change since last run');
  }
} catch {
  /* first run, or unreadable — write fresh */
}

await mkdir(dirname(OUT), { recursive: true });
await writeFile(OUT, JSON.stringify(payload, null, 2) + '\n');

console.log(`latest      : ${payload.latestTag ?? '(unresolved)'}`);
console.log(`downloads   : ${payload.total ?? '(unknown)'} across ${payload.releaseCount ?? '?'} releases`);
console.log(`stars       : ${payload.stars ?? '(unknown)'}`);
for (const [k, v] of Object.entries(payload.assets ?? {})) {
  console.log(`  ${k.padEnd(17)} ${v?.name ?? '-- missing --'}`);
}
console.log(`wrote ${OUT}`);

/**
 * Live repository facts for the stats row: GitHub stars from the repo record,
 * and the format and platform counts read off the README's own lists.
 *
 * Two unauthenticated calls every five minutes (24/hour) on top of the
 * releases poll in site.ts (30/hour) keeps a visitor inside GitHub's 60/hour
 * limit. Hidden tabs sit the poll out. Any failure leaves the build-time
 * values in place.
 */
import { REPO } from '../config/site';
import { readCache, remember } from './live-cache';

const API = `https://api.github.com/repos/${REPO}`;
const HEADERS = { Accept: 'application/vnd.github+json' };
const POLL = 10 * 60 * 1000;

const els = {
  stars: document.querySelector<HTMLElement>('[data-live="stars"]'),
  formats: document.querySelector<HTMLElement>('[data-live="formats"]'),
  platforms: document.querySelector<HTMLElement>('[data-live="platforms"]'),
};

const format = new Intl.NumberFormat('en-US').format;

function set(el: HTMLElement | null, value: string) {
  if (el && el.textContent !== value) el.textContent = value;
}

async function fetchStars() {
  const res = await fetch(API, { headers: HEADERS });
  if (!res.ok) return;
  const repo = (await res.json()) as { stargazers_count?: number };
  if (typeof repo.stargazers_count === 'number') {
    set(els.stars, format(repo.stargazers_count));
    remember({ stars: repo.stargazers_count });
  }
}

/** "- [x] Plays FLAC, ALAC, … and M4A" → 12; "Supported platforms: Windows …,
    macOS …, Linux …" → 3. Both lines are in the README's feature list. */
async function fetchReadme() {
  const res = await fetch(`${API}/readme`, { headers: HEADERS });
  if (!res.ok) return;
  const data = (await res.json()) as { content?: string; encoding?: string };
  if (!data.content || data.encoding !== 'base64') return;
  const text = atob(data.content.replace(/\n/g, ''));

  const plays = text.match(/^\s*(?:-\s*\[[ x]\]\s*)?Plays\s+(.+?)\s*$/im);
  if (plays) {
    const names = plays[1].split(/,\s*|\s+and\s+/).map((s) => s.trim()).filter(Boolean);
    if (names.length) {
      set(els.formats, String(names.length));
      remember({ formats: names.length });
    }
  }

  const plat = text.match(/^Supported platforms:\s*(.+?)\s*$/im);
  if (plat) {
    const found = ['windows', 'macos', 'linux'].filter((p) => plat[1].toLowerCase().includes(p));
    if (found.length) {
      set(els.platforms, String(found.length));
      remember({ platforms: found.length });
    }
  }
}

async function refresh() {
  if (document.visibilityState === 'hidden') return;
  await Promise.allSettled([fetchStars(), fetchReadme()]);
}

if (els.stars || els.formats || els.platforms) {
  /* Last live values first, so a reload never shows the build-time number
     while the fetch is in flight — or forever, if the rate limit is spent. */
  const cached = readCache();
  if (typeof cached.stars === 'number') set(els.stars, format(cached.stars));
  if (typeof cached.formats === 'number') set(els.formats, String(cached.formats));
  if (typeof cached.platforms === 'number') set(els.platforms, String(cached.platforms));

  refresh();
  setInterval(refresh, POLL);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') refresh();
  });
}

export {};

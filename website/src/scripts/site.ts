/**
 * Every line of client JS on the site lives here. Hand-written, no libraries.
 * Anything that CSS can do is left to CSS.
 */

import { REPO } from '../config/site';

const root = document.documentElement;
const live = document.getElementById('live-region');
const reduced = matchMedia('(prefers-reduced-motion: reduce)');

function announce(message: string) {
  if (!live) return;
  live.textContent = '';
  // A fresh text node is what actually triggers the announcement.
  requestAnimationFrame(() => {
    live.textContent = message;
  });
}

/* -------------------------------------------------------------------------
   Theme
   ------------------------------------------------------------------------- */

const THEME_KEY = 'noctis-theme';

/* Mirrors the two <meta name="theme-color"> values in Base.astro. Those tags
   switch on the OS scheme, not on html[data-theme], so a manual toggle has to
   rewrite them or the mobile browser chrome keeps the other theme's color. */
const THEME_BG: Record<string, string> = { dark: '#07080C', light: '#FAF9F7' };

function syncThemeColor(theme: string) {
  for (const m of document.querySelectorAll('meta[name="theme-color"]')) {
    m.setAttribute('content', THEME_BG[theme] ?? THEME_BG.dark);
  }
}

/* One switch in the header and one in the footer. The knob position is pure
   CSS off html[data-theme], so all this has to keep in sync is aria-checked. */
const toggles = document.querySelectorAll<HTMLElement>('[data-theme-toggle]');

function syncToggles(theme: string) {
  for (const el of toggles) el.setAttribute('aria-checked', String(theme === 'dark'));
}

function applyTheme(next: string, origin?: HTMLElement, event?: MouseEvent, persist = true) {
  const commit = () => {
    root.dataset.theme = next;
    syncThemeColor(next);
    syncToggles(next);
    try {
      /* persist=false is the OS following itself: clearing the key returns
         the site to auto mode so future visits track the OS again. */
      if (persist) localStorage.setItem(THEME_KEY, next);
      else localStorage.removeItem(THEME_KEY);
    } catch {
      /* private mode — the theme simply will not persist */
    }
  };

  const canAnimate =
    typeof document.startViewTransition === 'function' && !reduced.matches;

  if (!canAnimate) {
    // No View Transitions: a plain cross-fade (see motion.css). Reduced
    // motion keeps the instant swap.
    if (!reduced.matches) {
      root.setAttribute('data-theme-fade', '');
      setTimeout(() => root.removeAttribute('data-theme-fade'), 300);
    }
    commit();
    announce(`${next} theme`);
    return;
  }

  // Wipe expands from whichever switch was clicked, header or footer.
  const rect = origin?.getBoundingClientRect();
  const x = event?.clientX ?? (rect ? rect.left + rect.width / 2 : innerWidth / 2);
  const y = event?.clientY ?? (rect ? rect.top + rect.height / 2 : 0);
  root.style.setProperty('--vt-x', `${x}px`);
  root.style.setProperty('--vt-y', `${y}px`);
  root.dataset.vtTheme = '';

  /* Named only for the duration of the transition: the switch becomes its own
     live view-transition group (motion.css drops its snapshot animations), so
     the knob answers the click immediately instead of freezing until the wipe
     completes. */
  if (origin) origin.style.viewTransitionName = 'theme-toggle';

  const transition = document.startViewTransition(commit);
  transition.finished.finally(() => {
    delete root.dataset.vtTheme;
    if (origin) origin.style.viewTransitionName = '';
    announce(`${next} theme`);
  });
}

for (const el of toggles) {
  el.addEventListener('click', (event) => {
    applyTheme(root.dataset.theme === 'light' ? 'dark' : 'light', el, event as MouseEvent);
  });
}

syncToggles(root.dataset.theme ?? 'dark');
/* The boot script's theme-color metas switch on the OS scheme; if a stored
   manual pick disagrees with the OS, align the browser chrome on load too. */
syncThemeColor(root.dataset.theme ?? 'dark');

/* Follow the OS live: flipping the system (or browser) appearance re-themes
   the page on the spot. A deliberate OS-level switch is fresher signal than
   any earlier manual pick, so it also returns the site to auto mode. */
matchMedia('(prefers-color-scheme: light)').addEventListener('change', (e) => {
  applyTheme(e.matches ? 'light' : 'dark', undefined, undefined, false);
});

/* -------------------------------------------------------------------------
   Mobile menu
   ------------------------------------------------------------------------- */

const menuBtn = document.getElementById('menu-btn');
const sheet = document.getElementById('mobile-menu');
const FOCUSABLE = 'a[href], button:not([disabled]), input, [tabindex]:not([tabindex="-1"])';

function setMenu(open: boolean) {
  if (!sheet || !menuBtn) return;

  if (open) {
    sheet.hidden = false;
    // Next frame, so the transition has a start value to animate from.
    requestAnimationFrame(() => sheet.setAttribute('data-open', ''));
    document.body.style.overflow = 'hidden';
    sheet.querySelector<HTMLElement>(FOCUSABLE)?.focus();
  } else {
    sheet.removeAttribute('data-open');
    document.body.style.overflow = '';
    const done = () => {
      if (!sheet.hasAttribute('data-open')) sheet.hidden = true;
    };
    if (reduced.matches) done();
    else setTimeout(done, 240);
    menuBtn.focus();
  }

  menuBtn.setAttribute('aria-expanded', String(open));
  menuBtn.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
}

menuBtn?.addEventListener('click', () => setMenu(menuBtn.getAttribute('aria-expanded') !== 'true'));

sheet?.addEventListener('click', (event) => {
  const target = event.target as HTMLElement;
  if (target.closest('[data-close-menu]') || target.closest('a')) setMenu(false);
});

document.addEventListener('keydown', (event) => {
  if (!sheet || sheet.hidden) return;

  if (event.key === 'Escape') {
    setMenu(false);
    return;
  }

  if (event.key !== 'Tab') return;

  // Trap focus inside the open sheet.
  const items = [...sheet.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(
    (el) => el.offsetParent !== null
  );
  if (!items.length) return;

  const first = items[0];
  const last = items[items.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
});

/* -------------------------------------------------------------------------
   Package manager popover
   The popover attribute handles light dismiss and Escape; this adds the
   expanded state, roving arrow-key movement, and hover open/close for
   pointers that can hover.
   ------------------------------------------------------------------------- */

const pmRoot = document.getElementById('pm');
const pmMenu = document.getElementById('pm-menu');
const pmTrigger = document.getElementById('pm-trigger');

/* Focus is only pulled into the menu when it was opened deliberately (click
   or keyboard) — yanking it on hover-open would paint focus rings nobody
   asked for and hijack the keyboard mid-hover. */
let pmHoverOpened = false;

pmMenu?.addEventListener('toggle', (event) => {
  const open = (event as ToggleEvent).newState === 'open';
  pmTrigger?.setAttribute('aria-expanded', String(open));
  // The checked manager-tab radio, when the menu has one — arrow keys then
  // switch tabs natively. Falls back to the first button.
  if (open && !pmHoverOpened)
    pmMenu.querySelector<HTMLElement>('input:checked, button')?.focus();
  if (!open) pmHoverOpened = false;
});

/* Hover opens and closes it. pointerenter/leave on the wrapper covers both
   the trigger and the menu — a top-layer popover is still a DOM child — and
   the grace timer lets the pointer cross the gap between the two without the
   menu snapping shut. Touch pointers fall through to the click behaviour. */
const pmHover = matchMedia('(hover: hover) and (pointer: fine)');
let pmCloseTimer = 0;

pmRoot?.addEventListener('pointerenter', (event) => {
  if (!pmHover.matches || event.pointerType === 'touch' || !pmMenu) return;
  clearTimeout(pmCloseTimer);
  if (!pmMenu.matches(':popover-open')) {
    pmHoverOpened = true;
    pmMenu.showPopover();
  }
});

pmRoot?.addEventListener('pointerleave', (event) => {
  if (!pmHover.matches || event.pointerType === 'touch' || !pmMenu) return;
  clearTimeout(pmCloseTimer);
  pmCloseTimer = window.setTimeout(() => {
    if (pmMenu.matches(':popover-open')) pmMenu.hidePopover();
  }, 160);
});

pmMenu?.addEventListener('keydown', (event) => {
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
  const items = [...pmMenu.querySelectorAll<HTMLElement>('button')];
  if (!items.length) return;
  event.preventDefault();
  const at = items.indexOf(document.activeElement as HTMLElement);
  const step = event.key === 'ArrowDown' ? 1 : -1;
  items[(at + step + items.length) % items.length].focus();
});

pmTrigger?.setAttribute('aria-expanded', 'false');

/* -------------------------------------------------------------------------
   Copy to clipboard
   ------------------------------------------------------------------------- */

document.addEventListener('click', async (event) => {
  const button = (event.target as HTMLElement).closest<HTMLElement>('[data-copy]');
  if (!button) return;

  const text = button.dataset.copy ?? '';
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    announce('Copy failed — select the command and copy it manually');
    return;
  }

  button.setAttribute('data-copied', '');
  announce('Command copied to clipboard');
  setTimeout(() => button.removeAttribute('data-copied'), 1400);
});

/* -------------------------------------------------------------------------
   Inertia wheel scrolling

   CSS `scroll-behavior: smooth` (base.css) already animates anchor jumps.
   This gives the mouse wheel the same glide: a notched wheel lands ~100px
   jolts, which are eased through requestAnimationFrame instead. Trackpads
   already glide — a stream of small pixel deltas — and are left native,
   because taking them over is how smooth-scroll libraries break momentum
   flicks. Keyboard, scrollbar and touch scrolling stay native too.
   ------------------------------------------------------------------------- */

{
  let target = 0;
  let frame = 0;
  let gliding = false;

  const maxScroll = () => root.scrollHeight - innerHeight;

  /** True when an element between the wheel and the page scrolls on its own
      vertically — its wheel events are its business, not the glide's. */
  function scrollsItself(from: EventTarget | null): boolean {
    for (let el = from as HTMLElement | null; el && el !== document.body; el = el.parentElement) {
      if (
        el.scrollHeight > el.clientHeight + 1 &&
        /auto|scroll/.test(getComputedStyle(el).overflowY)
      ) {
        return true;
      }
    }
    return false;
  }

  function glide() {
    const remaining = target - scrollY;
    // Below ~4px the lerp step rounds to a standstill (scroll positions are
    // integers), so snap the last hop rather than spinning rAF forever.
    if (Math.abs(remaining) < 4) {
      scrollTo({ top: target, behavior: 'instant' });
      gliding = false;
      return;
    }
    // Lerp from the LIVE position, so anything else that moved the page since
    // the last frame is folded in rather than fought.
    scrollTo({ top: scrollY + remaining * 0.16, behavior: 'instant' });
    frame = requestAnimationFrame(glide);
  }

  addEventListener(
    'wheel',
    (event) => {
      if (reduced.matches) return;
      // Pinch-zoom and modified scrolls stay native.
      if (event.ctrlKey || event.shiftKey || event.defaultPrevented) return;
      // Trackpads announce themselves as pixel deltas well under one notch.
      if (event.deltaMode === 0 && Math.abs(event.deltaY) < 40) return;
      if (scrollsItself(event.target)) return;

      event.preventDefault();
      if (!gliding) target = scrollY;
      const step = event.deltaMode === 1 ? event.deltaY * 40 : event.deltaY;
      target = Math.max(0, Math.min(target + step, maxScroll()));
      if (!gliding) {
        gliding = true;
        frame = requestAnimationFrame(glide);
      }
    },
    { passive: false }
  );

  // Any other way of moving the page takes over the moment it is used.
  for (const cancel of ['keydown', 'pointerdown'] as const) {
    addEventListener(cancel, () => {
      if (!gliding) return;
      cancelAnimationFrame(frame);
      gliding = false;
    });
  }
}

/* -------------------------------------------------------------------------
   Live release feed

   Everything version-shaped on this page — the count, the version badge, the
   download button's target — is server-rendered, so it is only ever as fresh
   as the last BUILD. Two sources repair that in place:

   - /api/downloads.json, same-origin, rewritten by a workflow on release.
     Carries the version, the asset URLs and a total. Only as fresh as the
     workflow's last run.
   - api.github.com/repos/…/releases, queried directly. THE live source —
     GitHub bumps download_count within seconds of a download and lists a new
     release the moment it is published — so its count, version, release link
     and asset URLs all win over the feed's whenever it answers.

   The GitHub call is unauthenticated: 60 requests/hour per visitor IP, which
   is exactly the 60s poll below. Hidden tabs skip the poll (visibilitychange
   refreshes on return), so the quota is spent only while someone is looking.
   When the quota runs out the fetch 403s, returns null, and the same-origin
   feed's total quietly takes over — never a broken counter either way.

   PATCHING THE VERSION MATTERS AS MUCH AS THE COUNT. Before this, a new
   release left the badge reading the old version and — worse — left the
   download button pointing at the previous version's asset URL until someone
   rebuilt the site.
   ------------------------------------------------------------------------- */

const counter = document.querySelector<HTMLElement>('[data-download-total]');
const verEl = document.querySelector<HTMLElement>('[data-release-version]');
const verLink = document.querySelector<HTMLAnchorElement>('[data-release-link]');

/* Which asset each platform's button offers. Mirrors the `ctas` array in
   Hero.astro — if that changes, change this. */
const CTA_ASSET: Record<string, string> = {
  windows: 'windowsInstaller',
  macos: 'macosArm',
  linux: 'linuxAppImage',
};

interface Feed {
  total?: number;
  latestVersion?: string | null;
  latestUrl?: string;
  assets?: Record<string, { url?: string } | null>;
}

/* The feed is a same-origin static file, but its URLs become live hrefs on
   the download buttons — refuse anything that is not plain http(s) rather
   than trust the file forever. */
function safeHttp(u: string, fallback: string): string {
  try {
    const p = new URL(u, location.origin);
    if (p.protocol === 'https:' || p.protocol === 'http:') return p.href;
  } catch {
    /* malformed — keep the build-time href */
  }
  return fallback;
}

/** Applies everything in the feed that is not the count. */
function applyRelease(data: Feed) {
  const version = data.latestVersion;
  if (version && verEl && verEl.textContent !== `v${version}`) {
    verEl.textContent = `v${version}`;
  }
  if (data.latestUrl && verLink) verLink.href = safeHttp(data.latestUrl, verLink.href);

  for (const [os, key] of Object.entries(CTA_ASSET)) {
    const url = data.assets?.[key]?.url;
    if (!url) continue;
    for (const el of document.querySelectorAll<HTMLAnchorElement>(`[data-os-cta="${os}"]`)) {
      el.href = safeHttp(url, el.href);
    }
  }

  /* The download cards carry data-asset="<key>" — patch every key the data
     names, so the platform grid tracks a new release as fast as the hero. */
  for (const [key, asset] of Object.entries(data.assets ?? {})) {
    const url = asset?.url;
    if (!url) continue;
    for (const el of document.querySelectorAll<HTMLAnchorElement>(`[data-asset="${key}"]`)) {
      el.href = safeHttp(url, el.href);
    }
  }
}

/* Not `if (counter)`. When the feed is degraded the total is null and Hero
   renders no counter at all — but the version badge is still on the page, and
   that is exactly the situation where it most needs to repair itself once the
   feed recovers. Either element is reason enough to poll. */
if (counter || verEl) {
  const format = new Intl.NumberFormat('en-US').format;
  const FEED = '/api/downloads.json';
  /* 60s. The feed is a static same-origin file, so this is cheap — but note
     the real freshness ceiling is the workflow that WRITES the file, not this
     interval. Polling faster than the source changes buys nothing. */
  const POLL = 60 * 1000;
  const DURATION = 1400;
  // The house curve, so the count-up matches every other transition.
  const ease = (t: number) => 1 - Math.pow(1 - t, 3);

  const parsed = Number(counter?.dataset.downloadTotal);
  let shown = Number.isFinite(parsed) ? parsed : 0;
  let frame = 0;

  /** Rolls the visible number to `target` from wherever it currently sits. */
  function rollTo(target: number) {
    if (!counter || target === shown) return;

    cancelAnimationFrame(frame);

    if (reduced.matches) {
      shown = target;
      counter!.textContent = format(target);
      return;
    }

    const from = shown;
    const delta = target - from;
    const start = performance.now();

    const tick = (now: number) => {
      const progress = Math.min((now - start) / DURATION, 1);
      shown = Math.round(from + delta * ease(progress));
      counter!.textContent = format(shown);
      if (progress < 1) frame = requestAnimationFrame(tick);
      else shown = target;
    };
    frame = requestAnimationFrame(tick);
  }

  /* Mirrors the SELECTORS map in scripts/fetch-release.mjs, trimmed to the
     assets the page actually renders — if that map changes, change this. */
  const GH_ASSETS: Record<string, RegExp[]> = {
    windowsInstaller: [/Setup\.exe$/i],
    windowsPortable: [/windows-x64\.zip$/i],
    macosArm: [/osx-arm64\.dmg$/i, /osx-arm64\.zip$/i],
    macosIntel: [/osx-x64\.dmg$/i, /osx-x64\.zip$/i],
    linuxAppImage: [/\.AppImage$/i],
    linuxArm: [/linux-arm64\.tar\.gz$/i],
  };

  interface GitHubRelease {
    draft?: boolean;
    prerelease?: boolean;
    published_at?: string | null;
    tag_name?: string;
    html_url?: string;
    assets?: { name?: string; browser_download_url?: string; download_count?: number }[];
  }

  /** One call, two answers: the summed download_count across every release —
      the number GitHub itself bumps when someone downloads — and the newest
      stable release (never a draft or pre-release, sorted by published_at,
      the same rule as scripts/fetch-release.mjs). Nulls on any failure. */
  async function fetchGitHub(): Promise<{ total: number | null; release: Feed | null }> {
    try {
      const res = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=100`, {
        headers: { Accept: 'application/vnd.github+json' },
      });
      if (!res.ok) return { total: null, release: null };
      const releases = (await res.json()) as GitHubRelease[];
      if (!Array.isArray(releases)) return { total: null, release: null };

      let total = 0;
      for (const rel of releases) for (const a of rel.assets ?? []) total += a.download_count ?? 0;

      const latest = releases
        .filter((r) => !r.draft && !r.prerelease && r.tag_name && r.published_at)
        .sort((a, b) => Date.parse(b.published_at ?? '') - Date.parse(a.published_at ?? ''))[0];

      let release: Feed | null = null;
      if (latest?.tag_name) {
        const assets: Feed['assets'] = {};
        for (const [key, patterns] of Object.entries(GH_ASSETS)) {
          for (const re of patterns) {
            const hit = latest.assets?.find((a) => a.name && re.test(a.name));
            if (hit?.browser_download_url) {
              assets[key] = { url: hit.browser_download_url };
              break;
            }
          }
        }
        release = {
          // Tag names flow into the DOM — keep them to plain version tokens.
          latestVersion: latest.tag_name.replace(/^v/, '').replace(/[^\w.\-+]/g, ''),
          latestUrl: latest.html_url,
          assets,
        };
      }

      return { total: total > 0 ? total : null, release };
    } catch {
      return { total: null, release: null };
    }
  }

  async function refresh() {
    // Hidden tabs sit the poll out; visibilitychange below re-checks on return.
    if (document.visibilityState === 'hidden') return;

    let feedTotal: number | null = null;
    try {
      /* Cache-busted on purpose. The feed is rewritten hourly and both the
         browser cache and any CDN edge would otherwise keep serving the copy
         that was current when this page was built — which is the whole bug. */
      const res = await fetch(`${FEED}?t=${Date.now()}`, { cache: 'no-store' });
      if (res.ok) {
        const data = (await res.json()) as Feed;
        applyRelease(data);
        const total = Number(data?.total);
        if (Number.isFinite(total) && total > 0) feedTotal = total;
      }
    } catch {
      /* Feed offline or missing — GitHub below still gets its chance. */
    }

    /* Applied AFTER the feed on purpose: GitHub lists a new release the
       moment it is published, while the feed is only as fresh as the last
       deploy — the fresher source must land last. */
    const gh = await fetchGitHub();
    if (gh.release) applyRelease(gh.release);
    const total = gh.total ?? feedTotal;
    if (total) rollTo(total);
  }

  /* The opening flourish counts up from zero once per session; on every later
     view the number is simply correct on arrival and only moves when it
     actually changes. */
  const FLOURISHED = 'noctis-counted';
  let flourished = true;
  try {
    flourished = sessionStorage.getItem(FLOURISHED) === '1';
  } catch {
    /* private mode — treat it as already done rather than replaying it */
  }

  if (counter && !flourished && !reduced.matches && shown > 0) {
    const target = shown;
    shown = 0;
    counter.textContent = '0';
    new IntersectionObserver((entries, observer) => {
      if (!entries.some((e) => e.isIntersecting)) return;
      observer.disconnect();
      try {
        sessionStorage.setItem(FLOURISHED, '1');
      } catch {
        /* ignore */
      }
      rollTo(target);
      refresh();
    }, { threshold: 0.6 }).observe(counter);
  } else {
    refresh();
  }

  setInterval(refresh, POLL);
  // A tab restored after hours in the background is the staleness case people
  // actually notice, so re-check the moment it comes back.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') refresh();
  });

  // The one download the visitor cares most about seeing counted is their own.
  // GitHub bumps download_count within a few seconds of the click; two spaced
  // re-checks catch it without waiting out the poll interval.
  document.addEventListener('click', (event) => {
    const link = (event.target as HTMLElement).closest<HTMLAnchorElement>('a[href]');
    if (!link?.href.includes('/releases/download/')) return;
    setTimeout(refresh, 4000);
    setTimeout(refresh, 15000);
  });
}

export {};

/**
 * The last live values GitHub gave this browser, kept in localStorage.
 *
 * Every number on the page is server-rendered from the last BUILD, then
 * repaired by live fetches. Without this, each reload started from the stale
 * build-time value and only caught up once a fetch succeeded — and when
 * GitHub's 60/hour limit was spent, it never did, so the same visitor saw
 * v1.5.0 on one load and v1.4.3 on the next. Now a reload starts from the
 * newest values this browser has ever seen; fetches only move them forward.
 */
const KEY = 'noctis-live';

export interface LiveCache {
  release?: {
    latestVersion?: string | null;
    latestUrl?: string;
    assets?: Record<string, { url?: string } | null>;
  };
  total?: number;
  stars?: number;
  formats?: number;
  platforms?: number;
}

export function readCache(): LiveCache {
  try {
    const raw = localStorage.getItem(KEY);
    const parsed = raw ? JSON.parse(raw) : null;
    return parsed && typeof parsed === 'object' ? (parsed as LiveCache) : {};
  } catch {
    return {};
  }
}

export function remember(patch: Partial<LiveCache>) {
  try {
    localStorage.setItem(KEY, JSON.stringify({ ...readCache(), ...patch }));
  } catch {
    /* private mode — live values simply will not survive a reload */
  }
}

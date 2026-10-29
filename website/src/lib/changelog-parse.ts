/**
 * Release-note parser, shared by the client-side changelog refresh.
 * A line-for-line port of the parsing half of scripts/fetch-changelog.mjs —
 * if that changes, change this. Output is plain text only; the page renders
 * it with textContent, never as markup.
 */

export interface Section {
  title: string | null;
  items: string[];
}

export interface ParsedRelease {
  tag: string;
  version: string;
  label: string;
  publishedAt: string;
  url: string;
  prerelease: boolean;
  sections: Section[];
  summary: string | null;
}

const SECTION_MAP: [RegExp, string][] = [
  [/^(added|new|new features?|features?)$/i, 'New Features'],
  [/^(improved|improvements?|changed|changes)$/i, 'Improvements'],
  [/^(fixed|fixes|bug ?fixes?)$/i, 'Bug Fixes'],
];

const SECTION_ORDER = ['New Features', 'Improvements', 'Bug Fixes'];

function normaliseHeading(raw: string): string {
  const text = raw.trim().replace(/[:\s]+$/, '');
  for (const [re, label] of SECTION_MAP) if (re.test(text)) return label;
  return text;
}

function plain(md: string): string {
  return md
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/(\*\*|__)(.*?)\1/g, '$2')
    .replace(/(\*|_)(?=\S)(.*?)(?<=\S)\1/g, '$2')
    .replace(/<[^>]*>/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function contentSlice(body: string): string {
  const start = body.search(/^#{1,3}\s*What'?s\s+Changed\s*$/im);
  let text = start === -1 ? body : body.slice(start).replace(/^[^\n]*\n/, '');
  const stop = text.search(/^\s*(\*\*Full Changelog\*\*|---\s*$)/im);
  if (stop !== -1) text = text.slice(0, stop);
  return text;
}

function categorise(text: string): string {
  if (/^(added|new)\b/i.test(text)) return 'New Features';
  if (/^fix(ed)?\b/i.test(text)) return 'Bug Fixes';
  return 'Improvements';
}

function groupByVerb(items: string[]): Section[] {
  const buckets = new Map<string, Section>();
  for (const text of items) {
    const title = categorise(text);
    if (!buckets.has(title)) buckets.set(title, { title, items: [] });
    buckets.get(title)!.items.push(text);
  }
  return [...buckets.values()];
}

export function parseSections(body: string | null | undefined): Section[] {
  if (!body) return [];

  const sections: Section[] = [];
  let current: Section | null = null;

  for (const line of contentSlice(body).split(/\r?\n/)) {
    const heading = line.match(/^#{2,4}\s+(.+?)\s*$/);
    if (heading) {
      current = { title: normaliseHeading(heading[1]), items: [] };
      sections.push(current);
      continue;
    }
    const bullet = line.match(/^[-*+]\s+(.+?)\s*$/);
    if (!bullet) continue;
    const text = plain(bullet[1]);
    if (!text) continue;
    if (!current) {
      current = { title: null, items: [] };
      sections.push(current);
    }
    current.items.push(text);
  }

  const found = sections.filter((s) => s.items.length);
  const headed = found.some((s) => s.title !== null);
  const resolved = headed ? found : groupByVerb(found.flatMap((s) => s.items));

  const ranked = (s: Section) => {
    const i = SECTION_ORDER.indexOf(s.title ?? '');
    return i === -1 ? SECTION_ORDER.length : i;
  };
  return resolved.sort((a, b) => ranked(a) - ranked(b));
}

export function parseSummary(body: string | null | undefined): string | null {
  if (!body) return null;
  const lines = contentSlice(body)
    .split(/\r?\n/)
    .filter((l) => !/^\s*(#|[-*+]\s|\|)/.test(l))
    .map(plain)
    .filter(Boolean);
  return lines.join(' ') || null;
}

export function labelFor(tag: string, name: string | null, version: string): string {
  if (/^v?\d/.test(tag)) return version;
  const fromName = plain(name ?? '')
    .replace(/\bplugin\b/gi, '')
    .replace(/\bv?\d+(\.\d+)*\b/gi, '')
    .replace(/\s+/g, ' ')
    .trim();
  if (fromName) return fromName;
  const bare = tag.replace(/^plugin-/, '').replace(/-v[\d.]*$/, '');
  return bare.charAt(0).toUpperCase() + bare.slice(1);
}

interface GitHubRelease {
  draft?: boolean;
  prerelease?: boolean;
  published_at?: string | null;
  tag_name?: string;
  name?: string | null;
  html_url?: string;
  body?: string | null;
}

/** GitHub /releases payload → page entries, newest first. Only https URLs
    under this repo survive; anything else falls back to the releases list. */
export function fromGitHub(all: GitHubRelease[], repo: string): ParsedRelease[] {
  const safeUrl = (raw: string | undefined) => {
    try {
      const u = new URL(raw ?? '');
      if (u.protocol === 'https:' && u.hostname === 'github.com' && u.pathname.startsWith(`/${repo}/`))
        return u.href;
    } catch {
      /* fall through */
    }
    return `https://github.com/${repo}/releases`;
  };

  return all
    .filter((r) => !r.draft && r.published_at && r.tag_name)
    .sort((a, b) => Date.parse(b.published_at!) - Date.parse(a.published_at!))
    .map((r) => {
      const tag = r.tag_name!.replace(/[^\w.\-+]/g, '');
      const version = r.tag_name!.replace(/^v/, '').replace(/[^\w.\-+]/g, '');
      const sections = parseSections(r.body);
      return {
        tag,
        version,
        label: labelFor(tag, r.name ?? null, version),
        publishedAt: r.published_at!,
        url: safeUrl(r.html_url),
        prerelease: Boolean(r.prerelease),
        sections,
        summary: sections.length ? null : parseSummary(r.body),
      };
    });
}

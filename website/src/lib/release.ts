import raw from '../../public/api/downloads.json';
import { links } from '../config/site';

export interface ReleaseAsset {
  name: string | null;
  url: string;
  size: number | null;
  downloads?: number;
}

export interface ReleaseData {
  total: number | null;
  byRelease: Record<string, number>;
  latestVersion: string | null;
  latestTag: string | null;
  latestUrl: string;
  publishedAt: string | null;
  releaseCount: number | null;
  stars: number | null;
  openIssues: number | null;
  assets: Record<string, ReleaseAsset | null>;
  sizes: Record<string, string | null>;
  updatedAt: string;
  degraded: boolean;
}

export const release = raw as unknown as ReleaseData;

/** Asset URL, falling back to the /releases/latest redirect so links never 404. */
export function assetUrl(key: keyof typeof release.assets | string): string {
  return release.assets?.[key]?.url ?? links.releasesLatest;
}

export function assetSize(key: string): string | null {
  return release.sizes?.[key] ?? null;
}

/** Rendered server-side so the counter is correct with JS disabled. */
export const downloadsFormatted =
  release.total == null ? null : new Intl.NumberFormat('en-US').format(release.total);

export const versionLabel = release.latestVersion ? `v${release.latestVersion}` : null;

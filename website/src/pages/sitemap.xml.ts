import type { APIRoute } from 'astro';
import { site } from '../config/site';

/**
 * Derived from the pages directory so new routes appear automatically rather
 * than needing a second edit. /og is the social-card source, not a real page;
 * /404 is the error page.
 */
const EXCLUDE = new Set(['og', 'sitemap.xml', '404']);

const modules = import.meta.glob('./**/*.astro', { eager: true });

const routes = Object.keys(modules)
  .map((file) =>
    file
      .replace(/^\.\//, '')
      .replace(/\.astro$/, '')
      .replace(/(^|\/)index$/, '')
  )
  .filter((route) => !EXCLUDE.has(route))
  .map((route) => (route ? `/${route}` : '/'))
  .sort();

export const GET: APIRoute = () => {
  const urls = routes
    .map((route) => `  <url><loc>${new URL(route, site.url).href}</loc></url>`)
    .join('\n');

  const body = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls}
</urlset>
`;

  return new Response(body, {
    headers: { 'Content-Type': 'application/xml; charset=utf-8' },
  });
};

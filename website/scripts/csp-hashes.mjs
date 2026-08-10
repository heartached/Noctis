/**
 * Recompute CSP sha256 hashes for executable inline scripts in dist/*.html
 * and splice them into the script-src directive of dist/_headers.
 *
 * Vite inlines bundled component scripts under 4 KB, so the inline-script set
 * (and therefore the hash list) can change on any build. Runs as the last
 * build step, before dist/ is uploaded to Cloudflare Pages.
 */
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';

function htmlFiles(dir) {
  return readdirSync(dir).flatMap((name) => {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) return htmlFiles(p);
    return name.endsWith('.html') ? [p] : [];
  });
}

const hashes = new Set();
for (const file of htmlFiles('dist')) {
  const html = readFileSync(file, 'utf8');
  for (const m of html.matchAll(/<script([^>]*)>([\s\S]*?)<\/script>/g)) {
    if (/\bsrc=/.test(m[1]) || /ld\+json/.test(m[1]) || m[2] === '') continue;
    hashes.add(`'sha256-${createHash('sha256').update(m[2]).digest('base64')}'`);
  }
}

const headersPath = join('dist', '_headers');
const before = readFileSync(headersPath, 'utf8');
const list = [...hashes].join(' ');
const after = before.replace(
  /script-src 'self'[^;]*;/,
  `script-src 'self'${list ? ' ' + list : ''};`
);
writeFileSync(headersPath, after);
console.log(`csp-hashes: ${hashes.size} inline script hash(es) written to dist/_headers`);
for (const h of hashes) console.log('  ' + h);
